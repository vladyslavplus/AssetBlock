import copy
import json
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

from scripts.code_analysis.contracts import digest
from scripts.pair_verification import evaluation, evaluation_metrics, training_data, validation_evidence


def _canonical(value):
    return json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=False).encode("utf-8")


def _fixture(root):
    run_id = "pairs-evaluate-20000101-01"
    train_id = "pairs-train-20000101-01"
    prepare_id = "pairs-prepare-20000101-01"
    run = root / run_id
    run.mkdir(parents=True)
    train_run = root / train_id
    train_run.mkdir()
    lang_order = [("csharp", 40), ("java", 40), ("javascript", 40), ("python", 40),
                  ("typescript", 41)]
    strata = ["EXACT_COPY"] * 100 + ["COMMENT_FORMAT"] * 92 + ["AST_BOUND_RENAME"] * 9
    candidates, windows, epoch_scores, semantic = [], [], {}, {}
    for model in ("E0", "E3"):
        index = 0
        for language, count in lang_order:
            for _ in range(count):
                query = model + "-q" + str(index)
                stratum = strata[index]
                seed = language + "-seed-" + str(index % 10)
                positive, negative = query + "-p", query + "-n"
                candidates.append({"model": model, "queryId": query, "language": language,
                                   "stratum": stratum, "seedGroup": seed,
                                   "candidateFileIds": [positive, negative],
                                   "semanticTop20": [positive], "tokenTop20": [negative, positive],
                                   "relevance": {positive: "POSITIVE", negative: "REVIEWED_DISTRACTOR"}})
                semantic[(model, query)] = {positive: 0.9, negative: 0.1}
                for file_id in (positive, negative):
                    windows.append({"model": model, "queryId": query, "fileId": file_id,
                                    "windowPairs": [{"inputIds": [1, 2, 3],
                                                     "swappedInputIds": [1, 3, 2],
                                                     "truncated": False}]})
                index += 1
    candidate_artifact = {"schema": "pair-verification-validation-candidates-v1",
                          "candidates": candidates, "unknownExcludedPerModelLanguageStratum": {}}
    candidate_map = evaluation._candidate_rows(candidate_artifact)
    for epoch in range(1, 6):
        rows = []
        for candidate in candidates:
            model, query = candidate["model"], candidate["queryId"]
            for file_id in candidate["candidateFileIds"]:
                positive = file_id.endswith("-p")
                score = (0.9 if positive else 0.1) - 0.005 * epoch if positive else 0.1 + 0.005 * epoch
                rows.append({"model": model, "queryId": query, "fileId": file_id,
                             "language": candidate["language"], "stratum": candidate["stratum"],
                             "seedGroup": candidate["seedGroup"],
                             "relevance": candidate["relevance"][file_id], "score": score,
                             "semanticScore": semantic[(model, query)][file_id],
                             "tokenScore": 1.0 if file_id.endswith("-n") else 0.5,
                             "truncated": False, "swappedScore": score})
        epoch_scores[str(epoch)] = rows
    selected_epoch = evaluation_metrics.select_epoch(
        {int(epoch): [row for row in rows if row["model"] == "E0"]
         for epoch, rows in epoch_scores.items()})
    scores = epoch_scores[str(selected_epoch)]
    windows_raw = _canonical(windows)
    (train_run / "validation-window-pairs.json").write_bytes(windows_raw)
    checkpoints = []
    for epoch in range(1, 6):
        checkpoint_path = train_run / ("epoch-" + str(epoch) + ".safetensors")
        manifest_path = checkpoint_path.with_suffix(".json")
        checkpoint_bytes = ("checkpoint-" + str(epoch)).encode("ascii")
        manifest_bytes = _canonical({"epoch": epoch})
        checkpoint_path.write_bytes(checkpoint_bytes)
        manifest_path.write_bytes(manifest_bytes)
        checkpoints.append({"epoch": epoch, "relativePath": checkpoint_path.name,
                            "sha256": digest(checkpoint_bytes), "bytes": len(checkpoint_bytes),
                            "manifestSha256": digest(manifest_bytes)})
    train_evidence = {"schema": "pair-verification-training-evidence-v1", "outcome": "TRAINED",
                      "runId": train_id, "identity": {},
                      "validationWindowPairsSha256": digest(windows_raw),
                      "training": {"epochs": []}, "checkpoints": checkpoints}
    train_raw = _canonical(train_evidence)
    reload_evidence = {"schema": "pair-verification-reload-evidence-v1", "outcome": "SUCCEEDED",
                       "runId": train_id}
    reload_raw = _canonical(reload_evidence)
    prepare = {"evidence": {}, "evidenceRaw": b"{}", "evidenceSha256": digest(b"{}"),
               "config": {}, "configRaw": b"{}", "configSha256": digest(b"{}"),
               "trainingSha256": "training-pairs-hash", "validationSha256": "candidate-artifact-hash",
               "validation": candidate_artifact}
    train_evidence["identity"] = {"prepareEvidenceSha256": prepare["evidenceSha256"],
                                  "configSha256": prepare["configSha256"],
                                  "trainingPairsSha256": prepare["trainingSha256"],
                                  "candidateArtifactSha256": prepare["validationSha256"]}
    train_raw = _canonical(train_evidence)
    train = {"run": train_run, "evidence": train_evidence, "evidenceRaw": train_raw,
             "evidenceSha256": digest(train_raw), "reload": reload_evidence,
             "reloadRaw": reload_raw, "reloadSha256": digest(reload_raw),
             "markerSha256": digest((digest(reload_raw) + "\n").encode("ascii")),
             "epochs": [], "checkpoints": checkpoints}
    per_epoch = {}
    for epoch, rows in epoch_scores.items():
        e0 = [row for row in rows if row["model"] == "E0"]
        threshold = evaluation_metrics.choose_threshold(e0)
        per_epoch[epoch] = {"E0": {"threshold": threshold,
                                    "metrics": evaluation_metrics.summary(e0, threshold=threshold["threshold"] )}}
    selected_rows = evaluation._make_metric_rows(candidate_map,
        {(row["model"], row["queryId"], row["fileId"]): row for row in scores}, semantic)
    pipelines, controls, deltas, crossfit, orders, truncations = {}, {}, {}, {}, {}, {}
    headline_controls = {}
    for model in ("E0", "E3"):
        rows = selected_rows[model]
        threshold = evaluation_metrics.choose_threshold(rows)
        headline = [row for row in rows if row["stratum"] in evaluation_metrics.HEADLINE_STRATA]
        semantic_rows = [{**row, "score": row["semanticScore"]} for row in rows]
        token_rows = [{**row, "score": row["tokenScore"]} for row in rows]
        semantic_threshold = evaluation_metrics.choose_threshold(semantic_rows)
        token_threshold = evaluation_metrics.choose_threshold(token_rows)
        semantic_headline = [row for row in semantic_rows if row["stratum"] in evaluation_metrics.HEADLINE_STRATA]
        token_headline = [row for row in token_rows if row["stratum"] in evaluation_metrics.HEADLINE_STRATA]
        sem_head_summary = evaluation_metrics.summary(semantic_headline, threshold=semantic_threshold["threshold"])
        tok_head_summary = evaluation_metrics.summary(token_headline, threshold=token_threshold["threshold"])
        headline_controls[model] = {"semanticScoreThreshold": sem_head_summary["f1"]["macro"],
                                    "tokenRankTopOneCut": tok_head_summary["f1"]["macro"]}
        retrieval, retrieval_rows = evaluation._retrieval_controls(rows)
        pipelines[model] = {"threshold": threshold,
                            "headlineNonExactCopy": evaluation_metrics.summary(headline, threshold=threshold["threshold"]),
                            "allQueriesSecondary": evaluation_metrics.summary(rows, threshold=threshold["threshold"]),
                            "perStratum": evaluation_metrics.summary(rows, threshold=threshold["threshold"])["perStratum"],
                            "perLanguage": evaluation_metrics.summary(rows, threshold=threshold["threshold"])["perLanguage"],
                            "unknownExcluded": 0,
                            "unknownExcludedPerLanguage": {language: 0 for language, _ in lang_order},
                            "unknownExcludedPerStratum": {stratum: 0 for stratum in evaluation_metrics.STRATA}}
        controls[model] = {"semanticScoreThreshold": {"threshold": semantic_threshold,
                                                       "metrics": evaluation_metrics.summary(semantic_rows, threshold=semantic_threshold["threshold"])},
                           "tokenRankTopOneCut": {"threshold": token_threshold,
                                                  "metrics": evaluation_metrics.summary(token_rows, threshold=token_threshold["threshold"])},
                           "retrievalOnly": retrieval}
        deltas[model] = {"semanticScoreThreshold": evaluation_metrics.paired_deltas(
                            rows, semantic_rows, threshold_primary=threshold["threshold"],
                            threshold_control=semantic_threshold["threshold"]),
                         "tokenRankTopOneCut": evaluation_metrics.paired_deltas(
                            rows, token_rows, threshold_primary=threshold["threshold"],
                            threshold_control=token_threshold["threshold"]),
                         "retrievalOnly": {name: evaluation_metrics.paired_deltas(
                             rows, control_rows, threshold_primary=threshold["threshold"],
                             threshold_control=control_threshold)
                             for name, (control_rows, control_threshold) in retrieval_rows.items()}}
        crossfit[model] = evaluation_metrics.cross_fit(rows)
        orders[model] = evaluation._order_breakdown(rows, threshold["threshold"])
        truncations[model] = evaluation._truncation_breakdown(rows, threshold["threshold"])
    selected_e0_f1 = pipelines["E0"]["headlineNonExactCopy"]["f1"]["macro"]
    quality = evaluation.quality_outcome(selected_e0_f1, max(headline_controls["E0"].values()))
    candidate_scores_value = {"schema": evaluation.SCORE_SCHEMA, "selectedEpoch": selected_epoch,
                              "scores": scores, "perEpochScores": epoch_scores}
    candidate_scores_raw = _canonical(candidate_scores_value)
    (run / "candidate-scores.json").write_bytes(candidate_scores_raw)
    evaluation_value = {
        "schema": evaluation.EVALUATION_SCHEMA, "outcome": "SUCCEEDED", "runId": run_id,
        "selection": {"selectedEpoch": selected_epoch,
                      "perEpochE0": per_epoch},
        "pipelines": pipelines, "controls": controls, "pairedDeltas": deltas,
        "crossFit": crossfit, "orderBreakdowns": orders, "truncationBreakdowns": truncations,
        "latency": {"E0": {"timedQueries": 30}, "E3": {"timedQueries": 30}},
        "saturation": {},
        "candidateListFacts": evaluation._candidate_list_facts(candidate_map, selected_rows),
        "qualityOutcome": quality,
        "identities": {"prepareRunId": prepare_id, "prepareEvidenceSha256": prepare["evidenceSha256"],
                       "prepareMarkerSha256": digest((prepare["evidenceSha256"] + "\n").encode("ascii")),
                       "configSha256": prepare["configSha256"], "trainingPairsSha256": prepare["trainingSha256"],
                       "candidateArtifactSha256": prepare["validationSha256"], "trainingRunId": train_id,
                       "trainingEvidenceSha256": train["evidenceSha256"], "reloadEvidenceSha256": train["reloadSha256"],
                       "reloadMarkerSha256": train["markerSha256"],
                       "candidateScoresSha256": digest(candidate_scores_raw),
                       "candidateScoresBytes": len(candidate_scores_raw),
                       "checkpointSha256": checkpoints[selected_epoch - 1]["sha256"],
                       "validationWindowPairsSha256": train_evidence["validationWindowPairsSha256"]}}
    evaluation_raw = _canonical(evaluation_value)
    (run / "evaluation-evidence.json").write_bytes(evaluation_raw)
    (run / "SUCCEEDED").write_bytes((digest(evaluation_raw) + "\n").encode("ascii"))
    return run_id, train_id, prepare_id, prepare, train


