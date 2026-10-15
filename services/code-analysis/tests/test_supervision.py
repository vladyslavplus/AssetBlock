"""Ingress gate, deadlines, supervisor behavior, and safe logging."""

import logging
import sys
import time

import pytest

from conftest import (REPO_ROOT, SERVICE_DIR, TEST_SECRET, make_body,
                      make_metadata, make_zip)

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
    from app.supervisor import (await_worker, close_job_handle,
                                confirm_containment, spawn_worker, worker_env)
    import asyncio
    from pathlib import Path

    async def scenario():
        pid_file = tmp_path / "grandchild.pid"
        script = (
            "import subprocess, sys, time\n"
            "grandchild = subprocess.Popen([sys.executable, '-c', "
            "'import time; time.sleep(120)'])\n"
            f"open(r'{pid_file}', 'w').write(str(grandchild.pid))\n"
            "time.sleep(120)\n"
        )
        service_dir = tmp_path / "contained-service"
        app_dir = service_dir / "app"
        app_dir.mkdir(parents=True)
        (app_dir / "worker.py").write_text(script, encoding="utf-8")
        work = tmp_path / "work"
        work.mkdir()
        proc, mode, job = spawn_worker(service_dir, work,
                                       worker_env(service_dir, ()))
        assert mode == ("job" if sys.platform == "win32" else "group")
        try:
            deadline = time.perf_counter() + 10
            while not pid_file.exists() and time.perf_counter() < deadline:
                await asyncio.sleep(0.05)
            assert pid_file.exists(), "tree must be running before timeout"
            reason = await await_worker(proc, time.time() - 1, lambda: False,
                                        _never_disconnected)
            assert reason == "TIMEOUT"
            grandchild_pid = int(pid_file.read_text())
            assert confirm_containment(
                proc.pid, proc.pid if sys.platform != "win32" else None,
                (grandchild_pid,), job) is True
        finally:
            if proc.poll() is None:
                from app.supervisor import stop_containment
                stop_containment(proc)
            close_job_handle(proc)
    asyncio.run(scenario())


def test_completed_worker_stops_leftover_contained_descendant(
        tmp_path, service_app, workspace_root, monkeypatch):
    from fastapi.testclient import TestClient
    import app.service as service_module
    from app.supervisor import spawn_worker as real_spawn_worker

    pid_file = tmp_path / "grandchild.pid"
    service_dir = tmp_path / "completed-service"
    app_dir = service_dir / "app"
    app_dir.mkdir(parents=True)
    worker_script = """
import json, pathlib, subprocess, sys, time
workspace = pathlib.Path(sys.argv[1])
while not (workspace / "request.json").exists():
    time.sleep(0.01)
request = json.loads((workspace / "request.json").read_text())
metadata = request["metadata"]
(workspace / "envelope.json").write_text(json.dumps({
    "schema": "code-analysis-v1",
    "runId": metadata["runId"],
    "inputDigest": metadata["inputDigest"],
}))
child = subprocess.Popen([sys.executable, "-c", "import time; time.sleep(120)"])
""" + f"\npathlib.Path({str(pid_file)!r}).write_text(str(child.pid))\n"
    (app_dir / "worker.py").write_text(worker_script, encoding="utf-8")

    def spawn(_service_dir, workspace_dir, env):
        return real_spawn_worker(service_dir, workspace_dir, env)

    monkeypatch.setattr(service_module, "spawn_worker", spawn)
    client = TestClient(service_app)
    archive = make_zip({"src/a.js": b"function alpha() { return 1; }\n"})
    response = _post(client, make_metadata(archive), archive)

    assert response.status_code == 200
    assert pid_file.exists()
    assert not pid_alive(int(pid_file.read_text()))
    assert service_app.state.pipeline_state["unconfirmed"] == set()


