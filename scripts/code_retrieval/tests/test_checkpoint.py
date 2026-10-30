import json
import tempfile
import unittest
from pathlib import Path

try:
    import torch
    from scripts.code_analysis.frozen_encoder import state_hash
    from scripts.code_retrieval import checkpoint
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
        self.root = Path(self.temp.name) / "run"
        self.root.mkdir()
        self.support = Path(self.temp.name) / "upstream"
        self.upstream = tiny.write_upstream(self.support)
        self.device = torch.device("cpu")
        self.model = tiny.build_model(self.support, seed=0)
        self.fields = {"epoch": 1, "runId": "fixture-run", "trainableNames": [],
                       "trainableStateSha256": "0" * 64, "frozenStateSha256": "0" * 64}

    def write_epoch(self, epoch_dir, model):
        return checkpoint.write_epoch_checkpoint(epoch_dir, root=self.root, model=model, upstream=self.upstream,
                                                 fields=self.fields, support_dir=self.support)

    def load(self, epoch_dir, upstream=None):
        return checkpoint.load_trained(epoch_dir, upstream=upstream or self.upstream, device=self.device)

    def test_checkpoint_round_trip_keeps_the_full_state(self):
        epoch_dir = self.root / "epoch-1"
        manifest = self.write_epoch(epoch_dir, self.model)
        tokenizer, model, loaded = self.load(epoch_dir)
        self.assertEqual(state_hash(model), manifest["fullStateSha256"])
        self.assertFalse(model.training)
        self.assertTrue(all(not param.requires_grad for param in model.parameters()))
        self.assertEqual(manifest["selection"], checkpoint.SELECTION)
        self.assertIs(manifest["selectedForUse"], False)
        self.assertEqual(tokenizer.pad_token_id, 1)

    def test_epoch_checkpoint_is_write_once(self):
        epoch_dir = self.root / "epoch-1"
        self.write_epoch(epoch_dir, self.model)
        with self.assertRaises(FileExistsError):
            self.write_epoch(epoch_dir, self.model)

    def test_tampered_weights_are_refused(self):
        epoch_dir = self.root / "epoch-1"
        self.write_epoch(epoch_dir, self.model)
        weights = epoch_dir / "checkpoint" / checkpoint.WEIGHTS
        raw = bytearray(weights.read_bytes())
        raw[-1] ^= 0x01
        weights.write_bytes(bytes(raw))
        with self.assertRaisesRegex(ValueError, "checkpoint file drift"):
            self.load(epoch_dir)

    def test_missing_file_is_refused(self):
        epoch_dir = self.root / "epoch-1"
        self.write_epoch(epoch_dir, self.model)
        (epoch_dir / "checkpoint" / "config.json").unlink()
        with self.assertRaisesRegex(ValueError, "file set mismatch|extra or missing"):
            self.load(epoch_dir)

    def test_extra_file_is_refused(self):
        epoch_dir = self.root / "epoch-1"
        self.write_epoch(epoch_dir, self.model)
        (epoch_dir / "checkpoint" / "pytorch_model.bin").write_bytes(b"not a checkpoint")
        with self.assertRaisesRegex(ValueError, "extra or missing"):
            self.load(epoch_dir)

    def test_wrong_upstream_is_refused(self):
        epoch_dir = self.root / "epoch-1"
        self.write_epoch(epoch_dir, self.model)
        other = {**self.upstream, "modelRevision": "a-different-revision"}
        with self.assertRaisesRegex(ValueError, "upstream revision drift"):
            self.load(epoch_dir, upstream=other)

    def test_reference_from_another_model_is_detected(self):
        epoch_dir = self.root / "epoch-1"
        self.write_epoch(epoch_dir, self.model)
        other = tiny.build_model(self.support, seed=7)
        checkpoint.write_reference(epoch_dir, root=self.root, model=other, chunk_ids=REFERENCE_IDS,
                                   pad_id=1, device=self.device)
        result = checkpoint.reload_epoch(epoch_dir, root=self.root, upstream=self.upstream, device=self.device)
        self.assertFalse(result["passed"])
        self.assertGreater(result["maxAbsDifference"], checkpoint.RELOAD_TOLERANCE)

    def test_matching_reference_passes_reload(self):
        epoch_dir = self.root / "epoch-1"
        self.write_epoch(epoch_dir, self.model)
        checkpoint.write_reference(epoch_dir, root=self.root, model=self.model, chunk_ids=REFERENCE_IDS,
                                   pad_id=1, device=self.device)
        result = checkpoint.reload_epoch(epoch_dir, root=self.root, upstream=self.upstream, device=self.device)
        self.assertTrue(result["passed"])
        self.assertLessEqual(result["maxAbsDifference"], checkpoint.RELOAD_TOLERANCE)
        self.assertEqual(result["shape"], [2, 768])

    def test_tampered_reference_bytes_are_refused(self):
        epoch_dir = self.root / "epoch-1"
        self.write_epoch(epoch_dir, self.model)
        checkpoint.write_reference(epoch_dir, root=self.root, model=self.model, chunk_ids=REFERENCE_IDS,
                                   pad_id=1, device=self.device)
        path = epoch_dir / checkpoint.REFERENCE_NAME
        record = json.loads(path.read_text(encoding="utf-8"))
        record["vectorSha256"] = "0" * 64
        path.write_text(json.dumps(record), encoding="utf-8")
        with self.assertRaisesRegex(ValueError, "vector bytes drift"):
            checkpoint.reload_epoch(epoch_dir, root=self.root, upstream=self.upstream, device=self.device)


if __name__ == "__main__":
    unittest.main()
