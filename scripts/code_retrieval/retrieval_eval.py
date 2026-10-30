"""evaluate: semantic, token and union retrieval over the admitted validation plan, for E0..E3.

Runs in the training interpreter (CUDA). Verifies every export-index manifest (model keys,
index keys, file hashes) before any ranking, refuses a model whose identity differs from its
index, encodes queries with the same encode function as the index, and ranks each query only
over its own scored gallery:

- semantic: exhaustive file-level max-chunk cosine (evaluation.cpu_rank);
- token: Dolos bound pairs (baselines.token_stage), model independent, run once;
- union: top-20 token and top-20 semantic (baselines.union_candidates, budget 40), RRF separate.

Selection is one epoch by semantic-only language-equal seed-macro MRR@20 (tie Recall@20, then
earlier epoch); it writes selection.json and never edits a training file. A token branch that
cannot run is reported BLOCKED with its reason; semantic results are still delivered.
"""

import sys
import time
from collections import defaultdict

import torch

from scripts.code_analysis import baselines
from scripts.code_analysis.contracts import LANGUAGES, canonical, digest
from scripts.code_analysis.corpus import ARTIFACT_ROOT, write_once
from scripts.code_analysis.evaluation import (cpu_rank, freeze_protocol, language_equal_mean, query_metrics,
                                              seed_macro, timed_percentiles)
from scripts.code_analysis.representations import read_vectors
from scripts.feasibility_pilot.paths import ARTIFACT_ROOT as PILOT_ROOT
from scripts.feasibility_pilot.paths import ensure_inside

from . import (checkpoint, config as config_module, endpoints, environment_probe, index_export, inputs, prepare,
               retrieval_models, training, validation_plan)
from .paths import RETRIEVAL_ROOT, run_dir

EVAL_SCHEMA = "code-retrieval-evaluation-evidence-v1"
SELECTION_SCHEMA = "code-retrieval-selection-v1"
EVALUATION_EVIDENCE = "evaluation-evidence.json"
SELECTION_FILE = "selection.json"
MODEL_LABELS = ("E0", "E1", "E2", "E3")
TRAINED_EPOCHS = {"E1": 1, "E2": 2, "E3": 3}
WARMUP_QUERIES = 3
TIMED_MINIMUM = 30
DOLOS_TIMEOUT_SECONDS = 3600
RANK_LIMIT = 40
CUTOFFS = (5, 10, 20, 40)
CELL_BRANCHES = ("semantic", "token", "union", "rrf")
SELECTION_RULE = ("semantic-only language-equal seed-macro MRR@20 on validation; tie: language-equal "
                  "seed-macro Recall@20; then the earlier epoch")
SELECTION_SCOPE = "trained epochs only; the frozen model is not a selection candidate"
MEASUREMENT_SCOPE = {
    "queryEmbedding": "per query: endpoint tokenization, windowing and encoding; timed around encode, "
                      "CUDA synchronized before the stop",
    "queryRanking": "per query on CPU, pure Python: semantic cpu_rank and, when the token branch succeeded, "
                    "token rank_files, union_candidates and rrf_order; scoring is outside the timer",
}
STRATA = ("EXACT_COPY", "COMMENT_FORMAT", "AST_BOUND_RENAME")
TIE_TOLERANCE = 1e-12
PRIMARY = "PRIMARY"


def _protocol():
    protocol = inputs.parse_json(index_export.PROTOCOL_PATH.read_bytes(), "evaluation protocol")
    return protocol, freeze_protocol(protocol)["sha256"]


def _corpus(loaded, config_loaded, plan, prepare_binding):
    return {"inputsManifestSha256": loaded["manifestSha256"], "prepareRunId": prepare_binding["runId"],
            "validationReportSha256": plan["reportSha256"], "protocolSha256": _protocol()[1],
            "configSha256": config_loaded["configSha256"]}


