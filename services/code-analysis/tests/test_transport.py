"""Transport: auth precedes materialization; hash binding; honest failures."""

import logging

from conftest import TEST_SECRET, make_body, make_metadata, make_zip

from app import contracts as contracts_module
from app import policy as policy_module


def test_missing_secret_is_rejected_before_materialization(client, workspace_root):
    archive = make_zip({"a.js": b"function a() {}\n"})
    body, content_type = make_body(make_metadata(archive), archive)
    response = client.post("/internal/analysis", content=body,
                           headers={"content-type": content_type})
    assert response.status_code == 401
    assert response.json()["errorCode"] == "AUTH_MISSING"
    assert list(workspace_root.iterdir()) == []


def test_wrong_secret_is_rejected(client):
    archive = make_zip({"a.js": b"function a() {}\n"})
    body, content_type = make_body(make_metadata(archive), archive)
    response = client.post("/internal/analysis", content=body,
                           headers={"content-type": content_type,
                                    "x-analysis-secret": "wrong-secret-value-00000000000000"})
    assert response.status_code == 401
    assert response.json()["errorCode"] == "AUTH_INVALID"


def test_oversized_body_rejected(client):
    response = client.post(
        "/internal/analysis", content=b"x",
        headers={"content-type": "multipart/form-data; boundary=b",
                 "content-length": str(200 * 1024 * 1024),
                 "x-analysis-secret": TEST_SECRET})
    assert response.status_code == 413


def _post(client, metadata, archive):
    body, content_type = make_body(metadata, archive)
    return client.post("/internal/analysis", content=body,
                       headers={"content-type": content_type,
                                "x-analysis-secret": TEST_SECRET})


def test_content_hash_drift_conflicts(client):
    archive = make_zip({"a.js": b"function a() {}\n"})
    metadata = make_metadata(archive)
    tampered = make_zip({"a.js": b"function b() {}\n"})
    response = _post(client, metadata, tampered)
    assert response.status_code == 409
    assert response.json()["errorCode"] == "DIGEST_MISMATCH"


def test_input_digest_drift_conflicts(client):
    archive = make_zip({"a.js": b"function a() {}\n"})
    metadata = make_metadata(archive)
    metadata["materialDigest"] = "f" * 64
    response = _post(client, metadata, archive)
    assert response.status_code == 409


def _assert_unavailable(client, workspace_root, archive, metadata, body, content_type):
    from conftest import TEST_SECRET as _SECRET
    response = client.post("/internal/analysis", content=body,
                           headers={"content-type": content_type,
                                    "x-analysis-secret": _SECRET})
    assert response.status_code == 200
    assert response.json()["outcome"] == "FAILED"
    assert response.json()["errorCode"] == "REFERENCE_GRANT_MISSING"
    assert response.json()["policy"]["canAuthorizePublication"] is False
    assert list(workspace_root.iterdir()) == []
    followup = client.post("/internal/analysis", content=body,
                           headers={"content-type": content_type,
                                    "x-analysis-secret": _SECRET})
    assert followup.status_code == 200
    assert followup.json()["outcome"] == "FAILED"
    return response


def test_missing_toolchain_fails_honestly_and_cleans_up(client, workspace_root):
    archive = make_zip({"src/a.js": b"function alpha() { return 1; }\n"})
    metadata = make_metadata(archive)
    body, content_type = make_body(metadata, archive)
    _assert_unavailable(client, workspace_root, archive, metadata, body, content_type)


def test_snapshot_without_tools_fails_tool_missing(client, workspace_root, tmp_path,
                                                   app_config):
    import json
    from app import contracts as contracts_module
    gallery = tmp_path / "gallery"
    gallery.mkdir()
    (gallery / "g.js").write_bytes(b"function g() { return 1; }\n")
    manifest = {"schema": "code-reference-snapshot-v1",
                "snapshotId": contracts_module.sha256_hex(b"snap"),
                "revision": "t-v1",
                "modelKey": contracts_module.sha256_hex(b"m"),
                "indexKey": contracts_module.sha256_hex(b"i"),
                "partition": "train",
                "files": [{"fileId": contracts_module.sha256_hex(b"f1"),
                           "sha256": contracts_module.sha256_hex(
                               b"function g() { return 1; }\n"),
                           "languageGroup": "javascript", "dialect": "javascript",
                           "sensitivity": "PUBLIC", "origin": "FOREIGN",
                           "stagedName": "g.js",
                           "attribution": {"label": "synthetic"}}],
                "grant": {"purpose": "COMPARISON", "sourceRevision": "t-v1",
                         "recordedBy": "test"}}
    manifest_path = tmp_path / "snapshot.json"
    manifest_path.write_text(json.dumps(manifest))
    app_config.reference_manifest = manifest_path
    app_config.gallery_root = gallery
    archive = make_zip({"src/a.js": b"function alpha() { return 1; }\n"})
    metadata = make_metadata(archive)
    validated = contracts_module.validate_metadata(metadata)
    metadata["inputDigest"] = contracts_module.compute_analysis_digest(
        validated, policy_module.POLICY_DIGEST,
        {"snapshotId": manifest["snapshotId"], "modelKey": manifest["modelKey"],
         "indexKey": manifest["indexKey"]})
    response = _post(client, metadata, archive)
    assert response.status_code == 200
    assert response.json()["outcome"] == "FAILED"
    assert response.json()["errorCode"] == "TOOL_MISSING"
    assert list(workspace_root.iterdir()) == []


def test_sentinel_content_absent_from_response_and_logs(client, workspace_root,
                                                         caplog):
    sentinel = "SENTINEL-PRIVATE-CONTENT-9f8e7d6c5b4a"
    archive = make_zip({"src/secret-holder.js":
                        f"function hidden() {{ return '{sentinel}'; }}\n".encode()})
    metadata = make_metadata(archive)
    body, content_type = make_body(metadata, archive)
    with caplog.at_level(logging.INFO, logger="code-analysis"):
        response = _assert_unavailable(client, workspace_root, archive, metadata,
                                   body, content_type)
    assert sentinel not in response.text
    for record in caplog.records:
        assert sentinel not in record.getMessage()
