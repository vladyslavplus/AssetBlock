"""Canonical identities and fail-closed intake/rights contracts."""

import hashlib
import json
import re
from datetime import date
from pathlib import PurePosixPath

from . import SCHEMA

LANGUAGES = ("javascript", "typescript", "python", "java", "csharp")
PARTITIONS = ("train", "validation", "final-test")
PURPOSES = ("searchIndexAllowed", "trainingAllowed", "evaluationAllowed",
            "rawRedistributionAllowed", "derivedArtifactRedistributionAllowed")
CAPS = {"families": 25, "files": 1000, "fileBytes": 512 * 1024,
        "totalBytes": 100 * 1024 * 1024}


def canonical(value):
    return (json.dumps(value, sort_keys=True, ensure_ascii=False, separators=(",", ":"),
                       allow_nan=False) + "\n").encode("utf-8")


def digest(value):
    return hashlib.sha256(value).hexdigest()


def report(payload):
    return {**payload, "schema": SCHEMA, "Purpose": "CORPUS_BASELINE",
            "CanAuthorizePublication": False, "SecurityEvidence": "NOT_RUN"}


def sha(value):
    if not isinstance(value, str) or not re.fullmatch("[0-9a-f]{64}", value):
        raise ValueError("invalid SHA-256")
    return value


def relative(value):
    if not isinstance(value, str) or not value or any(c in value for c in "\\:%?#\x00"):
        raise ValueError("invalid relative path")
    if value.startswith("/") or any(p in {"", ".", ".."} for p in value.split("/")):
        raise ValueError("absolute/traversal path")
    for p in value.split("/"):
        if p.endswith((".", " ")) or re.fullmatch(r"(?i)(con|prn|aux|nul|com[1-9]|lpt[1-9])(\..*)?", p):
            raise ValueError("unsafe Windows path")
    return PurePosixPath(value).as_posix()


def validate_config(config):
    if config.get("schema") != SCHEMA or not config.get("sources"):
        raise ValueError("missing versioned nonempty source configuration")
    sources = config["sources"]
    if len(sources) > CAPS["families"]:
        raise ValueError("source family cap exceeded")
    ids, count, paths = set(), 0, set()
    for source in sources:
        sid = source["sourceId"]
        if sid in ids or not re.fullmatch(r"[a-z0-9-]+", sid):
            raise ValueError("invalid/duplicate source identity")
        ids.add(sid)
        if not re.fullmatch(r"[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+", source["repository"]):
            raise ValueError("invalid repository")
        if not re.fullmatch("[0-9a-f]{40}", source["commit"]):
            raise ValueError("immutable full commit required")
        if (not isinstance(source.get("lineage"), list) or not source["lineage"]
                or not all(isinstance(x, str) and x for x in source["lineage"])
                or not source.get("files") or not source.get("notices")):
            raise ValueError("lineage/files/notices required")
        for row in source["files"] + source["notices"]:
            path = relative(row["path"])
            key = (sid, path)
            if key in paths:
                raise ValueError("duplicate selected path")
            paths.add(key)
            sha(row["sha256"])
        for row in source["files"]:
            if row["language"] not in LANGUAGES:
                raise ValueError("unsupported language")
            for flag in ("generated", "vendor"):
                if flag in row and type(row[flag]) is not bool:
                    raise ValueError("classification flag must be boolean")
        count += len(source["files"])
    if count > CAPS["files"]:
        raise ValueError("original file cap exceeded")


def allowed(rights, purpose, source, file):
    """A recorded grant needs actual reviewed, hash-bound file and notice evidence."""
    if purpose not in PURPOSES or rights.get("status") != "REVIEWED":
        return False
    if not rights.get("reviewer") or not rights.get("rationale") or not rights.get("terms"):
        return False
    try:
        date.fromisoformat(rights["reviewedOn"])
    except (ValueError, KeyError, TypeError):
        return False
    expected = {row["path"]: row["sha256"] for row in source["notices"]}
    if not expected or rights.get("noticeHashes") != expected:
        return False
    if rights.get("commit") != source["commit"]:
        return False
    if rights.get("fileHashes", {}).get(file["path"]) != file["sha256"]:
        return False
    return rights.get("grants", {}).get(purpose) is True