class VerifyChainIntegrityTests(unittest.TestCase):
    def _verify(self, root, ids, prepare, train):
        with patch.object(validation_evidence, "RUN_ROOT", root), \
                patch.object(training_data, "load_bound_prepare", return_value=prepare), \
                patch.object(evaluation, "_train_binding", return_value=train):
            return validation_evidence.verify_chain(prepare_run_id=ids[2], train_run_id=ids[1],
                                                   evaluation_run_id=ids[0], inputs_path="unused",
                                                   config_path="unused")

    def test_verify_chain_accepts_authenticated_synthetic_documents(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp) / "runs"
            root.mkdir()
            ids = _fixture(root)
            package = self._verify(root, ids[:3], ids[3], ids[4])
            self.assertEqual(package["selectedEpoch"], 1)

    def test_verify_chain_rejects_tampered_metric(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp) / "runs"
            root.mkdir()
            ids = _fixture(root)
            evidence_path = root / ids[0] / "evaluation-evidence.json"
            value = json.loads(evidence_path.read_text(encoding="utf-8"))
            value["pipelines"]["E0"]["headlineNonExactCopy"]["f1"]["macro"] += 0.01
            raw = _canonical(value)
            evidence_path.write_bytes(raw)
            (evidence_path.parent / "SUCCEEDED").write_bytes((digest(raw) + "\n").encode("ascii"))
            with self.assertRaisesRegex(ValueError, "headline metric"):
                self._verify(root, ids[:3], ids[3], ids[4])

    def test_verify_chain_rejects_tampered_score_file(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp) / "runs"
            root.mkdir()
            ids = _fixture(root)
            score_path = root / ids[0] / "candidate-scores.json"
            score_path.write_bytes(score_path.read_bytes() + b" ")
            with self.assertRaisesRegex(ValueError, "candidate score artifact"):
                self._verify(root, ids[:3], ids[3], ids[4])

    def test_verify_chain_rejects_tampered_success_marker(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp) / "runs"
            root.mkdir()
            ids = _fixture(root)
            (root / ids[0] / "SUCCEEDED").write_text("tampered\n", encoding="ascii")
            with self.assertRaisesRegex(ValueError, "success marker"):
                self._verify(root, ids[:3], ids[3], ids[4])


if __name__ == "__main__":
    unittest.main()