def require_export_evidence(export_evidence, *, index_run_id, train_run_id, expectations, corpus):
    """Refuse indexes whose export evidence does not describe this index run, training run and corpus."""
    if export_evidence.get("schema") != index_export.EXPORT_SCHEMA or export_evidence.get("runId") != index_run_id:
        raise ValueError("export evidence schema or run id mismatch")
    if export_evidence.get("outcome") != "SUCCEEDED":
        raise ValueError("index export did not succeed; evaluation refuses its indexes")
    identity = export_evidence.get("identity") or {}
    if identity.get("trainRunId") != train_run_id or identity.get("trainingEvidenceSha256") != expectations["evidenceSha256"]:
        raise ValueError("export evidence was written for another training run")
    for key, value in corpus.items():
        if identity.get(key) != value:
            raise ValueError("export evidence identity drift: " + key)


def load_index(index_run, label, plan, corpus, model_key_value, export_evidence):
    """Verify one index: export evidence, model identity, index and gallery keys, file sizes and digests."""
    directory = run_dir(index_run) / index_export.INDEX_DIR / label
    manifest = inputs.parse_json(inputs.read_bytes(directory / index_export.MANIFEST_NAME, inputs.MAX_ARTIFACT_BYTES),
                                 "index manifest")
    if manifest.get("schema") != index_export.INDEX_SCHEMA or manifest.get("label") != label:
        raise ValueError("index manifest schema or label mismatch: " + label)
    if manifest.get("partition") != "validation":
        raise ValueError("index is not a validation-partition index")
    exported = export_evidence["models"].get(label) or {}
    if manifest["modelKey"] != exported.get("modelKey") or manifest["languages"] != exported.get("languages"):
        raise ValueError("index manifest differs from the export evidence: " + label)
    if retrieval_models.model_key(manifest["identity"]) != manifest["modelKey"]:
        raise ValueError("index model key does not match its identity: " + label)
    if manifest["modelKey"] != model_key_value:
        raise ValueError("mixed identity: loaded model differs from the index model: " + label)
    if manifest["corpus"] != corpus:
        raise ValueError("index corpus identity differs from this evaluation: " + label)
    indexes = {}
    for language in LANGUAGES:
        info = manifest["languages"][language]
        gallery = plan["galleries"][language]
        if index_export.gallery_key(gallery) != info["galleryKey"]:
            raise ValueError("index gallery differs from the admitted gallery: " + label + "/" + language)
        if index_export.index_key(manifest["modelKey"], gallery, corpus) != info["indexKey"]:
            raise ValueError("index key does not recompute: " + label + "/" + language)
        raw = inputs.read_bytes(directory / (language + ".fp32le"), inputs.MAX_ARTIFACT_BYTES)
        if len(raw) != info["vectorBytes"] or digest(raw) != info["vectorSha256"]:
            raise ValueError("index vector bytes drift: " + label + "/" + language)
        chunks_raw = inputs.read_bytes(directory / (language + ".chunks.json"), inputs.MAX_ARTIFACT_BYTES)
        if len(chunks_raw) != info["chunkManifestBytes"] or digest(chunks_raw) != info["chunkManifestSha256"]:
            raise ValueError("index chunk manifest drift: " + label + "/" + language)
        chunk_rows = inputs.parse_json(chunks_raw, "chunk manifest")
        if len(chunk_rows) != info["rows"]:
            raise ValueError("index row count drift: " + label + "/" + language)
        vectors = read_vectors(raw, info["rows"])
        indexes[language] = [{"fileId": row["fileId"], "vector": vector}
                             for row, vector in zip(chunk_rows, vectors)]
    return manifest, indexes


def query_vectors_for(entry, plan, sources_root, device):
    """Every query encoded with the loaded model; per-query embed time measured with CUDA synchronized."""
    encoded = {}
    for language in LANGUAGES:
        for query in plan["queries"][language]:
            started = time.perf_counter()
            windows = endpoints.endpoint_windows(query["endpoint"], sources_root, entry["tokenizer"])
            vectors = retrieval_models.encode(entry["model"], entry["tokenizer"], windows, device)
            torch.cuda.synchronize(device)
            encoded[query["queryId"]] = {"vectors": vectors, "embedSeconds": time.perf_counter() - started,
                                         "chunks": len(windows)}
    return encoded


