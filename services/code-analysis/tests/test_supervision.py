"""Ingress gate, deadlines, supervisor behavior, and safe logging."""

import logging
import sys
import time

import pytest

from conftest import TEST_SECRET, make_body, make_metadata, make_zip

from app import contracts
from app import policy as policy_module
from app.process_tree import pid_alive, run_native
from app.service import _cancelled_envelope, _cleanup_failed_envelope


def _post(client, metadata, archive, secret=TEST_SECRET):
    body, content_type = make_body(metadata, archive)
    return client.post("/internal/analysis", content=body,
                       headers={"content-type": content_type,
                                "x-analysis-secret": secret})


def test_expired_reservation_rejected_before_work(client, workspace_root):
    archive = make_zip({"a.js": b"function a() {}\n"})
    metadata = make_metadata(archive)
    expired = dict(metadata)
    expired["reservation"] = {**metadata["reservation"],
                              "hardDeadlineUtc": "2020-01-01T00:00:00Z"}
    validated = contracts.validate_metadata(expired)
    expired["inputDigest"] = contracts.compute_analysis_digest(
        validated, policy_module.POLICY_DIGEST, None)
    response = _post(client, expired, archive)
    assert response.status_code == 409
    assert list(workspace_root.iterdir()) == []


def test_naive_deadline_rejected_as_invalid():
    with pytest.raises(contracts.ContractError) as exc_info:
        contracts.parse_reservation_deadline("2030-01-01 00:00:00")
    assert exc_info.value.code == "RESERVATION_INVALID"
    assert contracts.parse_reservation_deadline("2030-01-01T00:00:00Z") > 0


def test_pending_cleanup_blocks_new_work(service_app, workspace_root):
    from fastapi.testclient import TestClient
    service_app.state.pipeline_state["pending_cleanups"].add("stuck-path")
    client = TestClient(service_app)
    archive = make_zip({"a.js": b"function a() {}\n"})
    response = _post(client, make_metadata(archive), archive)
    assert response.status_code == 503


def test_supervisor_kills_uncooperative_tree(tmp_path):
    from app.supervisor import await_worker, confirm_stopped, spawn_worker, worker_env
    import asyncio

    async def scenario():
        pid_file = tmp_path / "grandchild.pid"
        script = (
            "import subprocess, sys, time\n"
            "grandchild = subprocess.Popen([sys.executable, '-c', "
            "'import time; time.sleep(120)'])\n"
            f"open(r'{pid_file}', 'w').write(str(grandchild.pid))\n"
            "time.sleep(120)\n"
        )
        work = tmp_path / "work"
        work.mkdir()
        argv = [sys.executable, "-c", script]
        import subprocess
        if sys.platform == "win32":
            proc = subprocess.Popen(argv, cwd=str(work), shell=False,
                                    stdout=subprocess.DEVNULL,
                                    stderr=subprocess.DEVNULL,
                                    creationflags=subprocess.CREATE_NEW_PROCESS_GROUP)
        else:
            proc = subprocess.Popen(argv, cwd=str(work), shell=False,
                                    stdout=subprocess.DEVNULL,
                                    stderr=subprocess.DEVNULL,
                                    start_new_session=True)
        deadline = time.perf_counter() + 10
        while not pid_file.exists() and time.perf_counter() < deadline:
            await asyncio.sleep(0.05)
        assert pid_file.exists(), "tree must be running before timeout"
        reason = await await_worker(proc, time.time() - 1, lambda: False,
                                    _never_disconnected)
        assert reason == "TIMEOUT"
        assert confirm_stopped(proc.pid)
        grandchild_pid = int(pid_file.read_text())
        assert not pid_alive(grandchild_pid)

    asyncio.run(scenario())


async def _never_disconnected():
    return False


def test_noisy_child_hits_incremental_cap_without_ram_blowup(tmp_path):
    script = ("import sys, time\n"
              "block = b'x' * 65536\n"
              "for _ in range(2000):\n"
              "    try:\n"
              "        sys.stdout.buffer.write(block)\n"
              "    except BrokenPipeError:\n"
              "        break\n")
    started = time.perf_counter()
    result = run_native([sys.executable, "-c", script], cwd=tmp_path,
                        timeout_seconds=120, max_output_bytes=262144)
    assert result.output_overflow is True
    assert time.perf_counter() - started < 90
    assert len(result.stdout) + len(result.stderr) <= 262144


