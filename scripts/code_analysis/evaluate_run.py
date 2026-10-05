"""CPU evaluation against frozen vectors; final-test is never scored."""

import json
import time
from collections import defaultdict
from pathlib import Path

from scripts.feasibility_pilot.paths import ensure_inside

from .baselines import exact_copy_query_id, identity_sha_diagnostic_rank, rank_files, rrf_order, union_candidates
from .contracts import LANGUAGES, canonical, digest, report
from .corpus import ARTIFACT_ROOT
from .evaluation import cpu_rank, language_equal_mean, query_metrics, require_scoring_partition, seed_macro, timed_percentiles
from .fragment_labels import query_relevance, relation_index
from .representations import read_vectors
from .frozen_encoder import independently_accepted
from .reuse import verify_vector_artifacts
from .validator import REQUIRED_STRATA


def load_gallery_vectors(parent):
    parent = ensure_inside(Path(parent), ARTIFACT_ROOT)
    chunks = [json.loads(line) for line in (parent / "representations/chunks.jsonl").read_text(encoding="utf-8").splitlines() if line]
    manifest = json.loads((parent / "representations/manifest.json").read_bytes())
    vectors = read_vectors((parent / "representations/frozen-vectors.fp32le").read_bytes(), manifest["chunks"])
    rows = [{"fileId": chunk["fileId"], "fragmentId": chunk["fragmentId"], "chunkOrdinal": chunk["chunkOrdinal"],
             "partition": chunk["partition"], "language": chunk["language"], "vector": vectors[index]}
            for index, chunk in enumerate(chunks)]
    return rows, manifest


def scoring_gallery_file_ids(extract, *, partition, language):
    require_scoring_partition(partition)
    ids = []
    seen = set()
    for row in extract["files"]:
        if row.get("partition") != partition or row.get("language") != language:
            continue
        if not row.get("searchableExecutable") or row.get("generated"):
            continue
        file_id = row["fileId"]
        if file_id in seen:
            raise ValueError("duplicate scoring gallery file identity")
        seen.add(file_id)
        ids.append(file_id)
    return ids


def scoring_gallery_rows(extract, vector_rows, *, partition, language, allowed_file_ids):
    require_scoring_partition(partition)
    files = {row["fileId"]: row for row in extract["files"]}
    allowed = set(allowed_file_ids)
    selected = []
    for row in vector_rows:
        parent = files.get(row["fileId"])
        if parent is None:
            continue
        if row.get("partition") == "final-test" or parent.get("partition") == "final-test":
            continue
        if row.get("partition") != partition or parent.get("partition") != partition:
            continue
        if row.get("language") != language or parent.get("language") != language:
            continue
        if row["fileId"] not in allowed:
            continue
        selected.append(row)
    return selected