def token_branch(run_id, plan, sources_root, protocol):
    """Dolos token pairs per language, run once and shared by every model; BLOCKED with a reason on failure."""
    token = protocol["token"]
    pairs, raw_hashes, blocked, bound_counts = {}, {}, {}, {}
    for language in LANGUAGES:
        queries = plan["queries"][language]
        if not queries:
            pairs[language] = []
            continue
        staged = ensure_inside(ARTIFACT_ROOT / "runs" / run_id / "token-queries" / language, ARTIFACT_ROOT)
        files = [{"fileId": row["fileId"], "sha256": row["sha256"],
                  "absolutePath": str(sources_root / row["sourceId"] / row["path"])}
                 for row in plan["galleries"][language]]
        for query in queries:
            body = query["endpoint"]["body"]
            path = ensure_inside(staged / (query["queryId"] + ".bin"), ARTIFACT_ROOT)
            write_once(path, body, ARTIFACT_ROOT)
            files.append({"fileId": query["queryId"], "sha256": digest(body), "absolutePath": str(path)})
        result = baselines.token_stage(sources_root, language, files,
                                       PILOT_ROOT / "runs" / run_id / ("dolos-" + language),
                                       k=token["k"], window=token["window"], timeout_s=DOLOS_TIMEOUT_SECONDS)
        if result.get("status") == "SUCCEEDED":
            pairs[language] = result["boundPairs"]
            raw_hashes[language] = result.get("csvSha256")
            bound_counts[language] = len(result["boundPairs"])
        else:
            blocked[language] = result.get("reason") or "token branch failed"
            pairs[language] = []
    return {"status": "BLOCKED" if blocked else "SUCCEEDED", "reasons": blocked, "pairs": pairs,
            "rawOutputSha256": raw_hashes, "boundPairCount": bound_counts, "k": token["k"],
            "window": token["window"]}


def rank_query(query, vectors, indexed, token_pairs, token_status):
    """Semantic, token, union and RRF rankings for one query, over its own scored gallery only."""
    scored = set(query["scoredGallery"])
    rows = [row for row in indexed[query["language"]] if row["fileId"] in scored]
    semantic = [item["fileId"] for item in cpu_rank(vectors, rows, key="fileId", limit=RANK_LIMIT)]
    rankings = {"semantic": semantic}
    if token_status == "SUCCEEDED":
        token = [item["fileId"] for item in baselines.rank_files(token_pairs[query["language"]], query["queryId"],
                                                                 scored, limit=RANK_LIMIT)]
        rankings["token"] = token
        rankings["union"] = baselines.union_candidates(token[:20], semantic[:20], budget=RANK_LIMIT)
        rankings["rrf"] = baselines.rrf_order(token[:20], semantic[:20], constant=60, budget=RANK_LIMIT)
    return rankings


def score_query(query, rankings, token_status):
    if len(query["scoredGallery"]) <= max(CUTOFFS):
        raise ValueError("scored gallery must exceed the largest cutoff: " + query["queryId"])
    cells = {}
    false_positives = {}
    for branch, ranking in rankings.items():
        for k in CUTOFFS:
            cells[branch + "@" + str(k)] = query_metrics(ranking, query["relevantFileIds"], query["scoredGallery"],
                                                         k, corpus_minima_met=True)
        relevant = set(query["relevantFileIds"])
        false_positives[branch] = sum(1 for file_id in rankings[branch][:20] if file_id not in relevant)
    if token_status != "SUCCEEDED":
        for branch in ("token", "union", "rrf"):
            for k in CUTOFFS:
                cells[branch + "@" + str(k)] = {"status": "BLOCKED"}
    relevant = set(query["relevantFileIds"])
    first_relevant = next((position + 1 for position, file_id in enumerate(rankings["semantic"])
                           if file_id in relevant), None)
    return {"seedGroup": query["seedGroup"], "querySha256": query["querySha256"], "queryId": query["queryId"],
            "language": query["language"], "stratum": query["stratum"], "galleryCount": len(query["scoredGallery"]),
            "relevantCount": len(query["relevantFileIds"]),
            "unknownExcludedCount": len(query["excludedUnknownGalleryFiles"]),
            "semanticFirstRelevantRank": first_relevant,
            "falsePositivesTop20": false_positives, "cells": cells}


