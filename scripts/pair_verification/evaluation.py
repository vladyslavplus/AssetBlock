"""Score frozen validation pairs with the CUDA verifier and write immutable results.

This stage uses the pilot CUDA interpreter. It never loads final-test membership or source data.
"""

import json
import math
import os
import re
import time
from collections import defaultdict
from datetime import datetime
from pathlib import Path

from scripts.code_analysis.contracts import LANGUAGES, canonical, digest
from scripts.code_analysis.corpus import write_once
from scripts.code_analysis.evaluation import cpu_rank, timed_percentiles
from scripts.code_retrieval import index_verify, inputs, retrieval_eval
from scripts.feasibility_pilot.paths import REPO_ROOT, ensure_inside

from . import evaluation_metrics as metrics
from . import training, training_data, validation_candidates
from .checkpoints import load_epoch_checkpoint

PAIR_ROOT = REPO_ROOT / "artifacts" / "pair_verification"
RUN_ROOT = PAIR_ROOT / "runs"
SCORE_SCHEMA = "pair-verification-candidate-scores-v1"
EVALUATION_SCHEMA = "pair-verification-evaluation-evidence-v1"
TIMED_QUERIES = 30
WARMUP_QUERIES = 3
INFERENCE_BATCH_SIZE = 32
QUALITY_RULE = ("Fixed at evaluation time: POSITIVE_EFFECT iff headline E0 verifier macro F1 exceeds "
                "the best score-threshold control (semantic score or token-rank cut) by more than "
                "1e-12; otherwise FLAT_OR_NEGATIVE")


def quality_outcome(verifier_macro_f1, best_control_macro_f1):
    if not math.isfinite(float(verifier_macro_f1)) or not math.isfinite(float(best_control_macro_f1)):
        raise ValueError("quality comparison metrics are not finite")
    return {"rule": QUALITY_RULE,
            "status": "POSITIVE_EFFECT" if verifier_macro_f1 > best_control_macro_f1 + 1e-12
            else "FLAT_OR_NEGATIVE",
            "headlineE0VerifierMacroF1": verifier_macro_f1,
            "bestControlMacroF1": best_control_macro_f1,
            "delta": verifier_macro_f1 - best_control_macro_f1}


def _read_json(path, cap=256 * 1024 * 1024):
    path = Path(path)
    if path.stat().st_size > cap:
        raise ValueError("evaluation input exceeds byte limit")
    raw = path.read_bytes()
    return json.loads(raw), raw


def _train_binding(run_id, prepare):
    if not isinstance(run_id, str) or not re.fullmatch(r"pairs-train-[0-9]{8}-[0-9]{2}", run_id):
        raise ValueError("invalid training run id")
    run = ensure_inside(RUN_ROOT / run_id, RUN_ROOT)
    evidence, evidence_raw = _read_json(run / "training-evidence.json")
    reload_evidence, reload_raw = _read_json(run / "reload-evidence.json")
    marker = inputs.read_bytes(run / "SUCCEEDED", 128)
    if marker != (digest(reload_raw) + "\n").encode("ascii"):
        raise ValueError("training success marker differs from reload evidence")
    if (evidence.get("schema") != "pair-verification-training-evidence-v1"
            or evidence.get("runId") != run_id or evidence.get("outcome") != "TRAINED"):
        raise ValueError("training run is not a successful completed run")
    if (reload_evidence.get("schema") != "pair-verification-reload-evidence-v1"
            or reload_evidence.get("runId") != run_id or reload_evidence.get("outcome") != "SUCCEEDED"
            or reload_evidence.get("freshProcess") is not True or len(reload_evidence.get("epochs") or []) != 5):
        raise ValueError("training run lacks successful fresh-process reload evidence")
    identity = evidence.get("identity") or {}
    if (identity.get("prepareEvidenceSha256") != prepare["evidenceSha256"]
            or identity.get("configSha256") != prepare["configSha256"]
            or identity.get("trainingPairsSha256") != prepare["trainingSha256"]
            or identity.get("candidateArtifactSha256") != prepare["validationSha256"]):
        raise ValueError("training run differs from the admitted pair preparation")
    epochs = evidence.get("training", {}).get("epochs") or []
    checkpoints = evidence.get("checkpoints") or []
    if ([row.get("epoch") for row in epochs] != [1, 2, 3, 4, 5]
            or [row.get("epoch") for row in checkpoints] != [1, 2, 3, 4, 5]
            or [row.get("epoch") for row in reload_evidence["epochs"]] != [1, 2, 3, 4, 5]):
        raise ValueError("training run checkpoint or reload epoch set is incomplete")
    expected_counts = defaultdict(int)
    for row in prepare["validation"].get("candidates") or []:
        expected_counts[row["model"] + "/" + row["language"]] += len(row.get("candidateFileIds") or [])
    for row in epochs:
        order = row.get("validationPairOrder") or {}
        if (order.get("candidatePairCount") != sum(expected_counts.values())
                or order.get("candidatePairsPerModelLanguage") != dict(sorted(expected_counts.items()))):
            raise ValueError("training run lacks the complete per-epoch candidate-pair order evidence")
    return {"run": run, "evidence": evidence, "evidenceRaw": evidence_raw,
            "evidenceSha256": digest(evidence_raw), "reload": reload_evidence,
            "reloadRaw": reload_raw, "reloadSha256": digest(reload_raw),
            "markerSha256": digest(marker),
            "epochs": epochs, "checkpoints": checkpoints}