def evaluate_exact_copy(extract, labels, vector_rows, *, partition, corpus_minima_met):
    require_scoring_partition(partition)
    files = {f["fileId"]: f for f in extract["files"]}
    gallery_rows = [row for row in vector_rows if row["partition"] == partition]
    by_language = defaultdict(list)
    timings = []
    for seed in labels["seeds"]:
        query = files.get(seed["seedFileId"])
        if not query or query["partition"] != partition:
            continue
        query_vectors = [row["vector"] for row in gallery_rows if row["fileId"] == query["fileId"]]
        if not query_vectors:
            continue
        started = time.perf_counter()
        language_gallery = scoring_gallery_file_ids(extract, partition=partition, language=query["language"])
        ranking = cpu_rank(query_vectors, scoring_gallery_rows(
            extract, gallery_rows, partition=partition, language=query["language"],
            allowed_file_ids=language_gallery), key="fileId", limit=40)
        timings.append(time.perf_counter() - started)
        ranked_ids = [item["fileId"] for item in ranking]
        metrics20 = query_metrics(ranked_ids, seed["relevantFileIds"], language_gallery, 20, corpus_minima_met=corpus_minima_met)
        metrics5 = query_metrics(ranked_ids, seed["relevantFileIds"], language_gallery, 5, corpus_minima_met=corpus_minima_met)
        metrics10 = query_metrics(ranked_ids, seed["relevantFileIds"], language_gallery, 10, corpus_minima_met=corpus_minima_met)
        by_language[query["language"]].append({"seedGroup": query["duplicateGroup"], "querySha256": query["sha256"],
                                               "metrics": metrics20, "recall5": metrics5, "recall10": metrics10,
                                               "candidateCount": metrics20["candidateCount"]})
    if not by_language:
        raise ValueError("evaluation requires scored queries, gallery vectors, and reviewed relevance")
    languages = []
    for language, cases in sorted(by_language.items()):
        primary = seed_macro([{"seedGroup": c["seedGroup"], "querySha256": c["querySha256"],
                               "metrics": {"recall": c["metrics"]["recall"], "mrr": c["metrics"]["mrr"]}} for c in cases])
        k5 = seed_macro([{"seedGroup": c["seedGroup"], "querySha256": c["querySha256"],
                          "metrics": {"recall": c["recall5"]["recall"], "mrr": c["recall5"]["mrr"]}} for c in cases])
        k10 = seed_macro([{"seedGroup": c["seedGroup"], "querySha256": c["querySha256"],
                           "metrics": {"recall": c["recall10"]["recall"], "mrr": c["recall10"]["mrr"]}} for c in cases])
        languages.append({"language": language, "recall": primary["recall"], "mrr": primary["mrr"],
                          "recall5": k5["recall"], "recall10": k10["recall"], "recall20": primary["recall"],
                          "mrr20": primary["mrr"], "uniqueSeeds": primary["uniqueSeeds"],
                          "candidateCountMean": sum(c["candidateCount"] for c in cases) / len(cases)})
    latency = timed_percentiles(timings[1:]) if len(timings) > 30 else {"status": "INSUFFICIENT_TIMED_QUERIES", "count": max(0, len(timings) - 1)}
    return report({"partition": partition, "finalTestScored": False, "corpusMinimaMet": corpus_minima_met,
                   "primaryStatus": "PRIMARY" if corpus_minima_met else "DIAGNOSTIC_INSUFFICIENT_CORPUS",
                   "languages": languages, "equalLanguage": language_equal_mean(languages),
                   "latency": latency, "candidateUnit": "source-file",
                   "method": "exact-copy-query-reuses-identical-original-vectors",
                   "identityBaselineOnly": True,
                   "transformationStrataStatus": "OMITTED_NOT_REEMBEDDED",
                   "scoredTransformationStrata": ["EXACT_COPY"],
                   "transformationOmissions": ["COMMENT_FORMAT", "AST_BOUND_RENAME"]})


