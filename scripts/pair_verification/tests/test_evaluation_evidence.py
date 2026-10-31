import json
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

from scripts.code_analysis.contracts import digest
from scripts.pair_verification import evaluation, training, training_data, validation_evidence


class EvaluationEvidenceTests(unittest.TestCase):
    def test_final_test_text_requires_exact_membership_reference(self):
        allowed = {"identities": {
            "finalTestMembershipReference": {"relativePath": "runs/sample/final-test-membership.json"},
            "inputs": {"finalTestMembershipReference": {
                "relativePath": "runs/sample/final-test-membership.json"}}}}
        found = validation_evidence._final_test_scan([("preparation-evidence.json", allowed)])
        self.assertEqual(len(found), 2)
        with self.assertRaisesRegex(ValueError, "unapproved"):
            validation_evidence._final_test_scan([("evaluation-evidence.json", allowed)])
        config = {"identity": {"inputs": {"finalTestMembershipReference": {
            "relativePath": "runs/sample/final-test-membership.json"}}}}
        self.assertEqual(len(validation_evidence._final_test_scan([("experiment-config.json", config)])), 1)

    def test_candidate_orders_are_reconstructed_from_bound_artifact(self):
        candidate = {"candidateFileIds": ["p", "n"], "semanticTop20": ["p"],
                     "tokenTop20": ["n"], "relevance": {"p": "POSITIVE",
                                                              "n": "REVIEWED_DISTRACTOR"}}
        rows = [{"model": "E0", "queryId": "q", "fileId": "p", "language": "python",
                 "stratum": "COMMENT_FORMAT", "seedGroup": "seed", "relevance": "POSITIVE",
                 "score": 0.8, "semanticScore": 0.9, "tokenScore": 0.0},
                {"model": "E0", "queryId": "q", "fileId": "n", "language": "python",
                 "stratum": "COMMENT_FORMAT", "seedGroup": "seed", "relevance": "REVIEWED_DISTRACTOR",
                 "score": 0.2, "semanticScore": 0.1, "tokenScore": 1.0}]
        bound = validation_evidence._attach_candidate_orders(rows, {("E0", "q"): candidate})
        self.assertEqual(bound[0]["semanticTop20"], ["p"])
        self.assertEqual(bound[1]["candidateFileIds"], ["p", "n"])
        with self.assertRaisesRegex(ValueError, "outside"):
            validation_evidence._attach_candidate_orders([dict(rows[0], fileId="unknown")],
                                                         {("E0", "q"): candidate})

    def test_training_binding_refuses_missing_marker_and_incomplete_run(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            run_id = "pairs-train-20261031-09"
            run = root / run_id
            run.mkdir()
            with patch.object(evaluation, "RUN_ROOT", root):
                with self.assertRaises(FileNotFoundError):
                    evaluation._train_binding(run_id, {"evidenceSha256": "a", "configSha256": "b",
                                                       "trainingSha256": "c", "validationSha256": "d"})
                (run / "training-evidence.json").write_text(json.dumps({"runId": run_id,
                    "outcome": "TRAINED", "schema": "pair-verification-training-evidence-v1"}),
                    encoding="utf-8")
                reload_raw = b"{}"
                (run / "reload-evidence.json").write_bytes(reload_raw)
                (run / "SUCCEEDED").write_text(digest(reload_raw) + "\n", encoding="ascii")
                with self.assertRaisesRegex(ValueError, "reload evidence"):
                    evaluation._train_binding(run_id, {"evidenceSha256": "a", "configSha256": "b",
                                                       "trainingSha256": "c", "validationSha256": "d"})

    def test_candidate_rows_reject_unknown_relevance_and_over_budget_list(self):
        base = {"schema": "pair-verification-validation-candidates-v1", "candidates": [{
            "model": "E0", "queryId": "q", "candidateFileIds": ["p", "n"],
            "relevance": {"p": "POSITIVE", "n": "UNKNOWN"}}]}
        with self.assertRaisesRegex(ValueError, "unknown"):
            evaluation._candidate_rows(base)
        over_budget = {"schema": base["schema"], "candidates": [{
            "model": "E0", "queryId": "q", "candidateFileIds": [str(i) for i in range(41)],
            "relevance": {}}]}
        with self.assertRaisesRegex(ValueError, "over budget"):
            evaluation._candidate_rows(over_budget)

    def test_retrieval_controls_reproduce_top_k_and_union(self):
        rows = [{"queryId": "q", "fileId": "p", "relevance": "POSITIVE", "score": 0.8,
                 "language": "python", "seedGroup": "s", "stratum": "COMMENT_FORMAT",
                 "semanticTop20": ["p"], "tokenTop20": ["n"], "candidateFileIds": ["p", "n"]},
                {"queryId": "q", "fileId": "n", "relevance": "REVIEWED_DISTRACTOR", "score": 0.2,
                 "language": "python", "seedGroup": "s", "stratum": "COMMENT_FORMAT",
                 "semanticTop20": ["p"], "tokenTop20": ["n"], "candidateFileIds": ["p", "n"]}]
        summaries, controls = evaluation._retrieval_controls(rows)
        self.assertEqual(set(summaries), {"top5", "top10", "top20", "token/top5", "token/top10",
                                          "token/top20", "union"})
        self.assertEqual(summaries["top5"]["f1"]["macro"], 1.0)
        self.assertEqual(summaries["token/top5"]["f1"]["macro"], 0.0)
        self.assertEqual(controls["union"][1], 0.0)

    def test_latency_requires_thirty_timed_queries_after_warmup(self):
        with self.assertRaisesRegex(ValueError, "fewer than 30"):
            evaluation._latency_query_windows(list(range(32)))
        warmup, timed = evaluation._latency_query_windows(list(range(33)))
        self.assertEqual(warmup, [0, 1, 2])
        self.assertEqual(len(timed), 30)

    def test_candidate_prevalence_is_derived_from_score_rows(self):
        rows = ([{"model": "E0", "relevance": "POSITIVE"}] * 313
                + [{"model": "E0", "relevance": "REVIEWED_DISTRACTOR"}] * 5616
                + [{"model": "E3", "relevance": "POSITIVE"}] * 313
                + [{"model": "E3", "relevance": "REVIEWED_DISTRACTOR"}] * 5579)
        measured = validation_evidence._candidate_prevalence(rows, {"E0": 1122, "E3": 1122})
        self.assertEqual(measured["E0"]["knownCandidatePairs"], 5929)
        self.assertEqual(measured["E3"]["knownCandidatePairs"], 5892)
        self.assertAlmostEqual(measured["E0"]["negativePerPositiveKnownOnly"], 5616 / 313)
        self.assertAlmostEqual(measured["E3"]["negativeAndUnknownPerPositive"], 6701 / 313)

    def test_evaluation_training_output_hashes_must_match_training_evidence(self):
        train = {"checkpoints": [{"epoch": epoch, "sha256": "hash-" + str(epoch)}
                                  for epoch in range(1, 6)],
                 "evidence": {"validationWindowPairsSha256": "window-hash"}}
        evaluation_doc = {"identities": {"checkpointSha256": "hash-2",
                                          "validationWindowPairsSha256": "window-hash"}}
        validation_evidence._verify_training_output_bindings(evaluation_doc, train, 2)
        evaluation_doc["identities"]["checkpointSha256"] = "wrong"
        with self.assertRaisesRegex(ValueError, "checkpoint hash"):
            validation_evidence._verify_training_output_bindings(evaluation_doc, train, 2)
        evaluation_doc["identities"]["checkpointSha256"] = "hash-2"
        evaluation_doc["identities"]["validationWindowPairsSha256"] = "wrong"
        with self.assertRaisesRegex(ValueError, "window-pair hash"):
            validation_evidence._verify_training_output_bindings(evaluation_doc, train, 2)

    def test_failed_evidence_run_writes_failure_without_success_marker(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            with patch.object(validation_evidence, "PAIR_ROOT", root), \
                    patch.object(validation_evidence, "RUN_ROOT", root), \
                    patch.object(validation_evidence, "verify_chain", side_effect=ValueError("mismatch")):
                with self.assertRaisesRegex(ValueError, "mismatch"):
                    validation_evidence.run(run_id="pairs-evidence-20261031-09",
                                            prepare_run_id="prepare", train_run_id="train",
                                            evaluation_run_id="evaluate", inputs_path="unused",
                                            config_path="unused")
            run = root / "pairs-evidence-20261031-09"
            failed = json.loads((run / "evidence-package.json").read_text(encoding="utf-8"))
            self.assertEqual(failed["outcome"], "FAILED")
            self.assertFalse((run / "SUCCEEDED").exists())

    def test_evidence_output_collision_is_immutable(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            run = root / "pairs-evidence-20261031-09"
            run.mkdir()
            with patch.object(validation_evidence, "PAIR_ROOT", root), \
                    patch.object(validation_evidence, "RUN_ROOT", root):
                with self.assertRaises(FileExistsError):
                    validation_evidence.run(run_id=run.name, prepare_run_id="prepare", train_run_id="train",
                                            evaluation_run_id="evaluate", inputs_path="unused",
                                            config_path="unused")

    def test_quality_rule_accepts_flat_or_negative_effect(self):
        self.assertEqual(evaluation.quality_outcome(0.4, 0.4)["status"], "FLAT_OR_NEGATIVE")
        self.assertEqual(evaluation.quality_outcome(0.5, 0.4)["status"], "POSITIVE_EFFECT")

    def test_failed_evaluation_writes_evidence_without_success_marker(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            with patch.object(evaluation, "PAIR_ROOT", root), \
                    patch.object(evaluation, "RUN_ROOT", root), \
                    patch.object(training_data, "load_bound_prepare", side_effect=ValueError("chain drift")), \
                    patch.object(training, "check_gpu_driver_events", return_value={"eventCount": 0}):
                with self.assertRaisesRegex(ValueError, "chain drift"):
                    evaluation.run(run_id="pairs-evaluate-20261031-09", prepare_run_id="prepare",
                                   train_run_id="train", inputs_path="unused", config_path="unused")
            run = root / "pairs-evaluate-20261031-09"
            failed = json.loads((run / "evaluation-evidence.json").read_text(encoding="utf-8"))
            self.assertEqual(failed["outcome"], "FAILED")
            self.assertEqual(failed["gpuDriverLogCheckAfterFailure"], {"eventCount": 0})
            self.assertFalse((run / "SUCCEEDED").exists())

    def test_reconstruction_ledger_has_hashes_only(self):
        package = {"chain": {"prepareRunId": "prepare", "trainingRunId": "train",
                             "evaluationRunId": "evaluate"},
                   "statuses": {"trainingCompleted": {"status": "TRAINED"},
                                "qualityOutcome": {"status": "FLAT_OR_NEGATIVE"},
                                "reviewerAcceptance": "NOT_RECORDED"},
                   "reconstructionLedger": [{"document": "evaluation-evidence.json", "bytes": 10,
                                             "sha256": "a" * 64,
                                             "parser": "scripts.code_retrieval.inputs.parse_json"}],
                   "limitations": ["Local-only"]}
        report = validation_evidence.reconstruction_markdown(package)
        self.assertIn("sha256", report)
        self.assertNotIn("source text payload", report)


if __name__ == "__main__":
    unittest.main()