def _candidate_rows(candidate_artifact):
    rows = candidate_artifact.get("candidates")
    if candidate_artifact.get("schema") != "pair-verification-validation-candidates-v1" or not isinstance(rows, list):
        raise ValueError("validation candidate artifact schema is invalid")
    result, seen = {}, set()
    for item in rows:
        key = (item.get("model"), item.get("queryId"))
        if key in seen or key[0] not in {"E0", "E3"}:
            raise ValueError("validation candidate list identity is duplicated or invalid")
        seen.add(key)
        candidate_ids = item.get("candidateFileIds") or []
        if not candidate_ids or len(candidate_ids) != len(set(candidate_ids)) or len(candidate_ids) > 40:
            raise ValueError("validation candidate list is empty, duplicated, or over budget")
        relevance = item.get("relevance") or {}
        if set(relevance) != set(candidate_ids) or any(
                label not in {"POSITIVE", "REVIEWED_DISTRACTOR"} for label in relevance.values()):
            raise ValueError("candidate labels contain an unknown or unsupported value")
        if not any(label == "POSITIVE" for label in relevance.values()):
            raise ValueError("candidate list lacks its reviewed relevant file")
        result[key] = item
    if len(result) != 402:
        raise ValueError("candidate artifact does not contain 201 queries per pipeline")
    return result


def _candidate_window_rows(train, candidate_map):
    path = ensure_inside(train["run"] / "validation-window-pairs.json", train["run"])
    raw = inputs.read_bytes(path, 256 * 1024 * 1024)
    if digest(raw) != train["evidence"].get("validationWindowPairsSha256"):
        raise ValueError("training validation window-pair bytes differ from training evidence")
    records = inputs.parse_json(raw, "validation window-pair artifact")
    windows, seen = {}, set()
    for item in records:
        key = (item.get("model"), item.get("queryId"), item.get("fileId"))
        if key in seen:
            raise ValueError("validation window-pair identity is duplicated")
        seen.add(key)
        candidate = candidate_map.get(key[:2])
        if candidate is None or item["fileId"] not in candidate["relevance"]:
            raise ValueError("validation window pair is outside the frozen candidate artifact")
        pairs = item.get("windowPairs")
        if not isinstance(pairs, list) or not 1 <= len(pairs) <= 3:
            raise ValueError("validation candidate has an invalid window-pair count")
        if any(not isinstance(pair.get("inputIds"), list) or not pair["inputIds"]
               or len(pair["inputIds"]) > 512 for pair in pairs):
            raise ValueError("validation pair token layout is invalid")
        windows[key] = item
    if set(windows) != {(model, query, file_id) for (model, query), item in candidate_map.items()
                        for file_id in item["candidateFileIds"]}:
        raise ValueError("validation window pairs do not cover exact candidate lists")
    return windows, digest(raw), len(raw)


def _truncation_stats(windows, candidate_map):
    result = {}
    for model in ("E0", "E3"):
        for language in LANGUAGES:
            keys = [key for key in windows if key[0] == model
                    and candidate_map[key[:2]]["language"] == language]
            pair_count = sum(len(windows[key]["windowPairs"]) for key in keys)
            truncated_windows = sum(bool(pair["truncated"]) for key in keys
                                    for pair in windows[key]["windowPairs"])
            truncated_candidates = sum(any(pair["truncated"] for pair in windows[key]["windowPairs"])
                                       for key in keys)
            result[model + "/" + language] = {
                "candidatePairs": len(keys), "windowPairs": pair_count,
                "truncatedCandidatePairs": truncated_candidates,
                "truncatedWindowPairs": truncated_windows,
                "truncatedCandidateShare": truncated_candidates / len(keys) if keys else 0.0,
                "maximumInputTokens": max((len(pair["inputIds"]) for key in keys
                                            for pair in windows[key]["windowPairs"]), default=0)}
    return result


