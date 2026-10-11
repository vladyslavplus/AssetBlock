"""Multipart framing and bounded intake: order, stalls, boundary bytes."""

import asyncio
import time
from pathlib import Path

import pytest

from conftest import TEST_SECRET, make_body, make_metadata, make_zip

from app import contracts, transport
from app.service import _receive_intake


def _small_body(**overrides):
    archive = make_zip({"a.js": b"function a() {}\n"})
    metadata = make_metadata(archive)
    metadata.update(overrides)
    return make_body(metadata, archive)


def test_archive_first_order_rejected():
    archive = make_zip({"a.js": b"function a() {}\n"})
    metadata = make_metadata(archive)
    meta = contracts.canonical(metadata)
    boundary = "order-boundary-1"
    body = (
        f"--{boundary}\r\n"
        'Content-Disposition: form-data; name="archive"; filename="u.zip"\r\n'
        "Content-Type: application/octet-stream\r\n\r\n"
    ).encode() + archive + (
        f"\r\n--{boundary}\r\n"
        'Content-Disposition: form-data; name="metadata"\r\n'
        "Content-Type: application/json\r\n\r\n"
    ).encode() + meta + f"\r\n--{boundary}--\r\n".encode()
    with pytest.raises(contracts.ContractError) as exc_info:
        transport.parse_multipart(body, f"multipart/form-data; boundary={boundary}")
    assert exc_info.value.code == "MALFORMED_MULTIPART"


def test_unframed_boundary_bytes_inside_payload_accepted():
    boundary = "edge-boundary-9"
    archive = make_zip({"a.js": b"function a() {}\n"})
    metadata = make_metadata(archive)
    meta = contracts.canonical(metadata)
    # Unframed occurrences (no surrounding CRLF framing) are payload data.
    payload = b"xx--" + boundary.encode() + b"yy" + archive
    body = (
        f"--{boundary}\r\n"
        'Content-Disposition: form-data; name="metadata"\r\n'
        "Content-Type: application/json\r\n\r\n"
    ).encode() + meta + (
        f"\r\n--{boundary}\r\n"
        'Content-Disposition: form-data; name="archive"; filename="u.zip"\r\n'
        "Content-Type: application/octet-stream\r\n\r\n"
    ).encode() + payload + f"\r\n--{boundary}--\r\n".encode()
    parts = transport.parse_multipart(body, f"multipart/form-data; boundary={boundary}")
    assert parts.archive == payload


def test_duplicate_part_rejected():
    archive = make_zip({"a.js": b"function a() {}\n"})
    metadata = make_metadata(archive)
    body, content_type = make_body(metadata, archive)
    boundary = content_type.split("boundary=")[1]
    meta = contracts.canonical(metadata)
    dup = (
        f"--{boundary}\r\n"
        'Content-Disposition: form-data; name="metadata"\r\n'
        "Content-Type: application/json\r\n\r\n"
    ).encode() + meta + (
        f"\r\n--{boundary}\r\n"
        'Content-Disposition: form-data; name="metadata"\r\n'
        "Content-Type: application/json\r\n\r\n"
    ).encode() + meta + f"\r\n--{boundary}--\r\n".encode()
    with pytest.raises(contracts.ContractError) as exc_info:
        transport.parse_multipart(dup, content_type)
    assert exc_info.value.code == "MALFORMED_MULTIPART"


