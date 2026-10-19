"""Explicit inputs manifest: relative artifact paths with byte and canonical digests.

Every declared artifact is re-resolved against the local filesystem and verified
before use. A missing, oversized or digest-drifting artifact fails closed; nothing
falls back to a dated directory or the newest run.
"""

import json
from pathlib import Path, PurePosixPath

from scripts.code_analysis.contracts import canonical, digest, sha
from scripts.feasibility_pilot.paths import REPO_ROOT, ensure_inside

from .paths import CORPUS_ARTIFACT_ROOT
from . import validation

INPUT_SCHEMA = "code-retrieval-inputs-v1"

SOURCE_MANIFEST = "source-manifest"
SPLIT = "split"
EXTRACT = "extract"
REVIEWED_EXPORT = "reviewed-export"
INDEPENDENT_ACCEPTANCE = "independent-acceptance"
TRANSFORMATION_ACCEPTANCE = "transformation-acceptance"
# Historical transformation-only acceptance. It may contribute reviewed export
# metadata to the corpus index but can never satisfy the validation-query gate.
VALIDATION_ACCEPTANCE = "validation-acceptance"
# The complete reviewed file-retrieval validation corpus: hash-bound seeds,
# relevance targets, galleries, distractors and per-file evidence. This is the
# only role validation.admit consumes.
RETRIEVAL_VALIDATION_CORPUS = "retrieval-validation-corpus"
FINAL_TEST_SEAL = "final-test-seal"
RETRIEVAL_REVIEWED_LABELS = "retrieval-reviewed-labels"

ROLES = (SOURCE_MANIFEST, SPLIT, EXTRACT, REVIEWED_EXPORT, INDEPENDENT_ACCEPTANCE,
         TRANSFORMATION_ACCEPTANCE, VALIDATION_ACCEPTANCE, RETRIEVAL_VALIDATION_CORPUS,
         FINAL_TEST_SEAL, RETRIEVAL_REVIEWED_LABELS)

REQUIRED_ROLES = (SOURCE_MANIFEST, SPLIT, EXTRACT, FINAL_TEST_SEAL)

JSON_ARTIFACT_ROLES = (REVIEWED_EXPORT, INDEPENDENT_ACCEPTANCE, TRANSFORMATION_ACCEPTANCE,
                       VALIDATION_ACCEPTANCE, RETRIEVAL_VALIDATION_CORPUS,
                       RETRIEVAL_REVIEWED_LABELS)

CORPUS_SCHEMA = "code-corpus-v1"
RETRIEVAL_LABELS_SCHEMA = "code-retrieval-reviewed-labels-v1"

# Every declared role is a JSON corpus/retrieval artifact with a fixed schema and
# load-bearing keys. A payload that does not match its role is refused, so a role
# cannot be satisfied by an unrelated but self-consistent JSON document.
ROLE_SCHEMAS = {role: CORPUS_SCHEMA for role in ROLES}
ROLE_SCHEMAS[RETRIEVAL_REVIEWED_LABELS] = RETRIEVAL_LABELS_SCHEMA
# The validation corpus carries its own reviewed schema, imported from the
# admission module itself so the two can never drift apart.
ROLE_SCHEMAS[RETRIEVAL_VALIDATION_CORPUS] = validation.SCHEMA

ROLE_REQUIRED_KEYS = {
    SOURCE_MANIFEST: ("sources",),
    SPLIT: ("files", "families", "rightsSha256", "normalization"),
    EXTRACT: ("files", "fragments", "extractionSha256"),
    FINAL_TEST_SEAL: ("partition", "fileCount", "splitSha256", "extractionSha256", "rightsSha256",
                      "scored", "ranksInspected", "tuning"),
    REVIEWED_EXPORT: ("extraFiles", "extraFragments"),
    INDEPENDENT_ACCEPTANCE: ("extraFiles", "extraFragments"),
    TRANSFORMATION_ACCEPTANCE: (),
    VALIDATION_ACCEPTANCE: (),
    RETRIEVAL_VALIDATION_CORPUS: ("languages",),
    RETRIEVAL_REVIEWED_LABELS: ("trainTriplets",),
}

MAX_MANIFEST_BYTES = 8 * 1024 * 1024
MAX_ARTIFACT_BYTES = 64 * 1024 * 1024


