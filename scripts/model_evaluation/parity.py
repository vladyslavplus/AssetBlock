"""Parity stage: exact comparison of two candidate artifacts on a fixed query sample.

Used for harness versus service-stage output and for CPU versus GPU encoding of the
frozen base encoder. Nothing is tuned: every disagreement is reported with its query
id and kind, and an empty sample fails closed.
"""

import random
from pathlib import Path

from scripts.code_analysis.contracts import canonical, digest
from scripts.code_retrieval import inputs
from scripts.pair_verification import evidence

from . import candidates as candidates_module, guards, pipelines, prepare

PARITY_SCHEMA = "model-evaluation-parity-v1"
PARITY_NAME = "parity-report.json"
MEMBERSHIP, TIE_ORDER, RANK_ORDER, COUNTS, MATERIAL = "MEMBERSHIP", "TIE_ORDER", "RANK_ORDER", "COUNTS", "MATERIAL"
SCORE_FIELDS = ("tokenScore", "semanticScore")


def _score_key(row):
    return (row.get("tokenScore"), row.get("semanticScore"))


def _compare_lists(left, right, top_k):
    a, b = left[:top_k], right[:top_k]
    ids_a, ids_b = [row["fileId"] for row in a], [row["fileId"] for row in b]
    if ids_a == ids_b:
        return []
    if set(ids_a) != set(ids_b):
        return [{"kind": MEMBERSHIP, "onlyLeft": sorted(set(ids_a) - set(ids_b)),
                 "onlyRight": sorted(set(ids_b) - set(ids_a))}]
    scores_a = {row["fileId"]: _score_key(row) for row in a}
    scores_b = {row["fileId"]: _score_key(row) for row in b}
    tied = all(scores_a[x] == scores_a[y] and scores_b[x] == scores_b[y]
               for x, y in zip(ids_a, ids_b) if x != y)
    return [{"kind": TIE_ORDER if tied else RANK_ORDER, "left": ids_a, "right": ids_b}]


def _compare_query(left, right, top_k):
    findings = _compare_lists(left["candidates"], right["candidates"], top_k)
    if left["counts"] != right["counts"]:
        findings.append({"kind": COUNTS, "left": left["counts"], "right": right["counts"]})
    material = {}
    right_by_id = {row["fileId"]: row for row in right["candidates"]}
    for row in left["candidates"]:
        other = right_by_id.get(row["fileId"])
        if other is not None and row.get("material") != other.get("material"):
            material[row["fileId"]] = {"left": row.get("material"), "right": other.get("material")}
    if material:
        findings.append({"kind": MATERIAL, "files": material})
    return findings


def compare(left, right, *, query_ids, pipeline_names=(pipelines.PRODUCT,), top_k=20):
    """Pure comparator over two parsed candidate artifacts."""
    if not query_ids or len(set(query_ids)) != len(query_ids):
        raise ValueError("parity needs a nonempty sample without duplicates")
    if type(top_k) is not int or top_k < 1:
        raise ValueError("top-K must be a positive integer")
    for key in ("freezeManifestSha256", "preparedCorpusSha256"):
        if left[key] != right[key]:
            raise ValueError("artifacts were generated under different " + key)
    disagreements, compared = [], 0
    mismatched = set()
    for name in pipeline_names:
        for query_id in query_ids:
            if query_id not in left["queries"] or query_id not in right["queries"]:
                raise ValueError("sample query missing from an artifact: " + query_id)
            compared += 1
            found = _compare_query(left["queries"][query_id]["pipelines"][name],
                                   right["queries"][query_id]["pipelines"][name], top_k)
            for item in found:
                disagreements.append({"queryId": query_id, "pipeline": name, **item})
            if found:
                mismatched.add((name, query_id))
    return {"schema": PARITY_SCHEMA, "topK": top_k, "pipelines": list(pipeline_names),
            "sampleSize": len(query_ids), "comparisons": compared, "exactMatches": compared - len(mismatched),
            "exactMatchRate": (compared - len(mismatched)) / compared, "disagreements": disagreements}