def _score_checkpoint(model, windows, device, pad_id, *, include_swapped=False):
    original, swapped, keys, truncated = [], [], [], {}
    for key, item in windows.items():
        for pair in item["windowPairs"]:
            original.append(pair["inputIds"])
            if include_swapped:
                swapped.append(pair["swappedInputIds"])
            keys.append(key)
            truncated[key] = truncated.get(key, False) or bool(pair["truncated"])
    logits = training._forward_logits(model, original + swapped, device, pad_id=pad_id,
                                      batch_size=INFERENCE_BATCH_SIZE)
    def positive_probability(row):
        maximum = max(row)
        exp_values = [math.exp(value - maximum) for value in row]
        return exp_values[1] / sum(exp_values)
    probabilities = [positive_probability(row) for row in logits]
    count = len(original)
    scores, reverse_scores = {}, {}
    for index, key in enumerate(keys):
        scores.setdefault(key, []).append(probabilities[index])
        if include_swapped:
            reverse_scores.setdefault(key, []).append(probabilities[index + count])
    result = {}
    for key, values in scores.items():
        item = {"score": max(values), "windowCount": len(values), "truncated": truncated[key]}
        if include_swapped:
            reverse = reverse_scores[key]
            item["swappedScore"] = max(reverse)
            item["windowMeanAbsoluteSwapDifference"] = sum(abs(a - b) for a, b in zip(values, reverse)) / len(values)
            item["windowMaximumAbsoluteSwapDifference"] = max(abs(a - b) for a, b in zip(values, reverse))
        result[key] = item
    return result


def _semantic_scores(binding, candidate_map):
    indexed = {}
    for label in ("E0", "E3"):
        _, indexed[label] = index_verify.load_index(
            binding["chain"]["index"], label, binding["validation"], binding["corpus"],
            binding["export"]["models"][label]["modelKey"], binding["export"])
    query_vectors = {label: validation_candidates._query_vectors(binding, label) for label in ("E0", "E3")}
    query_records = {row["queryId"]: row for language in LANGUAGES
                     for row in binding["validation"]["queries"][language]}
    results = {}
    for (label, query_id), item in candidate_map.items():
        query = query_records.get(query_id)
        if query is None:
            raise ValueError("candidate query is absent from the bound validation plan")
        ranked = retrieval_eval.rank_query(query, query_vectors[label][query_id], indexed[label], {}, "BLOCKED")
        if ranked["semantic"][:20] != item["semanticTop20"]:
            raise ValueError("candidate semantic order differs from the verified retrieval ranker")
        gallery_rows = [row for row in indexed[label][query["language"]]
                        if row["fileId"] in set(query["scoredGallery"])]
        ranked_scores = cpu_rank(query_vectors[label][query_id], gallery_rows, key="fileId",
                                 limit=len(query["scoredGallery"]))
        score_by_file = {row["fileId"]: row["score"] for row in ranked_scores}
        if not set(item["candidateFileIds"]) <= set(score_by_file):
            raise ValueError("a candidate has no semantic control score in the strict gallery")
        results[(label, query_id)] = score_by_file
    return results


def _make_metric_rows(candidate_map, candidate_scores, semantic_scores):
    result = {label: [] for label in ("E0", "E3")}
    for key, item in candidate_map.items():
        label, query_id = key
        token_rank = {file_id: rank for rank, file_id in enumerate(item["tokenTop20"])}
        for file_id in item["candidateFileIds"]:
            score = candidate_scores[(label, query_id, file_id)]
            token_score = 1.0 / (token_rank[file_id] + 1) if file_id in token_rank else 0.0
            result[label].append({"model": label, "queryId": query_id, "fileId": file_id,
                                  "language": item["language"], "stratum": item["stratum"],
                                  "seedGroup": item["seedGroup"],
                                  "relevance": item["relevance"][file_id], "score": score["score"],
                                  "semanticScore": semantic_scores[key][file_id],
                                  "tokenScore": token_score, "truncated": score["truncated"],
                                  "swappedScore": score.get("swappedScore"),
                                  "semanticTop20": item["semanticTop20"],
                                  "tokenTop20": item["tokenTop20"],
                                  "candidateFileIds": item["candidateFileIds"]})
    return result


