"""Reuse verified frozen/index payloads only after extract, protocol, and artifact hashes match."""

import json
import os
import re
from pathlib import Path

from scripts.feasibility_pilot.paths import ensure_inside

from .code_index import PORT, preflight
from .contracts import canonical, digest, report, sha
from .corpus import ARTIFACT_ROOT
from .finite_labels import FROZEN_PROTOCOL_SHA
from .index_runtime import verify_runtime
from .index_writer import _verify_rows, prepare_payload, publish_index
from .representations import read_vectors, vector_bytes

REPRESENTATION_FILES = ("frozen-vectors.fp32le", "chunks.jsonl", "manifest.json", "source-mapping.u32le")


def parent_root(run_id=None):
    if not run_id or not re.fullmatch(r"[a-zA-Z0-9][a-zA-Z0-9_-]{0,79}", run_id):
        raise ValueError("explicit parent run id required")
    return ensure_inside(ARTIFACT_ROOT / "runs" / run_id, ARTIFACT_ROOT)


def _representation_file(parent, name):
    if name not in REPRESENTATION_FILES:
        raise ValueError("unknown representation file")
    path = ensure_inside(Path(parent) / "representations" / name, ARTIFACT_ROOT)
    if path.parts[-2:] != ("representations", name):
        raise ValueError("representation path component mismatch")
    return path


def verify_vector_artifacts(parent, frozen):
    parent = ensure_inside(Path(parent), ARTIFACT_ROOT)
    try:
        paths = {name: _representation_file(parent, name) for name in REPRESENTATION_FILES}
    except ValueError as exc:
        return report({"status": "FAILED", "reason": str(exc)})
    if any(not path.is_file() for path in paths.values()):
        return report({"status": "FAILED", "reason": "frozen representation files missing"})
    manifest = paths["manifest.json"].read_bytes()
    mapping = paths["source-mapping.u32le"].read_bytes()
    vector_sha = digest(paths["frozen-vectors.fp32le"].read_bytes())
    chunk_sha = digest(paths["chunks.jsonl"].read_bytes())
    manifest_sha = digest(manifest)
    mapping_sha = digest(mapping)
    if vector_sha != frozen.get("fullVectorSha256"):
        return report({"status": "FAILED", "reason": "frozen vector blob digest drift", "fullVectorSha256": vector_sha})
    if manifest_sha != frozen.get("representationManifestSha256"):
        return report({"status": "FAILED", "reason": "representation manifest digest drift"})
    parsed = json.loads(manifest)
    if parsed.get("chunkRowsSha256") != chunk_sha:
        return report({"status": "FAILED", "reason": "chunks.jsonl digest drift from manifest"})
    if not parsed.get("sourceMappingSha256"):
        return report({"status": "FAILED", "reason": "source mapping digest missing from manifest"})
    if mapping_sha != parsed["sourceMappingSha256"]:
        return report({"status": "FAILED", "reason": "source mapping digest drift"})
    return report({"status": "VERIFIED", "fullVectorSha256": vector_sha, "chunkRowsSha256": chunk_sha,
                   "sourceMappingSha256": mapping_sha, "representationManifestSha256": manifest_sha,
                   "modelKey": frozen.get("modelKey")})


def publication_provenance_ref(parent, extract, fragment):
    parent = ensure_inside(Path(parent), ARTIFACT_ROOT)
    extraction = extract.get("extractionSha256")
    sha(extraction)
    sha(fragment["sourceSha256"])
    return "run:" + str(parent) + "; extraction:" + extraction + "; source:" + fragment["sourceSha256"]


