"""Explicit inputs manifest: relative artifact paths with byte and canonical digests.

Every declared artifact is re-resolved against the local filesystem and verified
before use. A missing, oversized or digest-drifting artifact fails closed; nothing
falls back to a dated directory or the newest run.
"""

import json
from pathlib import Path, PurePosixPath
import re

from scripts.code_analysis.contracts import canonical, digest, sha
from scripts.feasibility_pilot.paths import REPO_ROOT, ensure_inside

from .paths import CORPUS_ARTIFACT_ROOT
from . import validation
from .corpus_index import build_final_test_membership

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
FINAL_TEST_MEMBERSHIP = "final-test-membership"
RETRIEVAL_REVIEWED_LABELS = "retrieval-reviewed-labels"
# Provenance record of one assembly run. Its bytes are pinned by the successor
# manifest that declares it. It carries no final-test material.
ASSEMBLY_DERIVATION = "assembly-derivation"
# Refused identifier only: a manifest declaring this role is rejected before any
# declared path is resolved or opened. It is not an input role.
FINAL_TEST_SEAL = "final-test-seal"

ROLES = (SOURCE_MANIFEST, SPLIT, EXTRACT, REVIEWED_EXPORT, INDEPENDENT_ACCEPTANCE,
         TRANSFORMATION_ACCEPTANCE, VALIDATION_ACCEPTANCE, RETRIEVAL_VALIDATION_CORPUS,
         FINAL_TEST_MEMBERSHIP, RETRIEVAL_REVIEWED_LABELS, ASSEMBLY_DERIVATION)

REQUIRED_ROLES = (SOURCE_MANIFEST, SPLIT, EXTRACT, FINAL_TEST_MEMBERSHIP)

CORPUS_SCHEMA = "code-corpus-v1"
RETRIEVAL_LABELS_SCHEMA = "code-retrieval-reviewed-labels-v1"
FINAL_TEST_MEMBERSHIP_SCHEMA = "code-retrieval-final-test-membership-v1"
ASSEMBLY_DERIVATION_SCHEMA = "code-retrieval-assembly-derivation-v1"

# Every declared role is a JSON corpus/retrieval artifact with a fixed schema and
# load-bearing keys. A payload that does not match its role is refused, so a role
# cannot be satisfied by an unrelated but self-consistent JSON document.
ROLE_SCHEMAS = {role: CORPUS_SCHEMA for role in ROLES}
ROLE_SCHEMAS[RETRIEVAL_REVIEWED_LABELS] = RETRIEVAL_LABELS_SCHEMA
ROLE_SCHEMAS[FINAL_TEST_MEMBERSHIP] = FINAL_TEST_MEMBERSHIP_SCHEMA
ROLE_SCHEMAS[ASSEMBLY_DERIVATION] = ASSEMBLY_DERIVATION_SCHEMA
# The validation corpus carries its own reviewed schema, imported from the
# admission module itself so the two can never drift apart.
ROLE_SCHEMAS[RETRIEVAL_VALIDATION_CORPUS] = validation.SCHEMA