def _control_summary(rows, score_field):
    control_rows = [{**row, "score": row[score_field]} for row in rows]
    threshold = metrics.choose_threshold(control_rows)
    return threshold, metrics.summary(control_rows, threshold=threshold["threshold"]), control_rows


def _retrieval_control_rows(rows, branch, limit):
    result = []
    grouped = defaultdict(list)
    for row in rows:
        grouped[row["queryId"]].append(row)
    for candidates in grouped.values():
        ranking = list(candidates[0][branch])
        if limit is not None:
            ranking = ranking[:limit]
        order = {file_id: index for index, file_id in enumerate(ranking)}
        for row in candidates:
            score = 1.0 / (order[row["fileId"]] + 1) if row["fileId"] in order else 0.0
            result.append({**row, "score": score})
    threshold = 0.0 if limit is None else 1.0 / limit
    return result, threshold


def _retrieval_controls(rows):
    output = {}
    controls = {}
    for branch in ("semanticTop20", "tokenTop20", "candidateFileIds"):
        for limit, suffix in ((5, "top5"), (10, "top10"), (20, "top20"), (None, "union")):
            if branch == "candidateFileIds" and suffix != "union":
                continue
            if branch != "candidateFileIds" and suffix == "union":
                continue
            control_rows, threshold = _retrieval_control_rows(rows, branch, limit)
            name = suffix if branch == "semanticTop20" else "token/" + suffix if branch == "tokenTop20" else "union"
            output[name] = metrics.summary(control_rows, threshold=threshold)
            controls[name] = (control_rows, threshold)
    return output, controls


def _order_breakdown(rows, threshold):
    eligible = [row for row in rows if row.get("swappedScore") is not None]
    grouped = defaultdict(list)
    for row in eligible:
        grouped[(row["language"], row["stratum"])].append(row)
    result = {}
    for (language, stratum), values in sorted(grouped.items()):
        result[language + "/" + stratum] = {
            "candidatePairs": len(values),
            "decisionAgreement": sum((row["score"] >= threshold) == (row["swappedScore"] >= threshold)
                                      for row in values) / len(values),
            "meanAbsoluteProbabilityDifference": sum(abs(row["score"] - row["swappedScore"])
                                                       for row in values) / len(values),
            "maximumAbsoluteProbabilityDifference": max(abs(row["score"] - row["swappedScore"])
                                                         for row in values)}
    return result


def _truncation_breakdown(rows, threshold):
    groups = {"truncated": [row for row in rows if row["truncated"]],
              "nonTruncated": [row for row in rows if not row["truncated"]]}
    return {name: (metrics.summary(values, threshold=threshold) if values else
                   {"status": "EMPTY", "candidatePairs": 0, "reason": "no validation window pairs in group"})
            for name, values in groups.items()}


def _candidate_list_facts(candidate_map, rows_by_label):
    facts = {}
    for label in ("E0", "E3"):
        lists = [item for (model, _), item in candidate_map.items() if model == label]
        relevant_one = sum(sum(value == "POSITIVE" for value in item["relevance"].values()) == 1
                           for item in lists)
        union_false = 0
        grouped = defaultdict(list)
        for row in rows_by_label[label]:
            grouped[row["queryId"]].append(row)
        for group in grouped.values():
            union_false += sum(row["relevance"] != "POSITIVE" for row in group)
        facts[label] = {"queryLists": len(lists), "exactlyOneRelevantFile": relevant_one,
                        "retrievalUnionFalseFindings": union_false}
    return facts


def _latency_query_windows(queries):
    if len(queries) < WARMUP_QUERIES + TIMED_QUERIES:
        raise ValueError("latency sample has fewer than 30 queries after warmup")
    return queries[:WARMUP_QUERIES], queries[WARMUP_QUERIES:WARMUP_QUERIES + TIMED_QUERIES]


