"""Recompute and freeze validation candidate lists from the verified retrieval exports."""

from collections import Counter
import subprocess
import sys

from scripts.code_analysis.contracts import LANGUAGES, canonical, digest
from scripts.code_analysis.representations import read_vectors
from scripts.code_retrieval import endpoints, index_verify, inputs, retrieval_eval

from .candidate_contract import (candidate_union, require_database_corroboration,
                                 require_metric_equality, require_same_orders, semantic_metrics)


def _query_vectors(binding, label):
    manifest_rows = binding["queryManifest"]["queries"]
    raw = binding["queryVectorsRaw"][label]
    count = sum(row["windows"] for row in manifest_rows)
    vectors = read_vectors(raw, count)
    result, cursor = {}, 0
    for row in manifest_rows:
        size = row["windows"]
        result[row["queryId"]] = vectors[cursor:cursor + size]
        cursor += size
    if cursor != len(vectors) or set(result) != {
            query["queryId"] for language in LANGUAGES for query in binding["validation"]["queries"][language]}:
        raise ValueError("query vectors do not cover the validation queries")
    return result


def _window_pairs(query_vectors, query_windows, indexed_rows, candidate_file_id, limit=3):
    candidate_rows = [row for row in indexed_rows if row["fileId"] == candidate_file_id]
    if not candidate_rows:
        raise ValueError("candidate file has no indexed windows")
    pairs = []
    for query_ordinal, query_vector in enumerate(query_vectors):
        for file_ordinal, row in enumerate(candidate_rows):
            score = sum(left * right for left, right in zip(query_vector, row["vector"]))
            pairs.append((float(score), query_ordinal, file_ordinal, row))
    pairs.sort(key=lambda item: (-item[0], item[1], item[2]))
    def bounds(window):
        spans = window.get("sourceTokenSpans")
        if not spans:
            return None
        return {"startByte": min(span["startByte"] for span in spans),
                "endByte": max(span["endByte"] for span in spans)}
    result = []
    for score, query_ordinal, file_ordinal, row in pairs[:limit]:
        query_start, query_end = query_windows[query_ordinal]["tokenStart"], query_windows[query_ordinal]["tokenEnd"]
        file_start, file_end = row.get("tokenStart"), row.get("tokenEnd")
        query_tokens, file_tokens = query_end - query_start, file_end - file_start
        pair_truncated = query_tokens > 254 or file_tokens > 253 or query_tokens + file_tokens > 507
        result.append({"cosine": score, "queryWindow": query_ordinal, "fileWindow": file_ordinal,
             "queryWindowCount": len(query_vectors), "fileWindowCount": len(candidate_rows),
             "queryTokenStart": query_start, "queryTokenEnd": query_end,
             "queryContentTokens": query_tokens, "fileContentTokens": file_tokens,
             "querySourceSpan": bounds(query_windows[query_ordinal]),
             "fileTokenStart": row.get("tokenStart"), "fileTokenEnd": row.get("tokenEnd"),
             "fileSourceSpan": bounds(row), "layoutTruncated": pair_truncated})
    return result


def candidate_relevance(candidate_ids, relevant_ids, scored_gallery, excluded_unknown):
    candidate_ids = list(candidate_ids)
    if len(candidate_ids) != len(set(candidate_ids)):
        raise ValueError("candidate relevance list contains duplicates")
    if not set(candidate_ids) <= set(scored_gallery) or set(candidate_ids) & set(excluded_unknown):
        raise ValueError("unknown or unscored file entered candidate relevance")
    relevant = set(relevant_ids)
    return {file_id: "POSITIVE" if file_id in relevant else "REVIEWED_DISTRACTOR"
            for file_id in candidate_ids}


def unknown_exclusions_per_model(counts):
    return {label: sum(value for (model, _, _), value in counts.items() if model == label)
            for label in ("E0", "E3")}


