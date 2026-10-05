"""Aggregate payload/identity/completeness validator; file existence is never sufficient."""

import json
from pathlib import Path

from scripts.feasibility_pilot.paths import ensure_inside

from .code_index import PORT
from .contracts import LANGUAGES, canonical, digest, report, sha
from .corpus import ARTIFACT_ROOT
from .evaluation import freeze_protocol, require_scoring_partition
from .finite_labels import FROZEN_PROTOCOL_SHA, RUBRIC
from .fragment_labels import diagnostic_map_complete, directed_relation_map, reviewed_fragment_pair
from .reuse import verify_reuse_receipt


REQUIRED_STAGE_OUTPUTS = {
    "intake": ("intake.json",),
    "split": ("split.json",),
    "extract": ("extract.json",),
    "export": ("export.json",),
    "summarize": ("summarize.json",),
    "frozen": ("frozen.json",),
    "token-baseline": ("token-baseline.json",),
    "evaluate": ("evaluate.json",),
    "license-review": ("license-review.json", "scancode.json"),
    "index-check": ("index-check.json",),
    "seal-final-test": ("seal-final-test.json",),
}

REQUIRED_METRICS = ("recall5", "recall10", "recall20", "mrr20")
REQUIRED_TOKEN_METRICS = ("tokenRecall5", "tokenRecall10", "tokenRecall20", "tokenMrr20")
REQUIRED_STRATA = ("EXACT_COPY", "COMMENT_FORMAT", "AST_BOUND_RENAME")


def _looks_like_sha(value):
    try:
        sha(value)
        return True
    except (TypeError, ValueError):
        return False


def _units(extract, fragment_export):
    files = {row["fileId"]: row for row in extract.get("files") or []}
    fragments = {row["fragmentId"]: row for row in extract.get("fragments") or []}
    for row in (fragment_export or {}).get("extraFiles") or []:
        files[row["fileId"]] = row
    for row in (fragment_export or {}).get("extraFragments") or []:
        fragments[row["fragmentId"]] = row
    return files, fragments


def _hash_bound(pair, fragments, label):
    return reviewed_fragment_pair(pair, fragments, label)


def _triplet_eligible(item, files, fragments):
    if item.get("rubric") != RUBRIC or item.get("partition") != "train":
        return False
    if item.get("status") not in (None, "REVIEWED"):
        return False
    if not item.get("reviewer") or not item.get("rationale"):
        return False
    language = item.get("language")
    query = fragments.get(item.get("queryFragmentId"))
    if language not in LANGUAGES or query is None:
        return False
    if item.get("querySha256") and query.get("sha256") != item.get("querySha256"):
        return False
    parent = files.get(query.get("fileId"))
    if not parent or parent.get("language") != language or parent.get("partition") != "train":
        return False
    positive = item.get("positive") or {}
    if not _hash_bound(positive, fragments, "SIMILAR"):
        return False
    if positive.get("leftFragmentId") not in (item.get("queryFragmentId"), query["fragmentId"]):
        return False
    original = fragments.get(positive.get("rightFragmentId"))
    original_parent = files.get(original["fileId"]) if original else None
    if not original_parent or original_parent.get("language") != language or original_parent.get("partition") != "train":
        return False
    negatives = item.get("negatives") or []
    right_ids = []
    for row in negatives:
        if not _hash_bound(row, fragments, "DISSIMILAR"):
            return False
        if row.get("leftFragmentId") not in (item.get("queryFragmentId"), query["fragmentId"]):
            return False
        right = fragments.get(row.get("rightFragmentId"))
        parent = files.get(right["fileId"]) if right else None
        if not parent or parent.get("language") != language or parent.get("partition") != "train":
            return False
        right_ids.append(right["fragmentId"])
    if len(right_ids) < 2 or len(set(right_ids)) < 2:
        return False
    return True


def _diagnostic_complete(language, ids, files, fragments, reviews):
    if len(ids) < 10 or len(ids) != len(set(ids)):
        return False
    for fragment_id in ids:
        fragment = fragments.get(fragment_id)
        parent = files.get(fragment["fileId"]) if fragment else None
        if not fragment or not parent:
            return False
        if parent.get("language") != language or parent.get("partition") != "validation":
            return False
    try:
        directed = directed_relation_map(fragments, reviews, gallery_ids=ids)
    except ValueError:
        return False
    return diagnostic_map_complete(ids, directed)


STAGE_PAYLOAD_KEYS = {
    "extract": "extract",
    "frozen": "frozen",
    "evaluate": "evaluation",
    "token-baseline": "token",
    "license-review": "scancode",
    "export": "export",
    "summarize": "sufficiency",
    "index-check": "index_check",
    "seal-final-test": "seal",
}