def evaluate_query_strata(extract, gallery_rows, query_records, *, partition, corpus_minima_met):
    require_scoring_partition(partition)
    files = {row["fileId"]: row for row in extract["files"]}
    strata = {}
    for stratum in REQUIRED_STRATA:
        cases = [row for row in query_records if row.get("stratum") == stratum and row.get("partition") == partition]
        if not cases:
            status = "GALLERY_IDENTITY_ONLY" if stratum == "EXACT_COPY" else "OMITTED_AWAITING_ACCEPTANCE"
            strata[stratum] = {"status": status, "queryVectors": 0, "queryCount": 0, "languages": [],
                               "omission": "no independently accepted query embeddings for this stratum"}
            continue
        by_language = defaultdict(list)
        for query in cases:
            if not query.get("vectors"):
                raise ValueError("query stratum missing bound vectors")
            language = query["language"]
            if query.get("partition") != partition:
                raise ValueError("query partition escaped scoring set")
            parent = files.get(query.get("parentFileId") or query.get("fileId"))
            if parent is None or parent.get("partition") != partition:
                raise ValueError("query parent partition escaped scoring set")
            if parent.get("language") != language:
                raise ValueError("query parent language mismatch")
            language_gallery = scoring_gallery_file_ids(extract, partition=partition, language=language)
            if not set(query["relevantFileIds"]) <= set(language_gallery):
                raise ValueError("relevance subset escaped scoring gallery")
            ranking = cpu_rank(query["vectors"], scoring_gallery_rows(
                extract, gallery_rows, partition=partition, language=language, allowed_file_ids=language_gallery),
                               key="fileId", limit=40)
            ranked_ids = [item["fileId"] for item in ranking]
            metrics = {k: query_metrics(ranked_ids, query["relevantFileIds"], language_gallery, k,
                                        corpus_minima_met=corpus_minima_met) for k in (5, 10, 20)}
            by_language[language].append({
                "seedGroup": query.get("seedGroup") or (parent or {}).get("duplicateGroup"),
                "querySha256": query["querySha256"], "queryId": query["queryId"],
                "vectorCount": len(query["vectors"]), "metrics": metrics[20],
                "recall5": metrics[5], "recall10": metrics[10],
            })
        languages = []
        for language, rows in sorted(by_language.items()):
            primary = seed_macro([{"seedGroup": c["seedGroup"], "querySha256": c["querySha256"],
                                   "metrics": {"recall": c["metrics"]["recall"], "mrr": c["metrics"]["mrr"]}} for c in rows])
            k5 = seed_macro([{"seedGroup": c["seedGroup"], "querySha256": c["querySha256"],
                              "metrics": {"recall": c["recall5"]["recall"], "mrr": c["recall5"]["mrr"]}} for c in rows])
            k10 = seed_macro([{"seedGroup": c["seedGroup"], "querySha256": c["querySha256"],
                               "metrics": {"recall": c["recall10"]["recall"], "mrr": c["recall10"]["mrr"]}} for c in rows])
            languages.append({"language": language, "recall": primary["recall"], "mrr": primary["mrr"],
                              "recall5": k5["recall"], "recall10": k10["recall"],
                              "uniqueSeeds": primary["uniqueSeeds"], "queryCount": len(rows),
                              "queryVectorCount": sum(c["vectorCount"] for c in rows)})
        strata[stratum] = {"status": "SCORED", "queryVectors": sum(len(c["vectors"]) for c in cases),
                           "queryCount": len(cases), "languages": languages}
    scored = [name for name, row in strata.items() if row["status"] == "SCORED"]
    omitted = [name for name, row in strata.items() if row["status"] != "SCORED"]
    return report({"partition": partition, "finalTestScored": False, "corpusMinimaMet": corpus_minima_met,
                   "queryEmbeddingsExecuted": bool(query_records), "identityBaselineDoesNotCloseStrata": True,
                   "identityBaselineOnly": scored != list(REQUIRED_STRATA) or not all(
                       (strata[name].get("status") == "SCORED" and strata[name].get("queryVectors"))
                       for name in REQUIRED_STRATA),
                   "transformationStrataStatus": "SCORED" if scored == list(REQUIRED_STRATA) else "PARTIAL_OR_OMITTED",
                   "scoredTransformationStrata": scored, "transformationOmissions": omitted, "strata": strata})


