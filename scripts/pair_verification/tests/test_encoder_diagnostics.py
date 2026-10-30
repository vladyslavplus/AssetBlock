import json
from pathlib import Path
import subprocess
import tempfile
import unittest
from types import SimpleNamespace
from unittest.mock import Mock, patch

import torch

from scripts.code_analysis.frozen_encoder import encode_chunks
from scripts.pair_verification import encoder_diagnostics


class Block(torch.nn.Module):
    def __init__(self, nonfinite):
        super().__init__()
        self.nonfinite = nonfinite

    def forward(self, hidden):
        return hidden * float("nan") if self.nonfinite else hidden


class Encoder(torch.nn.Module):
    def __init__(self, nonfinite):
        super().__init__()
        self.layer = torch.nn.ModuleList([Block(nonfinite)])


class TinyModel(torch.nn.Module):
    def __init__(self, nonfinite):
        super().__init__()
        self.weight = torch.nn.Parameter(torch.tensor(1.0), requires_grad=False)
        self.embeddings = torch.nn.Embedding(16, 768)
        self.encoder = Encoder(nonfinite)
        for parameter in self.parameters():
            parameter.requires_grad_(False)
        self.eval()

    def forward(self, input_ids, attention_mask):
        hidden = self.embeddings(input_ids)
        for layer in self.encoder.layer:
            hidden = layer(hidden)
        return SimpleNamespace(last_hidden_state=hidden)


class EncoderDiagnosticsTests(unittest.TestCase):
    def setUp(self):
        self.tokenizer = SimpleNamespace(pad_token_id=0)
        self.chunks = [
            {"inputIds": [1, 2, 3, 4, 5], "framingTokens": 4},
            {"inputIds": [1, 2, 3, 4, 5, 6], "framingTokens": 4},
        ]

    def test_nonfinite_writes_shapes_masks_and_layer_status_then_reraises_guard(self):
        model = TinyModel(nonfinite=True)
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "encoder-failure-diagnostics.json"
            with (patch.object(encoder_diagnostics, "_nvidia_snapshot", return_value={"available": False}),
                  patch.object(encoder_diagnostics, "ensure_inside", side_effect=lambda path, _root: Path(path)),
                  patch.object(encoder_diagnostics, "write_once",
                               side_effect=lambda destination, raw, _root: Path(destination).write_bytes(raw))):
                with self.assertRaisesRegex(ValueError, "^nonfinite vector$"):
                    encode_chunks(
                        model, self.tokenizer, self.chunks,
                        failure_diagnostics=lambda **details: encoder_diagnostics.capture_encoder_failure(
                            path, **details))
            record = json.loads(path.read_text(encoding="ascii"))
        self.assertEqual(record["inputIdsShape"], [2, 6])
        self.assertEqual(record["attentionMaskShape"], [2, 6])
        self.assertEqual(record["sequenceLengths"], [5, 6])
        self.assertEqual(record["maskStats"]["paddingTokens"], 1)
        self.assertEqual(record["maskStats"]["fullyMaskedRows"], 0)
        self.assertEqual(record["layerFiniteStatus"]["embeddings"], True)
        self.assertEqual(record["layerFiniteStatus"]["encoder.layer.0"], False)
        self.assertEqual(record["firstNonFiniteLayer"], "encoder.layer.0")
        self.assertFalse(record["reencode"]["sameDeviceDefaultBackend"]["finite"])
        self.assertFalse(record["reencode"]["cpuMathBackend"]["finite"])
        self.assertNotIn("sourceText", record)
        self.assertNotIn("vectors", record)

    def test_finite_batch_does_not_call_diagnostics(self):
        model = TinyModel(nonfinite=False)
        callback = Mock()
        vectors, evidence = encode_chunks(model, self.tokenizer, self.chunks,
                                          failure_diagnostics=callback)
        self.assertEqual(len(vectors), 2)
        self.assertTrue(evidence["fullStateUnchanged"])
        callback.assert_not_called()

    def test_diagnostic_failure_does_not_replace_original_guard_error(self):
        model = TinyModel(nonfinite=True)
        def broken_diagnostics(**_details):
            raise OSError("diagnostic destination unavailable")
        with self.assertRaisesRegex(ValueError, "^nonfinite vector$") as caught:
            encode_chunks(model, self.tokenizer, self.chunks, failure_diagnostics=broken_diagnostics)
        self.assertEqual(str(caught.exception), "nonfinite vector")

    def test_batch_index_uses_actual_batch_size(self):
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "encoder-failure-diagnostics.json"
            with (patch.object(encoder_diagnostics, "_nvidia_snapshot", return_value={"available": False}),
                  patch.object(encoder_diagnostics, "ensure_inside", side_effect=lambda value, _root: Path(value)),
                  patch.object(encoder_diagnostics, "write_once",
                               side_effect=lambda destination, raw, _root: Path(destination).write_bytes(raw))):
                encoder_diagnostics.capture_encoder_failure(
                    path, model=TinyModel(nonfinite=True), tokenizer=self.tokenizer, chunks=self.chunks,
                    batch_start=18, batch_size=6, device=torch.device("cpu"))
            record = json.loads(path.read_text(encoding="ascii"))
        self.assertEqual(record["batchIndex"], 3)
        self.assertEqual(record["batchSize"], 6)

    def test_nvidia_queries_have_timeout_and_record_timeout_state(self):
        with (patch.object(encoder_diagnostics.shutil, "which", return_value="nvidia-smi"),
              patch.object(encoder_diagnostics.subprocess, "run",
                           side_effect=subprocess.TimeoutExpired("nvidia-smi", 5)) as run):
            snapshot = encoder_diagnostics._nvidia_snapshot()
        self.assertEqual(run.call_count, 2)
        self.assertTrue(all(call.kwargs["timeout"] == encoder_diagnostics.NVIDIA_SMI_TIMEOUT_SECONDS
                            for call in run.call_args_list))
        self.assertEqual(snapshot["timeoutSeconds"], encoder_diagnostics.NVIDIA_SMI_TIMEOUT_SECONDS)
        self.assertTrue(snapshot["gpuTimedOut"])
        self.assertTrue(snapshot["processesTimedOut"])


if __name__ == "__main__":
    unittest.main()