def test_windows_worker_environment_uses_fixed_identity(tmp_path):
    from app.supervisor import worker_env

    service_dir = tmp_path / "service"
    service_dir.mkdir()
    env = worker_env(service_dir, ())
    if sys.platform == "win32":
        assert env["USERNAME"] == "code-analysis"
    else:
        assert "USERNAME" not in env


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
    from app.contracts import APPLICABILITY, STAGE_OUTCOME

    metadata = SimpleNamespace(run_id="r", input_digest="d", content_sha256="c",
                               asset_id="a", version_id="v")
    stages = [{"stage": name, "applicability": APPLICABILITY.MANDATORY.value,
               "outcome": STAGE_OUTCOME.SUCCEEDED.value, "retryable": False,
               "errorCode": None}
              for name in ("INVENTORY", "EXTRACTION", "TOKEN", "SEMANTIC",
                           "LICENSE", "UNION", "VERIFIER", "REPORT")]
    completed = {"stages": stages, "coverage": {"level": "FULL", "analyzedFiles": 1,
                 "omissions": [], "omissionOverflow": 0}, "findingOverflow": 2,
                 "policy": {"policyVersion": "p", "policyDigest": "a" * 64,
                            "verifierMode": "PENDING_RESEARCH_EVALUATION",
                            "canAuthorizePublication": False},
                 "securityEvidence": {"contentSha256": "c", "archiveEvidenceId": "archive",
                                      "malwareEvidenceId": "malware", "securityGeneration": 3},
                 "reservation": {"serviceKey": "code-analysis", "slotNumber": 0,
                                 "generation": 2}}
    envelope = _cleanup_failed_envelope(metadata, completed)
    assert envelope["outcome"] == "FAILED"
    assert envelope["errorCode"] == "CLEANUP_FAILED"
    assert envelope["failedStage"] == "REPORT"
    assert envelope["retryable"] is False
    assert [stage["outcome"] for stage in envelope["stages"]] == ["SUCCEEDED"] * 8
    assert envelope["findings"] == []
    assert envelope["findingOverflow"] == 2
    assert envelope["securityEvidence"] == completed["securityEvidence"]
    assert envelope["reservation"] == completed["reservation"]
    assert envelope["admittedBy"] is None
    assert envelope["policy"] == completed["policy"]
    assert envelope["coverage"] == completed["coverage"]
    assert envelope["cleanupError"] == "CLEANUP_FAILED"


def test_cleanup_failure_preserves_an_already_failed_computational_stage():
    from types import SimpleNamespace
    from app.contracts import APPLICABILITY, STAGE_OUTCOME

    metadata = SimpleNamespace(run_id="r", input_digest="d", content_sha256="c",
                               asset_id="a", version_id="v")
    stages = [{"stage": name, "applicability": APPLICABILITY.MANDATORY.value,
               "outcome": STAGE_OUTCOME.SUCCEEDED.value, "retryable": False,
               "errorCode": None}
              for name in ("INVENTORY", "EXTRACTION", "TOKEN", "SEMANTIC",
                           "LICENSE", "UNION", "VERIFIER", "REPORT")]
    stages[2].update(outcome=STAGE_OUTCOME.FAILED.value, errorCode="TOKENIZER_UNAVAILABLE",
                     retryable=True)
    failed = {"outcome": "FAILED", "errorCode": "TOKENIZER_UNAVAILABLE",
              "failedStage": "TOKEN", "retryable": True, "stages": stages,
              "policy": {"policyVersion": "p", "policyDigest": "a" * 64,
                         "verifierMode": "PENDING_RESEARCH_EVALUATION",
                         "canAuthorizePublication": False},
              "securityEvidence": {"contentSha256": "c", "archiveEvidenceId": "archive",
                                   "malwareEvidenceId": "malware", "securityGeneration": 3},
              "reservation": {"serviceKey": "code-analysis", "slotNumber": 0,
                              "generation": 2},
              "coverage": {"level": "PARTIAL", "analyzedFiles": 1,
                           "omissions": [], "omissionOverflow": 0},
              "findingOverflow": 0, "findings": [], "admittedBy": None,
              "cleanupError": None}

    envelope = _cleanup_failed_envelope(metadata, failed)

    assert envelope["errorCode"] == "CLEANUP_FAILED"
    assert envelope["failedStage"] == "REPORT"
    assert envelope["retryable"] is False
    assert envelope["stages"] == stages
    assert envelope["stages"][2]["errorCode"] == "TOKENIZER_UNAVAILABLE"
    assert envelope["stages"][2]["outcome"] == STAGE_OUTCOME.FAILED.value
    assert envelope["coverage"] == failed["coverage"]
    assert envelope["findings"] == []
    assert envelope["admittedBy"] is None
    assert envelope["cleanupError"] == "CLEANUP_FAILED"


def _install_contract_faithful_cleanup_worker(app_config, tmp_path):
    """Use a real supervised child with a production-built synthetic envelope.

    This isolates parent cleanup behavior from host tool, model, and index
    availability. The child is test-only and cannot publish or admit a report.
    """
    worker_root = tmp_path / "contract-worker"
    worker_app = worker_root / "app"
    worker_app.mkdir(parents=True)
    (worker_app / "worker.py").write_text(
        '''import json, sys
from pathlib import Path
from types import SimpleNamespace

service_root = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(service_root))
from app import contracts, policy
from app.contracts import APPLICABILITY, STAGE_OUTCOME, StageRecord
from app.stages import Pipeline, _success_envelope

workdir = Path(sys.argv[1])
request = json.loads((workdir / "request.json").read_text(encoding="utf-8"))
metadata = contracts.validate_metadata(request["metadata"])
context = SimpleNamespace(metadata=metadata, policy=policy.current_policy(), snapshot=None)
pipe = Pipeline(context=context)
for name in ("INVENTORY", "EXTRACTION", "TOKEN", "SEMANTIC", "LICENSE", "UNION", "VERIFIER"):
    pipe.record(StageRecord(stage=name, applicability=APPLICABILITY.MANDATORY.value,
                            outcome=STAGE_OUTCOME.SUCCEEDED.value,
                            tool_identity={"fixture": "contract-faithful-supervised-child"}))
envelope = _success_envelope(pipe)
(workdir / "envelope.json").write_bytes(contracts.canonical(envelope))
''', encoding="utf-8")
    app_config.service_dir = worker_root
    app_config.worker_path_entries = (str(SERVICE_DIR), str(REPO_ROOT))


