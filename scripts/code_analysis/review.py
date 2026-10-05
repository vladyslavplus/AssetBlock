"""Exact source endpoint binding for independent static review packages."""

from .contracts import relative, sha


def endpoint(files, source_id, path, *, expected_id=None, expected_sha256=None):
    """Resolve a full relative path exactly; suffixes and duplicate rows fail closed."""
    relative(path)
    matches = [row for row in files if row.get("sourceId") == source_id and row.get("path") == path]
    if len(matches) != 1:
        raise ValueError("endpoint requires exactly one full-path match")
    row = matches[0]
    sha(row["fileId"])
    sha(row["sha256"])
    if expected_id is not None and row["fileId"] != expected_id:
        raise ValueError("endpoint file identity mismatch")
    if expected_sha256 is not None and row["sha256"] != expected_sha256:
        raise ValueError("endpoint source hash mismatch")
    return {key: row[key] for key in ("fileId", "sha256", "sourceId", "path", "commit", "partition")}


def bind_pair(files, left_spec, right_spec, *, require_distinct=False):
    """Cross-file intent can require distinct IDs; valid same-file derivatives remain allowed."""
    left = endpoint(files, **left_spec)
    right = endpoint(files, **right_spec)
    if require_distinct and left["fileId"] == right["fileId"]:
        raise ValueError("intended cross-file pair resolved to same endpoint")
    if left["partition"] != right["partition"]:
        raise ValueError("pair endpoints cross partitions")
    return left, right
