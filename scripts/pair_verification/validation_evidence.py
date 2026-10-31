"""Verify and package pair research evidence using only bound run documents.

This stage uses the pinned CPU interpreter. It does not open final-test membership or source files.
"""

import json
import re
from pathlib import Path

from scripts.code_analysis.contracts import canonical, digest
from scripts.code_analysis.corpus import write_once
from scripts.code_retrieval import evidence_package, inputs
from scripts.feasibility_pilot.paths import REPO_ROOT, ensure_inside

from . import evaluation, evaluation_metrics, training_data

PAIR_ROOT = REPO_ROOT / "artifacts" / "pair_verification"
RUN_ROOT = PAIR_ROOT / "runs"
PACKAGE_SCHEMA = "pair-verification-evidence-package-v1"
PACKAGE_NAME = "evidence-package.json"
RECONSTRUCTION_NAME = "RECONSTRUCTION.md"
# Owner: the admitted preparation evidence reference; exact document and key paths only.
FINAL_TEST_REFERENCE_ALLOWLIST = {
    ("preparation-evidence.json", ("identities", "finalTestMembershipReference", "relativePath")),
    ("preparation-evidence.json", ("identities", "inputs", "finalTestMembershipReference", "relativePath")),
    ("experiment-config.json", ("identity", "inputs", "finalTestMembershipReference", "relativePath")),
}
LIMITATIONS = [
    "The reviewed training set is small and can overfit.",
    "Training positives are synthetic transformations, so verifier learns transformed-copy detection.",
    "Validation retrieval is near its ceiling, with EXACT_COPY queries saturated by construction.",
    "Training uses reviewed fragment pairs while validation scores fragment-to-file candidates.",
    "Validation metrics are selection-contaminated because validation selects epoch and thresholds.",
    "The quality rule was fixed at evaluation time, not in the preparation configuration.",
    "Latency excludes tokenization and E0 window alignment.",
    "The token-rank control is a per-query top-1 rank cut; its 0.843 headline F1 is not a global-threshold baseline.",
    "Final-test independence is unknown; this stage did not inspect final-test membership contents.",
    "Checkpoint redistribution is denied; all evidence and weights remain local.",
]


def _json(path, parser_name):
    raw = inputs.read_bytes(path, 256 * 1024 * 1024)
    value = inputs.parse_json(raw, parser_name)
    return value, raw


def _final_test_scan(documents):
    findings = []
    for name, value in documents:
        for path in evidence_package.final_test_hits(value):
            normalized_path = tuple(part for part in path if not part.startswith("["))
            allow_key = (name, normalized_path)
            if allow_key not in FINAL_TEST_REFERENCE_ALLOWLIST:
                raise ValueError("chain document contains an unapproved final-test text reference")
            node = value
            for part in normalized_path:
                node = node[part]
            if not isinstance(node, str) or not node.endswith("/final-test-membership.json"):
                raise ValueError("membership reference differs from its exact allowlisted form")
            findings.append({"document": name, "keyPath": list(normalized_path),
                             "referenceOnly": True})
    return findings


def _attach_candidate_orders(rows, candidate_map):
    result = []
    for row in rows:
        candidate = candidate_map.get((row.get("model"), row.get("queryId")))
        if candidate is None or row.get("fileId") not in candidate["candidateFileIds"]:
            raise ValueError("candidate score is outside the frozen candidate list")
        result.append({**row, "semanticTop20": candidate["semanticTop20"],
                       "tokenTop20": candidate["tokenTop20"],
                       "candidateFileIds": candidate["candidateFileIds"]})
    return result


def _candidate_prevalence(rows, unknown_by_model):
    result = {}
    for model in ("E0", "E3"):
        model_rows = [row for row in rows if row.get("model") == model]
        positive_count = sum(row.get("relevance") == "POSITIVE" for row in model_rows)
        known_count = len(model_rows)
        unknown_count = int(unknown_by_model.get(model, 0))
        negative_count = known_count - positive_count
        if positive_count <= 0 or negative_count < 0:
            raise ValueError("candidate prevalence has invalid score-row counts")
        result[model] = {
            "positivePairs": positive_count,
            "knownCandidatePairs": known_count,
            "knownNegativePairs": negative_count,
            "unknownExcluded": unknown_count,
            "negativePerPositiveKnownOnly": negative_count / positive_count,
            "negativeAndUnknownPerPositive": (negative_count + unknown_count) / positive_count,
        }
    return result


