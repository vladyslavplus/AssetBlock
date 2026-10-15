"""Restricted read-only reference snapshot access.

Comparison reads require an explicit snapshot manifest: immutable file
identities, revision, model and index keys, per-file sensitivity, and a
recorded comparison-purpose grant. Missing or ungranted references fail
honestly; an unavailable gallery is never reported as no matches.
"""

from __future__ import annotations

import json
from dataclasses import dataclass
from pathlib import Path

from . import contracts, mechanisms
from .contracts import SENSITIVITY, ContractError


@dataclass(frozen=True)
class ReferenceFile:
    file_id: str
    sha256: str
    language_group: str
    dialect: str
    sensitivity: str
    origin: str
    staged_rel_path: str
    attribution: dict


@dataclass(frozen=True)
class ReferenceSnapshot:
    snapshot_id: str
    revision: str
    model_key: str
    index_key: str
    partition: str
    gallery_root: Path
    files: tuple
    model_identity: dict | None = None
    extraction_sha256: str | None = None


MODEL_IDENTITY_FIELDS = frozenset({
    "checkpointSha256", "loadedStateSha256", "tokenizerHashes", "framing", "pooling", "mask",
    "preprocessing", "chunking", "dimension", "selectionSha256",
})


def load_manifest(manifest_path: Path, gallery_root: Path) -> ReferenceSnapshot:
    try:
        raw = json.loads(manifest_path.read_text(encoding="utf-8"))
    except (OSError, UnicodeDecodeError, json.JSONDecodeError) as exc:
        raise ContractError("REFERENCE_MANIFEST_INVALID", str(exc)[:200])
    if not isinstance(raw, dict):
        raise ContractError("REFERENCE_MANIFEST_INVALID", "shape")
    if raw.get("schema") != "code-reference-snapshot-v1":
        raise ContractError("REFERENCE_MANIFEST_INVALID", "schema")
    for name in ("snapshotId", "revision", "modelKey", "indexKey",
                 "partition", "files", "grant"):
        if name not in raw:
            raise ContractError("REFERENCE_MANIFEST_INVALID", f"missing {name}")
    if not contracts.is_sha256(raw["snapshotId"]):
        raise ContractError("REFERENCE_MANIFEST_INVALID", "snapshotId")
    if not contracts.is_opaque_id(str(raw["revision"])):
        raise ContractError("REFERENCE_MANIFEST_INVALID", "revision")
    for name in ("modelKey", "indexKey"):
        if not contracts.is_sha256(raw[name]):
            raise ContractError("REFERENCE_MANIFEST_INVALID", name)
    if raw["partition"] not in ("train", "validation"):
        raise ContractError("REFERENCE_MANIFEST_INVALID", "partition")
    grant = raw["grant"]
    if not isinstance(grant, dict) or grant.get("purpose") != "COMPARISON":
        raise ContractError("REFERENCE_GRANT_MISSING", "purpose")
    if not grant.get("sourceRevision") or not grant.get("recordedBy"):
        raise ContractError("REFERENCE_GRANT_MISSING", "grant identity")
    if not isinstance(raw["files"], list) or not raw["files"]:
        raise ContractError("REFERENCE_MANIFEST_INVALID", "empty gallery")
    gallery_root = mechanisms.ensure_inside(gallery_root, gallery_root)
    entries = []
    seen = set()
    for item in raw["files"]:
        entries.append(_validate_entry(item, gallery_root))
        if entries[-1].file_id in seen:
            raise ContractError("REFERENCE_MANIFEST_INVALID", "duplicate fileId")
        seen.add(entries[-1].file_id)
    model_identity = _validate_model_identity(raw.get("modelIdentity"))
    extraction_sha256 = raw.get("extractionSha256")
    if extraction_sha256 is not None and not contracts.is_sha256(extraction_sha256):
        raise ContractError("REFERENCE_MANIFEST_INVALID", "extractionSha256")
    return ReferenceSnapshot(
        snapshot_id=raw["snapshotId"],
        revision=str(raw["revision"]),
        model_key=raw["modelKey"],
        index_key=raw["indexKey"],
        partition=raw["partition"],
        gallery_root=gallery_root,
        files=tuple(entries),
        model_identity=model_identity,
        extraction_sha256=extraction_sha256,
    )