def combine_partition_evaluations(parts):
    coverage = {}
    omission_rows = []
    for partition in ("train", "validation"):
        payload = parts[partition]
        strata = payload.get("strata") or {}
        coverage[partition] = {}
        for name in REQUIRED_STRATA:
            row = strata.get(name) or {}
            languages = [item.get("language") for item in row.get("languages") or []]
            coverage[partition][name] = {
                "status": row.get("status"),
                "queryCount": row.get("queryCount") or 0,
                "queryVectors": row.get("queryVectors") or 0,
                "languages": languages,
            }
            if row.get("status") != "SCORED" or not row.get("queryVectors"):
                omission_rows.append({"partition": partition, "stratum": name,
                                      "status": row.get("status") or "OMITTED",
                                      "languages": languages})
            missing_languages = [item for item in LANGUAGES if item not in languages]
            if missing_languages:
                omission_rows.append({"partition": partition, "stratum": name,
                                      "status": "LANGUAGE_COVERAGE_INCOMPLETE",
                                      "languages": languages, "missingLanguages": missing_languages})
    scored = [name for name in REQUIRED_STRATA
              if all((parts[partition].get("strata") or {}).get(name, {}).get("status") == "SCORED"
                     and (parts[partition].get("strata") or {}).get(name, {}).get("queryVectors")
                     for partition in ("train", "validation"))]
    omitted = [name for name in REQUIRED_STRATA if name not in scored]
    language_complete = all(
        set((coverage[partition][name].get("languages") or [])) == set(LANGUAGES)
        and coverage[partition][name].get("status") == "SCORED"
        and coverage[partition][name].get("queryVectors")
        for partition in ("train", "validation") for name in REQUIRED_STRATA)
    return {
        "identityBaselineOnly": scored != list(REQUIRED_STRATA),
        "identityBaselineDoesNotCloseStrata": True,
        "transformationStrataStatus": "SCORED" if scored == list(REQUIRED_STRATA) else "PARTIAL_OR_OMITTED",
        "scoredTransformationStrata": scored,
        "transformationOmissions": omitted,
        "languagePartitionCoverage": coverage,
        "stratumOmissions": omission_rows,
        "allLanguagePartitionStrataComplete": language_complete,
        "queryEmbeddingsExecuted": any(parts[name].get("queryEmbeddingsExecuted") for name in parts),
    }


def evaluate_union(extract, labels, vector_rows, token_pairs, *, partition, corpus_minima_met):
    require_scoring_partition(partition)
    files = {f["fileId"]: f for f in extract["files"]}
    gallery_shas = {f["fileId"]: f["sha256"] for f in extract["files"]}
    by_language = defaultdict(list)
    for seed in labels["seeds"]:
        query = files.get(seed["seedFileId"])
        if not query or query["partition"] != partition:
            continue
        language_gallery = scoring_gallery_file_ids(extract, partition=partition, language=query["language"])
        query_vectors = [row["vector"] for row in vector_rows if row["fileId"] == query["fileId"] and row["partition"] == partition]
        if not query_vectors:
            continue
        query_id = exact_copy_query_id(query["fileId"])
        semantic = cpu_rank(query_vectors, scoring_gallery_rows(
            extract, vector_rows, partition=partition, language=query["language"], allowed_file_ids=language_gallery),
                            key="fileId", limit=40)
        token = rank_files(token_pairs, query_id, language_gallery, limit=40)
        token_ids = [row["fileId"] for row in token]
        semantic_ids = [row["fileId"] for row in semantic]
        if not set(token_ids) <= set(language_gallery):
            raise ValueError("token candidates escaped gallery")
        identity = identity_sha_diagnostic_rank(language_gallery, query_sha256=query["sha256"], gallery_shas=gallery_shas, limit=1)
        union_ids = union_candidates(token_ids[:20], semantic_ids[:20], budget=40)
        rrf_ids = rrf_order(token_ids[:20], semantic_ids[:20], constant=60, budget=40)
        relevant = seed["relevantFileIds"]

        def scored(ranking, k):
            ranked = [item for item in ranking if item in language_gallery]
            if not ranked:
                return {"recall": 0.0, "mrr": 0.0, "k": k, "galleryCount": len(language_gallery),
                        "relevantCount": len(set(relevant)), "candidateCount": 0, "status": "NO_TOOL_CANDIDATES"}
            return query_metrics(ranked, relevant, language_gallery, k, corpus_minima_met=corpus_minima_met)

        by_language[query["language"]].append({
            "seedGroup": query["duplicateGroup"], "querySha256": query["sha256"],
            "union40": scored(union_ids, 40), "union20": scored(union_ids, 20),
            "union10": scored(union_ids, 10), "union5": scored(union_ids, 5),
            "token40": scored(token_ids[:40], 40), "token20": scored(token_ids[:20], 20),
            "token10": scored(token_ids[:10], 10), "token5": scored(token_ids[:5], 5),
            "semantic40": scored(semantic_ids[:40], 40), "semantic20": scored(semantic_ids[:20], 20),
            "semantic10": scored(semantic_ids[:10], 10), "semantic5": scored(semantic_ids[:5], 5),
            "rrf40": scored(rrf_ids, 40), "rrf20": scored(rrf_ids, 20),
            "identityDiagnostic": [row["fileId"] for row in identity],
        })
    languages = []
    for language, cases in sorted(by_language.items()):
        def packed(field, rows=cases):
            return seed_macro([{"seedGroup": c["seedGroup"], "querySha256": c["querySha256"],
                                "metrics": {"recall": c[field]["recall"], "mrr": c[field]["mrr"]}} for c in rows])
        token20 = packed("token20")
        languages.append({"language": language,
                          "unionRecall40": packed("union40")["recall"], "tokenRecall40": packed("token40")["recall"],
                          "semanticRecall40": packed("semantic40")["recall"], "rrfRecall40Separate": packed("rrf40")["recall"],
                          "tokenRecall5": packed("token5")["recall"], "tokenRecall10": packed("token10")["recall"],
                          "tokenRecall20": token20["recall"], "tokenMrr20": token20["mrr"],
                          "semanticRecall5": packed("semantic5")["recall"], "semanticRecall10": packed("semantic10")["recall"],
                          "semanticRecall20": packed("semantic20")["recall"], "semanticMrr20": packed("semantic20")["mrr"],
                          "unionRecall5": packed("union5")["recall"], "unionRecall10": packed("union10")["recall"],
                          "unionRecall20": packed("union20")["recall"], "unionMrr20": packed("union20")["mrr"],
                          "uniqueSeeds": len({c["seedGroup"] for c in cases}),
                          "candidateCountMean": sum(c["token20"]["candidateCount"] for c in cases) / len(cases)})
    return report({"partition": partition, "finalTestScored": False, "corpusMinimaMet": corpus_minima_met,
                   "candidateUnion": {"tokenFiles": 20, "semanticFiles": 20, "maxUniqueFiles": 40, "rrfConstant": 60,
                                      "rrfOrderingReportedSeparately": True, "identityShaNotUsedInTokenBranch": True,
                                      "sameBudgetUnionControls": True},
                   "languages": languages, "method": "distinct-query-id Dolos token plus frozen semantic; RRF separate",
                   "transformationOmissions": ["COMMENT_FORMAT", "AST_BOUND_RENAME"]})