def _payload_digest(payload):
    return digest(canonical(payload))


def _is_binding_receipt(payload):
    return isinstance(payload, dict) and payload.get("status") == "REUSED" and payload.get("parentOutput") and payload.get("parentSha256")


def load_bound_output(root, output_name, expected_sha):
    artifact = ensure_inside(Path(root) / output_name, ARTIFACT_ROOT)
    if not artifact.is_file():
        raise ValueError("missing stage output " + output_name)
    raw = artifact.read_bytes()
    if digest(raw) != expected_sha:
        raise ValueError("stage output hash drift: " + output_name)
    try:
        payload = json.loads(raw)
    except (ValueError, TypeError):
        raise ValueError("stage output is not JSON: " + output_name)
    if _is_binding_receipt(payload):
        verify_reuse_receipt(payload, output_name)
        parent = ensure_inside(ARTIFACT_ROOT / "runs" / payload["parentRunId"] / payload["parentOutput"], ARTIFACT_ROOT)
        parent_raw = parent.read_bytes()
        if digest(parent_raw) != payload["parentSha256"]:
            raise ValueError("reuse parent output drift: " + output_name)
        outcome = json.loads(parent_raw)
        return {"kind": "receipt", "payload": outcome, "receipt": payload, "digest": _payload_digest(outcome)}
    return {"kind": "outcome", "payload": payload, "digest": _payload_digest(payload)}


def resolve_stage_outputs(root, stage_records=None):
    gaps = []
    resolved = {}
    records = dict(stage_records or {})
    root = Path(root)
    for name, outputs in REQUIRED_STAGE_OUTPUTS.items():
        record = records.get(name)
        path = root / (name + "-stage.json")
        if record is None and path.is_file():
            record = json.loads(path.read_bytes())
            records[name] = record
        if not record or record.get("ActualOutcome") != "SUCCEEDED":
            gaps.append("required stage " + name + " missing or not SUCCEEDED")
            continue
        bound = record.get("outputs") or {}
        chosen = outputs
        if name == "license-review":
            present = [item for item in outputs if item in bound]
            if not present:
                gaps.append("license-review missing output binding")
                continue
            chosen = tuple(present)
        loaded = None
        for output in chosen:
            expected = bound.get(output)
            if not _looks_like_sha(expected):
                gaps.append(name + " missing hash-bound output " + output)
                continue
            try:
                loaded = load_bound_output(root, output, expected)
            except ValueError as exc:
                gaps.append(str(exc))
                continue
        if loaded is None:
            continue
        if name == "index-check" and loaded["payload"].get("status") != "READY_VERIFIED":
            gaps.append("index-check outcome is not READY_VERIFIED")
        key = STAGE_PAYLOAD_KEYS.get(name)
        if key:
            resolved[key] = loaded
    return resolved, gaps


def _bind_resolved(name, provided, resolved, gaps):
    item = resolved.get(name)
    if item is None:
        return provided
    payload = item["payload"]
    if provided is not None and _payload_digest(provided) != item["digest"]:
        gaps.append(name + " payload digest drift from resolved stage output")
    return payload


def _metric_rows_ok(rows, fields):
    if not rows:
        return False
    return all(all(isinstance(row.get(field), (int, float)) for field in fields) for row in rows)


def _index_evidence_gaps(index_check, frozen):
    if index_check.get("status") != "READY_VERIFIED":
        return ["live index evidence is not READY_VERIFIED"]
    if not frozen:
        return ["live index missing frozen binding"]
    if index_check.get("modelKey") != frozen.get("modelKey"):
        return ["live index ModelKey binding drift"]
    expected = {row["partition"]: row["indexKey"] for row in frozen.get("partitions") or []}
    found = {name: row.get("indexKey") for name, row in (index_check.get("partitions") or {}).items()}
    keys = index_check.get("indexKeys") or found
    if not expected or keys != expected or found != expected:
        return ["live index IndexKey binding drift"]
    return []