def _latency(model, windows, candidate_map, label, device, pad_id, *, one_window=False):
    by_query = defaultdict(list)
    for key, value in windows.items():
        if key[0] == label:
            by_query[key[1]].append(value)
    queries = sorted(by_query)
    warmup_queries, timed_queries = _latency_query_windows(queries)

    def run_query(query_id):
        rows = []
        for item in by_query[query_id]:
            pairs = item["windowPairs"][:1] if one_window else item["windowPairs"]
            rows.extend(pair["inputIds"] for pair in pairs)
        training.torch.cuda.synchronize(device)
        started = time.perf_counter()
        training._forward_logits(model, rows, device, pad_id=pad_id, batch_size=INFERENCE_BATCH_SIZE)
        training.torch.cuda.synchronize(device)
        elapsed = time.perf_counter() - started
        count = len(by_query[query_id])
        return elapsed, elapsed / count, count

    for query_id in warmup_queries:
        run_query(query_id)
    samples_query, samples_pair = [], []
    for query_id in timed_queries:
        elapsed, per_pair, _ = run_query(query_id)
        samples_query.append(elapsed)
        samples_pair.append(per_pair)
    resources = training._device_resources()
    return {"warmupQueriesExcluded": WARMUP_QUERIES, "timedQueries": TIMED_QUERIES,
            "perQuerySeconds": timed_percentiles(samples_query),
            "amortizedPerCandidatePairSeconds": timed_percentiles(samples_pair),
            "candidatePairsPerQueryRange": [min(len(by_query[q]) for q in queries[:WARMUP_QUERIES + TIMED_QUERIES]),
                                            max(len(by_query[q]) for q in queries[:WARMUP_QUERIES + TIMED_QUERIES])],
            "resources": resources, "oneWindowVariant": one_window}


