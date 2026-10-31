import json
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

from scripts.pair_verification import training
from scripts.pair_verification.architecture import JointVerifier, freeze_except_last_blocks
from scripts.pair_verification.checkpoints import SUPPORT_FILES, write_epoch_checkpoint
from scripts.pair_verification.experiment import build
from scripts.pair_verification.input_layout import layout_pair_ids
from scripts.pair_verification.training_utils import (epoch_order, require_reload_match,
                                                       require_step_ceiling)
from scripts.code_analysis.contracts import digest


class TinyEncoderConfig:
    hidden_size = 12


class TinyBlock(__import__("torch").nn.Module):
    def __init__(self):
        super().__init__()
        self.weight = __import__("torch").nn.Parameter(__import__("torch").ones(12))


class TinyEncoder(__import__("torch").nn.Module):
    def __init__(self):
        super().__init__()
        self.config = TinyEncoderConfig()
        self.encoder = __import__("torch").nn.Module()
        self.encoder.layer = __import__("torch").nn.ModuleList([TinyBlock() for _ in range(6)])


class TrainingTests(unittest.TestCase):
    def test_validation_order_counts_candidate_keys_not_windows(self):
        import torch
        pair = {"inputIds": [1, 2], "swappedInputIds": [1, 3], "truncated": False}
        records = [
            {"model": "E0", "language": "csharp", "queryId": "q1", "fileId": "f1",
             "windowPairs": [pair, pair]},
            {"model": "E0", "language": "csharp", "queryId": "q1", "fileId": "f2",
             "windowPairs": [pair]},
            {"model": "E3", "language": "python", "queryId": "q2", "fileId": "f3",
             "windowPairs": [pair]},
        ]
        logits = [[0.0, 1.0], [0.0, 1.2], [0.0, 1.0], [0.0, 0.9],
                  [0.0, 0.5], [0.0, 0.7], [0.0, 0.5], [0.0, 0.4]]
        with patch.object(training, "_forward_logits", return_value=logits):
            result = training._validation_order_evidence(object(), records, 1, torch.device("cpu"))
        self.assertEqual(result["windowPairCount"], 4)
        self.assertEqual(result["candidatePairCount"], 3)
        self.assertEqual(result["candidatePairsPerModelLanguage"], {"E0/csharp": 2, "E3/python": 1})
        self.assertEqual(sum(result["candidatePairsPerModelLanguage"].values()), 3)

    def test_loop_step_counts_loss_scale_drop_as_skipped(self):
        import torch

        class DroppingScaler:
            def __init__(self, drop):
                self.scale = 8.0
                self.drop = drop

            def unscale_(self, optimizer):
                return None

            def get_scale(self):
                return self.scale

            def step(self, optimizer):
                return None

            def update(self):
                if self.drop:
                    self.scale /= 2

        parameter = torch.nn.Parameter(torch.tensor(1.0))
        parameter.grad = torch.tensor(1.0)
        optimizer = torch.optim.SGD([parameter], lr=0.1)
        self.assertTrue(training._step_optimizer(DroppingScaler(True), optimizer, [parameter], 1.0))
        self.assertFalse(training._step_optimizer(DroppingScaler(False), optimizer, [parameter], 1.0))

    def test_reload_evidence_refuses_same_process_bad_outcome_and_checkpoint_count(self):
        base = {"outcome": "TRAINED", "runId": "pairs-train-20261030-03", "processId": 17,
                "checkpoints": [{"epoch": epoch} for epoch in range(1, 6)]}
        with self.assertRaisesRegex(ValueError, "fresh process"):
            training._validate_reload_evidence(base, base["runId"], 17)
        bad_outcome = {**base, "outcome": "FAILED"}
        with self.assertRaisesRegex(ValueError, "not successful"):
            training._validate_reload_evidence(bad_outcome, base["runId"], 18)
        bad_count = {**base, "checkpoints": base["checkpoints"][:4]}
        with self.assertRaisesRegex(ValueError, "exactly five"):
            training._validate_reload_evidence(bad_count, base["runId"], 18)

    def test_reload_probe_hash_drift_refuses(self):
        with tempfile.TemporaryDirectory() as temp:
            probe_path = Path(temp) / "probe.json"
            probe_path.write_bytes(b'{"changed":true}')
            with self.assertRaisesRegex(ValueError, "probe artifact differs"):
                training._read_verified_probe(probe_path, {"probeArtifactSha256": "0" * 64})

    def test_reload_missing_or_mistyped_id_does_not_create_run_directory(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            with patch.object(training, "REPO_ROOT", root):
                with self.assertRaisesRegex(FileNotFoundError, "does not exist"):
                    training.reload_check(run_id="pairs-train-20261030-03")
                with self.assertRaisesRegex(ValueError, "invalid training run identifier"):
                    training.reload_check(run_id="pairs-train-../../escape")
            self.assertFalse((root / "artifacts").exists())

    def test_reload_failed_evidence_write_raises(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            run_id = "pairs-train-20261030-03"
            run = root / "artifacts" / "pair_verification" / "runs" / run_id
            run.mkdir(parents=True)
            (run / "training-evidence.json").write_text("{}", encoding="utf-8")
            with patch.object(training, "REPO_ROOT", root), \
                 patch.object(training, "_write_json", side_effect=PermissionError("write denied")):
                with self.assertRaisesRegex(PermissionError, "write denied"):
                    training.reload_check(run_id=run_id)
            self.assertFalse((run / "reload-evidence.json").exists())

    def test_failure_progress_keeps_epoch_step_loss_and_safe_reason(self):
        progress = training._failure_progress(3, 7, 0.125, [0.25, 0.125],
                                              [{"epoch": 1, "meanTrainingLoss": 0.5}], 11, 1)
        self.assertEqual(progress["epoch"], 3)
        self.assertEqual(progress["step"], 7)
        self.assertEqual(progress["lastLoss"], 0.125)
        self.assertEqual(progress["currentEpochMeanLoss"], 0.1875)
        self.assertEqual(progress["completedEpochs"], [{"epoch": 1, "meanTrainingLoss": 0.5}])
        error = RuntimeError("read failed at D:\\private\\source.py token=hidden")
        safe = training._safe_failure_reason(error)
        self.assertNotIn("D:\\private", safe)
        self.assertNotIn("hidden", safe)

    def test_prepare_evidence_pair_count_is_not_a_fixed_literal(self):
        from scripts.pair_verification.training_data import _require_prepare_pair_count
        source = {"evidence": {"counts": {"trainingPairs": 7}}, "training": {"pairCount": 7}}
        self.assertEqual(_require_prepare_pair_count(7, source), 7)
        with self.assertRaisesRegex(ValueError, "preparation evidence"):
            _require_prepare_pair_count(6, source)

    def test_batch_accepts_immutable_tuple_token_ids(self):
        import torch
        examples = [{"inputIds": (1, 2, 3), "swappedInputIds": (1, 4, 3), "label": 1},
                    {"inputIds": (1, 2), "swappedInputIds": (1, 4), "label": 0}]
        ids, mask, labels = training._batch(examples, [0, 1], [False, True], 0, torch.device("cpu"))
        self.assertEqual(ids.tolist(), [[1, 2, 3], [1, 4, 0]])
        self.assertEqual(mask.tolist(), [[1, 1, 1], [1, 1, 0]])
        self.assertEqual(labels.tolist(), [1, 0])

    def test_layout_records_right_truncation_and_pair_order(self):
        left, right = list(range(400)), list(range(500, 900))
        forward = layout_pair_ids(left, right, cls_id=1, mode_id=2, sep_id=3)
        reverse = layout_pair_ids(right, left, cls_id=1, mode_id=2, sep_id=3)
        self.assertEqual((forward.a_original_tokens, forward.a_retained_tokens), (400, 254))
        self.assertEqual((forward.b_original_tokens, forward.b_retained_tokens), (400, 253))
        self.assertTrue(forward.truncated)
        self.assertEqual(forward.input_ids[:3], reverse.input_ids[:3])
        self.assertNotEqual(forward.input_ids, reverse.input_ids)

    def test_only_last_four_encoder_blocks_and_head_are_trainable(self):
        model = JointVerifier(TinyEncoder())
        names = freeze_except_last_blocks(model, 4)
        self.assertTrue(any(name.startswith("head.") for name in names))
        self.assertFalse(any("layer.0." in name or "layer.1." in name for name in names))
        self.assertTrue(any("layer.2." in name for name in names))
        self.assertEqual({name for name, p in model.named_parameters() if p.requires_grad}, set(names))

    def test_order_augmentation_is_deterministic_and_covers_each_pair_once(self):
        rows = list(range(17))
        one, two = epoch_order(rows, seed=42, epoch=3), epoch_order(rows, seed=42, epoch=3)
        self.assertEqual(one, two)
        self.assertEqual(sorted(row["index"] for row in one), rows)
        self.assertTrue(any(row["swapped"] for row in one))
        self.assertTrue(any(not row["swapped"] for row in one))

    def test_skipped_step_ceiling_and_nonfinite_loss_refuse(self):
        require_step_ceiling(skipped_steps=1, attempted_steps=10, epoch_losses=[1.0], maximum_fraction=.1)
        with self.assertRaisesRegex(ValueError, "ceiling"):
            require_step_ceiling(skipped_steps=2, attempted_steps=10, epoch_losses=[1.0], maximum_fraction=.1)
        with self.assertRaisesRegex(ValueError, "non-finite"):
            require_step_ceiling(skipped_steps=0, attempted_steps=1, epoch_losses=[float("nan")],
                                 maximum_fraction=.1)

    def test_experiment_configuration_drift_refuses(self):
        from scripts.pair_verification.training_data import validate_frozen_config
        config, _, _ = build(identity={}, validation_identity={}, token_evidence={"status": "SUCCEEDED"})
        validate_frozen_config(config)
        config["optimization"]["epochs"] = 4
        with self.assertRaisesRegex(ValueError, "optimization"):
            validate_frozen_config(config)

    def test_checkpoint_collision_is_immutable(self):
        import torch
        from transformers import RobertaConfig, RobertaModel
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            model = JointVerifier(RobertaModel(RobertaConfig(vocab_size=31, hidden_size=12,
                num_hidden_layers=6, num_attention_heads=3, intermediate_size=24)))
            names = freeze_except_last_blocks(model, 4)
            support = {}
            for name in SUPPORT_FILES:
                raw = ("support:" + name).encode()
                support[name] = {"sha256": digest(raw), "bytes": len(raw)}
            upstream = {"modelRevision": "local", "fileHashes": {name: row["sha256"]
                        for name, row in support.items()}}
            target = root / "checkpoints" / "epoch-1.safetensors"
            kwargs = dict(root=root, model=model, upstream=upstream, support_hashes=support,
                          trainable_names=names, run_id="tiny", config_sha256="a" * 64,
                          prepare_evidence_sha256="b" * 64, training_pairs_sha256="c" * 64,
                          epoch=1, epoch_facts={"loss": 1.0})
            write_epoch_checkpoint(target, **kwargs)
            with self.assertRaises(FileExistsError):
                write_epoch_checkpoint(target, **kwargs)

    def test_authored_checkpoint_reloads_same_logits(self):
        import torch
        from transformers import RobertaConfig, RobertaModel
        from scripts.pair_verification.checkpoints import load_epoch_checkpoint, write_support_files
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            source = root / "upstream"
            source.mkdir()
            config = RobertaConfig(vocab_size=31, hidden_size=12, num_hidden_layers=6,
                                   num_attention_heads=3, intermediate_size=24)
            for name in SUPPORT_FILES:
                raw = config.to_json_string().encode() if name == "config.json" else ("tiny:" + name).encode()
                (source / name).write_bytes(raw)
            support_files = {name: digest((source / name).read_bytes()) for name in SUPPORT_FILES}
            upstream = {"modelRevision": "authored", "fileHashes": support_files}
            model = JointVerifier(RobertaModel(config)).eval()
            trainable = freeze_except_last_blocks(model, 4)
            support = write_support_files(root / "support", root=root, upstream=upstream,
                                          source_dir=source)
            target = root / "checkpoints" / "epoch-1.safetensors"
            manifest, manifest_sha = write_epoch_checkpoint(
                target, root=root, model=model, upstream=upstream, support_hashes=support,
                trainable_names=trainable, run_id="tiny-reload", config_sha256="a" * 64,
                prepare_evidence_sha256="b" * 64, training_pairs_sha256="c" * 64,
                epoch=1, epoch_facts={"loss": 1.0})
            expected_evidence = {"runId": "tiny-reload", "configSha256": "a" * 64,
                                 "prepareEvidenceSha256": "b" * 64,
                                 "trainingPairsSha256": "c" * 64}
            loaded, loaded_manifest = load_epoch_checkpoint(
                target, root=root, expected_manifest_sha256=manifest_sha,
                expected_training_evidence=expected_evidence, upstream=upstream, device="cpu")
            ids = torch.tensor([[0, 4, 5, 2]], dtype=torch.long)
            mask = torch.ones_like(ids)
            with torch.inference_mode():
                expected_logits = model(ids, mask).tolist()
                actual_logits = loaded(ids, mask).tolist()
            self.assertEqual(manifest["modelStateSha256"], loaded_manifest["modelStateSha256"])
            require_reload_match(expected_logits, actual_logits, tolerance=1e-6)

    def test_reload_mismatch_fails_closed(self):
        self.assertEqual(require_reload_match([[1.0, 2.0]], [[1.0, 2.0]], tolerance=1e-5)["values"], 2)
        with self.assertRaisesRegex(ValueError, "tolerance"):
            require_reload_match([[1.0, 2.0]], [[1.0, 2.1]], tolerance=1e-5)

    def test_training_failure_writes_evidence_without_marker(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            with patch.object(training, "REPO_ROOT", root), \
                 patch.object(training, "check_gpu_driver_events", return_value={"eventCount": 0}), \
                 patch.object(training, "load_bound_prepare", side_effect=ValueError("refused")):
                with self.assertRaisesRegex(ValueError, "refused"):
                    training.train(run_id="failure-test", prepare_run_id="prepared",
                                   inputs_path="inputs", config_path="config")
            run = root / "artifacts" / "pair_verification" / "runs" / "failure-test"
            failed = json.loads((run / "training-evidence.json").read_bytes())
            self.assertEqual(failed["outcome"], "FAILED")
            self.assertEqual(failed["failureReason"], "refused")
            self.assertFalse((run / "SUCCEEDED").exists())
            self.assertNotIn("source text", (run / "training-evidence.json").read_text())

    def test_keyboard_interrupt_writes_failed_evidence_then_reraises(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            with patch.object(training, "REPO_ROOT", root), \
                 patch.object(training, "check_gpu_driver_events", return_value={"eventCount": 0}), \
                 patch.object(training, "load_bound_prepare", side_effect=KeyboardInterrupt()):
                with self.assertRaises(KeyboardInterrupt):
                    training.train(run_id="interrupt-test", prepare_run_id="prepared",
                                   inputs_path="inputs", config_path="config")
            run = root / "artifacts" / "pair_verification" / "runs" / "interrupt-test"
            failed = json.loads((run / "training-evidence.json").read_bytes())
            self.assertEqual(failed["outcome"], "FAILED")
            self.assertEqual(failed["reason"], "KeyboardInterrupt")
            self.assertFalse((run / "SUCCEEDED").exists())

    def test_system_exit_writes_failed_evidence_then_reraises(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            with patch.object(training, "REPO_ROOT", root), \
                 patch.object(training, "check_gpu_driver_events", return_value={"eventCount": 0}), \
                 patch.object(training, "load_bound_prepare", side_effect=SystemExit(3)):
                with self.assertRaises(SystemExit) as raised:
                    training.train(run_id="system-exit-test", prepare_run_id="prepared",
                                   inputs_path="inputs", config_path="config")
            run = root / "artifacts" / "pair_verification" / "runs" / "system-exit-test"
            failed = json.loads((run / "training-evidence.json").read_bytes())
            self.assertEqual(raised.exception.code, 3)
            self.assertEqual(failed["outcome"], "FAILED")
            self.assertEqual(failed["reason"], "SystemExit")
            self.assertFalse((run / "SUCCEEDED").exists())

    def test_training_directory_collision_refuses(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            run = root / "artifacts" / "pair_verification" / "runs" / "collision"
            run.mkdir(parents=True)
            with patch.object(training, "REPO_ROOT", root):
                with self.assertRaisesRegex(FileExistsError, "collision"):
                    training.train(run_id="collision", prepare_run_id="prepared",
                                   inputs_path="inputs", config_path="config")

    def test_gpu_event_monitor_uses_hidden_subprocess_and_fails_on_event_153(self):
        import subprocess
        from datetime import datetime
        start = datetime.fromisoformat("2026-10-30T22:00:00+02:00")
        with patch.object(training.subprocess, "run", return_value=subprocess.CompletedProcess(
                args=[], returncode=0, stdout="[]", stderr="")) as run_process:
            result = training.check_gpu_driver_events(start)
        self.assertEqual(result["eventCount"], 0)
        self.assertEqual(run_process.call_args.kwargs["timeout"], 15)
        self.assertEqual(run_process.call_args.kwargs["creationflags"], subprocess.CREATE_NO_WINDOW)
        payload = '[{"timeCreated":"2026-10-30T22:01:00+02:00","Id":153}]'
        with patch.object(training.subprocess, "run", return_value=subprocess.CompletedProcess(
                args=[], returncode=0, stdout=payload, stderr="")):
            with self.assertRaises(training.NvidiaDriverEventError) as raised:
                training.check_gpu_driver_events(start)
        self.assertEqual(raised.exception.events[0]["timeCreated"], "2026-10-30T22:01:00+02:00")

    def test_windows_ram_peak_measurement_is_positive(self):
        self.assertGreater(training._ram_peak_bytes(), 0)


if __name__ == "__main__":
    unittest.main()