def _partition_row_payload(parent, frozen, extract, partition, index_key, snapshot_id):
    vectors_path = _representation_file(parent, "frozen-vectors.fp32le")
    chunks_path = _representation_file(parent, "chunks.jsonl")
    manifest = json.loads(_representation_file(parent, "manifest.json").read_bytes())
    chunks = [json.loads(line) for line in chunks_path.read_text(encoding="utf-8").splitlines() if line]
    vectors = read_vectors(vectors_path.read_bytes(), manifest["chunks"])
    bound = []
    for chunk, vector in zip(chunks, vectors):
        if chunk.get("partition") != partition:
            continue
        bound.append({**chunk, "vector": vector})
    bound.sort(key=lambda row: (row["fragmentId"], row["chunkOrdinal"]))
    fragment_ids = {row["fragmentId"] for row in bound}
    fragments = [{**row, "provenanceRef": publication_provenance_ref(parent, extract, row)}
                 for row in extract.get("fragments") or [] if row["fragmentId"] in fragment_ids]
    fragments.sort(key=lambda row: row["fragmentId"])
    return {
        "identity": {"index_key": index_key},
        "snapshot": {"snapshot_id": snapshot_id},
        "chunks": bound,
        "fragments": fragments,
        "expectedRows": len(bound),
        "vectorSha256": digest(vector_bytes([row["vector"] for row in bound])) if bound else None,
        "metadataDigest": digest(canonical([
            [{"fragmentId": row["fragmentId"], "chunkOrdinal": row["chunkOrdinal"],
              "representationSha256": row.get("representationSha256"),
              "sourceStart": row.get("sourceStart"), "sourceEnd": row.get("sourceEnd")} for row in bound],
            [{"fragmentId": row["fragmentId"], "startByte": row["startByte"], "endByte": row["endByte"],
              "sha256": row["sha256"]} for row in fragments],
        ])),
    }


def reuse_frozen(extract, protocol, *, parent=None):
    parent = ensure_inside(Path(parent or parent_root()), ARTIFACT_ROOT)
    frozen = json.loads((parent / "frozen.json").read_bytes())
    parent_extract = (parent / "extract.json").read_bytes()
    if digest(canonical(extract)) != digest(parent_extract):
        raise ValueError("frozen reuse requires identical extract payload")
    if digest(canonical(protocol)) != FROZEN_PROTOCOL_SHA:
        raise ValueError("frozen reuse requires frozen protocol digest")
    verified = verify_vector_artifacts(parent, frozen)
    if verified.get("status") != "VERIFIED":
        raise ValueError(verified.get("reason") or "frozen artifact verification failed")
    return report({
        "status": "REUSED",
        "parentRunId": parent.name,
        "noRebuild": True,
        "frozenSha256": digest((parent / "frozen.json").read_bytes()),
        "extractSha256": digest(parent_extract),
        "protocolSha256": FROZEN_PROTOCOL_SHA,
        "modelKey": frozen.get("modelKey"),
        "indexKeys": {row["partition"]: row["indexKey"] for row in frozen.get("partitions") or []},
        "vectorVerification": verified,
        "finalTestScored": False,
        "noOptimizerCreated": True,
    })


def check_live_index(frozen, *, connect=None, parent=None, extract=None):
    expected = {row["partition"]: {"indexKey": row["indexKey"], "rows": len(row.get("rowOrdinals") or []),
                                   "gallerySha256": row.get("gallerySha256")}
                for row in frozen.get("partitions") or []}
    opener = connect
    if opener is None:
        dsn = os.environ.get("ASSETBLOCK_CODE_INDEX_DSN")
        if not dsn:
            return report({"status": "UNVERIFIED", "reason": "sandbox DSN unavailable; live index not checked",
                           "expected": {name: {"indexKey": item["indexKey"], "rows": item["rows"]}
                                        for name, item in expected.items()}})
        def opener():
            import psycopg
            return psycopg.connect(dsn, autocommit=True)
    try:
        connection = opener()
    except (OSError, ValueError, ImportError) as exc:
        return report({"status": "UNVERIFIED", "reason": "sandbox connection failed: " + type(exc).__name__,
                       "expected": {name: {"indexKey": item["indexKey"], "rows": item["rows"]}
                                    for name, item in expected.items()}})
    try:
        artifacts = None
        if parent is not None or extract is not None:
            parent = ensure_inside(Path(parent or parent_root()), ARTIFACT_ROOT)
            artifacts = verify_vector_artifacts(parent, frozen)
            if artifacts.get("status") != "VERIFIED":
                return report({"status": "FAILED", "reason": artifacts.get("reason") or "representation verification failed"})
            extract = extract or json.loads((parent / "extract.json").read_bytes())
        preflight(connection)
        rows = {}
        with connection.cursor() as cursor:
            for partition, item in expected.items():
                cursor.execute(
                    "SELECT state,partition,expected_rows,model_key,vector_sha256,snapshot_id FROM code_lab.code_indexes WHERE index_key=%s",
                    (item["indexKey"],))
                found = cursor.fetchone()
                if not found or found[0] != "READY" or found[1] != partition:
                    return report({"status": "FAILED", "reason": "index missing or not READY", "partition": partition})
                cursor.execute("SELECT count(*) FROM code_lab.code_embeddings WHERE index_key=%s", (item["indexKey"],))
                count = cursor.fetchone()[0]
                if count != found[2] or (item["rows"] and count != item["rows"]):
                    return report({"status": "FAILED", "reason": "index row-count drift", "partition": partition,
                                   "expectedRows": found[2], "storedRows": count})
                if parent is None:
                    return report({"status": "FAILED", "reason": "live index row immutability requires representation parent"})
                payload = _partition_row_payload(parent, frozen, extract, partition, item["indexKey"], found[5])
                if not payload["vectorSha256"] or found[4] != payload["vectorSha256"]:
                    return report({"status": "FAILED", "reason": "stored partition vector digest drift",
                                   "partition": partition})
                try:
                    _verify_rows(cursor, payload)
                except ValueError as exc:
                    return report({"status": "FAILED", "reason": str(exc), "partition": partition})
                rows[partition] = {"state": "READY", "rows": count, "modelKey": found[3],
                                   "indexKey": item["indexKey"], "vectorSha256": found[4],
                                   "metadataDigest": payload["metadataDigest"]}
        if frozen.get("modelKey") and any(row["modelKey"] != frozen["modelKey"] for row in rows.values()):
            return report({"status": "FAILED", "reason": "stored ModelKey drift"})
        return report({"status": "READY_VERIFIED", "port": PORT, "partitions": rows, "noRebuild": True,
                       "artifactVerification": (artifacts or {}).get("status")})
    finally:
        connection.close()