def run(*, run_id, prepare_run_id, train_run_id, inputs_path, config_path):
    if not isinstance(run_id, str) or not re.fullmatch(r"pairs-evaluate-[0-9]{8}-[0-9]{2}", run_id):
        raise ValueError("invalid evaluation run identifier")
    target = ensure_inside(RUN_ROOT / run_id, RUN_ROOT)
    target.mkdir(parents=True, exist_ok=False)
    started = time.monotonic()
    run_started_at = datetime.now().astimezone()
    source = None
    stage = "chain-binding"
    try:
        source = training_data.load_bound_prepare(prepare_run_id=prepare_run_id, inputs_path=inputs_path,
                                                 config_path=config_path)
        train = _train_binding(train_run_id, source)
        if train["evidence"]["identity"].get("runId") != train_run_id:
            raise ValueError("training evidence does not bind its run identifier")
        stage = "validation-pair-binding"
        candidate_map = _candidate_rows(source["validation"])
        windows, windows_sha, windows_bytes = _candidate_window_rows(train, candidate_map)
        truncation_stats = _truncation_stats(windows, candidate_map)
        driver_pre = training.check_gpu_driver_events(run_started_at)
        training.upstream_checkpoint.configure_numerics()
        device = training.upstream_checkpoint.cuda_device()
        upstream = training.upstream_checkpoint.upstream_identity()
        from transformers import RobertaTokenizerFast
        tokenizer = RobertaTokenizerFast.from_pretrained(str(train["run"] / "support"), local_files_only=True,
                                                          trust_remote_code=False)
        semantic_scores = _semantic_scores(source["bound"], candidate_map)
        epoch_scores, epoch_summaries, epoch_e0_rows = {}, {}, {}
        order_metrics_by_epoch = []
        stage = "checkpoint-inference"
        for epoch, checkpoint_row in zip(range(1, 6), train["checkpoints"]):
            if checkpoint_row.get("epoch") != epoch or checkpoint_row.get("selection") != "PENDING_VALIDATION":
                raise ValueError("checkpoint list is incomplete or selection state changed")
            checkpoint_path = ensure_inside(train["run"] / checkpoint_row["relativePath"], train["run"])
            model, manifest = load_epoch_checkpoint(
                checkpoint_path, root=train["run"], expected_manifest_sha256=checkpoint_row["manifestSha256"],
                expected_training_evidence={"runId": train_run_id,
                                            "configSha256": train["evidence"]["identity"]["configSha256"],
                                            "prepareEvidenceSha256": train["evidence"]["identity"]["prepareEvidenceSha256"],
                                            "trainingPairsSha256": train["evidence"]["identity"]["trainingPairsSha256"]},
                upstream=upstream, device=device)
            scored = _score_checkpoint(model, windows, device, tokenizer.pad_token_id)
            candidate_scores = {(model_label, query_id, file_id): value
                                for (model_label, query_id, file_id), value in scored.items()}
            epoch_rows = _make_metric_rows(candidate_map, candidate_scores, semantic_scores)
            e0 = epoch_rows["E0"]
            epoch_e0_rows[epoch] = e0
            threshold = metrics.choose_threshold(e0)
            epoch_summaries[str(epoch)] = {"E0": {"threshold": threshold,
                                                    "metrics": metrics.summary(e0, threshold=threshold["threshold"])}}
            epoch_scores[str(epoch)] = scored
            order_metrics_by_epoch.append((epoch, (train["evidence"]["training"]["epochs"][epoch - 1]
                                                    .get("validationPairOrder") or {})))
            del model
        stage = "epoch-selection"
        selected_epoch = metrics.select_epoch(epoch_e0_rows)
        selected_checkpoint = train["checkpoints"][selected_epoch - 1]
        model, selected_manifest = load_epoch_checkpoint(
            ensure_inside(train["run"] / selected_checkpoint["relativePath"], train["run"]),
            root=train["run"], expected_manifest_sha256=selected_checkpoint["manifestSha256"],
            expected_training_evidence={"runId": train_run_id,
                                        "configSha256": train["evidence"]["identity"]["configSha256"],
                                        "prepareEvidenceSha256": train["evidence"]["identity"]["prepareEvidenceSha256"],
                                        "trainingPairsSha256": train["evidence"]["identity"]["trainingPairsSha256"]},
            upstream=upstream, device=device)
        selected_scores = _score_checkpoint(model, windows, device, tokenizer.pad_token_id, include_swapped=True)
        candidate_scores = {(label, query, file_id): row for (label, query, file_id), row in selected_scores.items()}
        metric_rows = _make_metric_rows(candidate_map, candidate_scores, semantic_scores)
        results, controls, deltas, order_breakdowns, truncation_breakdowns = {}, {}, {}, {}, {}
        headline_control_f1 = {}
        cross_fit = {}
        for label in ("E0", "E3"):
            rows = metric_rows[label]
            threshold = metrics.choose_threshold(rows)
            verifier = metrics.summary(rows, threshold=threshold["threshold"])
            headline_rows = [row for row in rows if row["stratum"] in metrics.HEADLINE_STRATA]
            headline_summary = metrics.summary(headline_rows, threshold=threshold["threshold"])
            semantic_threshold, semantic_control, semantic_rows = _control_summary(rows, "semanticScore")
            token_threshold, token_control, token_rows = _control_summary(rows, "tokenScore")
            semantic_headline = [row for row in semantic_rows if row["stratum"] in metrics.HEADLINE_STRATA]
            token_headline = [row for row in token_rows if row["stratum"] in metrics.HEADLINE_STRATA]
            semantic_headline_summary = metrics.summary(
                semantic_headline, threshold=semantic_threshold["threshold"])
            token_headline_summary = metrics.summary(token_headline, threshold=token_threshold["threshold"])
            headline_control_f1[label] = {"semanticScoreThreshold": semantic_headline_summary["f1"]["macro"],
                                          "tokenRankTopOneCut": token_headline_summary["f1"]["macro"]}
            retrieval, retrieval_control_rows = _retrieval_controls(rows)
            unknown_counts = source["validation"].get("unknownExcludedPerModelLanguageStratum") or {}
            unknown_excluded = sum(value for key, value in unknown_counts.items()
                                   if key.startswith(label + "/"))
            unknown_by_language = {language: sum(value for key, value in unknown_counts.items()
                                                 if key.startswith(label + "/" + language + "/"))
                                   for language in LANGUAGES}
            unknown_by_stratum = {stratum: sum(value for key, value in unknown_counts.items()
                                                if key.endswith("/" + stratum) and key.startswith(label + "/"))
                                  for stratum in metrics.STRATA}
            results[label] = {"threshold": threshold, "headlineNonExactCopy": headline_summary,
                              "allQueriesSecondary": verifier, "perStratum": verifier["perStratum"],
                              "perLanguage": verifier["perLanguage"],
                              "unknownExcluded": unknown_excluded,
                              "unknownExcludedPerLanguage": unknown_by_language,
                              "unknownExcludedPerStratum": unknown_by_stratum}
            controls[label] = {"semanticScoreThreshold": {"threshold": semantic_threshold,
                                                            "metrics": semantic_control,
                                                            "headlineNonExactCopy": semantic_headline_summary},
                               "tokenRankTopOneCut": {"threshold": token_threshold,
                                                            "metrics": token_control,
                                                            "headlineNonExactCopy": token_headline_summary,
                                                            "scoreDefinition": "per-query reciprocal-rank cut; threshold 1.0 selects token rank 1 (top-1), not a global score threshold; raw token pair scores were not persisted"},
                               "retrievalOnly": retrieval}
            deltas[label] = {"semanticScoreThreshold": metrics.paired_deltas(
                                 rows, semantic_rows, threshold_primary=threshold["threshold"],
                                 threshold_control=semantic_threshold["threshold"]),
                             "tokenRankTopOneCut": metrics.paired_deltas(
                                 rows, token_rows, threshold_primary=threshold["threshold"],
                                 threshold_control=token_threshold["threshold"])}
            deltas[label]["retrievalOnly"] = {
                name: metrics.paired_deltas(rows, control_rows,
                                            threshold_primary=threshold["threshold"],
                                            threshold_control=control_threshold)
                for name, (control_rows, control_threshold) in retrieval_control_rows.items()}
            order_breakdowns[label] = _order_breakdown(rows, threshold["threshold"])
            truncation_breakdowns[label] = _truncation_breakdown(rows, threshold["threshold"])
            cross_fit[label] = metrics.cross_fit(rows)
        stage = "latency"
        training.torch.cuda.reset_peak_memory_stats(device)
        latency = {label: _latency(model, windows, candidate_map, label, device, tokenizer.pad_token_id)
                   for label in ("E0", "E3")}
        if any(row["perQuerySeconds"]["p95"] > 5 for row in latency.values()):
            latency["oneWindowVariant"] = {label: _latency(
                model, windows, candidate_map, label, device, tokenizer.pad_token_id, one_window=True)
                for label in ("E0", "E3")}
        driver_post = training.check_gpu_driver_events(run_started_at)
        facts = _candidate_list_facts(candidate_map, metric_rows)
        train_epoch_order = [{"epoch": epoch, "validationPairOrder": value}
                             for epoch, value in order_metrics_by_epoch]
        chosen_e0 = results["E0"]["headlineNonExactCopy"]["f1"]["macro"]
        best_control = max(headline_control_f1["E0"].values())
        quality = quality_outcome(chosen_e0, best_control)
        score_rows_by_epoch = {}
        for epoch in range(1, 6):
            source_scores = selected_scores if epoch == selected_epoch else epoch_scores[str(epoch)]
            joined = {(label, query, file_id): value for (label, query, file_id), value in source_scores.items()}
            rows = []
            for (label, query_id), candidate in candidate_map.items():
                token_rank = {file_id: rank for rank, file_id in enumerate(candidate["tokenTop20"])}
                for file_id in candidate["candidateFileIds"]:
                    score = joined[(label, query_id, file_id)]
                    row = {"model": label, "queryId": query_id, "fileId": file_id,
                           "language": candidate["language"], "stratum": candidate["stratum"],
                           "seedGroup": candidate["seedGroup"],
                           "relevance": candidate["relevance"][file_id], "score": score["score"],
                           "semanticScore": semantic_scores[(label, query_id)][file_id],
                           "tokenScore": 1.0 / (token_rank[file_id] + 1) if file_id in token_rank else 0.0,
                           "windowCount": score["windowCount"], "truncated": score["truncated"]}
                    if epoch == selected_epoch:
                        row.update({"swappedScore": score["swappedScore"],
                                    "windowMeanAbsoluteSwapDifference": score[
                                        "windowMeanAbsoluteSwapDifference"],
                                    "windowMaximumAbsoluteSwapDifference": score[
                                        "windowMaximumAbsoluteSwapDifference"]})
                    rows.append(row)
            score_rows_by_epoch[str(epoch)] = rows
        candidate_score_value = {"schema": SCORE_SCHEMA, "prepareRunId": prepare_run_id,
                                 "trainingRunId": train_run_id, "selectedEpoch": selected_epoch,
                                 "perEpochScores": score_rows_by_epoch,
                                 "scores": score_rows_by_epoch[str(selected_epoch)]}
        scores_raw = canonical(candidate_score_value)
        scores_path = target / "candidate-scores.json"
        write_once(scores_path, scores_raw, PAIR_ROOT)
        evidence = {"schema": EVALUATION_SCHEMA, "runId": run_id, "outcome": "SUCCEEDED",
                    "identities": {"prepareRunId": prepare_run_id,
                                   "prepareEvidenceSha256": source["evidenceSha256"],
                                   "prepareMarkerSha256": digest(
                                       (source["evidenceSha256"] + "\n").encode("ascii")),
                                   "configSha256": source["configSha256"],
                                   "trainingPairsSha256": source["trainingSha256"],
                                   "candidateArtifactSha256": source["validationSha256"],
                                   "trainingRunId": train_run_id,
                                   "trainingEvidenceSha256": train["evidenceSha256"],
                                   "reloadEvidenceSha256": train["reloadSha256"],
                                   "reloadMarkerSha256": train["markerSha256"],
                                   "checkpointSha256": selected_checkpoint["sha256"],
                                   "validationWindowPairsSha256": windows_sha,
                                   "candidateScoresSha256": digest(scores_raw),
                                   "candidateScoresBytes": len(scores_raw)},
                    "selection": {"rule": "D9 language-equal E0 candidate-pair AUPRC; tie macro F1 at D10 threshold; earlier epoch",
                                  "selectedEpoch": selected_epoch,
                                  "perEpochE0": epoch_summaries,
                                  "checkpointStatus": {"selectedForUse": False,
                                                       "approvedForUse": False,
                                                       "manifestSelection": selected_manifest["selection"]}},
                    "thresholdRule": "one threshold maximizes language-equal macro F1; ties choose higher threshold",
                    "pipelines": results, "controls": controls, "pairedDeltas": deltas,
                    "crossFit": cross_fit, "orderBreakdowns": order_breakdowns,
                    "trainingOrderEvidenceByEpoch": train_epoch_order,
                    "truncationBreakdowns": truncation_breakdowns,
                    "truncationStatistics": truncation_stats,
                    "candidateListFacts": facts,
                    "latency": latency,
                    "qualityOutcome": quality,
                    "candidateContract": source["config"].get("candidateContract"),
                    "provenance": {"validationSelectionContaminated": True,
                                   "validationUsedForGradients": False,
                                   "tokenControl": "rank-derived reciprocal score because token pair scores were not persisted",
                                   "candidateListsUsedAsSoleCandidateSource": True},
                    "environment": {"python": __import__("sys").version.split()[0],
                                    "torch": training.torch.__version__,
                                    "cuda": training.torch.version.cuda,
                                    "device": training.torch.cuda.get_device_name(device),
                                    "processId": os.getpid()},
                    "gpuDriverLogChecks": {"provider": "nvlddmkm", "eventId": 153,
                                           "runStartedAt": run_started_at.isoformat(timespec="seconds"),
                                           "before": driver_pre, "after": driver_post},
                    "resources": training._device_resources(),
                    "elapsedSeconds": time.monotonic() - started,
                    "validationWindowPairsBytes": windows_bytes,
                    "saturation": facts,
                    "tokenControlLimitation": "Exact token-pair scores were not persisted in the bound candidate artifact. Control uses reciprocal rank of the recorded tokenTop20 order; this preserves rank order but not score gaps.",
                    "unknownExclusionsPerModelLanguageStratum": source["validation"].get(
                        "unknownExcludedPerModelLanguageStratum") or {}}
        from .evidence import validate as validate_evidence
        validate_evidence(evidence)
        evidence_raw = canonical(evidence)
        write_once(target / "evaluation-evidence.json", evidence_raw, PAIR_ROOT)
        write_once(target / "SUCCEEDED", (digest(evidence_raw) + "\n").encode("ascii"), PAIR_ROOT)
        return {"outcome": "SUCCEEDED", "directory": target, "evidenceSha256": digest(evidence_raw),
                "selectedEpoch": selected_epoch}
    except BaseException as exc:
        gpu_post_failure = None
        try:
            gpu_post_failure = training.check_gpu_driver_events(run_started_at)
        except training.NvidiaDriverEventError as event_error:
            gpu_post_failure = {"eventCount": len(event_error.events), "events": event_error.events}
        except Exception as monitor_error:
            gpu_post_failure = {"monitorFailure": type(monitor_error).__name__}
        failed = {"schema": EVALUATION_SCHEMA, "runId": run_id, "outcome": "FAILED",
                  "failedStage": stage, "failureReason": type(exc).__name__,
                  "identities": ({"prepareRunId": prepare_run_id, "trainingRunId": train_run_id,
                                  "prepareEvidenceSha256": source["evidenceSha256"]} if source else None),
                  "gpuDriverLogCheckAfterFailure": gpu_post_failure}
        failed_raw = canonical(failed)
        if not (target / "evaluation-evidence.json").exists():
            write_once(target / "evaluation-evidence.json", failed_raw, PAIR_ROOT)
        raise