def build(binding, *, token_run_id, tokenizer):
    validation, corpus = binding["validation"], binding["corpus"]
    indexes, manifests = {}, {}
    for label in ("E0", "E3"):
        manifests[label], indexes[label] = index_verify.load_index(
            binding["chain"]["index"], label, validation, corpus,
            binding["export"]["models"][label]["modelKey"], binding["export"])

    protocol = inputs.parse_json(index_verify.PROTOCOL_PATH.read_bytes(), "evaluation protocol")
    creationflags = subprocess.CREATE_NO_WINDOW if sys.platform == "win32" else 0
    token = retrieval_eval.token_branch(token_run_id, validation, binding["loaded"]["sourcesRoot"], protocol,
                                        creationflags=creationflags)
    if token["status"] != "SUCCEEDED":
        raise ValueError("token branch is required for the validation candidate contract")
    expected = (binding["evaluation"].get("token") or {}).get("boundPairCount") or {}
    if token["boundPairCount"] != expected:
        raise ValueError("token branch per-language normalized bound-pair counts differ")

    model_rows = {label: [] for label in ("E0", "E3")}
    first_orders, second_orders = {}, {}
    vectors_by_model = {label: _query_vectors(binding, label) for label in ("E0", "E3")}
    for replay, destination in ((1, first_orders), (2, second_orders)):
        for label in ("E0", "E3"):
            destination[label] = {}
            for language in LANGUAGES:
                for query in validation["queries"][language]:
                    ranking = retrieval_eval.rank_query(
                        query, vectors_by_model[label][query["queryId"]], indexes[label],
                        token["pairs"], token["status"])
                    destination[label][query["queryId"]] = ranking["semantic"]
                    if replay == 1:
                        model_rows[label].append(retrieval_eval.score_query(query, ranking, token["status"]))
    require_same_orders(first_orders, second_orders)
    metrics = semantic_metrics(model_rows)
    require_metric_equality(metrics, binding["evaluation"])

    agreement_rows = (binding["agreement"].get("records") or {})
    database_summary = require_database_corroboration(agreement_rows)
    candidates, counts, truncation, truncation_pairs = [], Counter(), Counter(), Counter()
    candidate_pair_count, truncated_candidate_pair_count = Counter(), Counter()
    unknown_excluded, list_counts = Counter(), Counter()
    query_count = 0
    for label in ("E0", "E3"):
        for language in LANGUAGES:
            indexed = indexes[label][language]
            for query in validation["queries"][language]:
                query_count += 1
                semantic_ids = first_orders[label][query["queryId"]]
                from scripts.code_analysis.baselines import rank_files
                token_ids = [row["fileId"] for row in rank_files(
                    token["pairs"][language], query["queryId"], query["scoredGallery"], limit=20)]
                ids = candidate_union(semantic_ids, token_ids, query["scoredGallery"])
                relevance = candidate_relevance(ids, query["relevantFileIds"], query["scoredGallery"],
                                                query["excludedUnknownGalleryFiles"])
                query_windows = endpoints.endpoint_windows(query["endpoint"],
                                                           binding["loaded"]["sourcesRoot"], tokenizer)
                if len(query_windows) != len(vectors_by_model["E0"][query["queryId"]]):
                    raise ValueError("query window layout differs from the stored vector count")
                pairs = {file_id: _window_pairs(vectors_by_model["E0"][query["queryId"]], query_windows,
                                                indexes["E0"][language], file_id) for file_id in ids}
                stratum_key = (label, language, query["stratum"])
                candidate_pair_count[stratum_key] += len(ids)
                list_counts[stratum_key] += 1
                unknown_excluded[(label, language, query["stratum"])] += len(query["excludedUnknownGalleryFiles"])
                truncated_candidate_pair_count[stratum_key] += sum(
                    any(pair["layoutTruncated"] for pair in pairs[file_id]) for file_id in ids)
                for file_id in ids:
                    for pair in pairs[file_id]:
                        truncated = pair["layoutTruncated"]
                        truncation[(label, language)] += int(truncated)
                        truncation_pairs[(label, language)] += 1
                counts[(label, language, query["stratum"])] += len(ids)
                candidates.append({"model": label, "queryId": query["queryId"], "language": language,
                                   "stratum": query["stratum"], "seedGroup": query["seedGroup"],
                                   "scoredGalleryCount": len(query["scoredGallery"]),
                                   "semanticTop20": semantic_ids[:20], "tokenTop20": token_ids,
                                   "candidateFileIds": ids, "relevance": relevance,
                                   "windowPairs": pairs})

    database_summary = {key: value for key, value in database_summary.items()}
    output_directories = {"queryStaging": f"artifacts/code_analysis/runs/{token_run_id}/token-queries",
                          "toolOutputs": f"artifacts/feasibility_pilot/runs/{token_run_id}/dolos-<language>"}
    value = {"schema": "pair-verification-validation-candidates-v1",
             "candidateMode": "TOKEN_UNION",
             "provenance": {"semanticLists": "recomputed twice; orders identical",
                            "aggregateMetrics": "recomputed; aggregate-verified; not persisted by the earlier run",
                            "priorPerQueryOrdersPersisted": False,
                            "databaseCorroboration": database_summary,
                            "tokenBranch": {"status": token["status"],
                                            "reasons": token.get("reasons") or {},
                                            "boundPairCount": token["boundPairCount"],
                                            "rawOutputSha256": token["rawOutputSha256"],
                                            "outputDirectories": output_directories}},
             "queryCountPerModel": {label: 201 for label in ("E0", "E3")},
             "aggregateMetrics": metrics,
             "aggregateMetricTolerance": 1e-9,
             "aggregateMetricDenominator": 201,
             "countsPerModelLanguageStratum": {"/".join(key): count for key, count in sorted(counts.items())},
             "queryListsPerModelLanguageStratum": {
                 "/".join(key): count for key, count in sorted(list_counts.items())},
             "unknownExcludedPerModelLanguageStratum": {
                 "/".join(key): count for key, count in sorted(unknown_excluded.items())},
             "truncationWindowPairCount": {"/".join(key): count for key, count in sorted(truncation.items())},
             "truncationWindowPairSharePerModelLanguage": {
                 "/".join(key): truncation[key] / count for key, count in sorted(truncation_pairs.items())},
             "candidatePairCountPerModelLanguageStratum": {
                 "/".join(key): count for key, count in sorted(candidate_pair_count.items())},
             "truncatedCandidatePairCountPerModelLanguageStratum": {
                 "/".join(key): count for key, count in sorted(truncated_candidate_pair_count.items())},
             "truncatedCandidatePairSharePerModelLanguageStratum": {
                 "/".join(key): truncated_candidate_pair_count[key] / count
                 for key, count in sorted(candidate_pair_count.items())},
             "candidates": candidates}
    return value, canonical(value), {"token": token, "metrics": metrics,
                                     "unknownExcludedPerModel": unknown_exclusions_per_model(unknown_excluded),
                                     "outputDirectories": output_directories,
                                     "queryCount": query_count}