def live_index_evidence(live, *, frozen, reused=None):
    if live.get("status") != "READY_VERIFIED":
        return report({k: v for k, v in live.items() if k != "dsn"})
    expected = {row["partition"]: row["indexKey"] for row in frozen.get("partitions") or []}
    parts = live.get("partitions") or {}
    found = {name: row.get("indexKey") for name, row in parts.items()}
    if not expected or found != expected:
        return report({"status": "FAILED", "reason": "live index IndexKey binding drift", "expectedKeys": expected})
    model = frozen.get("modelKey")
    stored = live.get("modelKey") or next((row.get("modelKey") for row in parts.values()), None)
    if model and stored != model:
        return report({"status": "FAILED", "reason": "live index ModelKey binding drift"})
    reused = reused or {}
    return report({
        "status": "READY_VERIFIED",
        "port": live.get("port") or PORT,
        "partitions": parts,
        "noRebuild": True,
        "reuseStatus": reused.get("status"),
        "parentRunId": reused.get("parentRunId"),
        "frozenSha256": reused.get("frozenSha256"),
        "modelKey": model or stored,
        "indexKeys": expected,
        "artifactVerification": live.get("artifactVerification") or (reused.get("vectorVerification") or {}).get("status"),
    })


def reuse_receipt(*, parent_run_id, parent_output, parent_sha256):
    sha(parent_sha256)
    relative = Path(parent_output).as_posix()
    if relative != parent_output or ".." in relative.split("/"):
        raise ValueError("reuse receipt parent output path rejected")
    return report({
        "status": "REUSED",
        "parentRunId": parent_run_id,
        "parentOutput": relative,
        "parentSha256": parent_sha256,
        "noRebuild": True,
        "gpuRerun": False,
        "scancodeRerun": False,
    })


def verify_reuse_receipt(payload, expected_name):
    if payload.get("status") != "REUSED":
        return
    parent_run = payload.get("parentRunId")
    parent_output = payload.get("parentOutput")
    parent_sha = payload.get("parentSha256")
    if not parent_run or not parent_output or payload.get("linkedOutput"):
        raise ValueError("reuse receipt missing parent binding")
    sha(parent_sha)
    if Path(parent_output).name != expected_name:
        raise ValueError("reuse parent output name mismatch")
    parent = ensure_inside(ARTIFACT_ROOT / "runs" / parent_run / parent_output, ARTIFACT_ROOT)
    if not parent.is_file() or digest(parent.read_bytes()) != parent_sha:
        raise ValueError("reuse parent output drift")