ROLE_REQUIRED_KEYS = {
    SOURCE_MANIFEST: ("sources",),
    SPLIT: ("files", "families", "rightsSha256", "normalization"),
    EXTRACT: ("files", "fragments", "extractionSha256"),
    FINAL_TEST_MEMBERSHIP: ("partition", "fileCount", "finalTestFileIds", "finalTestFiles",
                            "galleryFileIds", "finalTestMembershipSha256", "inputPins", "rightsSha256",
                            "scored", "ranksInspected", "tuning"),
    REVIEWED_EXPORT: ("extraFiles", "extraFragments"),
    INDEPENDENT_ACCEPTANCE: ("extraFiles", "extraFragments"),
    TRANSFORMATION_ACCEPTANCE: (),
    VALIDATION_ACCEPTANCE: (),
    RETRIEVAL_VALIDATION_CORPUS: ("languages",),
    RETRIEVAL_REVIEWED_LABELS: ("trainTriplets",),
    ASSEMBLY_DERIVATION: ("outputOwners", "canonicalSha256"),
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


def _validate_final_test_membership(value, artifacts):
    expected_keys = {"schema", "partition", "fileCount", "finalTestFileIds", "finalTestFiles",
                     "galleryFileIds", "finalTestMembershipSha256", "inputPins", "rightsSha256",
                     "scored", "ranksInspected", "tuning"}
    if set(value) != expected_keys:
        raise ValueError("final-test membership keyset mismatch")
    if value.get("schema") != FINAL_TEST_MEMBERSHIP_SCHEMA or value.get("partition") != "final-test":
        raise ValueError("final-test membership schema or partition mismatch")
    count = value.get("fileCount")
    ids = value.get("finalTestFileIds")
    rows = value.get("finalTestFiles")
    gallery = value.get("galleryFileIds")
    if type(count) is not int or count <= 0:
        raise ValueError("final-test membership fileCount must be a positive integer")
    for name, entries in (("finalTestFileIds", ids), ("galleryFileIds", gallery)):
        if (not isinstance(entries, list) or any(not isinstance(item, str) or not item for item in entries)
                or entries != sorted(set(entries))):
            raise ValueError("final-test membership {} must be sorted unique string IDs".format(name))
    if count != len(ids) or not set(gallery) <= set(ids):
        raise ValueError("final-test membership count or gallery ownership mismatch")
    row_keys = {"fileId", "sourceId", "path", "partition", "familyId", "language",
                "sha256", "normalizedSha256", "bytes"}
    if not isinstance(rows, list) or len(rows) != count:
        raise ValueError("final-test membership row count mismatch")
    if [row.get("fileId") for row in rows if isinstance(row, dict)] != ids:
        raise ValueError("final-test membership row IDs mismatch")
    for row in rows:
        if not isinstance(row, dict) or set(row) != row_keys:
            raise ValueError("final-test membership row keyset mismatch")
        for key in ("fileId", "sourceId", "path", "partition", "familyId", "language"):
            if not isinstance(row.get(key), str) or not row[key]:
                raise ValueError("final-test membership row string field invalid: " + key)
        if row["partition"] != "final-test":
            raise ValueError("final-test membership row partition mismatch")
        if type(row.get("bytes")) is not int or row["bytes"] <= 0:
            raise ValueError("final-test membership row bytes invalid")
        for key in ("sha256", "normalizedSha256"):
            if not isinstance(row.get(key), str) or not re.fullmatch(r"[0-9a-f]{64}", row[key]):
                raise ValueError("final-test membership row digest invalid: " + key)
    for key in ("finalTestMembershipSha256", "rightsSha256"):
        if not isinstance(value.get(key), str) or not re.fullmatch(r"[0-9a-f]{64}", value[key]):
            raise ValueError("final-test membership digest invalid: " + key)
    pins = value.get("inputPins")
    pin_roles = (SOURCE_MANIFEST, SPLIT, EXTRACT)
    if not isinstance(pins, dict) or set(pins) != set(pin_roles):
        raise ValueError("final-test membership input pin roles mismatch")
    for role in pin_roles:
        pin = pins.get(role)
        if not isinstance(pin, dict) or set(pin) != {"relativePath", "bytes", "byteSha256", "canonicalSha256"}:
            raise ValueError("final-test membership input pin schema mismatch: " + role)
        if (not isinstance(pin["relativePath"], str) or not pin["relativePath"] or "\\" in pin["relativePath"]
                or type(pin["bytes"]) is not int or pin["bytes"] <= 0
                or any(not isinstance(pin[key], str) or not re.fullmatch(r"[0-9a-f]{64}", pin[key])
                       for key in ("byteSha256", "canonicalSha256"))):
            raise ValueError("final-test membership input pin value invalid: " + role)
        entry = artifacts.get(role)
        if entry is None or pin != {"relativePath": entry["relativePath"], "bytes": entry["bytes"],
                                    "byteSha256": entry["byteSha256"],
                                    "canonicalSha256": entry["canonicalSha256"]}:
            raise ValueError("final-test membership input pin mismatch: " + role)
    if any(value.get(flag) is not False for flag in ("scored", "ranksInspected", "tuning")):
        raise ValueError("final-test membership non-relevance flags must be false")
    manifest, split, extract = (artifacts[role]["value"] for role in pin_roles)
    input_pins = {role: {"relativePath": artifacts[role]["relativePath"],
                         "bytes": artifacts[role]["bytes"],
                         "byteSha256": artifacts[role]["byteSha256"],
                         "canonicalSha256": artifacts[role]["canonicalSha256"]}
                  for role in pin_roles}
    expected = build_final_test_membership(manifest, split, extract, input_pins)
    if value != expected:
        raise ValueError("final-test membership disagrees with authenticated source/split/extract")


def read_bytes(path, cap):
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


def _json_object(pairs):
    value = {}
    for key, item in pairs:
        if key in value:
            raise ValueError("duplicate JSON member: " + key)
        value[key] = item
    return value


def parse_json(raw, description):
    try:
        return json.loads(raw, object_pairs_hook=_json_object,
                          parse_constant=lambda value: (_ for _ in ()).throw(
                              ValueError("non-finite JSON number: " + value)))
    except (json.JSONDecodeError, UnicodeDecodeError) as exc:
        raise ValueError(description + " is not valid UTF-8 JSON") from exc


def load_document(manifest, manifest_sha, *, repo_root, artifact_root, required_roles=REQUIRED_ROLES,
                  check_membership=True):
    if manifest.get("schema") != INPUT_SCHEMA:
        raise ValueError("unexpected inputs manifest schema")
    entries = manifest.get("artifacts")
    if not isinstance(entries, list) or not entries:
        raise ValueError("inputs manifest requires a nonempty artifacts list")
    if any(isinstance(entry, dict) and entry.get("role") == FINAL_TEST_SEAL for entry in entries):
        raise ValueError("final-test-seal role is forbidden; use authenticated final-test-membership")

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
        blob = read_bytes(target, MAX_ARTIFACT_BYTES)
        byte_sha = digest(blob)
        declared_byte = entry.get("byteSha256")
        if declared_byte is None:
            raise ValueError("declared byte digest required: " + relative)
        if byte_sha != sha(declared_byte):
            raise ValueError("byte digest drift: " + relative)
        value = parse_json(blob, "declared JSON input " + relative)
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

    missing = [role for role in required_roles if role not in artifacts]
    if missing:
        raise ValueError("inputs manifest missing required roles: " + ", ".join(missing))
    if check_membership:
        _validate_final_test_membership(artifacts[FINAL_TEST_MEMBERSHIP]["value"], artifacts)
    return {"schema": INPUT_SCHEMA, "manifestSha256": manifest_sha,
            "repoRoot": repo_root, "artifactRoot": artifact_root,
            "corpusRunRoot": corpus_root, "sourcesRoot": corpus_root / "sources",
            "artifacts": artifacts}


def load(path, *, repo_root=REPO_ROOT, artifact_root=CORPUS_ARTIFACT_ROOT):
    raw = read_bytes(path, MAX_MANIFEST_BYTES)
    manifest = parse_json(raw, "inputs manifest")
    return load_document(manifest, digest(canonical(manifest)), repo_root=repo_root,
                         artifact_root=artifact_root)


def value(loaded, role):
    entry = loaded["artifacts"].get(role)
    return None if entry is None else entry["value"]


def identity(loaded, role):
    entry = loaded["artifacts"][role]
    return {"relativePath": entry["relativePath"], "bytes": entry["bytes"],
            "byteSha256": entry["byteSha256"], "canonicalSha256": entry["canonicalSha256"]}