def evaluate_fragment_diagnostics(extract, galleries, vector_rows, *, corpus_minima_met, fragment_reviews=None,
                                 extra_fragments=()):
    require_scoring_partition("validation")
    files = {f["fileId"]: f for f in extract["files"]}
    fragments = {f["fragmentId"]: f for f in extract["fragments"]}
    reviews = fragment_reviews
    if reviews is None:
        reviews = []
    relations = relation_index(extract, reviews, extra_fragments=extra_fragments)
    reports = {}
    for language, ids in galleries.items():
        if len(ids) != len(set(ids)):
            raise ValueError("duplicate diagnostic gallery identity")
        units = [fragments[item] for item in ids]
        cases = []
        for fragment in units:
            relevant, gallery_ids, pending = query_relevance(fragment["fragmentId"], ids, relations)
            query_vectors = [row["vector"] for row in vector_rows if row["fragmentId"] == fragment["fragmentId"]]
            gallery_rows = [row for row in vector_rows if row["fragmentId"] in gallery_ids]
            ranking = cpu_rank(query_vectors, gallery_rows, key="fragmentId", limit=40)
            ranked = [item["fragmentId"] for item in ranking]
            metrics = {k: query_metrics(ranked, relevant, gallery_ids, k, corpus_minima_met=corpus_minima_met)
                       for k in (5, 10, 20)}
            status = "PENDING_INCOMPLETE_RELATIONS" if pending else metrics[20]["status"]
            cases.append({"seedGroup": files[fragment["fileId"]]["duplicateGroup"], "querySha256": fragment["sha256"],
                          "galleryCount": len(gallery_ids), "metrics": metrics[20], "recall5": metrics[5],
                          "recall10": metrics[10], "queryFragmentId": fragment["fragmentId"],
                          "relevantCount": len(relevant), "pending": pending, "status": status})
        reports[language] = {"queries": len(ids), "macro20": seed_macro(
            [{"seedGroup": c["seedGroup"], "querySha256": c["querySha256"], "metrics": c["metrics"]} for c in cases]),
                             "status": "DIAGNOSTIC_NARROW_GALLERY", "candidateUnit": "fragment",
                             "cases": [{"queryFragmentId": c["queryFragmentId"], "galleryCount": c["galleryCount"],
                                        "relevantCount": c["relevantCount"],
                                        "recall5": c["recall5"]["recall"], "recall10": c["recall10"]["recall"],
                                        "recall20": c["metrics"]["recall"], "mrr20": c["metrics"]["mrr"],
                                        "status": c["status"]} for c in cases]}
    return report({"partition": "validation", "finalTestScored": False, "languages": reports,
                   "missingLanguages": sorted({"javascript", "typescript", "python", "java", "csharp"} - set(galleries))})