def reuse_index(extract, protocol, *, parent=None, connect=None):
    reused = reuse_frozen(extract, protocol, parent=parent)
    frozen = json.loads((Path(parent or parent_root()) / "frozen.json").read_bytes())
    live = check_live_index(frozen, connect=connect, parent=parent or parent_root(), extract=extract)
    evidence = live_index_evidence(live, frozen=frozen, reused=reused)
    if evidence.get("status") != "READY_VERIFIED":
        raise ValueError(evidence.get("reason") or "live index unverified")
    return evidence


def snapshot_identity(parent):
    parent = ensure_inside(Path(parent), ARTIFACT_ROOT)
    intake_raw = (parent / "intake.json").read_bytes()
    split_raw = (parent / "split.json").read_bytes()
    intake = json.loads(intake_raw)
    return {
        "snapshot_id": intake["snapshotId"],
        "manifest_sha256": digest(intake_raw),
        "rights_sha256": intake["rightsSha256"],
        "split_sha256": digest(split_raw),
    }


def restore_partition_payload(parent, frozen, extract, partition):
    parent = ensure_inside(Path(parent), ARTIFACT_ROOT)
    part = next(row for row in frozen["partitions"] if row["partition"] == partition)
    bound = _partition_row_payload(parent, frozen, extract, partition, part["indexKey"],
                                   json.loads((parent / "intake.json").read_bytes())["snapshotId"])
    files = [row for row in extract["files"] if row["fileId"] in {f["fileId"] for f in bound["fragments"]}]
    identity = {
        "index_key": part["indexKey"],
        "model_key": frozen["modelKey"],
        "gallery_sha256": part["gallerySha256"],
        "representation_sha256": frozen["representationManifestSha256"],
        "partition": partition,
    }
    return prepare_payload(snapshot_identity(parent), identity, files, bound["fragments"], bound["chunks"])


def restore_index_from_representations(connection, frozen, *, parent, extract, runtime_root, runtime_review):
    verify_runtime(runtime_root, runtime_review)
    parent = ensure_inside(Path(parent), ARTIFACT_ROOT)
    verified = verify_vector_artifacts(parent, frozen)
    if verified.get("status") != "VERIFIED":
        raise ValueError(verified.get("reason") or "representation verification failed")
    published = {}
    for partition in ("train", "validation", "final-test"):
        payload = restore_partition_payload(parent, frozen, extract, partition)
        published[partition] = publish_index(connection, payload, runtime_root=runtime_root,
                                             runtime_review=runtime_review)
    return report({"status": "RESTORED_FROM_REPRESENTATIONS", "partitions": published,
                   "gpuRerun": False, "reembedded": False, "artifactVerification": verified["status"]})


def _dsn_opener():
    dsn = os.environ.get("ASSETBLOCK_CODE_INDEX_DSN")
    if not dsn:
        raise ValueError("sandbox DSN unavailable; live index not checked")
    import psycopg
    return psycopg.connect(dsn, autocommit=True)


def restore_live_index(frozen, *, parent=None, extract=None, connect=None, runtime_root=None, runtime_review=None):
    parent = ensure_inside(Path(parent or parent_root()), ARTIFACT_ROOT)
    extract = extract or json.loads((parent / "extract.json").read_bytes())
    reused = {"status": "REUSED", "parentRunId": parent.name}
    live = check_live_index(frozen, connect=connect, parent=parent, extract=extract)
    if live.get("status") == "READY_VERIFIED":
        return live_index_evidence(live, frozen=frozen, reused=reused)
    missing = live.get("status") == "FAILED" and "index missing or not READY" in (live.get("reason") or "")
    if not missing:
        return report({k: v for k, v in live.items() if k != "dsn"})
    if runtime_root is None or runtime_review is None:
        return report({"status": "UNVERIFIED",
                       "reason": "index restore requires current runtime gate; live index missing or not READY",
                       "live": {k: v for k, v in live.items() if k != "dsn"}})
    opener = connect or _dsn_opener
    connection = opener()
    try:
        restore_index_from_representations(connection, frozen, parent=parent, extract=extract,
                                           runtime_root=runtime_root, runtime_review=runtime_review)
    finally:
        connection.close()
    live = check_live_index(frozen, connect=opener, parent=parent, extract=extract)
    return live_index_evidence(live, frozen=frozen, reused=reused)
