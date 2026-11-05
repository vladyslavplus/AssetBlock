"""Score stage: pure CPU metrics for every pipeline from the frozen manifest.

Gates run in this order before anything is computed: manifest hash gate, bound
corpus, candidate artifact hash, identities and pooling, corpus-keyed single-run registry. A
failure after registration leaves failed-run evidence and no result.
"""


from scripts.code_analysis.contracts import canonical, digest, sha
from scripts.code_retrieval import inputs
from scripts.pair_verification import evidence

from . import (candidates as candidates_module, guards, manifest as manifest_module, metrics, pipelines,
               prepare as prepare_module, provenance)

RESULT_SCHEMA = "model-evaluation-result-v1"
RESULT_NAME = "score-result.json"
MAX_CANDIDATES_BYTES = 512 * 1024 * 1024


def _read_candidates(path, expected_sha256, manifest_sha, prepared_sha):
    raw = inputs.read_bytes(path, MAX_CANDIDATES_BYTES)
    if digest(raw) != sha(expected_sha256):
        raise ValueError("candidate artifact differs from the supplied hash")
    document = inputs.parse_json(raw, "candidate artifact")
    if canonical(document) != raw or document.get("schema") != candidates_module.CANDIDATES_SCHEMA:
        raise ValueError("candidate artifact is not canonical or has an unexpected schema")
    if document["freezeManifestSha256"] != manifest_sha or document["preparedCorpusSha256"] != prepared_sha:
        raise ValueError("candidate artifact is not bound to this freeze manifest and corpus")
    if document["pipelines"] != list(pipelines.PIPELINES):
        raise ValueError("candidate artifact pipelines differ from the contract")
    return document


def check_pooling(manifest, prepared, document):
    """Pooling rule at scoring: every file in the pooled top-K of TOKEN, E0 and E3 must be judged.

    A file counts as judged when its label is not UNKNOWN, or when an explicit UNKNOWN carries pooling
    provenance. Under the union rule the provenance must name every system whose top-K holds the file.
    """
    pooling, problems = manifest["pooling"], []
    for query in prepared["queries"]:
        pool = {row["system"]: row["fileIds"] for row in document["queries"][query["queryId"]]["pool"]}
        gallery = set(query["judgments"])
        wanted = gallery if pooling["rule"] == "EXHAUSTIVE" else {
            file_id for system in candidates_module.POOL_SYSTEMS for file_id in pool[system]}
        for file_id in sorted(wanted):
            provenance = set(query["judgments"][file_id]["provenance"])
            surfaced = {system for system in candidates_module.POOL_SYSTEMS if file_id in pool[system]}
            if not provenance or (pooling["rule"] != "EXHAUSTIVE" and not surfaced <= provenance):
                problems.append("%s:%s" % (query["queryId"], file_id))
        sample = pool.get("RANDOM_SAMPLE", [])
        if sample != candidates_module.pool_sample(pooling, query["queryId"], gallery):
            problems.append("%s:RANDOM_SAMPLE:not-the-frozen-sample" % query["queryId"])
        for file_id in sorted(sample):
            entry = query["judgments"].get(file_id, {"provenance": []})
            if "RANDOM_SAMPLE" not in entry["provenance"]:
                problems.append("%s:%s:RANDOM_SAMPLE" % (query["queryId"], file_id))
    if problems:
        raise ValueError("pooled files are not judged with full provenance: " + ", ".join(problems))


def _pipeline_queries(prepared, document, pipeline):
    if set(document["queries"]) != {query["queryId"] for query in prepared["queries"]}:
        raise ValueError("candidate artifact does not cover the prepared queries")
    records = []
    for query in prepared["queries"]:
        record = {key: query[key] for key in ("queryId", "language", "family", "seedGroup", "stratum")}
        if query["dialect"] is not None:
            record["dialect"] = query["dialect"]
        record["judgments"] = {fid: entry["label"] for fid, entry in query["judgments"].items()}
        entry = document["queries"][query["queryId"]]["pipelines"][pipeline]
        record["findings"] = list(entry["findings"])
        record["ranking"] = [row["fileId"] for row in entry["candidates"]]
        records.append(record)
    return metrics.normalize_queries(records)


