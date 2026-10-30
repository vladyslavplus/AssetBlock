import json
import shutil
import tempfile
import unittest
from pathlib import Path
from unittest import mock

try:
    import torch
    from scripts.code_analysis.contracts import canonical, digest
    from scripts.code_analysis.frozen_encoder import state_hash
    from scripts.code_retrieval import checkpoint, index_verify
    from scripts.code_retrieval.tests import _tiny_encoder as tiny
    TORCH_IMPORT_ERROR = None
except ImportError as exc:  # torch-less interpreters skip the trained-encoder checks
    TORCH_IMPORT_ERROR = exc

REFERENCE_IDS = [[0, 6, 2, 7, 8, 9, 2], [0, 10, 11, 12, 2]]


@unittest.skipIf(TORCH_IMPORT_ERROR is not None, "torch/transformers not importable")
class CheckpointTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name) / "retrieval"
        self.root.mkdir()
        self.support = Path(self.temp.name) / "upstream"
        self.upstream = tiny.write_upstream(self.support)
        self.device = torch.device("cpu")
        self.run = self.write_run("fixture-run", seed=0)
        self.expectations = index_verify.load_training_evidence(self.run)

    def test_training_binding_refuses_each_field_drift_and_accepts_the_bound_run(self):
        expectations = {"identity": {
            "inputsManifestSha256": "a" * 64, "configSha256": "b" * 64, "configByteSha256": "c" * 64,
            "prepare": {"runId": "prepare-fixture", "prepareDataSha256": "d" * 64,
                        "trainingAdmissionSha256": "e" * 64, "admittedTriplets": 111}}}
        base = {"loaded": {"manifestSha256": "a" * 64},
                "config_loaded": {"configSha256": "b" * 64, "byteSha256": "c" * 64},
                "prepare_binding": {"runId": "prepare-fixture", "prepareDataSha256": "d" * 64,
                                    "trainingAdmissionSha256": "e" * 64, "admittedTriplets": 111}}
        index_verify.require_training_binding(expectations, **base)
        prepare = base["prepare_binding"]
        drifted = {
            "inputsManifestSha256": {"loaded": {"manifestSha256": "0" * 64}},
            "configSha256": {"config_loaded": {"configSha256": "0" * 64, "byteSha256": "c" * 64}},
            "configByteSha256": {"config_loaded": {"configSha256": "b" * 64, "byteSha256": "0" * 64}},
            "prepare.runId": {"prepare_binding": {**prepare, "runId": "other-prepare"}},
            "prepare.prepareDataSha256": {"prepare_binding": {**prepare, "prepareDataSha256": "0" * 64}},
            "prepare.trainingAdmissionSha256": {"prepare_binding": {**prepare, "trainingAdmissionSha256": "0" * 64}},
            "prepare.admittedTriplets": {"prepare_binding": {**prepare, "admittedTriplets": 110}},
        }
        for name, override in drifted.items():
            with self.subTest(field=name), self.assertRaises(ValueError) as caught:
                index_verify.require_training_binding(expectations, **{**base, **override})
            self.assertEqual(str(caught.exception), "training evidence binding drift: " + name)

    def test_config_bytes_are_compared_only_when_the_evidence_records_them(self):
        expectations = {"identity": {"inputsManifestSha256": "a" * 64, "configSha256": "b" * 64,
                                     "prepare": {"runId": "p", "prepareDataSha256": "d" * 64,
                                                 "trainingAdmissionSha256": "e" * 64, "admittedTriplets": 1}}}
        index_verify.require_training_binding(
            expectations, loaded={"manifestSha256": "a" * 64},
            config_loaded={"configSha256": "b" * 64, "byteSha256": "0" * 64},
            prepare_binding={"runId": "p", "prepareDataSha256": "d" * 64, "trainingAdmissionSha256": "e" * 64,
                             "admittedTriplets": 1})

    def identity(self, run_name):
        return {"runId": run_name, "configSha256": "b" * 64, "inputsManifestSha256": "a" * 64,
                "prepare": {"runId": "prepare-fixture", "admittedTriplets": 1}}

    def write_run(self, run_name, seed):
        """Three checkpoints plus a SUCCEEDED training evidence file that binds them."""
        run = self.root / "runs" / run_name
        identity = self.identity(run_name)
        epochs = []
        for number in (1, 2, 3):
            model = tiny.build_model(self.support, seed=seed + number)
            epoch_dir = run / ("epoch-" + str(number))
            fields = {"epoch": number, **identity, "trainableNames": [], "trainableStateSha256": "0" * 64,
                      "frozenStateSha256": "0" * 64}
            checkpoint.write_epoch_checkpoint(epoch_dir, root=self.root, model=model, upstream=self.upstream,
                                              fields=fields, support_dir=self.support)
            manifest_bytes = (epoch_dir / index_verify.CHECKPOINT_MANIFEST_NAME).read_bytes()
            epochs.append({"epoch": number, "checkpointManifestSha256": digest(manifest_bytes)})
        evidence = {"schema": index_verify.TRAIN_EVIDENCE_SCHEMA, "runId": run_name, "outcome": "SUCCEEDED",
                    "CanAuthorizePublication": False,
                    "selection": {"selectedForUse": False, "selection": index_verify.SELECTION},
                    "identity": {key: identity[key] for key in ("configSha256", "inputsManifestSha256", "prepare")},
                    "epochs": epochs}
        checkpoint.write_once(run / index_verify.TRAIN_EVIDENCE_NAME, canonical(evidence), self.root)
        return run

    def expected(self, number, expectations=None):
        return (expectations or self.expectations)["epochs"][str(number)]

    def load(self, number, run=None, expectations=None):
        epoch_dir = (run or self.run) / ("epoch-" + str(number))
        expected = self.expected(number, expectations)
        return checkpoint.load_trained(epoch_dir, upstream=self.upstream, device=self.device,
                                       expected_manifest_sha256=expected["checkpointManifestSha256"],
                                       expected_identity=expected["identity"])

    def test_checkpoint_round_trip_keeps_the_full_state(self):
        tokenizer, model, manifest = self.load(1)
        self.assertEqual(state_hash(model), manifest["fullStateSha256"])
        self.assertFalse(model.training)
        self.assertTrue(all(not param.requires_grad for param in model.parameters()))
        self.assertIs(manifest["selectedForUse"], False)
        self.assertEqual(tokenizer.pad_token_id, 1)

    def test_epoch_checkpoint_is_write_once(self):
        with self.assertRaises(FileExistsError):
            checkpoint.write_epoch_checkpoint(self.run / "epoch-1", root=self.root,
                                              model=tiny.build_model(self.support, seed=9), upstream=self.upstream,
                                              fields=self.identity(self.run.name), support_dir=self.support)

    def test_whole_foreign_epoch_is_refused(self):
        other = self.write_run("other-run", seed=50)
        shutil.rmtree(self.run / "epoch-1")
        shutil.copytree(other / "epoch-1", self.run / "epoch-1")  # self-consistent, but not the bound one
        with self.assertRaisesRegex(ValueError, "not the one training evidence binds"):
            self.load(1)

    def test_epoch_copied_under_another_number_is_refused(self):
        shutil.copytree(self.run / "epoch-1", self.root / "epoch-1-copy")
        shutil.rmtree(self.run / "epoch-3")
        shutil.move(str(self.root / "epoch-1-copy"), str(self.run / "epoch-3"))
        with self.assertRaisesRegex(ValueError, "not the one training evidence binds"):
            self.load(3)

    def test_edited_but_otherwise_consistent_manifest_is_refused(self):
        path = self.run / "epoch-2" / index_verify.CHECKPOINT_MANIFEST_NAME
        manifest = json.loads(path.read_text(encoding="utf-8"))
        manifest["fullStateSha256"] = "0" * 64
        path.write_bytes(canonical(manifest))
        with self.assertRaisesRegex(ValueError, "not the one training evidence binds"):
            self.load(2)

    def test_identity_mismatch_with_the_evidence_is_refused(self):
        expected = self.expected(1)
        wrong = dict(expected["identity"], configSha256="e" * 64)
        with self.assertRaisesRegex(ValueError, "identity differs"):
            checkpoint.load_trained(self.run / "epoch-1", upstream=self.upstream, device=self.device,
                                    expected_manifest_sha256=expected["checkpointManifestSha256"],
                                    expected_identity=wrong)

    def test_tampered_weights_are_refused(self):
        weights = self.run / "epoch-1" / "checkpoint" / index_verify.WEIGHTS
        raw = bytearray(weights.read_bytes())
        raw[-1] ^= 0x01
        weights.write_bytes(bytes(raw))
        with self.assertRaisesRegex(ValueError, "checkpoint file drift"):
            self.load(1)

    def test_missing_file_is_refused(self):
        (self.run / "epoch-1" / "checkpoint" / "config.json").unlink()
        with self.assertRaisesRegex(ValueError, "extra or missing"):
            self.load(1)

    def test_extra_file_is_refused(self):
        (self.run / "epoch-1" / "checkpoint" / "pytorch_model.bin").write_bytes(b"not a checkpoint")
        with self.assertRaisesRegex(ValueError, "extra or missing"):
            self.load(1)

    def test_support_file_swapped_between_checks_is_detected(self):
        real = checkpoint.RobertaTokenizerFast.from_pretrained

        def swap_then_load(*args, **kwargs):
            (self.run / "epoch-1" / "checkpoint" / "special_tokens_map.json").write_bytes(b"{ }")
            return real(*args, **kwargs)

        with mock.patch.object(checkpoint.RobertaTokenizerFast, "from_pretrained", side_effect=swap_then_load):
            with self.assertRaisesRegex(ValueError, "checkpoint file drift: special_tokens_map.json"):
                self.load(1)

    def test_wrong_upstream_is_refused(self):
        other = {**self.upstream, "modelRevision": "a-different-revision"}
        expected = self.expected(1)
        with self.assertRaisesRegex(ValueError, "upstream revision drift"):
            checkpoint.load_trained(self.run / "epoch-1", upstream=other, device=self.device,
                                    expected_manifest_sha256=expected["checkpointManifestSha256"],
                                    expected_identity=expected["identity"])

    def test_full_state_mismatch_is_refused(self):
        model = tiny.build_model(self.support, seed=3)
        with self.assertRaisesRegex(ValueError, "full state differs"):
            checkpoint._check_encoder(model, "0" * 64)

    def test_training_evidence_must_be_a_succeeded_publication_free_record(self):
        path = self.run / index_verify.TRAIN_EVIDENCE_NAME
        evidence = json.loads(path.read_text(encoding="utf-8"))
        evidence["outcome"] = "FAILED_RELOAD"
        path.write_bytes(canonical(evidence))
        with self.assertRaisesRegex(ValueError, "SUCCEEDED"):
            index_verify.load_training_evidence(self.run)

    def test_reference_from_another_model_is_detected(self):
        epoch_dir = self.run / "epoch-1"
        other = tiny.build_model(self.support, seed=7)
        checkpoint.write_reference(epoch_dir, root=self.root, model=other, chunk_ids=REFERENCE_IDS,
                                   pad_id=1, device=self.device)
        result = checkpoint.reload_epoch(epoch_dir, root=self.root, upstream=self.upstream, device=self.device,
                                         expected=self.expected(1))
        self.assertFalse(result["passed"])
        self.assertGreater(result["maxAbsDifference"], checkpoint.RELOAD_TOLERANCE)

    def test_matching_reference_passes_reload_and_reports_full_state_match(self):
        epoch_dir = self.run / "epoch-1"
        model = tiny.build_model(self.support, seed=1)
        checkpoint.write_reference(epoch_dir, root=self.root, model=model, chunk_ids=REFERENCE_IDS,
                                   pad_id=1, device=self.device)
        result = checkpoint.reload_epoch(epoch_dir, root=self.root, upstream=self.upstream, device=self.device,
                                         expected=self.expected(1))
        self.assertTrue(result["passed"])
        self.assertIs(result["fullStateMatches"], True)
        self.assertLessEqual(result["maxAbsDifference"], checkpoint.RELOAD_TOLERANCE)
        self.assertEqual(result["shape"], [2, 768])

    def test_recheck_writes_a_new_file_and_keeps_the_original(self):
        epoch_dir = self.run / "epoch-1"
        model = tiny.build_model(self.support, seed=1)
        checkpoint.write_reference(epoch_dir, root=self.root, model=model, chunk_ids=REFERENCE_IDS,
                                   pad_id=1, device=self.device)
        checkpoint.reload_epoch(epoch_dir, root=self.root, upstream=self.upstream, device=self.device,
                                expected=self.expected(1))
        before = (epoch_dir / index_verify.RELOAD_NAME).read_bytes()
        checkpoint.reload_epoch(epoch_dir, root=self.root, upstream=self.upstream, device=self.device,
                                expected=self.expected(1), check_name=index_verify.RELOAD_RECHECK_NAME)
        self.assertTrue((epoch_dir / index_verify.RELOAD_RECHECK_NAME).is_file())
        self.assertEqual((epoch_dir / index_verify.RELOAD_NAME).read_bytes(), before)

    def test_tampered_reference_bytes_are_refused(self):
        epoch_dir = self.run / "epoch-1"
        model = tiny.build_model(self.support, seed=1)
        checkpoint.write_reference(epoch_dir, root=self.root, model=model, chunk_ids=REFERENCE_IDS,
                                   pad_id=1, device=self.device)
        path = epoch_dir / index_verify.REFERENCE_NAME
        record = json.loads(path.read_text(encoding="utf-8"))
        record["vectorSha256"] = "0" * 64
        path.write_text(json.dumps(record), encoding="utf-8")
        with self.assertRaisesRegex(ValueError, "vector bytes drift"):
            checkpoint.reload_epoch(epoch_dir, root=self.root, upstream=self.upstream, device=self.device,
                                    expected=self.expected(1))


if __name__ == "__main__":
    unittest.main()
