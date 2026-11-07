"""Candidates stage: per-query candidate lists and emitted findings for every pipeline.

Encoding is isolated here so scoring stays pure CPU. Models run one after another
(frozen base, trained retrieval, then verifier) and are released between phases.
The stage writes one hashed, write-once artifact; any failure leaves failed-run
evidence and no artifact.
"""

import random
import time
from collections import defaultdict
from pathlib import Path

from scripts.code_analysis.contracts import canonical, digest
from scripts.code_retrieval import inputs
from scripts.pair_verification import evidence
from scripts.pair_verification.input_layout import layout_pair_ids, window_content
# The window-pair selection is the exact routine used to freeze the verifier's validation lists.
from scripts.pair_verification.validation_candidates import _window_pairs

from . import guards, manifest as manifest_module, pipelines, prepare, product_search, provenance, resources
from .components import release

CANDIDATES_SCHEMA = "model-evaluation-candidates-v1"
CANDIDATES_NAME = "candidates.json"
POOL_SYSTEMS = ("TOKEN", "E0", "E3")


def verify_bound_inputs(prepare_dir, manifest_sha256):
    """Frozen manifest (corpus bound) checked against the externally supplied hash, and the prepared corpus."""
    manifest, manifest_sha = prepare.read_manifest(prepare_dir, manifest_sha256)
    corpus = manifest["corpus"]
    if corpus["status"] != manifest_module.CORPUS_BOUND:
        raise ValueError("the freeze manifest does not bind a corpus")
    raw = (Path(prepare_dir) / prepare.PREPARED_NAME).read_bytes()
    if digest(raw) != corpus["identity"]["corpusManifestSha256"]:
        raise ValueError("prepared corpus differs from the manifest corpus identity")
    prepared = inputs.parse_json(raw, "prepared corpus")
    if canonical(prepared) != raw:
        raise ValueError("prepared corpus bytes are not canonical")
    if prepared["freezeManifestSha256"] != corpus["identity"]["previousManifestSha256"]:
        raise ValueError("prepared corpus is not bound to the previous freeze version")
    return manifest, manifest_sha, prepared, digest(raw)


def check_identities(manifest, identities):
    """Recorded or loaded identities against the frozen manifest; any difference is a refusal.

    Returns the device the run used: the three models must share one device from the frozen pair.
    """
    models, preprocessing = manifest["models"], manifest["preprocessing"]
    threshold = manifest["operatingPoints"]["validationThresholdRuleResult"]["threshold"]
    execution, devices = manifest["execution"], manifest["cpuGpuParity"]["devices"]
    for name in ("e0", "e3", "verifier"):
        identity = identities[name]
        if identity.get("stateSha256") != models[name]["stateSha256"]:
            raise ValueError("model state differs from the frozen manifest: " + name)
        if name == "verifier":
            if identity.get("threshold") != threshold:
                raise ValueError("verifier threshold differs from the frozen threshold")
        elif (identity.get("tokenizerHashes"), identity.get("chunking")) != (
                models[name]["tokenizerHashes"], preprocessing["chunking"]):
            raise ValueError("tokenizer files or chunking differ from the frozen manifest: " + name)
    used = {identities[name].get("device") for name in ("e0", "e3", "verifier")}
    if len(used) != 1 or not used <= set(devices):
        raise ValueError("device differs from the frozen device pair or between models")
    if identities["tokenScorer"] != {"dolosK": execution["dolosK"], "dolosWindow": execution["dolosWindow"]}:
        raise ValueError("token scorer differs from the frozen Dolos shape")
    return used.pop()


def component_identities(components):
    """Declared identities; reading them never loads a model."""
    return {"e0": components.e0.identity, "e3": components.e3.identity, "verifier": components.verifier.identity,
            "tokenScorer": getattr(components.token_scorer, "identity", None)}


def assert_identities(manifest, components):
    """The loaded components must be exactly what the frozen manifest names; nothing is encoded before this."""
    return check_identities(manifest, component_identities(components))