def test_stalled_upload_releases_gate_and_cleans_up(tmp_path):
    from app.service import ServiceConfig
    from app import workspace as workspace_module
    from conftest import SERVICE_DIR, REPO_ROOT

    archive = make_zip({"a.js": b"function a() {}\n"})
    body, content_type = make_body(make_metadata(archive), archive)
    first = body[:200]

    class StallingRequest:
        async def stream(self):
            yield first
            await asyncio.sleep(30)

    root = tmp_path / "ws"
    root.mkdir()
    config = ServiceConfig(secret=TEST_SECRET, workspace_root=root,
                           service_dir=SERVICE_DIR,
                           worker_path_entries=(str(SERVICE_DIR), str(REPO_ROOT)),
                           upload_deadline_seconds=2.0)
    space = workspace_module.Workspace(root, "stall-test-run-id")
    try:
        with pytest.raises(contracts.ContractError) as exc_info:
            _run_receive(StallingRequest(), config, space, content_type)
        assert exc_info.value.code == "DEADLINE_EXCEEDED"
    finally:
        space.cleanup()
    assert list(root.iterdir()) == []


def _run_receive(request, config, space, content_type):
    import asyncio as _asyncio
    import time as _time
    return _asyncio.run(_receive_intake(
        request, config, space, space.path / "body.bin",
        content_type, _time.time(), "0" * 64))


def _chunked_request(body, sizes):
    chunks = []
    offset = 0
    index = 0
    while offset < len(body):
        size = sizes[index % len(sizes)]
        chunks.append(body[offset:offset + size])
        offset += size
        index += 1

    class ChunkedRequest:
        async def stream(self):
            for chunk in chunks:
                yield chunk
    return ChunkedRequest()


def _receive_config(tmp_path, root_name="ws"):
    from app.service import ServiceConfig
    from conftest import SERVICE_DIR, REPO_ROOT
    root = tmp_path / root_name
    root.mkdir(exist_ok=True)
    return ServiceConfig(secret=TEST_SECRET, workspace_root=root,
                         service_dir=SERVICE_DIR,
                         worker_path_entries=(str(SERVICE_DIR), str(REPO_ROOT)),
                         upload_deadline_seconds=30.0), root


def test_identical_body_identical_intake_across_chunkings(tmp_path):
    from app import workspace as workspace_module
    archive = make_zip({"a.js": b"function a() {}\n"})
    body, content_type = make_body(make_metadata(archive), archive)
    outcomes = []
    for sizes in ([1], [7], [64, 3, 5000], [len(body)]):
        config, root = _receive_config(tmp_path, f"ws-{sizes[0]}")
        space = workspace_module.Workspace(root, "chunk-matrix-run")
        try:
            intake = _run_receive(_chunked_request(body, sizes), config, space,
                                  content_type)
            staged = (space.path / "body.bin").read_bytes()
            outcomes.append((intake["early_metadata"]["runId"],
                             intake["total_bytes"], staged == body))
        finally:
            space.cleanup()
    assert outcomes and all(out == outcomes[0] for out in outcomes)
    assert outcomes[0][1] == len(body)
    assert outcomes[0][2] is True


def test_prefix_cap_without_metadata_fails(tmp_path):
    from app import workspace as workspace_module
    boundary = "cap-boundary-3"
    # Archive-first bulk: the first part completes but is not metadata.
    bulk = b"z" * (transport.SCAN_PREFIX_CAP + 1024)
    body = (
        f"--{boundary}\r\n"
        'Content-Disposition: form-data; name="archive"; filename="u.zip"\r\n'
        "Content-Type: application/octet-stream\r\n\r\n"
    ).encode() + bulk + f"\r\n--{boundary}--\r\n".encode()
    content_type = f"multipart/form-data; boundary={boundary}"

    class BulkRequest:
        async def stream(self):
            yield body

    config, root = _receive_config(tmp_path, "ws-cap")
    from app import workspace as workspace_module2
    space = workspace_module.Workspace(root, "cap-run")
    try:
        with pytest.raises(contracts.ContractError) as exc_info:
            _run_receive(BulkRequest(), config, space, content_type)
        assert exc_info.value.code == "MALFORMED_MULTIPART"
    finally:
        space.cleanup()
    assert list(root.iterdir()) == []