def cell_cases(records, cell):
    """Only PRIMARY cells count; diagnostic or blocked cells are skipped, never averaged in."""
    return [{"seedGroup": row["seedGroup"], "querySha256": row["querySha256"],
             "metrics": {"recall": row["cells"][cell]["recall"], "mrr": row["cells"][cell]["mrr"]}}
            for row in records if row["cells"][cell].get("status") == PRIMARY]


def aggregate(records, cells):
    """Per-language seed-macro, then language-equal overall; strata broken out the same way."""
    table = {"languages": {}, "overall": {}, "strata": {}}
    for cell in cells:
        per_language = {}
        for language in LANGUAGES:
            rows = [row for row in records if row["language"] == language]
            if not rows:
                continue
            cases = cell_cases(rows, cell)
            if not cases:
                per_language[language] = {"status": "BLOCKED"}
                continue
            macro = seed_macro(cases)
            per_language[language] = {"recall": macro["recall"], "mrr": macro["mrr"],
                                      "uniqueSeeds": macro["uniqueSeeds"],
                                      "uniqueQueryCases": macro["uniqueQueryCases"]}
        scored = [row for row in per_language.values() if "recall" in row]
        table["languages"][cell] = per_language
        if scored:
            overall = language_equal_mean(scored)
            table["overall"][cell] = {"recall": overall["recall"], "mrr": overall["mrr"],
                                      "languages": overall["languages"]}
        else:
            table["overall"][cell] = {"status": "BLOCKED"}
        for stratum in STRATA:
            by_language = []
            for language in LANGUAGES:
                rows = [row for row in records if row["stratum"] == stratum and row["language"] == language]
                cases = cell_cases(rows, cell)
                if cases:
                    macro = seed_macro(cases)
                    by_language.append({"recall": macro["recall"], "mrr": macro["mrr"]})
            if by_language:
                overall_stratum = language_equal_mean(by_language)
                table["strata"].setdefault(stratum, {})[cell] = {"recall": overall_stratum["recall"],
                                                                 "mrr": overall_stratum["mrr"],
                                                                 "languages": len(by_language)}
            else:
                table["strata"].setdefault(stratum, {})[cell] = {"status": "OMITTED"}
    return table


def _delta_stats(deltas):
    return {"seeds": len(deltas),
            "meanDifference": sum(deltas) / len(deltas) if deltas else None,
            "improved": sum(1 for d in deltas if d > TIE_TOLERANCE),
            "worse": sum(1 for d in deltas if d < -TIE_TOLERANCE),
            "equal": sum(1 for d in deltas if abs(d) <= TIE_TOLERANCE)}