def validate_run(root, *, protocol, labels, extract=None, frozen=None, evaluation=None, token=None, scancode=None,
                 export=None, seal=None, sufficiency=None, fragment_export=None, vector_verification=None,
                 index_check=None, stage_records=None):
    gaps = []
    root = Path(root)
    protocol_hash = freeze_protocol(protocol)["sha256"]
    resolved, stage_gaps = resolve_stage_outputs(root, stage_records)
    gaps.extend(stage_gaps)
    extract = _bind_resolved("extract", extract, resolved, gaps)
    frozen = _bind_resolved("frozen", frozen, resolved, gaps)
    evaluation = _bind_resolved("evaluation", evaluation, resolved, gaps)
    token = _bind_resolved("token", token, resolved, gaps)
    scancode = _bind_resolved("scancode", scancode, resolved, gaps)
    export = _bind_resolved("export", export, resolved, gaps)
    seal = _bind_resolved("seal", seal, resolved, gaps)
    sufficiency = _bind_resolved("sufficiency", sufficiency, resolved, gaps)
    index_check = _bind_resolved("index_check", index_check, resolved, gaps)
    if fragment_export is not None and export is not None and _payload_digest(fragment_export) != _payload_digest(export):
        gaps.append("fragment_export payload digest drift from resolved export")
    export_payload = export or {}
    if protocol_hash != FROZEN_PROTOCOL_SHA:
        gaps.append("protocol digest drift from frozen methodology")
    if protocol.get("finalTestScoringAllowed") is not False:
        gaps.append("final-test scoring must remain prohibited")
    if frozen is None:
        gaps.append("missing frozen payload")
    else:
        vector_hash = frozen.get("fullVectorSha256")
        if not _looks_like_sha(vector_hash):
            gaps.append("frozen full-vector digest missing")
        if frozen.get("finalTestScored") is not False:
            gaps.append("frozen stage scored final-test")
        if frozen.get("noOptimizerCreated") is not True:
            gaps.append("optimizer created during frozen baseline")
        if frozen.get("status") not in (None, "REUSED", "RECORDED", "SUCCEEDED"):
            gaps.append("frozen stage outcome is not successful")
        if vector_verification:
            if vector_verification.get("status") != "VERIFIED":
                gaps.append("frozen vector/manifest artifacts failed hash verification")
            elif vector_verification.get("fullVectorSha256") != vector_hash:
                gaps.append("frozen vector verification digest drift")
        else:
            gaps.append("frozen vector/manifest artifacts were not verified")
    if evaluation is None:
        gaps.append("missing evaluation payload")
    else:
        try:
            require_scoring_partition(evaluation.get("partition", "validation"))
        except ValueError:
            gaps.append("evaluation partition is not a permitted scoring partition")
        if evaluation.get("finalTestScored") is not False:
            gaps.append("evaluation scored final-test")
        if not evaluation.get("languages") or "equalLanguage" not in evaluation:
            gaps.append("evaluation lacks computed language metrics")
        if evaluation.get("method") in (None, "") or "summarize" in str(evaluation.get("method", "")).lower():
            if not evaluation.get("candidateUnit"):
                gaps.append("evaluation is summarize-only or missing ranking method")
        if not _metric_rows_ok(evaluation.get("languages") or [], REQUIRED_METRICS):
            gaps.append("evaluation missing frozen Recall@5/10/20 or MRR@20")
        union = evaluation.get("union") or []
        if not _metric_rows_ok(union, REQUIRED_TOKEN_METRICS):
            gaps.append("evaluation missing token Recall@5/10/20 or MRR@20")
        partitions = evaluation.get("partitions") or {}
        if "train" not in partitions or "validation" not in partitions:
            gaps.append("evaluation missing train or validation partition")
        if evaluation.get("partitionOmissions"):
            gaps.append("evaluation retained partition omissions")
        scored = set(evaluation.get("scoredTransformationStrata") or [])
        if evaluation.get("identityBaselineOnly") is not False or evaluation.get("transformationStrataStatus") != "SCORED":
            gaps.append("evaluation identity-only; transformation strata omitted")
        elif set(REQUIRED_STRATA) - scored:
            gaps.append("evaluation missing mandatory transformation strata")
        actual_minima = (sufficiency or {}).get("status") == "FILE_COUNTS_READY"
        if evaluation.get("corpusMinimaMet") is not actual_minima:
            gaps.append("evaluation corpusMinimaMet does not match sufficiency evidence")
        if evaluation.get("primaryStatus") == "PRIMARY" and not actual_minima:
            gaps.append("primary metrics emitted without corpus minima")
        latency = evaluation.get("latency") or {}
        if evaluation.get("primaryStatus") == "PRIMARY" and latency.get("count", 0) < 30:
            gaps.append("primary evaluation lacks 30 timed queries after warmup")
    if token is None or token.get("status") != "SUCCEEDED":
        gaps.append("token baseline k=23/w=17 not succeeded")
    elif token.get("k") != 23 or token.get("window") != 17:
        gaps.append("main token baseline k/w drift")
    elif not token.get("boundPairs") and not token.get("boundPairCount"):
        gaps.append("token baseline missing bound pairs")
    if scancode is None or scancode.get("outcome") != "SUCCEEDED" or scancode.get("status") == "FAILED":
        gaps.append("ScanCode license review did not succeed")
    elif scancode.get("coverageComplete") is not True:
        gaps.append("ScanCode coverage of required sources/notices is incomplete")
    files, fragments = _units(extract or {}, export_payload)
    if export is None or export.get("status") not in ("READY", "READY_WITH_GAPS"):
        gaps.append("missing or empty supervised export payload")
    else:
        triplets = export_payload.get("trainTriplets") or export.get("trainTriplets") or export.get("triplets") or []
        eligible = {item.get("language") for item in triplets if _triplet_eligible(item, files, fragments)}
        if eligible != set(LANGUAGES):
            gaps.append("train fragment triplets missing independently reviewed languages")
    diagnostics = export_payload.get("diagnosticGalleries") or labels.get("diagnosticGalleries") or {}
    reviews = list(export_payload.get("pairReviews") or labels.get("pairReviews") or [])
    complete = True
    for language in LANGUAGES:
        if not _diagnostic_complete(language, diagnostics.get(language) or [], files, fragments, reviews):
            complete = False
    if not complete:
        gaps.append("fragment diagnostic galleries missing languages")
    if not index_check:
        gaps.append("live index evidence is not READY_VERIFIED")
    else:
        gaps.extend(_index_evidence_gaps(index_check, frozen))
    if seal is None or seal.get("scored") is not False:
        gaps.append("missing or scored final-test seal")
    elif not seal.get("seedIdentities") or not seal.get("querySha256s") or not seal.get("relevance"):
        gaps.append("final-test seal lacks query/ground-truth identities")
    if sufficiency is None or sufficiency.get("status") != "FILE_COUNTS_READY":
        gaps.append("corpus sufficiency is not FILE_COUNTS_READY")
    if not labels.get("seeds") or len(labels["seeds"]) < 300:
        gaps.append("hash-bound finite seeds incomplete")
    if labels.get("finiteReviewedNegativePairs") != 12000:
        gaps.append("finite reviewed negative cardinality drift")
    fragment_rows = (extract or {}).get("fragments") or []
    if not 0 < len(fragment_rows) <= 5000:
        gaps.append("canonical fragment cap/count invalid")
    if set(labels.get("trainTripletLanguages") or []) == set(LANGUAGES) and "train fragment triplets missing independently reviewed languages" in gaps:
        pass
    status = "READY_FOR_REVIEW" if not gaps else "CHANGES_NEEDED"
    return report({"status": status, "gaps": gaps, "protocolSha256": protocol_hash,
                   "sandboxPort": PORT, "CanAuthorizePublication": False,
                   "reviewerApproval": False, "root": str(root),
                   "requiredStages": sorted(REQUIRED_STAGE_OUTPUTS)})