def test_split_deadlines_keeps_upload_out_of_execution():
    from app.service import ServiceConfig, split_deadlines
    from conftest import SERVICE_DIR, REPO_ROOT
    config = ServiceConfig(secret="x" * 32, workspace_root=Path("."),
                           service_dir=SERVICE_DIR,
                           worker_path_entries=(),
                           upload_deadline_seconds=300.0,
                           overall_deadline_seconds=1080.0)
    started = 1000000.0
    intake, execution = split_deadlines(started, started + 100000.0, config)
    assert intake == started + 300.0
    assert execution == started + 1080.0


def test_split_deadlines_reservation_binds_both():
    from app.service import ServiceConfig, split_deadlines
    from conftest import SERVICE_DIR
    config = ServiceConfig(secret="x" * 32, workspace_root=Path("."),
                           service_dir=SERVICE_DIR,
                           worker_path_entries=(),
                           upload_deadline_seconds=300.0,
                           overall_deadline_seconds=1080.0)
    started = 1000000.0
    intake, execution = split_deadlines(started, started + 100.0, config)
    assert intake == started + 100.0
    assert execution == started + 100.0


def test_split_deadlines_overall_caps_intake():
    from app.service import ServiceConfig, split_deadlines
    from conftest import SERVICE_DIR
    config = ServiceConfig(secret="x" * 32, workspace_root=Path("."),
                           service_dir=SERVICE_DIR,
                           worker_path_entries=(),
                           upload_deadline_seconds=300.0,
                           overall_deadline_seconds=60.0)
    started = 1000000.0
    intake, execution = split_deadlines(started, started + 100000.0, config)
    assert intake == started + 60.0
    assert execution == started + 60.0


def test_bootstrap_write_failure_fences_worker(
        service_app, workspace_root, tmp_path, monkeypatch):
    """Bootstrap request.json write fails after a live spawn.

    The fenced finalizer still stops and confirms the worker: no live
    descendants after release, workspace cleaned, gate free. The spawn
    itself is stubbed with a real sleeping process because this host
    cannot attach job containment; stop/confirm/cleanup are all real.
    """
    import asyncio as _asyncio
    import pathlib as _pathlib
    import subprocess as _sp
    import sys as _sys
    import time as _time
    import app.service as service_module
    from conftest import make_metadata, make_zip
    from app import contracts

    real_write_text = _pathlib.Path.write_text

    def failing_write(self, *args, **kwargs):
        if self.name == "request.json":
            raise OSError("injected bootstrap failure")
        return real_write_text(self, *args, **kwargs)

    monkeypatch.setattr(_pathlib.Path, "write_text", failing_write)
    long_proc = _sp.Popen(
        [_sys.executable, "-c", "import time; time.sleep(120)"],
        cwd=str(tmp_path), shell=False,
        stdout=_sp.DEVNULL, stderr=_sp.DEVNULL)
    assert long_proc.pid > 0

    def fake_spawn(service_dir, workspace_dir, env):
        return long_proc, "test", None

    monkeypatch.setattr(service_module, "spawn_worker", fake_spawn)
    archive = make_zip({"src/a.js": b"function alpha() { return 1; }\n"})
    metadata = make_metadata(archive)
    body, content_type = make_body(metadata, archive)
    from conftest import TEST_SECRET
    from fastapi.testclient import TestClient
    client = TestClient(service_app)
    try:
        response = client.post("/internal/analysis", content=body,
                               headers={"content-type": content_type,
                                        "x-analysis-secret": TEST_SECRET})
        assert response.status_code == 200
        envelope = response.json()
        assert envelope["outcome"] == "FAILED"
        assert envelope["errorCode"] == "INTERNAL_ERROR"
        from app.process_tree import pid_alive
        assert not pid_alive(long_proc.pid)
        assert list(workspace_root.iterdir()) == []
        followup = client.post("/internal/analysis", content=body,
                               headers={"content-type": content_type,
                                        "x-analysis-secret": TEST_SECRET})
        assert followup.status_code == 200
    finally:
        if long_proc.poll() is None:
            long_proc.kill()
