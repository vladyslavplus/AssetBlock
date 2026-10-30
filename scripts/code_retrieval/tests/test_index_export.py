import json
import tempfile
import unittest
from pathlib import Path
from unittest import mock

try:
    import torch
    from scripts.code_analysis.contracts import digest
    from scripts.code_retrieval import index_export, index_verify
    TORCH_IMPORT_ERROR = None
except ImportError as exc:  # the export stage imports torch; torch-less interpreters skip it
    TORCH_IMPORT_ERROR = exc

DIM = 768
MODEL_LABELS = ("E0", "E1", "E2", "E3")


def unit(index):
    vector = [0.0] * DIM
    vector[index % DIM] = 1.0
    return vector


def h(name):
    return digest(name.encode("utf-8"))


def identity(label):
    return {"kind": "frozen" if label == "E0" else "trained", "label": label, "fullStateSha256": h("state-" + label),
            "tokenizerHashes": {"vocab": h("vocab")}, "chunking": "chunking-fixture", "pooling": "mean-fixture",
            "dimension": DIM}


def fake_windows(rows, tokenizer):
    """Tiny synthetic chunk rows and file coverage in place of the real tokenizer."""
    chunk_rows = [{"fileId": row["fileId"], "fragmentId": row["fileId"], "chunkOrdinal": 0, "tokenStart": 0,
                   "tokenEnd": 1, "totalInputTokens": 1, "representationSha256": h("representation"),
                   "inputIds": [0]} for row in rows]
    file_rows = [{"fileId": row["fileId"], "chunkCount": 1, "originalTokenCount": 1, "retainedUniqueTokenCount": 1,
                  "omittedTokenCount": 0, "partialMLCoverage": False} for row in rows]
    return chunk_rows, file_rows


@unittest.skipIf(TORCH_IMPORT_ERROR is not None, "export imports unavailable (torch)")
class ExportModelIdentityTests(unittest.TestCase):
    def test_export_records_every_model_key_without_a_missing_name(self):
        # Reaches the model-key line of the export with the loaders and encode step patched. A name that
        # no longer exists fails inside the export, which records FAILED_ERROR and fails this assertion.
        models = {label: {"identity": identity(label), "model": None, "tokenizer": None} for label in MODEL_LABELS}
        galleries = {"python": [{"fileId": h("file"), "sha256": h("source"), "body": "x"}]}
        plan = {"galleries": galleries, "queries": {}, "reportSha256": h("report")}
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            target = root / "export-fixture"
            target.mkdir()
            with mock.patch.object(index_export, "RETRIEVAL_ROOT", root), \
                    mock.patch.object(index_export, "protocol_sha256", return_value=h("protocol")), \
                    mock.patch.object(index_export, "gallery_windows", side_effect=fake_windows), \
                    mock.patch.object(index_export.retrieval_models, "frozen_model", return_value=models["E0"]), \
                    mock.patch.object(index_export.retrieval_models, "trained_model",
                                      side_effect=lambda run, epoch, expectations, device: models[f"E{epoch}"]), \
                    mock.patch.object(index_export.retrieval_models, "encode",
                                      side_effect=lambda model, tokenizer, windows, device:
                                      [unit(0) for _ in windows]), \
                    mock.patch.object(index_export, "encode_chunks",
                                      side_effect=lambda model, tokenizer, chunks, batch_size:
                                      ([unit(0) for _ in chunks], None)), \
                    mock.patch.object(index_export.training, "process_working_set", return_value={}), \
                    mock.patch.object(index_export.training, "device_facts", return_value={}), \
                    mock.patch.object(torch.cuda, "reset_peak_memory_stats"), \
                    mock.patch.object(torch.cuda, "max_memory_allocated", return_value=0), \
                    mock.patch.object(torch.cuda, "max_memory_reserved", return_value=0), \
                    mock.patch.object(torch.cuda, "empty_cache"), \
                    mock.patch.object(torch.cuda, "synchronize"):
                index_export._export(
                    run_id="export-fixture", target=target, loaded={"manifestSha256": h("manifest")},
                    config_loaded={"configSha256": h("config")}, plan=plan,
                    prepare_binding={"runId": "prepare-fixture"},
                    expectations={"evidenceSha256": h("train"), "epochs": {}}, train_run_id="train-fixture",
                    reference_raw=b"{}", reference_probe={}, live_probe={}, comparison={"result": "MATCH"},
                    device="cuda:0")
                evidence = json.loads((target / index_verify.EXPORT_EVIDENCE).read_text(encoding="utf-8"))
        self.assertEqual(evidence["outcome"], "SUCCEEDED", evidence["reason"])
        self.assertIsNone(evidence["reason"])
        for label in MODEL_LABELS:
            with self.subTest(label=label):
                self.assertEqual(evidence["models"][label]["modelKey"], index_verify.model_key(identity(label)))


if __name__ == "__main__":
    unittest.main()