def _validate_role(role, value, relative):
    if not isinstance(value, dict):
        raise ValueError("declared input role is not a JSON object: " + role)
    expected = ROLE_SCHEMAS[role]
    if value.get("schema") != expected:
        raise ValueError("unexpected schema for role {}: {}".format(role, relative))
    for key in ROLE_REQUIRED_KEYS[role]:
        if key not in value:
            raise ValueError("role {} is missing key {}: {}".format(role, key, relative))



def _read_bytes(path, cap):
    with Path(path).open("rb") as stream:
        raw = stream.read(cap + 1)
    if len(raw) > cap:
        raise ValueError("artifact byte cap exceeded: " + str(path))
    return raw


def _relative(value):
    if not isinstance(value, str) or not value or "\\" in value or ":" in value:
        raise ValueError("invalid relative artifact path")
    if value.startswith("/") or any(part in {"", ".", ".."} for part in value.split("/")):
        raise ValueError("absolute or traversal artifact path")
    return PurePosixPath(value).as_posix()


def _resolve(relative, root):
    return ensure_inside(root / Path(*PurePosixPath(relative).parts), root)


def load(path, *, repo_root=REPO_ROOT, artifact_root=CORPUS_ARTIFACT_ROOT):
    raw = _read_bytes(path, MAX_MANIFEST_BYTES)
    try:
        manifest = json.loads(raw)
    except json.JSONDecodeError as exc:
        raise ValueError("inputs manifest is not valid JSON") from exc
    if manifest.get("schema") != INPUT_SCHEMA:
        raise ValueError("unexpected inputs manifest schema")
    entries = manifest.get("artifacts")
    if not isinstance(entries, list) or not entries:
        raise ValueError("inputs manifest requires a nonempty artifacts list")

    corpus_root = _resolve(_relative(manifest.get("corpusRunRoot", "")), artifact_root)
    if not corpus_root.is_dir() or not (corpus_root / "sources").is_dir():
        raise ValueError("corpusRunRoot must contain a sources directory")

    artifacts, seen = {}, set()
    for entry in entries:
        role = entry.get("role")
        if role not in ROLES:
            raise ValueError("unknown input role: " + str(role))
        if role in seen:
            raise ValueError("duplicate input role: " + role)
        seen.add(role)
        relative = _relative(entry.get("path"))
        target = _resolve(relative, artifact_root)
        if not target.is_file():
            raise ValueError("declared input missing: " + relative)
        blob = _read_bytes(target, MAX_ARTIFACT_BYTES)
        byte_sha = digest(blob)
        declared_byte = entry.get("byteSha256")
        if declared_byte is None:
            raise ValueError("declared byte digest required: " + relative)
        if byte_sha != sha(declared_byte):
            raise ValueError("byte digest drift: " + relative)
        try:
            value = json.loads(blob)
        except json.JSONDecodeError as exc:
            raise ValueError("declared JSON input is unparsable: " + relative) from exc
        canonical_sha = digest(canonical(value))
        declared_canonical = entry.get("canonicalSha256")
        if declared_canonical is None:
            raise ValueError("declared canonical digest required: " + relative)
        if canonical_sha != sha(declared_canonical):
            raise ValueError("canonical digest drift: " + relative)
        _validate_role(role, value, relative)
        artifacts[role] = {"role": role, "relativePath": relative, "path": target,
                           "bytes": len(blob), "byteSha256": byte_sha,
                           "canonicalSha256": canonical_sha, "value": value, "raw": blob}

    missing = [role for role in REQUIRED_ROLES if role not in artifacts]
    if missing:
        raise ValueError("inputs manifest missing required roles: " + ", ".join(missing))
    return {"schema": INPUT_SCHEMA, "manifestSha256": digest(canonical(manifest)),
            "repoRoot": repo_root, "artifactRoot": artifact_root,
            "corpusRunRoot": corpus_root, "sourcesRoot": corpus_root / "sources",
            "artifacts": artifacts}


def value(loaded, role):
    entry = loaded["artifacts"].get(role)
    return None if entry is None else entry["value"]


def identity(loaded, role):
    entry = loaded["artifacts"][role]
    return {"relativePath": entry["relativePath"], "bytes": entry["bytes"],
            "byteSha256": entry["byteSha256"], "canonicalSha256": entry["canonicalSha256"]}