def _crossfit_imbalance(cross_fit):
    by_language = {}
    for language in evaluation_metrics.LANGUAGES:
        values = []
        for fold in cross_fit["E0"]["folds"]:
            stats = fold["heldMetrics"]["perLanguage"].get(language)
            values.append(stats["counts"]["queries"] if stats else 0)
        if any(values):
            by_language[language] = values
    return by_language


def _verify_training_output_bindings(evaluation_value, train, selected_epoch):
    checkpoint = next((row for row in train["checkpoints"] if row.get("epoch") == selected_epoch), None)
    identity = evaluation_value.get("identities") or {}
    if checkpoint is None or identity.get("checkpointSha256") != checkpoint.get("sha256"):
        raise ValueError("evaluation checkpoint hash differs from selected training epoch")
    if identity.get("validationWindowPairsSha256") != train["evidence"].get("validationWindowPairsSha256"):
        raise ValueError("evaluation validation window-pair hash differs from training evidence")


def verify_chain(*, prepare_run_id, train_run_id, evaluation_run_id, inputs_path, config_path):
    prepare = training_data.load_bound_prepare(prepare_run_id=prepare_run_id,
                                               inputs_path=inputs_path, config_path=config_path)
    train = evaluation._train_binding(train_run_id, prepare)
    evaluation_run = ensure_inside(RUN_ROOT / evaluation_run_id, RUN_ROOT)
    evaluation_value, evaluation_raw = _json(evaluation_run / "evaluation-evidence.json",
                                             "pair evaluation evidence")
    if (evaluation_value.get("schema") != evaluation.EVALUATION_SCHEMA
            or evaluation_value.get("runId") != evaluation_run_id
            or evaluation_value.get("outcome") != "SUCCEEDED"):
        raise ValueError("evaluation run is not a complete successful run")
    if inputs.read_bytes(evaluation_run / "SUCCEEDED", 128) != (digest(evaluation_raw) + "\n").encode("ascii"):
        raise ValueError("evaluation success marker differs from its evidence")
    identity = evaluation_value.get("identities") or {}
    expected = {"prepareRunId": prepare_run_id, "prepareEvidenceSha256": prepare["evidenceSha256"],
                "prepareMarkerSha256": digest((prepare["evidenceSha256"] + "\n").encode("ascii")),
                "configSha256": prepare["configSha256"], "trainingPairsSha256": prepare["trainingSha256"],
                "candidateArtifactSha256": prepare["validationSha256"], "trainingRunId": train_run_id,
                "trainingEvidenceSha256": train["evidenceSha256"], "reloadEvidenceSha256": train["reloadSha256"],
                "reloadMarkerSha256": train["markerSha256"]}
    if any(identity.get(key) != value for key, value in expected.items()):
        raise ValueError("evaluation run does not bind the verified preparation and training runs")
    score_path = ensure_inside(evaluation_run / "candidate-scores.json", evaluation_run)
    score_value, score_raw = _json(score_path, "candidate scores")
    if (digest(score_raw) != identity.get("candidateScoresSha256")
            or len(score_raw) != identity.get("candidateScoresBytes")
            or score_value.get("schema") != evaluation.SCORE_SCHEMA
            or score_value.get("selectedEpoch") != evaluation_value.get("selection", {}).get("selectedEpoch")):
        raise ValueError("candidate score artifact is partial or differs from evaluation evidence")
    candidates = prepare["validation"].get("candidates") or []
    expected_scores = sum(len(row.get("candidateFileIds") or []) for row in candidates)
    if len(score_value.get("scores") or []) != expected_scores:
        raise ValueError("candidate score count differs from the frozen candidate artifact")
    score_ids = {(row.get("model"), row.get("queryId"), row.get("fileId"))
                 for row in score_value["scores"]}
    expected_ids = {(row.get("model"), row.get("queryId"), file_id)
                    for row in candidates for file_id in row.get("candidateFileIds") or []}
    if len(score_ids) != len(score_value["scores"]) or score_ids != expected_ids:
        raise ValueError("candidate scores do not cover the unique frozen candidate pairs")
    if set(score_value.get("perEpochScores") or {}) != {"1", "2", "3", "4", "5"}:
        raise ValueError("candidate scores do not contain all five epoch outputs")
    candidate_map = evaluation._candidate_rows(prepare["validation"])
    expected_by_id = {(model, query, file_id): item
                      for (model, query), item in candidate_map.items()
                      for file_id in item["candidateFileIds"]}
    for epoch, rows in score_value["perEpochScores"].items():
        if len(rows) != expected_scores:
            raise ValueError("epoch score count differs from frozen candidate denominator")
        observed = set()
        for row in rows:
            key = (row.get("model"), row.get("queryId"), row.get("fileId"))
            candidate = expected_by_id.get(key)
            if candidate is None or key in observed:
                raise ValueError("epoch score identity is outside the frozen candidate set")
            observed.add(key)
            if (row.get("language") != candidate["language"]
                    or row.get("stratum") != candidate["stratum"]
                    or row.get("seedGroup") != candidate["seedGroup"]
                    or row.get("relevance") != candidate["relevance"][key[2]]):
                raise ValueError("score metadata differs from the frozen candidate artifact")
            if any(not isinstance(row.get(field), (int, float)) for field in ("score", "semanticScore", "tokenScore")):
                raise ValueError("candidate score is missing a numeric value")
        if observed != expected_ids:
            raise ValueError("epoch scores do not reproduce the frozen candidate identities")
    selected_epoch = evaluation_value.get("selection", {}).get("selectedEpoch")
    if type(selected_epoch) is not int or selected_epoch not in range(1, 6):
        raise ValueError("evaluation selection is empty or outside the five trained epochs")
    selected_raw_rows = score_value["perEpochScores"][str(selected_epoch)]
    if canonical(selected_raw_rows) != canonical(score_value["scores"]):
        raise ValueError("selected score rows differ from selected epoch score bytes")
    selected_rows = _attach_candidate_orders(selected_raw_rows, candidate_map)
    epoch_rows_by_epoch = {}
    for epoch in range(1, 6):
        e0_rows = [row for row in score_value["perEpochScores"][str(epoch)] if row["model"] == "E0"]
        epoch_threshold = evaluation_metrics.choose_threshold(e0_rows)
        epoch_summary = evaluation_metrics.summary(e0_rows, threshold=epoch_threshold["threshold"])
        stored = (evaluation_value.get("selection", {}).get("perEpochE0", {}).get(str(epoch), {})
                  .get("E0", {}))
        if (abs(stored.get("threshold", {}).get("threshold", float("inf"))
                - epoch_threshold["threshold"]) > 1e-12
                or abs(stored.get("metrics", {}).get("auprc", {}).get("macro", float("inf"))
                       - epoch_summary["auprc"]["macro"]) > 1e-12
                or abs(stored.get("metrics", {}).get("f1", {}).get("macro", float("inf"))
                       - epoch_summary["f1"]["macro"]) > 1e-12):
            raise ValueError("per-epoch selection metrics differ from candidate score bytes")
        if canonical(stored.get("metrics")) != canonical(epoch_summary):
            raise ValueError("per-epoch language or stratum metrics differ from candidate score bytes")
        epoch_rows_by_epoch[epoch] = e0_rows
    if evaluation_metrics.select_epoch(epoch_rows_by_epoch) != selected_epoch:
        raise ValueError("selected epoch differs from the frozen selection rule")
    selected_controls = {}
    selected_headline_controls = {}
    for label in ("E0", "E3"):
        rows = [row for row in selected_rows if row["model"] == label]
        threshold = evaluation_metrics.choose_threshold(rows)
        headline = [row for row in rows if row["stratum"] in evaluation_metrics.HEADLINE_STRATA]
        headline_summary = evaluation_metrics.summary(headline, threshold=threshold["threshold"])
        stored_pipeline = evaluation_value["pipelines"][label]
        if abs(stored_pipeline["threshold"]["threshold"] - threshold["threshold"]) > 1e-12:
            raise ValueError("pipeline threshold differs from candidate score bytes")
        if canonical(stored_pipeline["allQueriesSecondary"]) != canonical(
                evaluation_metrics.summary(rows, threshold=threshold["threshold"])):
            raise ValueError("all-query metrics differ from candidate score bytes")
        if canonical(stored_pipeline["perLanguage"]) != canonical(
                evaluation_metrics.summary(rows, threshold=threshold["threshold"])["perLanguage"]):
            raise ValueError("per-language metrics differ from candidate score bytes")
        expected_strata = evaluation_metrics.summary(rows, threshold=threshold["threshold"])["perStratum"]
        if canonical(stored_pipeline["perStratum"]) != canonical(expected_strata):
            raise ValueError("per-stratum metrics differ from candidate score bytes")
        for name in ("precision", "recall", "f1", "auprc", "falseFindingsPerQuery"):
            if abs(stored_pipeline["headlineNonExactCopy"][name]["macro"]
                   - headline_summary[name]["macro"]) > 1e-12:
                raise ValueError("headline metric differs from candidate score bytes")
        for field, control_name in (("semanticScore", "semanticScoreThreshold"),
                                    ("tokenScore", "tokenRankTopOneCut")):
            control_rows = [{**row, "score": row[field]} for row in rows]
            control_threshold = evaluation_metrics.choose_threshold(control_rows)
            stored_control = evaluation_value["controls"][label][control_name]
            if abs(stored_control["threshold"]["threshold"] - control_threshold["threshold"]) > 1e-12:
                raise ValueError("{} control threshold differs from candidate score bytes".format(control_name))
            control_summary = evaluation_metrics.summary(control_rows, threshold=control_threshold["threshold"])
            if canonical(stored_control["metrics"]) != canonical(control_summary):
                raise ValueError("control metrics differ from candidate score bytes")
            selected_controls[(label, control_name)] = control_threshold
            headline_control_rows = [row for row in control_rows
                                     if row["stratum"] in evaluation_metrics.HEADLINE_STRATA]
            selected_headline_controls[(label, control_name)] = evaluation_metrics.summary(
                headline_control_rows, threshold=control_threshold["threshold"])["f1"]["macro"]
        retrieval, retrieval_rows = evaluation._retrieval_controls(rows)
        for name, expected_summary in retrieval.items():
            stored_summary = evaluation_value["controls"][label]["retrievalOnly"].get(name)
            if canonical(stored_summary) != canonical(expected_summary):
                raise ValueError("retrieval-only metrics differ from the frozen candidate order")
        verifier_threshold = evaluation_value["pipelines"][label]["threshold"]["threshold"]
        expected_deltas = {
            "semanticScoreThreshold": evaluation_metrics.paired_deltas(
                rows, [{**row, "score": row["semanticScore"]} for row in rows],
                threshold_primary=verifier_threshold,
                threshold_control=selected_controls[(label, "semanticScoreThreshold")]["threshold"]),
            "tokenRankTopOneCut": evaluation_metrics.paired_deltas(
                rows, [{**row, "score": row["tokenScore"]} for row in rows],
                threshold_primary=verifier_threshold,
                threshold_control=selected_controls[(label, "tokenRankTopOneCut")]["threshold"]),
            "retrievalOnly": {name: evaluation_metrics.paired_deltas(
                rows, control_rows, threshold_primary=verifier_threshold,
                threshold_control=control_threshold)
                for name, (control_rows, control_threshold) in retrieval_rows.items()}}
        if canonical(evaluation_value["pairedDeltas"][label]) != canonical(expected_deltas):
            raise ValueError("paired control deltas differ from candidate score bytes")
        if canonical(evaluation_value["crossFit"][label]) != canonical(evaluation_metrics.cross_fit(rows)):
            raise ValueError("cross-fit evidence differs from candidate score bytes")
        if canonical(evaluation_value["orderBreakdowns"][label]) != canonical(
                evaluation._order_breakdown(rows, verifier_threshold)):
            raise ValueError("pair-order breakdown differs from candidate score bytes")
        if canonical(evaluation_value["truncationBreakdowns"][label]) != canonical(
                evaluation._truncation_breakdown(rows, verifier_threshold)):
            raise ValueError("truncation breakdown differs from candidate score bytes")
    _verify_training_output_bindings(evaluation_value, train, selected_epoch)
    quality = evaluation_value.get("qualityOutcome") or {}
    if quality.get("rule") != evaluation.QUALITY_RULE:
        raise ValueError("quality outcome lacks its numeric decision rule")
    e0_rows = [row for row in selected_rows if row["model"] == "E0"
               and row["stratum"] in evaluation_metrics.HEADLINE_STRATA]
    e0_f1 = evaluation_metrics.summary(
        e0_rows, threshold=evaluation_value["pipelines"]["E0"]["threshold"]["threshold"])["f1"]["macro"]
    best_control = max(selected_headline_controls[("E0", name)]
                       for name in ("semanticScoreThreshold", "tokenRankTopOneCut"))
    expected_quality = evaluation.quality_outcome(e0_f1, best_control)
    if quality != expected_quality:
        raise ValueError("quality outcome differs from its stated numeric rule")
    if canonical(evaluation_value.get("candidateListFacts")) != canonical(
            evaluation._candidate_list_facts(candidate_map, {
                label: [row for row in selected_rows if row["model"] == label]
                for label in ("E0", "E3")})):
        raise ValueError("candidate-list saturation facts differ from candidate score bytes")
    prep_doc, train_doc, reload_doc = prepare["evidence"], train["evidence"], train["reload"]
    chain_documents = [("preparation-evidence.json", prep_doc),
                       ("training-evidence.json", train_doc),
                       ("reload-evidence.json", reload_doc),
                       ("experiment-config.json", prepare["config"]),
                       ("evaluation-evidence.json", evaluation_value)]
    final_test_references = _final_test_scan(chain_documents)
    identities = {
        "preparationEvidenceSha256": prepare["evidenceSha256"],
        "preparationMarkerSha256": digest((prepare["evidenceSha256"] + "\n").encode("ascii")),
        "trainingEvidenceSha256": train["evidenceSha256"],
        "reloadEvidenceSha256": train["reloadSha256"],
        "reloadMarkerSha256": train["markerSha256"],
        "evaluationEvidenceSha256": digest(evaluation_raw),
        "evaluationMarkerSha256": digest((digest(evaluation_raw) + "\n").encode("ascii")),
        "candidateScoresSha256": digest(score_raw), "candidateScoresBytes": len(score_raw),
        "candidateArtifactSha256": prepare["validationSha256"],
        "trainingPairsSha256": prepare["trainingSha256"],
        "experimentConfigSha256": prepare["configSha256"],
        "checkpointHashes": [row["sha256"] for row in train["checkpoints"]],
    }
    from scripts.code_analysis.frozen_encoder import file_hash
    for row in train["checkpoints"]:
        checkpoint = ensure_inside(train["run"] / row["relativePath"], train["run"])
        manifest = checkpoint.with_suffix(".json")
        if (checkpoint.stat().st_size != row.get("bytes")
                or file_hash(checkpoint) != row.get("sha256")
                or file_hash(manifest) != row.get("manifestSha256")):
            raise ValueError("checkpoint or manifest bytes differ from training evidence")
    ledger = []
    for name, value, raw in (("preparation-evidence.json", prep_doc, prepare["evidenceRaw"]),
                             ("training-evidence.json", train_doc, train["evidenceRaw"]),
                             ("reload-evidence.json", reload_doc, train["reloadRaw"]),
                             ("experiment-config.json", prepare["config"], prepare["configRaw"]),
                             ("evaluation-evidence.json", evaluation_value, evaluation_raw),
                             ("candidate-scores.json", score_value, score_raw)):
        parsed = inputs.parse_json(raw, name)
        if canonical(parsed) != canonical(value):
            raise ValueError("reconstruction parser returned inconsistent document")
        ledger.append({"document": name, "sha256": digest(raw), "bytes": len(raw),
                       "schema": value.get("schema"), "outcome": value.get("outcome", "HASHED_ARTIFACT"),
                       "parser": "scripts.code_retrieval.inputs.parse_json"})
    candidate_prevalence = _candidate_prevalence(
        selected_raw_rows, {label: sum(value for key, value in
                                      (prepare["validation"].get("unknownExcludedPerModelLanguageStratum") or {}).items()
                                      if key.startswith(label + "/")) for label in ("E0", "E3")})
    prevalence_text = "; ".join(
        "{}: {} positives / {} known pairs (1:{:.1f} known negatives per positive; 1:{:.1f} counting UNKNOWN)".format(
            label, values["positivePairs"], values["knownCandidatePairs"],
            values["negativePerPositiveKnownOnly"], values["negativeAndUnknownPerPositive"])
        for label, values in candidate_prevalence.items())
    limitations = list(LIMITATIONS)
    limitations.append("Measured candidate prevalence from selected score rows: " + prevalence_text + ".")
    imbalance = _crossfit_imbalance(evaluation_value["crossFit"])
    imbalance_text = ", ".join("{} {} vs {} queries".format(language, *counts)
                                for language, counts in imbalance.items())
    limitations.append("Seed files are sorted and alternate across all languages; cross-fit held-out query counts "
                       "by language/fold are uneven: " + imbalance_text + ".")
    package = {"schema": PACKAGE_SCHEMA, "outcome": "SUCCEEDED",
               "chain": {"prepareRunId": prepare_run_id, "trainingRunId": train_run_id,
                         "evaluationRunId": evaluation_run_id},
               "statuses": {"trainingCompleted": {"status": "TRAINED",
                                                   "evidenceSha256": train["evidenceSha256"]},
                            "qualityOutcome": {"status": quality["status"], "rule": quality["rule"],
                                               "delta": quality["delta"]},
                            "reviewerAcceptance": "NOT_RECORDED"},
               "identities": identities,
               "selectedEpoch": selected_epoch,
               "metrics": {"pipelines": evaluation_value["pipelines"],
                           "candidatePrevalence": candidate_prevalence,
                           "controls": evaluation_value["controls"],
                           "pairedDeltas": evaluation_value["pairedDeltas"],
                           "crossFit": evaluation_value["crossFit"],
                           "latency": evaluation_value["latency"],
                           "saturation": evaluation_value["saturation"]},
               "reconstructionLedger": ledger,
               "finalTestTextScan": {"allowlist": [
                   {"document": name, "keyPath": list(path)}
                   for name, path in sorted(FINAL_TEST_REFERENCE_ALLOWLIST)],
                   "references": final_test_references, "membershipContentsOpened": False},
               "limitations": limitations,
               "checkpointAuthorization": {"selectedForUse": False, "approvedForUse": False,
                                           "productAuthorizationChanged": False},
               "redistribution": "DENIED; local-only artifacts"}
    return package