def paired_deltas(records_by_label, cell_pairs, reference="E0"):
    """Seed-level differences E_n minus E0, overall and per language: mean, improved / worse / equal."""
    result = {}
    for label in MODEL_LABELS[1:]:
        for cell, metric in cell_pairs:
            per_seed = {}
            for language in LANGUAGES:
                seeds = defaultdict(lambda: {"model": [], "base": []})
                for model_label in (label, reference):
                    seen = set()
                    for row in records_by_label[model_label]:
                        if row["language"] != language or row["cells"][cell].get("status") != PRIMARY:
                            continue
                        key = (row["seedGroup"], row["querySha256"])
                        if key in seen:
                            continue
                        seen.add(key)
                        side = "model" if model_label == label else "base"
                        seeds[row["seedGroup"]][side].append(row["cells"][cell][metric])
                for seed, values in seeds.items():
                    if values["model"] and values["base"]:
                        per_seed[(language, seed)] = (sum(values["model"]) / len(values["model"])
                                                      - sum(values["base"]) / len(values["base"]))
            result.setdefault(label, {})[cell + "." + metric] = {
                **_delta_stats(list(per_seed.values())),
                "perLanguage": {language: _delta_stats([delta for (lang, _), delta in per_seed.items()
                                                        if lang == language])
                                for language in LANGUAGES}}
    return result


def saturation(records_by_label):
    """Semantic rank-1 and full-recall@20 query counts per language and overall, per model."""
    def tally(rows):
        queries = len(rows)
        rank_one = sum(1 for row in rows if row["semanticFirstRelevantRank"] == 1)
        full_recall = sum(1 for row in rows if row["cells"]["semantic@20"]["recall"] >= 1.0 - TIE_TOLERANCE)
        return {"queries": queries,
                "firstRelevantAtRankOne": rank_one,
                "firstRelevantAtRankOneShare": rank_one / queries if queries else None,
                "recallAt20One": full_recall,
                "recallAt20OneShare": full_recall / queries if queries else None}

    result = {}
    for label, records in records_by_label.items():
        result[label] = {"overall": tally(records),
                         "perLanguage": {language: tally([row for row in records if row["language"] == language])
                                         for language in LANGUAGES}}
    return result


def selection_record(run_id, scores, frozen, checkpoint_digests, chosen):
    """The rule's choice, with the choice's semantic@20 differences from the frozen model; never approved."""
    return {"schema": SELECTION_SCHEMA, "rule": SELECTION_RULE, "scope": SELECTION_SCOPE, "runId": run_id,
            "scores": scores, "choice": chosen, "status": "CHOSEN_BY_RULE", "approvedForUse": False,
            "comparedAgainstFrozen": {"model": chosen,
                                      "mrrDifference": scores[chosen]["mrr"] - frozen["mrr"],
                                      "recallDifference": scores[chosen]["recall"] - frozen["recall"]},
            "checkpointManifestSha256": checkpoint_digests, "trainingManifestsEdited": False}


def choose_epoch(scores):
    """Highest MRR@20, then Recall@20, each compared within TIE_TOLERANCE; a tie keeps the earlier epoch."""
    chosen = None
    for label in sorted(scores):
        if chosen is None:
            chosen = label
            continue
        best, row = scores[chosen], scores[label]
        mrr_gap = row["mrr"] - best["mrr"]
        if mrr_gap > TIE_TOLERANCE or (abs(mrr_gap) <= TIE_TOLERANCE and row["recall"] - best["recall"] > TIE_TOLERANCE):
            chosen = label
    return chosen


