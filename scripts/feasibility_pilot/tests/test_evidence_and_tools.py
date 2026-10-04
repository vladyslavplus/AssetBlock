import hashlib
import json
import tempfile
import unittest
from pathlib import Path
from typing import Any

import torch

from scripts.feasibility_pilot.evidence import REQUIRED_STAGE_ARTIFACTS, hash_tree, validate_evidence_tree
from scripts.feasibility_pilot.inventory import extraction_binding_error, load_code_fixtures, load_license_fixtures, successful_extracted_ids
from scripts.feasibility_pilot.tools import dolos_reuse_allowed, evaluate_scancode_report
from scripts.feasibility_pilot.train import _amp_maybe_step, _inject_named_overflow, _track_optimizer_steps, tensors_close


class ExtractionBindingTests(unittest.TestCase):
    def test_missing_and_stale_reports_block_training(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            files = [
                type(
                    "F",
                    (),
                    {"fixture_id": "ok", "sha256": "aa", "training_allowed": True, "diagnostic_only": False},
                )()
            ]
            self.assertEqual(extraction_binding_error(root, files), "extraction report is missing")
            (root / "extraction-cases.json").write_text("[]", encoding="utf-8")
            (root / "inventory.json").write_text(json.dumps([{"id": "ok", "sha256": "bb"}]), encoding="utf-8")
            self.assertIn("stale", extraction_binding_error(root, files) or "")

    def test_parse_failure_is_not_successful_extracted(self) -> None:
        ids = successful_extracted_ids(
            [
                {"fixtureId": "ok", "ActualOutcome": "VALID_EXTRACTED", "ExpectationMatched": True, "operationalFailure": False},
                {"fixtureId": "bad", "ActualOutcome": "INVALID_PARSE", "ExpectationMatched": True, "operationalFailure": False, "TrainingAllowed": True},
            ]
        )
        self.assertEqual(ids, {"ok"})


class ValidatorEvidenceTests(unittest.TestCase):
    def test_missing_stage_with_files_is_blocked(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            (root / "train-retrieval.json").write_text(json.dumps({"outcome": "SUCCEEDED", "updates": 5}), encoding="utf-8")
            (root / "stages.json").write_text("[]", encoding="utf-8")
            result = validate_evidence_tree(root, run_id="test-missing-stage")
            self.assertEqual(result["status"], "BLOCKED")
            self.assertTrue(any("mandatory stage record is missing" in str(g.get("reason")) for g in result["evidenceGaps"]))

    def test_malformed_and_failed_payloads_are_blocked(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            (root / "stages.json").write_text(
                json.dumps([{"stage": "train-retrieval", "outcome": "SUCCEEDED", "executionState": "COMPLETED", "mandatoryForPilot": True}]),
                encoding="utf-8",
            )
            (root / "train-retrieval.json").write_text("{not-json", encoding="utf-8")
            malformed = validate_evidence_tree(root, run_id="test-malformed")
            self.assertEqual(malformed["status"], "BLOCKED")
            (root / "train-retrieval.json").write_text(json.dumps({"outcome": "FAILED", "updates": 0}), encoding="utf-8")
            failed = validate_evidence_tree(root, run_id="test-failed")
            self.assertEqual(failed["status"], "BLOCKED")
            self.assertTrue(any("FAILED training payload" in str(g.get("reason")) for g in failed["evidenceGaps"]))

    def test_deleted_csv_blocks_tools(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            csv_path = Path(tmp) / "gone.csv"
            (root / "stages.json").write_text(
                json.dumps([{"stage": "tools", "outcome": "SUCCEEDED", "executionState": "COMPLETED", "mandatoryForPilot": True}]),
                encoding="utf-8",
            )
            (root / "token-results.json").write_text(
                json.dumps([{"outcome": "SUCCEEDED", "rawCsv": str(csv_path), "csvSha256": "abc", "inputBinding": {"a": "b"}}]),
                encoding="utf-8",
            )
            (root / "license-results.json").write_text(json.dumps({"outcome": "SUCCEEDED", "signalEvaluation": {"ok": True}}), encoding="utf-8")
            (root / "tool-compatibility.json").write_text("[]", encoding="utf-8")
            result = validate_evidence_tree(root, run_id="test-csv")
            self.assertEqual(result["status"], "BLOCKED")
            self.assertTrue(any("Dolos CSV is missing" in str(g.get("reason")) for g in result["evidenceGaps"]))

    def _seed_otherwise_valid(self, root: Path) -> None:
        files = load_code_fixtures()
        packages = load_license_fixtures()
        csv_dir = root / "dolos"
        csv_dir.mkdir()
        langs: list[str] = []
        tokens = []
        for item in files:
            if item.expected_outcome != "VALID_EXTRACTED" or item.diagnostic_only:
                continue
            key = item.dialect if item.dialect == "tsx" else item.language
            if key in langs:
                continue
            langs.append(key)
            csv_path = csv_dir / f"{key}.csv"
            csv_path.write_text("id,score\n1,1\n", encoding="utf-8")
            tokens.append(
                {
                    "language": key,
                    "outcome": "SUCCEEDED",
                    "executionState": "COMPLETED",
                    "rawCsv": str(csv_path),
                    "csvSha256": hashlib.sha256(csv_path.read_bytes()).hexdigest(),
                    "inputBinding": {"bound": True},
                }
            )
        cases = [
            {
                "fixtureId": item.fixture_id,
                "RequiredCase": item.required_case,
                "ExpectationMatched": True,
                "ActualOutcome": item.expected_outcome,
            }
            for item in files
        ]
        retrieval_ckpt = root / "checkpoints" / "retrieval" / "checkpoint"
        pair_ckpt = root / "checkpoints" / "pair" / "checkpoint"
        retrieval_ckpt.mkdir(parents=True)
        pair_ckpt.mkdir(parents=True)
        (retrieval_ckpt / "weights.bin").write_bytes(b"retrieval")
        (pair_ckpt / "weights.bin").write_bytes(b"pair")
        (pair_ckpt / "pair_head.pt").write_bytes(b"head")
        train_retrieval = {
            "architecture": "retrieval-encoder",
            "outcome": "SUCCEEDED",
            "updates": 5,
            "encoderChanged": True,
            "frozenInvariant": True,
            "checkpointDir": str(retrieval_ckpt),
            "checkpointHash": hash_tree(retrieval_ckpt),
            "successfulAttempts": [{"attempt": i, "skipped": False, "optimizerStepped": True} for i in range(1, 6)],
            "gpu": {"training": {"isTrainingPeak": True, "maxAllocatedBytes": 8}},
        }
        train_pair = {
            **train_retrieval,
            "architecture": "joint-input-pair-verifier",
            "headChanged": True,
            "checkpointDir": str(pair_ckpt),
            "checkpointHash": hash_tree(pair_ckpt),
        }
        payloads: dict[str, Any] = {
            "environment.json": {},
            "dependency-manifest.json": {},
            "input-manifest.json": {},
            "inventory.json": [],
            "fragments.jsonl": "",
            "coverage.json": [],
            "extraction-cases.json": cases,
            "token-results.json": tokens,
            "license-results.json": {
                "outcome": "SUCCEEDED",
                "executionState": "COMPLETED",
                "policyCompliant": True,
                "filesWithScanErrors": [],
                "signalEvaluation": {
                    "ok": True,
                    "evaluations": [{"id": pkg["id"], "ok": True} for pkg in packages],
                },
            },
            "tool-compatibility.json": [],
            "frozen-retrieval.json": {"finite": True, "frozenInvariant": True},
            "train-retrieval.json": train_retrieval,
            "reload-retrieval.json": {"matched": True, "finite": True, "fallbackUsed": False, "actualDim": 768},
            "train-pair.json": train_pair,
            "reload-pair.json": {"matched": True, "finite": True, "fallbackUsed": False, "actualDim": 2},
            "summary.md": "ok",
            "compatibility.csv": "capability,outcome\n",
            "measurements.csv": "stage,durationSeconds\n",
            "decisions.json": {"Purpose": "FEASIBILITY"},
        }
        for name, payload in payloads.items():
            path = root / name
            if name.endswith(".json"):
                path.write_text(json.dumps(payload), encoding="utf-8")
            else:
                path.write_text(str(payload), encoding="utf-8")
        stages = [
            {"stage": stage, "outcome": "SUCCEEDED", "executionState": "COMPLETED", "mandatoryForPilot": True}
            for stage in REQUIRED_STAGE_ARTIFACTS
        ]
        (root / "stages.json").write_text(json.dumps(stages), encoding="utf-8")

    def _assert_not_ready(self, result: dict, fragment: str) -> None:
        self.assertNotEqual(result["status"], "READY_FOR_REVIEW")
        self.assertTrue(any(fragment in str(g.get("reason")) for g in result["evidenceGaps"]), result["evidenceGaps"])

    def test_empty_tokens_are_not_ready(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            self._seed_otherwise_valid(root)
            (root / "token-results.json").write_text("[]", encoding="utf-8")
            result = validate_evidence_tree(root, run_id="test-empty-tokens")
            self._assert_not_ready(result, "token-results.json is empty")

    def test_empty_extraction_cases_are_not_ready(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            self._seed_otherwise_valid(root)
            (root / "extraction-cases.json").write_text("[]", encoding="utf-8")
            result = validate_evidence_tree(root, run_id="test-empty-extract")
            self._assert_not_ready(result, "extraction-cases.json is empty")

    def test_empty_training_object_is_not_ready(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            self._seed_otherwise_valid(root)
            (root / "train-retrieval.json").write_text("{}", encoding="utf-8")
            result = validate_evidence_tree(root, run_id="test-empty-train")
            self._assert_not_ready(result, "not a complete object payload")

    def test_missing_training_outcome_is_not_ready(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            self._seed_otherwise_valid(root)
            payload = json.loads((root / "train-retrieval.json").read_text(encoding="utf-8"))
            del payload["outcome"]
            (root / "train-retrieval.json").write_text(json.dumps(payload), encoding="utf-8")
            result = validate_evidence_tree(root, run_id="test-missing-outcome")
            self._assert_not_ready(result, "missing outcome")

    def test_encoder_unchanged_is_not_ready(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            self._seed_otherwise_valid(root)
            payload = json.loads((root / "train-retrieval.json").read_text(encoding="utf-8"))
            payload["encoderChanged"] = False
            (root / "train-retrieval.json").write_text(json.dumps(payload), encoding="utf-8")
            result = validate_evidence_tree(root, run_id="test-encoder")
            self._assert_not_ready(result, "encoderChanged is not true")

    def test_frozen_invariant_false_is_not_ready(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            self._seed_otherwise_valid(root)
            payload = json.loads((root / "train-retrieval.json").read_text(encoding="utf-8"))
            payload["frozenInvariant"] = False
            (root / "train-retrieval.json").write_text(json.dumps(payload), encoding="utf-8")
            result = validate_evidence_tree(root, run_id="test-frozen-inv")
            self._assert_not_ready(result, "frozenInvariant is not true")

    def test_missing_checkpoint_is_not_ready(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            self._seed_otherwise_valid(root)
            payload = json.loads((root / "train-retrieval.json").read_text(encoding="utf-8"))
            payload["checkpointDir"] = str(root / "checkpoints" / "retrieval" / "missing")
            (root / "train-retrieval.json").write_text(json.dumps(payload), encoding="utf-8")
            result = validate_evidence_tree(root, run_id="test-missing-ckpt")
            self._assert_not_ready(result, "checkpoint directory is missing")

    def test_changed_checkpoint_hash_is_not_ready(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            self._seed_otherwise_valid(root)
            (root / "checkpoints" / "retrieval" / "checkpoint" / "weights.bin").write_bytes(b"changed")
            result = validate_evidence_tree(root, run_id="test-changed-ckpt")
            self._assert_not_ready(result, "checkpoint hash does not match")


class DolosReuseTests(unittest.TestCase):
    def test_changed_inputs_or_missing_csv_block_reuse(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            src = Path(tmp) / "a.js"
            src.write_text("ok", encoding="utf-8")
            csv_path = Path(tmp) / "pairs.csv"
            csv_path.write_text("id,score\n1,1\n", encoding="utf-8")
            from scripts.feasibility_pilot.inventory import sha256_file

            item = {
                "outcome": "SUCCEEDED",
                "rawCsv": str(csv_path),
                "csvSha256": sha256_file(csv_path),
                "k": 23,
                "window": 17,
                "inputBinding": {src.as_posix(): sha256_file(src)},
            }
            self.assertTrue(dolos_reuse_allowed(item, [src]))
            src.write_text("changed", encoding="utf-8")
            self.assertFalse(dolos_reuse_allowed(item, [src]))
            csv_path.unlink()
            src.write_text("ok", encoding="utf-8")
            self.assertFalse(dolos_reuse_allowed(item, [src]))


class ScanCodeExpectationTests(unittest.TestCase):
    def test_zero_exit_missing_required_signal_or_file_error_fails(self) -> None:
        report = {
            "outcome": "SUCCEEDED",
            "filesWithScanErrors": [],
            "files": [{"path": "licenses/mit/LICENSE", "scanErrors": [], "licenseDetections": [], "copyrights": []}],
        }
        packages = [
            {
                "id": "lic-mit",
                "expectedSignals": {"license": "MIT"},
                "files": [{"relativePath": "licenses/mit/LICENSE"}],
            }
        ]
        self.assertFalse(evaluate_scancode_report(report, packages)["ok"])
        report["files"][0]["licenseDetections"] = [{"license_expression_spdx": "MIT"}]
        self.assertTrue(evaluate_scancode_report(report, packages)["ok"])
        report["filesWithScanErrors"] = ["licenses/mit/LICENSE"]
        report["files"][0]["scanErrors"] = ["boom"]
        self.assertFalse(evaluate_scancode_report(report, packages)["ok"])

    def test_expected_unknown_passes(self) -> None:
        report = {
            "outcome": "SUCCEEDED",
            "filesWithScanErrors": [],
            "files": [{"path": "licenses/custom/LICENSE", "scanErrors": [], "licenseDetections": [], "copyrights": []}],
        }
        packages = [{"id": "lic-custom", "expectedSignals": {"license": "UNKNOWN_OR_CUSTOM"}, "files": [{"relativePath": "licenses/custom/LICENSE"}]}]
        self.assertTrue(evaluate_scancode_report(report, packages)["ok"])


class ReloadCompareTests(unittest.TestCase):
    def test_change_after_index_31_mismatches(self) -> None:
        stored = torch.zeros(768)
        actual = stored.clone()
        actual[32] = 0.5
        result = tensors_close(actual, stored)
        self.assertFalse(result["matched"])

    def test_missing_query_is_failure_not_fallback(self) -> None:
        from scripts.feasibility_pilot.reload_worker import _reload_retrieval

        payload = _reload_retrieval(None, None, {"vector": [0.0] * 8, "dim": 8}, 8, torch.device("cpu"))
        self.assertFalse(payload["matched"])
        self.assertEqual(payload["reason"], "missing query/reference")
        self.assertFalse(payload["fallbackUsed"])


class AmpBookkeepingTests(unittest.TestCase):
    def test_overflow_in_head_or_other_param_does_not_step(self) -> None:
        if not torch.cuda.is_available():
            self.skipTest("CUDA required")
        device = torch.device("cuda")
        model = torch.nn.Sequential(torch.nn.Linear(4, 4), torch.nn.Linear(4, 2)).to(device)
        for param in model.parameters():
            param.requires_grad_(True)
        optimizer = torch.optim.SGD(model.parameters(), lr=0.1)
        scaler = torch.amp.GradScaler("cuda", enabled=True)
        step_state = _track_optimizer_steps(optimizer)
        x = torch.ones(2, 4, device=device)
        optimizer.zero_grad(set_to_none=True)
        with torch.amp.autocast("cuda", dtype=torch.float16):
            loss = model(x).sum()
        scaler.scale(loss).backward()
        _inject_named_overflow(model, ["1.weight"])
        stepped, _ = _amp_maybe_step(scaler=scaler, optimizer=optimizer, module=model, loss=loss, step_state=step_state)
        self.assertFalse(stepped)
        self.assertEqual(step_state["count"], 0)
        optimizer.zero_grad(set_to_none=True)
        with torch.amp.autocast("cuda", dtype=torch.float16):
            loss = model(x).sum()
        scaler.scale(loss).backward()
        _inject_named_overflow(model, ["0.weight"])
        stepped, _ = _amp_maybe_step(scaler=scaler, optimizer=optimizer, module=model, loss=loss, step_state=step_state)
        self.assertFalse(stepped)
        self.assertEqual(step_state["count"], 0)


if __name__ == "__main__":
    unittest.main()