def check_devices(protocol, left, right):
    """Two artifacts of one manifest that differ only in the recorded device, one from each frozen device."""
    if {left["device"], right["device"]} != set(protocol["devices"]) or left["device"] == right["device"]:
        raise ValueError("parity needs one artifact from each frozen device")

    def without_device(document):
        return {name: {key: value for key, value in identity.items() if key != "device"}
                if isinstance(identity, dict) else identity for name, identity in document["modelIdentities"].items()}
    if without_device(left) != without_device(right):
        raise ValueError("artifacts differ in more than the recorded device")


def parity_sample(protocol, query_ids):
    """The frozen parity sample: ``sampleSize`` query ids drawn with the frozen seed from the prepared queries."""
    ordered = sorted(query_ids)
    if len(set(ordered)) != len(ordered) or len(ordered) < protocol["sampleSize"]:
        raise ValueError("the prepared corpus has fewer distinct queries than the frozen parity sample size")
    generator = random.Random("%d:parity" % protocol["seed"])
    return sorted(generator.sample(ordered, protocol["sampleSize"]))


def _artifact(run_root, run_id, expected_sha256, manifest_sha, prepared_sha):
    raw = (guards.run_dir(run_id, run_root) / candidates_module.CANDIDATES_NAME).read_bytes()
    if digest(raw) != expected_sha256:
        raise ValueError("candidate artifact differs from the supplied hash")
    document = inputs.parse_json(raw, "candidate artifact")
    if canonical(document) != raw or document.get("schema") != candidates_module.CANDIDATES_SCHEMA:
        raise ValueError("candidate artifact is not canonical or has an unexpected schema")
    if document["freezeManifestSha256"] != manifest_sha or document["preparedCorpusSha256"] != prepared_sha:
        raise ValueError("candidate artifact is not bound to the bound manifest and corpus")
    return document


def run(*, run_id, prepare_run_id, manifest_sha256, left_run_id, left_sha256, right_run_id, right_sha256,
        run_root=guards.RUN_ROOT):
    """Compare two candidate artifacts under the frozen ``cpuGpuParity`` protocol of the bound manifest.

    The sample, the depth and the pipeline come from the frozen protocol; nothing is chosen at the command line.
    """
    guards.refuse_sealed(ids=[run_id, prepare_run_id, left_run_id, right_run_id])
    target = guards.run_dir(run_id, run_root)
    prepare_dir = guards.run_dir(prepare_run_id, run_root)
    manifest, manifest_sha, prepared, prepared_sha = candidates_module.verify_bound_inputs(prepare_dir,
                                                                                         manifest_sha256)
    protocol = manifest["cpuGpuParity"]
    query_ids = parity_sample(protocol, [query["queryId"] for query in prepared["queries"]])
    guards.refuse_sealed(ids=query_ids)
    left = _artifact(run_root, left_run_id, left_sha256, manifest_sha, prepared_sha)
    right = _artifact(run_root, right_run_id, right_sha256, manifest_sha, prepared_sha)
    for artifact in (left, right):
        device = candidates_module.check_identities(manifest, artifact["modelIdentities"])
        if device != artifact["device"]:
            raise ValueError("candidate artifact device differs from its frozen component identities")
    check_devices(protocol, left, right)
    report = compare(left, right, query_ids=query_ids, pipeline_names=(protocol["pipeline"],),
                     top_k=protocol["topK"])
    report.update({"protocol": {"model": protocol["model"], "compares": list(protocol["compares"]),
                                "devices": {"left": left["device"], "right": right["device"]},
                                "onDisagreement": protocol["onDisagreement"], "seed": protocol["seed"],
                                "queryIds": query_ids},
                   "limitationReported": bool(report["disagreements"]), "freezeManifestSha256": manifest_sha,
                   "preparedCorpusSha256": prepared_sha})
    report.update({"runId": run_id, "left": {"runId": left_run_id, "sha256": left_sha256},
                   "right": {"runId": right_run_id, "sha256": right_sha256}})
    evidence.validate(report)
    raw = canonical(report)
    guards.write_output(Path(target) / PARITY_NAME, raw, run_root)
    return {"outcome": "SUCCEEDED", "runId": run_id, "directory": target, "reportSha256": digest(raw),
            "exactMatchRate": report["exactMatchRate"], "disagreements": len(report["disagreements"])}