def run(*, run_id, inputs_path, config_path, prepare_run_id, train_run_id, index_run_id, reference_probe_path):
    loaded = inputs.load(inputs_path)
    config_loaded = config_module.load(config_path)
    index = prepare.reviewed_index(loaded)
    prepare_binding, triplets, prepare_payload = training.admitted_binding(loaded, config_loaded, prepare_run_id, index)
    expectations = checkpoint.load_training_evidence(run_dir(train_run_id))
    checkpoint.require_training_binding(expectations, loaded=loaded, config_loaded=config_loaded,
                                        prepare_binding=prepare_binding)
    plan = validation_plan.build(loaded, index, prepare_payload["validationQueries"])
    reference_raw = inputs.read_bytes(reference_probe_path, inputs.MAX_ARTIFACT_BYTES)
    reference_probe = inputs.parse_json(reference_raw, "reference probe")
    live_probe = environment_probe.build_probe(triplets, loaded["sourcesRoot"])
    comparison = environment_probe.compare(reference_probe, live_probe)
    export_evidence = inputs.parse_json(
        inputs.read_bytes(run_dir(index_run_id) / index_export.EXPORT_EVIDENCE, inputs.MAX_ARTIFACT_BYTES),
        "export evidence")
    corpus = _corpus(loaded, config_loaded, plan, prepare_binding)
    require_export_evidence(export_evidence, index_run_id=index_run_id, train_run_id=train_run_id,
                            expectations=expectations, corpus=corpus)
    device = checkpoint.cuda_device()
    checkpoint.configure_numerics()
    target = run_dir(run_id)
    target.mkdir(parents=True, exist_ok=False)
    return _evaluate(run_id=run_id, target=target, loaded=loaded, plan=plan, prepare_binding=prepare_binding,
                     expectations=expectations, train_run_id=train_run_id, index_run_id=index_run_id,
                     reference_raw=reference_raw, reference_probe=reference_probe, live_probe=live_probe,
                     comparison=comparison, export_evidence=export_evidence, corpus=corpus, device=device)