def test_worker_failure_keeps_sentinel_out_of_response_and_logs(client, caplog):
    # The exception fires inside the spawned worker (binary supported file
    # whose name carries the sentinel): the failure detail must never reach
    # the response or the logs, only the allowlisted error code.
    sentinel = "SENTINEL-EXCEPTION-4b7c2e91aa"
    archive = make_zip({f"src/{sentinel}.js": b"\x00\x01\x02binary"})
    metadata = make_metadata(archive)
    body, content_type = make_body(metadata, archive)
    from conftest import TEST_SECRET as _SECRET
    with caplog.at_level(logging.INFO, logger="code-analysis"):
        response = client.post("/internal/analysis", content=body,
                               headers={"content-type": content_type,
                                        "x-analysis-secret": _SECRET})
    assert response.status_code == 200
    assert response.json()["outcome"] == "FAILED"
    assert response.json()["errorCode"] == "EXTRACTION_FAILED"
    assert sentinel not in response.text
    for record in caplog.records:
        assert sentinel not in record.getMessage()


def test_cleanup_failure_overrides_success_with_withheld_findings():
    from types import SimpleNamespace
    metadata = SimpleNamespace(run_id="r", input_digest="d", content_sha256="c",
                               asset_id="a", version_id="v")
    completed = {"stages": [{"stage": "REPORT"}], "coverage": {"level": "FULL"},
                 "findingOverflow": 0}
    envelope = _cleanup_failed_envelope(metadata, completed)
    assert envelope["outcome"] == "FAILED"
    assert envelope["errorCode"] == "CLEANUP_FAILED"
    assert envelope["findings"] == []
    assert envelope["admittedBy"] is None


def test_parent_cleanup_failure_blocks_and_withholds(client, workspace_root,
                                                     monkeypatch):
    import app.workspace as workspace_module
    real_rmtree = workspace_module.shutil.rmtree

    def failing_rmtree(path, ignore_errors=False):
        real_rmtree(path, ignore_errors=ignore_errors)
        Path = __import__("pathlib").Path
        Path(path).mkdir(parents=True, exist_ok=True)

    monkeypatch.setattr(workspace_module.shutil, "rmtree", failing_rmtree)
    archive = make_zip({"src/a.js": b"function alpha() { return 1; }\n"})
    response = _post(client, make_metadata(archive), archive)
    assert response.status_code == 200
    assert response.json()["outcome"] == "FAILED"
    assert response.json()["errorCode"] == "CLEANUP_FAILED"
    assert response.json()["findings"] == []
    # The stuck workspace blocks subsequent work until the sweeper retries.
    followup = _post(client, make_metadata(archive), archive)
    assert followup.status_code == 503
    assert any(p.is_dir() for p in workspace_root.iterdir())


def test_unconfirmed_run_holds_gate_without_cleanup(service_app, workspace_root,
                                                   monkeypatch):
    import subprocess as _sp
    import app.service as service_module
    from fastapi.testclient import TestClient
    monkeypatch.setattr(service_module, "confirm_containment",
                        lambda *args, **kwargs: False)

    def stub_spawn(service_dir, workspace_dir, env):
        proc = _sp.Popen(
            [sys.executable, "-c", "import time; time.sleep(120)"],
            cwd=str(workspace_dir), shell=False,
            stdout=_sp.DEVNULL, stderr=_sp.DEVNULL,
            start_new_session=sys.platform != "win32")
        return proc, "test", None

    monkeypatch.setattr(service_module, "spawn_worker", stub_spawn)
    client = TestClient(service_app)
    archive = make_zip({"src/a.js": b"function alpha() { return 1; }\n"})
    metadata = make_metadata(archive)
    reservation = dict(metadata["reservation"])
    reservation["hardDeadlineUtc"] = "2030-01-01T00:00:05Z"
    import datetime as _dt
    soon = (_dt.datetime.now(_dt.timezone.utc) + _dt.timedelta(seconds=8)
            ).strftime("%Y-%m-%dT%H:%M:%SZ")
    reservation["hardDeadlineUtc"] = soon
    metadata["reservation"] = reservation
    from app import contracts as _contracts
    from app import policy as _policy
    validated = _contracts.validate_metadata(metadata)
    metadata["inputDigest"] = _contracts.compute_analysis_digest(
        validated, _policy.POLICY_DIGEST, None)
    first = _post(client, metadata, archive)
    assert first.status_code == 500
    state = service_app.state.pipeline_state
    assert len(state["unconfirmed"]) == 1
    # Workspace is fenced, not cleaned.
    kept = [p for p in workspace_root.iterdir() if p.is_dir()]
    assert len(kept) == 1
    # The gate stays held: the next request cannot start.
    second = _post(client, make_metadata(archive), archive)
    assert second.status_code == 503
    # The sweeper never clears fenced evidence.
    from app import workspace as workspace_module
    result = workspace_module.sweep_stale(workspace_root, set(state["unconfirmed"]))
    assert kept[0].exists()
    assert len(state["unconfirmed"]) == 1
    assert result["removed"] == 0