def _compute(manifest, prepared, document):
    metrics.check_registration(manifest["metrics"], manifest["aggregation"])
    required = manifest["replacementBaselines"]
    bootstrap = manifest["bootstrap"]
    shared = {"resamples": bootstrap["resamples"], "seed": bootstrap["seed"],
              "confidence": bootstrap["confidence"]}
    queries = {name: _pipeline_queries(prepared, document, name) for name in pipelines.PIPELINES}
    summaries = {name: metrics.summarize(rows, bootstrap=shared) for name, rows in queries.items()}
    result = {}
    for name in pipelines.PIPELINES:
        if name in required:
            baselines = deltas = None
        else:
            baselines = {base: summaries[base] for base in required}
            deltas = {base: metrics.paired_bootstrap_delta(queries[name], queries[base], **shared)
                      for base in required}
        result[name] = {
            "summary": summaries[name],
            "claimGates": metrics.evaluate_claim_gates(summaries[name], gates=manifest["claimGates"],
                                                       minimum_counts=manifest["minimumCounts"],
                                                       baselines=baselines, paired_deltas=deltas,
                                                       required_baselines=required),
            "pairedDeltaVsProduct": None if name == pipelines.PRODUCT else metrics.paired_bootstrap_delta(
                queries[name], queries[pipelines.PRODUCT], **shared),
            "retrievalRanking": metrics.ranking_summary(queries[name], bootstrap=shared),
            "retrievalRankingPairedDeltaVsProduct": None if name == pipelines.PRODUCT else
            metrics.paired_bootstrap_delta(queries[name], queries[pipelines.PRODUCT],
                                           statistics=metrics.ranking_statistics, **shared)}
    return result


NOTES = (
    "Emission rules differ by pipeline. A rank cut has no abstention, so it emits a finding on no-match queries "
    "and its spurious-alert rate is a measured property of the rule, not a defect of the encoder.",
    "Retrieval-ranking metrics use the ordered candidate lists only, with the full-gallery recall denominator "
    "and UNKNOWN files removed; they do not depend on any emission rule.",
    "Claim-gate outcomes follow the frozen numeric rule. Reviewer acceptance is a separate status and is not derived.",
)


def three_statuses(pipeline_results):
    """Run completion, numeric claim-gate outcome and reviewer acceptance are independent facts."""
    return {"runCompleted": True,
            "claimGateOutcome": {name: value["claimGates"]["overall"] for name, value in pipeline_results.items()},
            "reviewerAcceptance": "NOT_RECORDED"}


def build_outcome(*, run_id, manifest_sha, prepared_sha, candidates_sha256, registration, pipeline_results,
                  code_identity):
    return {"schema": RESULT_SCHEMA, "runId": run_id, "freezeManifestSha256": manifest_sha,
            "preparedCorpusSha256": prepared_sha, "candidatesSha256": candidates_sha256,
            "registration": registration, "statuses": three_statuses(pipeline_results),
            "notes": list(NOTES), "codeIdentity": code_identity, "pipelines": pipeline_results}


def run(*, run_id, prepare_run_id, candidates_run_id, manifest_sha256, candidates_sha256,
        run_root=guards.RUN_ROOT, registry_root=guards.REGISTRY_ROOT, rerun_of=None, reason=None,
        capture=provenance.capture):
    guards.refuse_sealed(ids=[run_id, prepare_run_id, candidates_run_id])
    target = guards.run_dir(run_id, run_root)
    prepare_dir = guards.run_dir(prepare_run_id, run_root)
    manifest_path = prepare_dir / manifest_module.MANIFEST_NAME
    manifest = guards.verify_manifest_bytes(manifest_path.read_bytes(), manifest_sha256)
    manifest_module.validate_manifest(manifest)
    _, manifest_sha, prepared, prepared_sha = candidates_module.verify_bound_inputs(prepare_dir, manifest_sha256)
    document = _read_candidates(guards.run_dir(candidates_run_id, run_root) / candidates_module.CANDIDATES_NAME,
                                candidates_sha256, manifest_sha, prepared_sha)
    if candidates_module.check_identities(manifest, document["modelIdentities"]) != document["device"]:
        raise ValueError("candidate artifact records another device than its model identities")
    check_pooling(manifest, prepared, document)
    triples = guards.model_triples(manifest["models"])
    registration = guards.register_scoring_run(manifest_sha256=manifest_sha,
                                               key_sha256=guards.model_key(manifest["models"]),
                                               package_triple_sha256=triples["packageTripleSha256"],
                                               state_triple_sha256=triples["stateTripleSha256"],
                                               corpus_identity_sha256=prepare_module.corpus_identity_sha256(prepared),
                                               label_sha256=prepare_module.label_sha256(prepared), run_id=run_id, root=registry_root, rerun_of=rerun_of, reason=reason,
                                               allowed_reasons=tuple(manifest["rerunRule"]["allowedReasons"]),
                                               max_free_runs=manifest["rerunRule"]["maxFreeScoringRuns"])
    try:
        pipeline_results = _compute(manifest, prepared, document)
        outcome = build_outcome(run_id=run_id, manifest_sha=manifest_sha, prepared_sha=prepared_sha,
                                candidates_sha256=candidates_sha256, registration=registration,
                                pipeline_results=pipeline_results, code_identity=capture())
        evidence.validate(outcome)
        raw = canonical(outcome)
        guards.write_output(target / RESULT_NAME, raw, run_root)
    except (FileExistsError, guards.SealedInputRefused):
        raise
    except Exception as error:
        raise guards.record_failure(target, run_root, "score", error) from error
    return {"outcome": "SUCCEEDED", "runId": run_id, "directory": target, "resultSha256": digest(raw)}