def test_parent_cleanup_failure_blocks_and_withholds(client, app_config,
                                                     workspace_root, tmp_path,
                                                     monkeypatch):
    import app.workspace as workspace_module
    real_rmtree = workspace_module.shutil.rmtree

    _install_contract_faithful_cleanup_worker(app_config, tmp_path)
    archive = make_zip({"src/a.js": b"function alpha() { return 1; }\n"})

    # Prove same supervised child graph succeeds before inducing cleanup fault.
    baseline = _post(client, make_metadata(archive), archive)
    assert baseline.status_code == 200
    assert baseline.json()["outcome"] == "COMPLETED"
    assert [stage["outcome"] for stage in baseline.json()["stages"]] == ["SUCCEEDED"] * 8
    assert baseline.json()["policy"]["verifierMode"] == "PENDING_RESEARCH_EVALUATION"
    assert baseline.json()["policy"]["canAuthorizePublication"] is False
    assert baseline.json()["admittedBy"] is None

    def failing_rmtree(path, ignore_errors=False):
        real_rmtree(path, ignore_errors=ignore_errors)
        Path = __import__("pathlib").Path
        Path(path).mkdir(parents=True, exist_ok=True)

    monkeypatch.setattr(workspace_module.shutil, "rmtree", failing_rmtree)
    response = _post(client, make_metadata(archive), archive)
    assert response.status_code == 200
    assert response.json()["outcome"] == "FAILED"
    assert response.json()["errorCode"] == "CLEANUP_FAILED"
    assert response.json()["findings"] == []
    assert response.json()["failedStage"] == "REPORT"
    assert response.json()["retryable"] is False
    assert [stage["outcome"] for stage in response.json()["stages"]] == ["SUCCEEDED"] * 8
    assert response.json()["policy"]["verifierMode"] == "PENDING_RESEARCH_EVALUATION"
    assert response.json()["policy"]["canAuthorizePublication"] is False
    assert response.json()["admittedBy"] is None
    assert response.json()["cleanupError"] == "CLEANUP_FAILED"
    # The stuck workspace blocks subsequent work until the sweeper retries.
    followup = _post(client, make_metadata(archive), archive)
    assert followup.status_code == 503
    assert any(p.is_dir() for p in workspace_root.iterdir())


def test_unconfirmed_run_holds_gate_without_cleanup(service_app, workspace_root,
                                                   monkeypatch):
    import subprocess as _sp
    import app.service as service_module
    from fastapi.testclient import TestClient
    spawned = []
    monkeypatch.setattr(service_module, "confirm_containment",
                        lambda *args, **kwargs: False)

    def stub_spawn(service_dir, workspace_dir, env):
        proc = _sp.Popen(
            [sys.executable, "-c", "import time; time.sleep(120)"],
            cwd=str(workspace_dir), shell=False,
            stdout=_sp.DEVNULL, stderr=_sp.DEVNULL,
            start_new_session=sys.platform != "win32")
        spawned.append(proc)
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
    try:
        first = _post(client, metadata, archive)
        assert first.status_code == 500
        state = service_app.state.pipeline_state
        assert len(state["unconfirmed"]) == 1
        # This deliberately uncontained fixture models denied termination:
        # workspace stays fenced while its known Popen handle remains live.
        kept = [p for p in workspace_root.iterdir() if p.is_dir()]
        assert len(kept) == 1
        assert any(proc.poll() is None for proc in spawned)
        second = _post(client, make_metadata(archive), archive)
        assert second.status_code == 503
        from app import workspace as workspace_module
        result = workspace_module.sweep_stale(workspace_root, set(state["unconfirmed"]))
        assert kept[0].exists()
        assert len(state["unconfirmed"]) == 1
        assert result["removed"] == 0
    finally:
        # Reap only the synthetic process handles created by this test.
        for proc in spawned:
            if proc.poll() is None:
                proc.kill()
                proc.wait(timeout=10)
