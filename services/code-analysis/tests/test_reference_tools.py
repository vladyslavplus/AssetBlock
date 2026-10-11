"""Reference manifest validation and tool-failure honesty."""

import json

import pytest

from app import contracts
from app.reference_index import load_manifest
from app.stages import _normalize_scancode


def _manifest(tmp_path, files, grant=None, **overrides):
    gallery = tmp_path / "gallery"
    gallery.mkdir(exist_ok=True)
    entries = []
    for name, content, extra in files:
        data = content.encode()
        target = gallery / name
        target.write_bytes(data)
        row = {"fileId": contracts.sha256_hex(b"file:" + name.encode()),
               "sha256": contracts.sha256_hex(data),
               "languageGroup": "javascript", "dialect": "javascript",
               "sensitivity": "PUBLIC", "origin": "FOREIGN",
               "stagedName": name, "attribution": {"label": "synthetic MIT"}}
        row.update(extra)
        entries.append(row)
    manifest = {"schema": "code-reference-snapshot-v1",
                "snapshotId": contracts.sha256_hex(b"snapshot"),
                "revision": "synthetic-v1",
                "modelKey": contracts.sha256_hex(b"model"),
                "indexKey": contracts.sha256_hex(b"index"),
                "partition": "train",
                "files": entries,
                "grant": grant or {"purpose": "COMPARISON",
                                  "sourceRevision": "synthetic-v1",
                                  "recordedBy": "test"}}
    manifest.update(overrides)
    path = tmp_path / "snapshot.json"
    path.write_text(json.dumps(manifest))
    return path, gallery


def test_valid_manifest_loads(tmp_path):
    path, gallery = _manifest(tmp_path, [("g1.js", "function g() { return 1; }\n", {})])
    snapshot = load_manifest(path, gallery)
    assert len(snapshot.files) == 1
    assert snapshot.partition == "train"


def test_final_test_partition_refused(tmp_path):
    path, gallery = _manifest(tmp_path, [("g1.js", "x", {})])
    assert load_manifest(path, gallery).partition == "train"
    manifest = json.loads(path.read_text())
    manifest["partition"] = "final-test"
    path.write_text(json.dumps(manifest))
    with pytest.raises(contracts.ContractError) as exc_info:
        load_manifest(path, gallery)
    assert exc_info.value.code == "REFERENCE_MANIFEST_INVALID"


def test_missing_grant_refused(tmp_path):
    path, gallery = _manifest(tmp_path, [("g1.js", "x", {})],
                              grant={"purpose": "TRAINING", "sourceRevision": "s",
                                     "recordedBy": "t"})
    with pytest.raises(contracts.ContractError) as exc_info:
        load_manifest(path, gallery)
    assert exc_info.value.code == "REFERENCE_GRANT_MISSING"


def test_private_entry_with_attribution_refused(tmp_path):
    path, gallery = _manifest(
        tmp_path, [("g1.js", "x", {"sensitivity": "PRIVATE", "origin": "FOREIGN",
                                   "attribution": {"label": "leak"}})])
    with pytest.raises(contracts.ContractError) as exc_info:
        load_manifest(path, gallery)
    assert exc_info.value.code == "REFERENCE_MANIFEST_INVALID"


def test_public_entry_without_attribution_refused(tmp_path):
    path, gallery = _manifest(tmp_path, [("g1.js", "x", {"attribution": {}})])
    with pytest.raises(contracts.ContractError) as exc_info:
        load_manifest(path, gallery)
    assert exc_info.value.code == "REFERENCE_MANIFEST_INVALID"


def test_scancode_concern_is_not_a_tool_failure():
    payload = {"files": [
        {"path": "/tmp/staged/a.js", "scan_errors": [],
         "license_detections": [{"license_expression_spdx": "MIT"}],
         "copyrights": [{"copyright": "2024 Someone"}],
         "package_data": []},
        {"path": "/tmp/staged/b.js", "scan_errors": [],
         "license_detections": [], "copyrights": [], "package_data": []},
    ]}
    rows, errors = _normalize_scancode(
        payload, {"a.js": "src/a.js", "b.js": "src/b.js"})
    assert errors == []
    assert {r["path"]: r["expression"] for r in rows} == {
        "src/a.js": "MIT", "src/b.js": "UNKNOWN"}