class Measure:
    """Add elapsed time to a result bucket and to the shared per-stage timer."""

    def __init__(self, timer):
        self.timer = timer

    def __call__(self, bucket, key, call, stage=None):
        started = self.timer.clock()
        try:
            return call()
        finally:
            elapsed = self.timer.clock() - started
            bucket[key] = bucket.get(key, 0.0) + elapsed
            self.timer.add(stage or key, elapsed)


def _index(encoder, files, store, timer, label):
    """Windows and vectors for every file, one encoder at a time; also the seconds spent per file."""
    index, seconds = {}, {}
    for row in files:
        raw = store.read(row["fileId"], row["sha256"])
        started = timer.clock()
        windows = timer.run(label + "Preprocessing", lambda: encoder.windows(raw, row))
        vectors = timer.run(label + "Encoding", lambda: encoder.embed(windows["chunks"]))
        index[row["fileId"]] = {"windows": windows, "vectors": vectors}
        seconds[row["fileId"]] = timer.clock() - started
    return index, seconds


def _semantic(query_index, gallery_ids, index, language_group, limit):
    """The product's semantic channel: per-fragment windows, service aggregation, in-memory exact search."""
    gallery = {fid: index[fid]["vectors"] for fid in gallery_ids}
    return product_search.semantic_rank(query_index["vectors"], gallery, language_group=language_group,
                                        limit=limit)


def pool_sample(pooling, query_id, gallery_ids):
    """Deterministic random recall sample for one query (string seed, sorted gallery)."""
    ordered = sorted(gallery_ids)
    generator = random.Random("%d:%s" % (pooling["randomSeed"], query_id))
    return sorted(generator.sample(ordered, min(pooling["randomRecallSampleSize"], len(ordered))))


def _verifier_scores(verifier, query_entry, candidates, index, gallery_pairs, timer):
    """Max positive probability over the highest-cosine base window pairs of each candidate."""
    scored = {}
    started = timer.clock()
    for row in candidates:
        file_entry = index[row["fileId"]]
        rows = [{"fileId": row["fileId"], "vector": vector, "tokenStart": window["tokenStart"],
                 "tokenEnd": window["tokenEnd"], "sourceTokenSpans": window.get("sourceTokenSpans")}
                for vector, window in zip(file_entry["vectors"], file_entry["windows"]["chunks"])]
        pairs = _window_pairs(query_entry["vectors"], query_entry["windows"]["chunks"], rows,
                              row["fileId"], limit=gallery_pairs)
        layouts = []
        for pair in pairs:
            q = query_entry["windows"]["chunks"][pair["queryWindow"]]
            f = file_entry["windows"]["chunks"][pair["fileWindow"]]
            layouts.append(layout_pair_ids(window_content(q), window_content(f), cls_id=q["inputIds"][0],
                                           mode_id=q["inputIds"][1], sep_id=q["inputIds"][2]))
        scored[row["fileId"]] = (pairs, layouts)
    timer.add("windowAlignment", timer.clock() - started)
    flat = [layout.input_ids for pairs, layouts in scored.values() for layout in layouts]
    probabilities = iter(timer.run("verifierInference", lambda: verifier.probabilities(flat)) if flat else [])
    result = {}
    for file_id, (pairs, layouts) in scored.items():
        values = [next(probabilities) for _ in layouts]
        result[file_id] = {"score": max(values) if values else None,
                           "pairs": [{"cosine": p["cosine"], "queryWindow": p["queryWindow"],
                                      "fileWindow": p["fileWindow"], "layoutTruncated": layout.truncated,
                                      "probability": value}
                                     for p, layout, value in zip(pairs, layouts, values)]}
    return result