def seal_final_test(split, labels, extract, *, protocol=None, query_artifacts=None, rights_sha256=None):
    files = {f["fileId"]: f for f in extract["files"] if f["partition"] == "final-test"}
    seeds = [s for s in labels["seeds"] if s.get("seedFileId") in files]
    seeds = sorted(seeds, key=lambda row: row["seedFileId"])
    gallery = sorted(f["fileId"] for f in files.values() if f.get("searchableExecutable") and not f.get("generated"))
    identities = [{"seedFileId": row["seedFileId"], "familyId": files[row["seedFileId"]].get("familyId"),
                   "querySha256": files[row["seedFileId"]]["sha256"],
                   "relevantFileIds": sorted(row.get("relevantFileIds") or []),
                   "distractorFileIds": sorted(row.get("distractorFileIds") or []),
                   "unknownExclusions": sorted(row.get("unknownFileIds") or [])} for row in seeds]
    query_hashes = sorted(item["querySha256"] for item in identities)
    relevance = {item["seedFileId"]: item["relevantFileIds"] for item in identities}
    artifact_hashes = []
    for path in sorted(query_artifacts or ()):
        artifact_hashes.append(digest(Path(path).read_bytes()))
    payload = {"partition": "final-test", "fileCount": len(files), "seedCount": len(seeds),
               "seedIdentities": identities, "querySha256s": query_hashes, "relevance": relevance,
               "galleryFileIds": gallery, "gallerySha256": digest(canonical(gallery)),
               "splitSha256": digest(canonical(split)), "extractionSha256": extract["extractionSha256"],
               "labelsSha256": digest(canonical(labels)),
               "protocolSha256": digest(canonical(protocol)) if protocol else None,
               "rightsSha256": rights_sha256 or split.get("rightsSha256"),
               "queryArtifactSha256": digest(canonical(artifact_hashes)) if artifact_hashes else None,
               "scored": False, "ranksInspected": False, "tuning": False}
    payload["sealSha256"] = digest(canonical(payload))
    return report(payload)