def reconstruction_markdown(package):
    lines = ["# Pair verification evidence reconstruction", "",
             "Built from the canonical evidence package and the repository JSON parser.",
             "Hashes and counts only; no source text or vectors.", "", "## Chain", ""]
    for stage, run_id in package["chain"].items():
        lines.append("- {}: `{}`".format(stage, run_id))
    lines += ["", "## Statuses", "",
              "- Training completed: {}".format(package["statuses"]["trainingCompleted"]["status"]),
              "- Quality outcome: {}".format(package["statuses"]["qualityOutcome"]["status"]),
              "- Reviewer acceptance: {}".format(package["statuses"]["reviewerAcceptance"]),
              "", "## Reconstruction ledger", ""]
    for row in package["reconstructionLedger"]:
        lines.append("- {}: {} bytes, sha256 `{}` ({})".format(
            row["document"], row["bytes"], row["sha256"], row["parser"]))
    lines += ["", "## Limitations", ""]
    lines.extend("- " + item for item in package["limitations"])
    lines += ["", "## Authorization", "",
              "- Checkpoint selected for use: false.",
              "- Checkpoint approved for use: false.",
              "- Reviewer acceptance: not recorded.",
              "- Redistribution: denied.", ""]
    return "\n".join(lines)


def run(*, run_id, prepare_run_id, train_run_id, evaluation_run_id, inputs_path, config_path):
    if not isinstance(run_id, str) or not re.fullmatch(r"pairs-evidence-[0-9]{8}-[0-9]{2}", run_id):
        raise ValueError("invalid evidence package run identifier")
    target = ensure_inside(RUN_ROOT / run_id, RUN_ROOT)
    target.mkdir(parents=True, exist_ok=False)
    try:
        package = verify_chain(prepare_run_id=prepare_run_id, train_run_id=train_run_id,
                               evaluation_run_id=evaluation_run_id, inputs_path=inputs_path,
                               config_path=config_path)
        raw = canonical(package)
        write_once(target / PACKAGE_NAME, raw, PAIR_ROOT)
        ledger_raw = reconstruction_markdown(package).encode("utf-8")
        write_once(target / RECONSTRUCTION_NAME, ledger_raw, PAIR_ROOT)
        write_once(target / "SUCCEEDED", (digest(raw) + "\n").encode("ascii"), PAIR_ROOT)
        return {"outcome": "SUCCEEDED", "directory": target, "evidenceSha256": digest(raw),
                "reconstructionSha256": digest(ledger_raw)}
    except BaseException as exc:
        failure = {"schema": PACKAGE_SCHEMA, "runId": run_id, "outcome": "FAILED",
                   "failureReason": type(exc).__name__}
        raw = canonical(failure)
        if not (target / PACKAGE_NAME).exists():
            write_once(target / PACKAGE_NAME, raw, PAIR_ROOT)
        raise