def _compute(manifest, manifest_sha, prepared, prepared_sha, components, store, timer, capture):
    # authored inputs are scanned before anything is loaded or encoded
    guards.refuse_sealed(manifest=manifest)
    guards.refuse_sealed(manifest=component_identities(components))
    device = assert_identities(manifest, components)
    budget, points, pooling = manifest["candidateBudget"], manifest["operatingPoints"], manifest["pooling"]
    if pooling["depthK"] > min(budget["tokenTop"], budget["semanticTop"]):
        raise ValueError("pool depth exceeds the retained channel depth")
    files = {row["fileId"]: row for row in prepared["files"]}
    queries = prepared["queries"]
    by_language = defaultdict(list)
    for query in queries:
        by_language[query["language"]].append(query)
    per_query = {query["queryId"]: 0.0 for query in queries}

    # Phase 1: token pair scores per language (CPU, external tool); one sample per query (batch share).
    token_pairs = {}
    for language, language_queries in by_language.items():
        gallery = [row for row in files.values() if row["kind"] == "gallery" and row["language"] == language]
        wanted = gallery + [files[q["queryFileId"]] for q in language_queries]
        batch = [{"fileId": row["fileId"], "sha256": row["sha256"],
                  "raw": store.read(row["fileId"], row["sha256"])} for row in wanted]
        started = timer.clock()
        token_pairs[language] = components.token_scorer.pair_scores(language, batch)
        share = (timer.clock() - started) / len(language_queries)
        for query in language_queries:
            per_query[query["queryId"]] += share
            timer.add("tokenScoring", share)

    needed = [row for row in files.values()
              if row["kind"] == "gallery" and row["language"] in by_language] + [
        files[q["queryFileId"]] for q in queries]
    # Phase 2 and 3: one encoder at a time; each is released as soon as its stage ends.
    e0_index, e0_seconds = _index(components.e0, needed, store, timer, "base")
    release(components.e0)
    e3_index, e3_seconds = _index(components.e3, needed, store, timer, "trained")
    release(components.e3)

    result_queries = {}
    verifier_inputs = {}
    measure = Measure(timer)
    for query in queries:
        language = query["language"]
        gallery_ids = sorted(fid for fid, row in files.items()
                             if row["kind"] == "gallery" and row["language"] == language)
        qid = query["queryFileId"]
        stage = {}
        token_ranked = measure(stage, "tokenRanking", lambda: pipelines.rank_token(
            token_pairs[language], qid, gallery_ids, limit=len(gallery_ids)))
        semantic_e0 = measure(stage, "baseRanking", lambda: _semantic(
            e0_index[qid], gallery_ids, e0_index, language, budget["semanticTop"]))
        semantic_e3 = measure(stage, "trainedRanking", lambda: _semantic(
            e3_index[qid], gallery_ids, e3_index, language, budget["semanticTop"]))
        per_query[query["queryId"]] += e0_seconds[qid] + e3_seconds[qid] + sum(stage.values())
        shape = dict(token_top=budget["tokenTop"], semantic_top=budget["semanticTop"],
                     union_budget=budget["maxCandidates"])
        product = pipelines.build_candidates(token_ranked, semantic_e0, **shape)
        tuned = pipelines.build_candidates(token_ranked, semantic_e3, **shape)
        cut = points["tokenRankCut"]["rank"]
        control = [{"fileId": row["fileId"], "tokenRank": row["rank"], "tokenScore": row["score"]}
                   for row in token_ranked[:budget["tokenTop"]]]
        windows = {"queryWindows": len(e0_index[qid]["windows"]["chunks"]),
                   "queryOmittedTokens": e0_index[qid]["windows"]["omittedTokenCount"],
                   "queryOmittedFragments": e0_index[qid]["windows"].get("omittedFragmentCount", 0),
                   "galleryOmittedTokens": sum(e0_index[fid]["windows"]["omittedTokenCount"]
                                               for fid in gallery_ids),
                   "galleryOmittedFragments": sum(e0_index[fid]["windows"].get("omittedFragmentCount", 0)
                                                  for fid in gallery_ids),
                   "galleryWindows": sum(len(e0_index[fid]["windows"]["chunks"]) for fid in gallery_ids)}
        depth = pooling["depthK"]
        pool = [{"system": "TOKEN", "fileIds": [row["fileId"] for row in token_ranked[:depth]]},
                {"system": "E0", "fileIds": [row["fileId"] for row in semantic_e0[:depth]]},
                {"system": "E3", "fileIds": [row["fileId"] for row in semantic_e3[:depth]]},
                {"system": "RANDOM_SAMPLE", "fileIds": pool_sample(pooling, query["queryId"], gallery_ids)}]
        result_queries[query["queryId"]] = {
            "windows": windows, "pool": pool, "stageSeconds": dict(stage),
            "pipelines": {
                pipelines.PRODUCT: {"candidates": product["candidates"], "counts": product["counts"],
                                    "findings": pipelines.product_findings(product["candidates"]),
                                    "timingsSeconds": {}},
                pipelines.TOKEN_RANK_CUT: {"candidates": control, "counts": {"tokenCandidates": len(token_ranked)},
                                           "findings": pipelines.token_rank_cut_findings(token_ranked, cut),
                                           "timingsSeconds": {}},
                pipelines.FINE_TUNED_RETRIEVAL: {
                    "candidates": tuned["candidates"], "counts": tuned["counts"],
                    "findings": pipelines.fine_tuned_findings(tuned["candidates"],
                                                              points["semanticRankCut"]["rank"]),
                    "timingsSeconds": {}},
                pipelines.FINE_TUNED_RETRIEVAL_PLUS_VERIFIER: {
                    "candidates": [dict(row) for row in tuned["candidates"]], "counts": tuned["counts"],
                    "findings": [], "timingsSeconds": {}}}}
        verifier_inputs[query["queryId"]] = (qid, result_queries[query["queryId"]]["pipelines"][
            pipelines.FINE_TUNED_RETRIEVAL_PLUS_VERIFIER])

    # Phase 4: verifier over the trained-retrieval candidates, aligned with base windows.
    threshold = points["validationThresholdRuleResult"]["threshold"]
    for query_id, (qid, record) in verifier_inputs.items():
        scores = measure(record["timingsSeconds"], "verifier", lambda: _verifier_scores(
            components.verifier, e0_index[qid], record["candidates"], e0_index, budget["verifierWindowPairs"],
            timer), stage="verifierTotal")
        for row in record["candidates"]:
            row["verifierScore"] = scores[row["fileId"]]["score"]
            row["windowPairs"] = scores[row["fileId"]]["pairs"]
        record["findings"] = pipelines.verifier_findings(record["candidates"], threshold)
        per_query[query_id] += record["timingsSeconds"]["verifier"]
    release(components.verifier)

    for query in queries:
        timer.add("endToEndQuery", per_query[query["queryId"]])
    return {"schema": CANDIDATES_SCHEMA, "device": device, "freezeManifestSha256": manifest_sha,
            "preparedCorpusSha256": prepared_sha, "pipelines": list(pipelines.PIPELINES),
            "modelIdentities": component_identities(components),
            "resources": resources.resource_record(timer, manifest["resourceProtocol"]),
            "codeIdentity": capture(), "queries": result_queries}


def run(*, run_id, prepare_run_id, manifest_sha256, components, store, run_root=guards.RUN_ROOT,
        clock=time.perf_counter, capture=provenance.capture):
    guards.refuse_sealed(ids=[run_id, prepare_run_id])
    target = guards.run_dir(run_id, run_root)
    manifest, manifest_sha, prepared, prepared_sha = verify_bound_inputs(
        guards.run_dir(prepare_run_id, run_root), manifest_sha256)
    try:
        timer = resources.StageTimer(clock, warmup=manifest["resourceProtocol"]["warmupQueries"])
        document = _compute(manifest, manifest_sha, prepared, prepared_sha, components, store, timer, capture)
        guards.refuse_sealed(manifest=document)
        evidence.validate(document)
        raw = canonical(document)
        guards.write_output(target / CANDIDATES_NAME, raw, run_root)
    except guards.SealedInputRefused:
        raise
    except FileExistsError:
        raise
    except Exception as error:
        raise guards.record_failure(target, run_root, "candidates", error) from error
    return {"outcome": "SUCCEEDED", "runId": run_id, "directory": target, "candidatesSha256": digest(raw)}