def _evaluate(*, run_id, target, loaded, plan, prepare_binding, expectations, train_run_id, index_run_id,
              reference_raw, reference_probe, live_probe, comparison, export_evidence, corpus, device):
    started = time.monotonic()
    protocol = _protocol()[0]
    sources_root = loaded["sourcesRoot"]
    results, failure, token, indexes_seen, measurements = {}, None, None, {}, {}
    selection = None
    try:
        write_once(target / index_export.REFERENCE_PROBE, reference_raw, RETRIEVAL_ROOT)
        write_once(target / index_export.LIVE_PROBE, canonical(live_probe), RETRIEVAL_ROOT)
        token = token_branch(run_id, plan, sources_root, protocol)
        for label in MODEL_LABELS:
            entry = retrieval_models.frozen_model(device) if label == "E0" else \
                retrieval_models.trained_model(run_dir(train_run_id), TRAINED_EPOCHS[label], expectations, device)
            model_key_value = retrieval_models.model_key(entry["identity"])
            manifest, indexed = load_index(index_run_id, label, plan, corpus, model_key_value, export_evidence)
            indexes_seen[label] = {"modelKey": model_key_value,
                                   "indexKeys": {language: manifest["languages"][language]["indexKey"]
                                                 for language in LANGUAGES}}
            torch.cuda.reset_peak_memory_stats(device)
            encoded = query_vectors_for(entry, plan, sources_root, device)
            records, rank_seconds = [], []
            for position, query in enumerate(q for language in LANGUAGES for q in plan["queries"][language]):
                ranking_started = time.perf_counter()
                rankings = rank_query(query, encoded[query["queryId"]]["vectors"], indexed, token["pairs"],
                                      token["status"])
                rank_seconds.append(time.perf_counter() - ranking_started)
                record = score_query(query, rankings, token["status"])
                record["embedSeconds"] = encoded[query["queryId"]]["embedSeconds"]
                record["position"] = position
                records.append(record)
            cells = sorted({name for row in records for name in row["cells"]})
            results[label] = {"records": records, "table": aggregate(records, cells)}
            timed = slice(WARMUP_QUERIES, None)
            embed = [row["embedSeconds"] for row in records[timed]]
            rank = rank_seconds[WARMUP_QUERIES:]
            measurements[label] = {
                "queryEmbedding": timed_percentiles(embed) if len(embed) >= TIMED_MINIMUM else None,
                "queryRanking": timed_percentiles(rank) if len(rank) >= TIMED_MINIMUM else None,
                "timedQueries": len(embed), "warmupExcluded": WARMUP_QUERIES, "device": {
                    "embedding": str(device), "ranking": "cpu"},
                "peakAllocatedVRAM": torch.cuda.max_memory_allocated(device),
                "peakReservedVRAM": torch.cuda.max_memory_reserved(device),
                "workingSet": training.process_working_set(),
                "indexBytes": sum(manifest["languages"][language]["vectorBytes"]
                                  + manifest["languages"][language]["chunkManifestBytes"] for language in LANGUAGES)}
            if any(value is None for value in (measurements[label]["queryEmbedding"],
                                              measurements[label]["queryRanking"])):
                raise ValueError("fewer than {} timed queries after warmup for {}".format(TIMED_MINIMUM, label))
            del entry
            torch.cuda.empty_cache()
        scores = {label: {"mrr": results[label]["table"]["overall"]["semantic@20"]["mrr"],
                          "recall": results[label]["table"]["overall"]["semantic@20"]["recall"]}
                  for label in TRAINED_EPOCHS}
        chosen = choose_epoch({label: scores[label] for label in TRAINED_EPOCHS})
        checkpoint_digests = {label: expectations["epochs"][str(TRAINED_EPOCHS[label])]["checkpointManifestSha256"]
                              for label in TRAINED_EPOCHS}
        selection = selection_record(run_id, scores, results["E0"]["table"]["overall"]["semantic@20"],
                                     checkpoint_digests, chosen)
        write_once(target / SELECTION_FILE, canonical(selection), RETRIEVAL_ROOT)
    except Exception as exc:  # evaluation evidence records every failed run
        failure = ("FAILED_ERROR", type(exc).__name__ + ": " + str(exc))
    outcome = "SUCCEEDED" if failure is None else failure[0]
    evidence = {"schema": EVAL_SCHEMA, "runId": run_id, "outcome": outcome,
                "reason": None if failure is None else failure[1], "Purpose": "CODE_RETRIEVAL_VALIDATION_EVALUATION",
                "CanAuthorizePublication": False, "SecurityEvidence": "NOT_RUN",
                "identity": {**corpus, "prepare": prepare_binding, "trainRunId": train_run_id,
                             "trainingEvidenceSha256": expectations["evidenceSha256"],
                             "indexRunId": index_run_id, "indexExportOutcome": export_evidence["outcome"],
                             "indexes": indexes_seen},
                "referenceProbe": {"referenceSha256": digest(reference_raw), "comparison": comparison},
                "token": None if token is None else {key: token[key] for key in (
                    "status", "reasons", "rawOutputSha256", "boundPairCount", "k", "window")},
                "tables": {label: results[label]["table"] for label in results},
                "denominators": {label: {"queries": len(results[label]["records"]),
                                         "galleryCounts": _range([row["galleryCount"] for row in results[label]["records"]]),
                                         "relevantCounts": _range([row["relevantCount"] for row in results[label]["records"]]),
                                         "unknownExcluded": sum(row["unknownExcludedCount"] for row in results[label]["records"])}
                                  for label in results},
                "falsePositivesTop20": {label: _sum_fp(results[label]["records"]) for label in results},
                "pairedDeltas": paired_deltas({label: results[label]["records"] for label in results},
                                              [("semantic@20", "recall"), ("semantic@20", "mrr"),
                                               ("union@40", "recall")]) if len(results) == len(MODEL_LABELS) else None,
                "saturation": saturation({label: results[label]["records"] for label in results}),
                "selection": selection,
                "measurementScope": MEASUREMENT_SCOPE,
                "measurements": measurements,
                "environment": {"python": sys.executable, "torch": torch.__version__, **training.device_facts(device)},
                "elapsedSeconds": round(time.monotonic() - started, 3)}
    write_once(target / EVALUATION_EVIDENCE, canonical(evidence), RETRIEVAL_ROOT)
    return {"command": "evaluate", "runId": run_id, "outcome": outcome, "directory": str(target)}


def _range(values):
    return {"min": min(values), "max": max(values), "mean": sum(values) / len(values)} if values else None


def _sum_fp(records):
    totals = defaultdict(int)
    for row in records:
        for branch, count in row["falsePositivesTop20"].items():
            totals[branch] += count
    return dict(totals)