def evaluate_root(root, *, partition="validation", vector_root=None, token=None, labels=None, corpus_minima_met=None):
    require_scoring_partition(partition)
    root = ensure_inside(Path(root), ARTIFACT_ROOT)
    extract_path = root / "extract.json"
    if not extract_path.exists():
        raise ValueError("evaluation requires extract.json")
    extract = json.loads(extract_path.read_bytes())
    labels = labels or json.loads((root / "materialize-labels.json").read_bytes())
    vector_root = Path(vector_root) if vector_root else root
    if not (vector_root / "representations/frozen-vectors.fp32le").exists():
        raise ValueError("evaluation requires frozen vectors; pass an explicit vector_root for reuse")
    frozen = json.loads((vector_root / "frozen.json").read_bytes()) if (vector_root / "frozen.json").exists() else None
    if frozen:
        verified = verify_vector_artifacts(vector_root, frozen)
        if verified.get("status") != "VERIFIED":
            raise ValueError(verified.get("reason") or "vector verification failed")
    else:
        verified = {"status": "UNVERIFIED_LOCAL_VECTORS"}
    token = token or json.loads((root / "token-baseline.json").read_bytes())
    if token.get("status") != "SUCCEEDED":
        raise ValueError("evaluation requires succeeded token baseline")
    rows, _manifest = load_gallery_vectors(vector_root)
    if corpus_minima_met is None:
        summary = root / "summarize.json"
        corpus_minima_met = summary.exists() and json.loads(summary.read_bytes()).get("status") == "FILE_COUNTS_READY"
    semantic = evaluate_exact_copy(extract, labels, rows, partition=partition, corpus_minima_met=corpus_minima_met)
    union = evaluate_union(extract, labels, rows, token.get("boundPairs") or [], partition=partition,
                           corpus_minima_met=corpus_minima_met)
    query_path = root / "query-embeddings.json"
    query_records = []
    if query_path.exists():
        bound = json.loads(query_path.read_bytes())
        if not independently_accepted(bound.get("acceptedDrafts") or {}):
            raise ValueError("query embeddings present without independent acceptance")
        query_records = bound.get("queries") or []
    strata = evaluate_query_strata(extract, rows, query_records, partition=partition,
                                   corpus_minima_met=corpus_minima_met)
    return report({**semantic, "union": union["languages"], "tokenStatus": token.get("status"),
                   "vectorVerification": verified.get("status"),
                   "candidateUnion": union.get("candidateUnion"),
                   "identityBaselineOnly": strata["identityBaselineOnly"],
                   "identityBaselineDoesNotCloseStrata": True,
                   "queryEmbeddingsExecuted": strata["queryEmbeddingsExecuted"],
                   "transformationStrataStatus": strata["transformationStrataStatus"],
                   "scoredTransformationStrata": strata["scoredTransformationStrata"],
                   "transformationOmissions": strata["transformationOmissions"],
                   "strata": strata["strata"]})