def test_scancode_file_error_is_a_tool_failure():
    payload = {"files": [
        {"path": "/tmp/staged/a.js", "scan_errors": ["timeout"],
         "license_detections": [], "copyrights": [], "package_data": []},
    ]}
    _rows, errors = _normalize_scancode(payload, {"a.js": "src/a.js"})
    assert errors == ["src/a.js"]


def _nested_manifest(tmp_path):
    from app.reference_index import load_manifest
    gallery = tmp_path / "gallery"
    (gallery / "pkg").mkdir(parents=True)
    (gallery / "lib").mkdir(parents=True)
    entries = []
    for rel in ("pkg/Widget.tsx", "lib/Widget.tsx"):
        data = f"// {rel}\nexport const v{len(entries)} = {len(entries)};\n".encode()
        (gallery / rel).write_bytes(data)
        entries.append({
            "fileId": contracts.sha256_hex(b"file:" + rel.encode()),
            "sha256": contracts.sha256_hex(data),
            "languageGroup": "typescript", "dialect": "tsx",
            "sensitivity": "PUBLIC", "origin": "FOREIGN",
            "stagedName": rel,
            "attribution": {"label": f"synthetic {rel}"}})
    manifest = {"schema": "code-reference-snapshot-v1",
                "snapshotId": contracts.sha256_hex(b"nested"),
                "revision": "nested-v1",
                "modelKey": contracts.sha256_hex(b"m"),
                "indexKey": contracts.sha256_hex(b"i"),
                "partition": "train",
                "files": entries,
                "grant": {"purpose": "COMPARISON", "sourceRevision": "nested-v1",
                         "recordedBy": "test"}}
    path = tmp_path / "snapshot.json"
    path.write_text(json.dumps(manifest))
    return load_manifest(path, gallery)


def test_nested_same_basename_files_read_distinctly(tmp_path):
    from app.reference_index import read_gallery_bytes
    snapshot = _nested_manifest(tmp_path)
    assert snapshot.files[0].staged_rel_path == "pkg/Widget.tsx"
    assert snapshot.files[1].staged_rel_path == "lib/Widget.tsx"
    first = read_gallery_bytes(snapshot, snapshot.files[0])
    second = read_gallery_bytes(snapshot, snapshot.files[1])
    assert first != second
    assert b"pkg/Widget.tsx" in first
    assert b"lib/Widget.tsx" in second


def test_traversal_staged_name_rejected(tmp_path):
    from app.reference_index import load_manifest
    gallery = tmp_path / "gallery"
    gallery.mkdir()
    (gallery / "ok.js").write_bytes(b"x")
    manifest = {"schema": "code-reference-snapshot-v1",
                "snapshotId": contracts.sha256_hex(b"nested"),
                "revision": "nested-v1",
                "modelKey": contracts.sha256_hex(b"m"),
                "indexKey": contracts.sha256_hex(b"i"),
                "partition": "train",
                "files": [{
                    "fileId": contracts.sha256_hex(b"evil"),
                    "sha256": contracts.sha256_hex(b"x"),
                    "languageGroup": "javascript", "dialect": "javascript",
                    "sensitivity": "PUBLIC", "origin": "FOREIGN",
                    "stagedName": "../evil.js",
                    "attribution": {"label": "evil"}}],
                "grant": {"purpose": "COMPARISON", "sourceRevision": "s",
                         "recordedBy": "t"}}
    path = tmp_path / "snapshot.json"
    path.write_text(json.dumps(manifest))
    with pytest.raises(contracts.ContractError) as exc_info:
        load_manifest(path, gallery)
    assert exc_info.value.code == "REFERENCE_MANIFEST_INVALID"