def _validate_model_identity(raw) -> dict | None:
    if raw is None:
        return None
    if not isinstance(raw, dict) or set(raw) != MODEL_IDENTITY_FIELDS:
        raise ContractError("REFERENCE_MANIFEST_INVALID", "modelIdentity")
    if not contracts.is_sha256(raw["checkpointSha256"]):
        raise ContractError("REFERENCE_MANIFEST_INVALID", "modelIdentity")
    if not contracts.is_sha256(raw["loadedStateSha256"]):
        raise ContractError("REFERENCE_MANIFEST_INVALID", "modelIdentity")
    if not contracts.is_sha256(raw["selectionSha256"]):
        raise ContractError("REFERENCE_MANIFEST_INVALID", "modelIdentity")
    if raw["dimension"] != 768:
        raise ContractError("REFERENCE_MANIFEST_INVALID", "modelIdentity")
    hashes = raw["tokenizerHashes"]
    if not isinstance(hashes, dict) or not hashes:
        raise ContractError("REFERENCE_MANIFEST_INVALID", "modelIdentity")
    for name, value in hashes.items():
        if not contracts.is_sha256(value):
            raise ContractError("REFERENCE_MANIFEST_INVALID", "modelIdentity")
    for name in ("framing", "pooling", "mask", "preprocessing", "chunking"):
        if not isinstance(raw[name], str) or not raw[name] or len(raw[name]) > 256:
            raise ContractError("REFERENCE_MANIFEST_INVALID", "modelIdentity")
    return dict(raw)


def _validate_entry(item: dict, gallery_root: Path) -> ReferenceFile:
    if not isinstance(item, dict):
        raise ContractError("REFERENCE_MANIFEST_INVALID", "entry shape")
    allowed = {"fileId", "sha256", "languageGroup", "dialect", "sensitivity",
               "origin", "stagedName", "attribution"}
    if set(item) - allowed:
        raise ContractError("REFERENCE_MANIFEST_INVALID", "entry fields")
    if not contracts.is_sha256(item.get("fileId", "")):
        raise ContractError("REFERENCE_MANIFEST_INVALID", "fileId")
    if not contracts.is_sha256(item.get("sha256", "")):
        raise ContractError("REFERENCE_MANIFEST_INVALID", "sha256")
    group = item.get("languageGroup")
    if group not in contracts.LANGUAGE_GROUPS:
        raise ContractError("REFERENCE_MANIFEST_INVALID", "languageGroup")
    if item.get("dialect") not in contracts.LANGUAGE_GROUPS[group]:
        raise ContractError("REFERENCE_MANIFEST_INVALID", "dialect")
    if item.get("sensitivity") not in (SENSITIVITY.PUBLIC.value, SENSITIVITY.PRIVATE.value):
        raise ContractError("REFERENCE_MANIFEST_INVALID", "sensitivity")
    if item.get("origin") not in ("OWN", "FOREIGN"):
        raise ContractError("REFERENCE_MANIFEST_INVALID", "origin")
    staged = _validate_staged_rel_path(item.get("stagedName"), gallery_root)
    attribution = item.get("attribution") or {}
    if not isinstance(attribution, dict):
        raise ContractError("REFERENCE_MANIFEST_INVALID", "attribution")
    if item.get("sensitivity") == SENSITIVITY.PUBLIC.value and not attribution.get("label"):
        raise ContractError("REFERENCE_MANIFEST_INVALID", "public attribution")
    if item.get("sensitivity") == SENSITIVITY.PRIVATE.value and attribution:
        raise ContractError("REFERENCE_MANIFEST_INVALID", "private attribution leak")
    return ReferenceFile(
        file_id=item["fileId"],
        sha256=item["sha256"],
        language_group=group,
        dialect=item["dialect"],
        sensitivity=item["sensitivity"],
        origin=item["origin"],
        staged_rel_path=staged,
        attribution={k: str(v)[:512] for k, v in attribution.items()},
    )


def _validate_staged_rel_path(raw, gallery_root: Path) -> str:
    """Keep the full root-relative path; basename-only matching is forbidden."""
    if not isinstance(raw, str) or not raw or len(raw) > 512:
        raise ContractError("REFERENCE_MANIFEST_INVALID", "stagedName")
    normalized = raw.replace("\\", "/").strip("/")
    if (not normalized or normalized.startswith("/")
            or any(part in ("", ".", "..") for part in normalized.split("/"))):
        raise ContractError("REFERENCE_MANIFEST_INVALID", "stagedName")
    if any(part.endswith((".", " ")) for part in normalized.split("/")):
        raise ContractError("REFERENCE_MANIFEST_INVALID", "stagedName")
    candidate = mechanisms.ensure_inside(gallery_root / normalized, gallery_root)
    try:
        if not candidate.is_file() or candidate.is_symlink():
            raise ContractError("REFERENCE_MANIFEST_INVALID", "stagedName")
    except OSError:
        raise ContractError("REFERENCE_MANIFEST_INVALID", "stagedName")
    return normalized


def read_gallery_bytes(snapshot: ReferenceSnapshot, entry: ReferenceFile) -> bytes:
    path = mechanisms.ensure_inside(snapshot.gallery_root / entry.staged_rel_path,
                                    snapshot.gallery_root)
    if path.is_symlink():
        raise ContractError("REFERENCE_MANIFEST_INVALID", "gallery link")
    data = path.read_bytes()
    if contracts.sha256_hex(data) != entry.sha256:
        raise ContractError("REFERENCE_MANIFEST_INVALID", "gallery drift")
    return data
