import json
import os
import tempfile
import time
import unittest
from pathlib import Path
from unittest import mock

try:
    import torch
    from scripts.code_analysis.contracts import canonical
    from scripts.code_retrieval import admission, checkpoint, training
    from scripts.code_retrieval.tests import _tiny_encoder as tiny
    TORCH_IMPORT_ERROR = None
except ImportError as exc:  # torch-less interpreters skip the trained-encoder checks
    TORCH_IMPORT_ERROR = exc

ITEM_IDS = [[0, 6, 2, 7, 8, 2], [0, 9, 2], [0, 10, 11, 2], [0, 12, 2]]
SETTINGS = {"temperature": 0.07, "gradientAccumulation": 4, "gradientClip": 1.0, "epochs": 1,
            "trainableEncoderBlocks": 2, "learningRate": 1e-5, "deadlineMinutes": 90}


@unittest.skipIf(TORCH_IMPORT_ERROR is not None, "torch/transformers not importable")
class TrainingTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.support = self.root / "upstream"
        self.upstream = tiny.write_upstream(self.support)
        self.device = torch.device("cpu")

    def trainer(self, count, *, settings=None):
        target = self.root / ("run-" + str(count) + "-" + str(time.monotonic_ns()))
        target.mkdir()
        model = tiny.build_model(self.support, seed=0)
        return training.Trainer(model=model, pad_id=1, items=[{"ids": ITEM_IDS}] * count, reference_ids=[],
                                device=self.device, settings=settings or SETTINGS, seed=42,
                                identity={"upstream": self.upstream}, target=target, started=time.monotonic())

    # -- trainable selection and sampling -----------------------------------
    def test_only_the_last_blocks_train(self):
        model = tiny.build_model(self.support)
        names = training.select_trainable(model, 2)
        self.assertTrue(names)
        self.assertTrue(all(name.startswith(("encoder.layer.1.", "encoder.layer.2.")) for name in names))
        self.assertEqual({name.split(".")[2] for name in names}, {"1", "2"})
        self.assertEqual(names, sorted(names))
        frozen = [name for name, param in model.named_parameters() if not param.requires_grad]
        self.assertIn("embeddings.word_embeddings.weight", frozen)

    def test_invalid_block_counts_are_refused(self):
        model = tiny.build_model(self.support)
        for blocks in (0, tiny.LAYERS + 1, True, 1.0):
            with self.subTest(blocks=blocks), self.assertRaisesRegex(ValueError, "out of range"):
                training.select_trainable(model, blocks)

    def test_epoch_order_is_deterministic_per_seed_and_epoch(self):
        first = training.epoch_order(10, 42, 1)
        self.assertEqual(first, training.epoch_order(10, 42, 1))
        self.assertNotEqual(first, training.epoch_order(10, 42, 2))
        self.assertEqual(sorted(first), list(range(10)))

    def test_final_partial_group_keeps_its_real_size(self):
        groups = training.accumulation_groups(list(range(10)), 4)
        self.assertEqual([len(group) for group in groups], [4, 4, 2])
        self.assertEqual(sum(groups, []), list(range(10)))

    def test_reference_rows_are_distinct_and_capped(self):
        items = [{"fragmentIds": ["a", "b", "c", "d"], "ids": [[1], [2], [3], [4]]},
                 {"fragmentIds": ["a", "e", "f", "g"], "ids": [[5], [6], [7], [8]]}]
        fragment_ids, ids = training.reference_rows(items, 5)
        self.assertEqual(fragment_ids, ["a", "b", "c", "d", "e"])
        self.assertEqual(ids, [[1], [2], [3], [4], [6]])

    # -- loss and gradient gating -------------------------------------------
    def test_loss_is_cross_entropy_over_the_three_candidates_only(self):
        model = tiny.build_model(self.support)
        vectors = checkpoint.encode_rows(model, ITEM_IDS, 1, self.device, amp=False)
        logits = torch.stack([vectors[1] @ vectors[0], vectors[2] @ vectors[0], vectors[3] @ vectors[0]]) / 0.07
        expected = -torch.log_softmax(logits, 0)[0].item()
        loss = training.triplet_loss(model, {"ids": ITEM_IDS}, 1, self.device, 0.07)
        self.assertAlmostEqual(loss.item(), expected, places=5)

    def test_finite_gradients_apply_a_step(self):
        param = torch.nn.Parameter(torch.zeros(3))
        optimizer = torch.optim.AdamW([param], lr=0.1)
        scaler = torch.amp.GradScaler("cpu", enabled=False)
        param.grad = torch.ones(3)
        applied, norm = training.finish_step(optimizer=optimizer, scaler=scaler, parameters=[param],
                                             finite_loss=True, clip=1.0)
        self.assertTrue(applied)
        self.assertGreater(norm, 0)
        self.assertGreater(float(param.abs().sum()), 0)

    def test_non_finite_gradient_skips_the_step_without_moving_weights(self):
        param = torch.nn.Parameter(torch.zeros(3))
        optimizer = torch.optim.AdamW([param], lr=0.1)
        scaler = torch.amp.GradScaler("cpu", enabled=False)
        param.grad = torch.tensor([float("nan"), 0.0, 0.0])
        applied, norm = training.finish_step(optimizer=optimizer, scaler=scaler, parameters=[param],
                                             finite_loss=True, clip=1.0)
        self.assertFalse(applied)
        self.assertIsNone(norm)
        self.assertTrue(torch.equal(param.detach(), torch.zeros(3)))
        self.assertIsNone(param.grad)

    def test_injected_non_finite_loss_is_counted_skipped_not_successful(self):
        trainer = self.trainer(4)
        before = {name: tensor.clone() for name, tensor in trainer.baseline_tensors.items()}
        real = training.triplet_loss
        with mock.patch.object(training, "triplet_loss", side_effect=lambda *a, **k: real(*a, **k) * float("nan")):
            trainer.train_epoch(1)
        self.assertEqual(trainer.skipped, 1)
        self.assertEqual(trainer.successful, 0)
        self.assertEqual(trainer.nonfinite, 1)
        parameters = dict(trainer.model.named_parameters())
        for name, tensor in before.items():
            self.assertTrue(torch.equal(parameters[name].detach().cpu(), tensor), name)
        trainer.close()

    def test_finite_epoch_counts_every_group_as_successful(self):
        trainer = self.trainer(8)
        before = {name: tensor.clone() for name, tensor in trainer.baseline_tensors.items()}
        measured = trainer.train_epoch(1)
        self.assertEqual((trainer.successful, trainer.skipped, trainer.nonfinite), (2, 0, 0))
        self.assertEqual(len(measured["stepMeans"]), 2)
        self.assertTrue(training.encoder_changed(training.tensor_deltas(trainer.model, before)))
        trainer.close()

    # -- binding, endpoints and outcome -------------------------------------
    def binding_fixture(self, **overrides):
        admitted = {"status": admission.ADMITTED, "totals": {"admittedTriplets": 2}, "languages": {}}
        payload = {"schema": "code-retrieval-prepare-data-v1", "status": admission.ADMITTED, "runId": "prep-1",
                   "identity": {"inputsManifestSha256": "a" * 64, "configSha256": "b" * 64,
                                "configByteSha256": "c" * 64},
                   "trainingAdmission": admitted}
        payload.update(overrides.get("payload", {}))
        raw = canonical(payload)
        loaded = {"manifestSha256": overrides.get("inputs", "a" * 64)}
        config_loaded = {"configSha256": overrides.get("config", "b" * 64), "byteSha256": "c" * 64}
        recomputed = overrides.get("recomputed", admitted)
        return raw, dict(loaded=loaded, config_loaded=config_loaded, recomputed=recomputed, admitted_count=2)

    def test_matching_prepare_run_binds(self):
        raw, kwargs = self.binding_fixture()
        binding = training.bind_prepare_run(raw, "prep-1", **kwargs)
        self.assertEqual(binding["admittedTriplets"], 2)

    def test_inputs_digest_drift_is_refused(self):
        raw, kwargs = self.binding_fixture(inputs="d" * 64)
        with self.assertRaisesRegex(ValueError, "inputsManifestSha256"):
            training.bind_prepare_run(raw, "prep-1", **kwargs)

    def test_config_digest_drift_is_refused(self):
        raw, kwargs = self.binding_fixture(config="e" * 64)
        with self.assertRaisesRegex(ValueError, "configSha256"):
            training.bind_prepare_run(raw, "prep-1", **kwargs)

    def test_non_admitted_prepare_status_is_refused(self):
        raw, kwargs = self.binding_fixture(payload={"status": "BLOCKED_INSUFFICIENT_REVIEWED_TRAIN_LABELS"})
        with self.assertRaisesRegex(ValueError, "ADMITTED"):
            training.bind_prepare_run(raw, "prep-1", **kwargs)

    def test_trainingadmission_digest_drift_is_refused(self):
        raw, kwargs = self.binding_fixture(recomputed={"status": admission.ADMITTED,
                                                       "totals": {"admittedTriplets": 2}, "languages": {"x": 1}})
        with self.assertRaisesRegex(ValueError, "digest differs"):
            training.bind_prepare_run(raw, "prep-1", **kwargs)

    def test_wrong_admitted_count_is_refused(self):
        raw, kwargs = self.binding_fixture()
        kwargs["admitted_count"] = 3
        with self.assertRaisesRegex(ValueError, "count differs"):
            training.bind_prepare_run(raw, "prep-1", **kwargs)

    def test_wrong_prepare_run_identity_is_refused(self):
        raw, kwargs = self.binding_fixture()
        with self.assertRaisesRegex(ValueError, "identity mismatch"):
            training.bind_prepare_run(raw, "prep-2", **kwargs)

    def test_validation_or_final_test_endpoint_is_refused(self):
        for partition in ("validation", "final-test"):
            with self.subTest(partition=partition):
                triplet = {"endpoints": [{"fragment": {"fragmentId": "f"}, "parent": {"partition": partition}}] * 4}
                with self.assertRaisesRegex(ValueError, "non-train endpoint"):
                    training.require_train_endpoints(triplet)

    def test_failed_or_incomplete_runs_never_succeed(self):
        good = dict(failure=None, epochs_done=3, epochs_expected=3, planned=12, successful=12,
                    nonfinite_losses=0, encoder_changed_flag=True, frozen_unchanged=True, reload_passed=True)
        self.assertEqual(training.decide(**good), ("SUCCEEDED", None))
        cases = [
            ({"failure": ("FAILED_DEADLINE", "late")}, "FAILED_DEADLINE"),
            ({"epochs_done": 1}, "FAILED_INCOMPLETE"),
            ({"nonfinite_losses": 1}, "FAILED_NONFINITE_LOSS"),
            ({"successful": 10}, "FAILED_STEP_SHORTFALL"),
            ({"encoder_changed_flag": False}, "FAILED_ENCODER_UNCHANGED"),
            ({"frozen_unchanged": False}, "FAILED_FROZEN_CHANGED"),
            ({"reload_passed": False}, "FAILED_RELOAD"),
        ]
        for change, expected in cases:
            with self.subTest(expected=expected):
                self.assertEqual(training.decide(**{**good, **change})[0], expected)

    def test_nine_of_ten_steps_is_the_minimum_success(self):
        good = dict(failure=None, epochs_done=3, epochs_expected=3, planned=10, nonfinite_losses=0,
                    encoder_changed_flag=True, frozen_unchanged=True, reload_passed=True)
        self.assertEqual(training.decide(successful=9, **good)[0], "SUCCEEDED")
        self.assertEqual(training.decide(successful=8, **good)[0], "FAILED_STEP_SHORTFALL")

    def test_block_summary_groups_changed_tensors_per_layer(self):
        deltas = [{"name": "encoder.layer.1.a", "l2Delta": 0.5}, {"name": "encoder.layer.1.b", "l2Delta": 0.0},
                  {"name": "encoder.layer.2.a", "l2Delta": 2.0}]
        blocks = training.block_summary(deltas)
        self.assertEqual([row["layer"] for row in blocks], [1, 2])
        self.assertEqual(blocks[0]["changedTensors"], 1)
        self.assertFalse(training.encoder_changed(deltas))

    def test_working_set_is_a_positive_int_on_windows_and_none_elsewhere(self):
        working_set = training.process_working_set()
        if os.name == "nt":
            self.assertIsInstance(working_set["currentBytes"], int)
            self.assertIsInstance(working_set["peakBytes"], int)
            self.assertGreater(working_set["currentBytes"], 0)
            self.assertGreaterEqual(working_set["peakBytes"], working_set["currentBytes"])
        else:
            self.assertIsNone(working_set)

    def test_setup_failure_writes_failed_evidence_and_never_reloads(self):
        root = self.root / "retrieval"
        target = root / "runs" / "setup-failure"
        target.mkdir(parents=True)
        config = {"seed": 42, "training": SETTINGS}
        loaded = {"manifestSha256": "a" * 64, "sourcesRoot": self.root}
        config_loaded = {"config": config, "configSha256": "b" * 64, "byteSha256": "c" * 64,
                         "protocolSha256": "d" * 64}
        with mock.patch.object(checkpoint, "load_upstream", side_effect=RuntimeError("setup boom")), \
                mock.patch.object(training, "reload_in_fresh_process") as reload:
            result = training._execute(
                run_id="setup-failure", target=target, root=root, loaded=loaded, config_loaded=config_loaded,
                config_path=self.root / "config.json", settings=SETTINGS, triplets=[], prepare_binding={},
                reference_probe_raw=b"{}", reference_probe={}, live_probe={}, comparison={},
                device=torch.device("cpu"), started=time.monotonic())
        reload.assert_not_called()
        evidence = json.loads((target / training.EVIDENCE).read_text(encoding="utf-8"))
        self.assertEqual(evidence["outcome"], "FAILED_ERROR")
        self.assertIn("setup boom", evidence["reason"])
        self.assertEqual(evidence["epochs"], [])
        self.assertNotEqual(evidence["outcome"], "SUCCEEDED")
        self.assertEqual(result["outcome"], "FAILED_ERROR")


if __name__ == "__main__":
    unittest.main()
