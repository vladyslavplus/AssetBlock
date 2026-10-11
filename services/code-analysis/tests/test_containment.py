"""Containment regressions: shared group/job kill, fail-closed, live tools."""

import subprocess
import sys
import time
from pathlib import Path

import pytest

from app.process_tree import pid_alive
from app.supervisor import (assign_to_job, close_job, confirm_containment,
                            create_job, job_active_processes,
                            read_tool_pids, stop_containment)


def test_job_assignment_opens_process_with_required_quota_and_terminate_rights():
    class Kernel:
        def __init__(self):
            self.calls = []

        def OpenProcess(self, access, inherit, process_id):
            self.calls.append((access, inherit, process_id))
            return 17

        def AssignProcessToJobObject(self, job, process):
            assert (job, process) == (23, 17)
            return True

        def CloseHandle(self, handle):
            self.calls.append(handle)

    kernel = Kernel()
    assert assign_to_job((23, kernel), 101)
    assert kernel.calls == [(0x0101, False, 101), 17]


@pytest.mark.skipif(sys.platform == "win32", reason="POSIX process groups")
def test_native_tool_and_grandchild_join_explicit_worker_group(tmp_path):
    import json
    import os
    import signal
    service_dir = Path(__file__).resolve().parent.parent
    marker = tmp_path / "group.json"
    child = (
        "import os,json,subprocess,sys,time\n"
        "grandchild=subprocess.Popen([sys.executable,'-c','import time;time.sleep(120)'])\n"
        f"open({str(marker)!r},'w').write(json.dumps([os.getpid(),os.getpgid(0),grandchild.pid]))\n"
        "time.sleep(120)\n"
    )
    worker = tmp_path / "group_worker.py"
    worker.write_text(
        "import os,sys\n"
        f"sys.path.insert(0,{str(service_dir)!r})\n"
        "from app.process_tree import run_native,minimal_env,GROUP_ENV_VAR\n"
        "os.environ[GROUP_ENV_VAR]=str(os.getpid())\n"
        "env=minimal_env()\n"
        "assert env[GROUP_ENV_VAR]==str(os.getpid())\n"
        f"run_native([sys.executable,'-c',{child!r}],cwd=__import__('pathlib').Path({str(tmp_path)!r}),"
        "timeout_seconds=120,env=env,containment_pgid=os.getpid())\n"
    )
    proc = subprocess.Popen([sys.executable, str(worker)], start_new_session=True)
    try:
        _wait_file(marker, 15)
        tool, group, grandchild = json.loads(marker.read_text())
        assert group == proc.pid
        assert os.getpgid(grandchild) == proc.pid
        os.killpg(proc.pid, signal.SIGKILL)
        proc.wait(timeout=10)
        assert confirm_containment(proc.pid, proc.pid, (tool, grandchild))
    finally:
        if proc.poll() is None:
            os.killpg(proc.pid, signal.SIGKILL)
            proc.wait(timeout=10)


def _tree_script(pid_file: Path, grandchild_pid_file: Path) -> str:
    return (
        "import subprocess, sys, time\n"
        "open(r'" + str(pid_file) + "', 'w').write(str(__import__('os').getpid()))\n"
        "grandchild = subprocess.Popen([sys.executable, '-c', "
        "'import time; time.sleep(120)'])\n"
        "open(r'" + str(grandchild_pid_file) + "', 'w').write(str(grandchild.pid))\n"
        "time.sleep(120)\n"
    )


def _wait_file(path: Path, timeout: float = 30.0) -> None:
    deadline = time.perf_counter() + timeout
    while not path.exists():
        if time.perf_counter() > deadline:
            raise TimeoutError(f"never appeared: {path}")
        time.sleep(0.1)


def test_tool_tree_killed_and_confirmed_before_cleanup(tmp_path):
    import threading as _threading
    from app.process_tree import run_native
    from app.supervisor import confirm_containment as confirm
    from app.supervisor import kill_tree
    work = tmp_path / "work"
    work.mkdir()
    pid_file = work / "child.pid"
    grandchild_file = work / "grandchild.pid"
    recorded = []
    outcome = {}

    def run():
        outcome['r'] = run_native(
            [sys.executable, "-c", _tree_script(pid_file, grandchild_file)],
            cwd=work, timeout_seconds=120,
            pid_callback=recorded.append)

    worker = _threading.Thread(target=run, daemon=True)
    worker.start()
    _wait_file(pid_file)
    _wait_file(grandchild_file)
    assert recorded, "tool PID must be recorded at spawn, before waiting"
    grandchild_pid = int(grandchild_file.read_text())
    assert pid_alive(recorded[0]) and pid_alive(grandchild_pid)
    kill_tree(recorded[0], force=True)
    assert confirm(recorded[0], None, (grandchild_pid,), None) is True
    assert not pid_alive(recorded[0])
    assert not pid_alive(grandchild_pid)
    worker.join(timeout=60)
    assert outcome['r'].was_cancelled is False
    assert outcome['r'].timed_out is False
    cleaned = work / "cleaned"
    cleaned.mkdir()
    assert cleaned.exists()


def test_worker_starts_suspended_before_resume(tmp_path):
    from app.supervisor import _resume, _spawn_suspended, close_proc_handle, worker_env
    from pathlib import Path as _Path
    service_dir = _Path(__file__).resolve().parent.parent
    work = tmp_path / "work"
    work.mkdir()
    marker = work / "started.txt"
    env = worker_env(service_dir, ())
    proc = _spawn_suspended(
        [sys.executable, "-c",
         "open(r'" + str(marker) + "', 'w').write('up')"],
        work, env)
    try:
        time.sleep(1.0)
        assert not marker.exists(), "suspended worker must not execute"
        _resume(proc)
        deadline = time.perf_counter() + 15.0
        while not marker.exists() and time.perf_counter() < deadline:
            time.sleep(0.1)
        assert marker.read_text() == "up"
    finally:
        from app.process_tree import pid_alive as _alive
        from app.supervisor import kill_tree as _kill
        if _alive(proc.pid):
            _kill(proc.pid, force=True)
        close_proc_handle(proc)


def test_spawn_fails_closed_without_scope(tmp_path, monkeypatch):
    from pathlib import Path as _Path
    import app.supervisor as supervisor_module
    from app.supervisor import spawn_worker, worker_env
    service_dir = _Path(__file__).resolve().parent.parent
    work = tmp_path / "work"
    work.mkdir()
    env = worker_env(service_dir, ())
    if sys.platform != "win32":
        proc, mode, job = spawn_worker(service_dir, work, env)
        assert mode == "group"
        proc.kill()
        return
    real_create = supervisor_module.create_job
    # No job object at all: refusal before any worker code runs.
    monkeypatch.setattr(supervisor_module, "create_job", lambda: None)
    with pytest.raises(RuntimeError, match="no job"):
        spawn_worker(service_dir, work, env)
    # Assignment denied after a suspended spawn: destroyed pre-resume.
    monkeypatch.setattr(supervisor_module, "create_job", real_create)
    monkeypatch.setattr(supervisor_module, "assign_to_job", lambda job, pid: False)
    with pytest.raises(RuntimeError) as exc_info:
        spawn_worker(service_dir, work, env)
    import re as _re
    match = _re.search(r"pid (\d+)", str(exc_info.value))
    assert match, "fail-closed error must identify the stopped worker"
    stopped = int(match.group(1))
    deadline = time.perf_counter() + 15.0
    while pid_alive(stopped) and time.perf_counter() < deadline:
        time.sleep(0.2)
    assert not pid_alive(stopped)




def test_host_without_job_runtime_is_blocked(tmp_path):
    """BLOCKED verdict on hosts that forbid nested job assignment.

    No silent outer success and no pipeline: either the spawn attaches to
    our own job (capable host) or it refuses before any worker code runs.
    """
    from pathlib import Path as _Path
    from app.supervisor import close_job_handle, spawn_worker, worker_env
    service_dir = _Path(__file__).resolve().parent.parent
    work = tmp_path / "work"
    work.mkdir()
    env = worker_env(service_dir, ())
    if sys.platform != "win32":
        proc, mode, job = spawn_worker(service_dir, work, env)
        assert mode == "group"
        proc.kill()
        return
    try:
        proc, mode, job = spawn_worker(service_dir, work, env)
    except RuntimeError:
        assert not (work / "envelope.json").exists()
        assert not (work / "tool-pids.txt").exists()
        return
    try:
        assert mode == "job"
    finally:
        stop_containment(proc)
        close_job_handle(proc)


def _local_tool_paths():
    from pathlib import Path as _Path
    root = _Path(__file__).resolve().parents[3]
    node = _Path("C:/Program Files/nodejs/node.exe")
    cli = root / "artifacts/feasibility_pilot/env/dolos/node_modules/@dodona/dolos/dist/cli.js"
    scancode = root / "artifacts/feasibility_pilot/env/scancode/Scripts/scancode.exe"
    if not (node.exists() and cli.exists() and scancode.exists()):
        return None
    return node, cli, scancode


def _observed_versions(service_dir, node, cli, scancode):
    from app.main import _probe_tools
    return _probe_tools(service_dir, node, cli, scancode)


def _requires_job_runtime():
    from conftest import requires_containment as _rc
    return _rc()


@_requires_job_runtime()
def test_worker_tool_stopped_through_real_pipeline(tmp_path):
    import json as _json
    import time as _time
    from pathlib import Path as _Path
    from conftest import make_metadata, make_zip
    from app.process_tree import pid_alive as _alive
    from app.supervisor import (close_job_handle as _close,
                                confirm_containment as _confirm,
                                read_tool_pids as _read_pids,
                                spawn_worker as _spawn,
                                stop_containment as _stop,
                                worker_env as _env)
    service_dir = _Path(__file__).resolve().parent.parent
    node, _cli, scancode = _local_tool_paths()
    work = tmp_path / "work"
    work.mkdir()
    archive = make_zip({"src/a.js": b"function alpha() { return 1; }\n"
                                    b"function beta() { return 2; }\n"})
    metadata = make_metadata(archive)
    (work / "archive.bin").write_bytes(archive)
    request_doc = {
        "metadata": dict(metadata), "archiveRel": "archive.bin",
        "referenceManifest": None, "galleryRoot": None,
        "config": {"workspaceRoot": str(work), "nodeExe": None, "dolosCli": None,
                   "scancodeExe": str(scancode), "modelDir": None,
                   "modelManifest": None, "referenceManifest": None,
                   "galleryRoot": None, "indexDsn": None,
                   "overallDeadlineSeconds": 600, "tokenDeadlineSeconds": 180,
                   "scancodeDeadlineSeconds": 600,
                   "observedToolVersions": _observed_versions(service_dir, node, None,
                                                              scancode)},
    }
    env = _env(service_dir, ())
    proc, mode, job = _spawn(service_dir, work, env)
    try:
        (work / "request.json").write_text(_json.dumps(request_doc))
        deadline = _time.perf_counter() + 180
        while not (work / "tool-pids.txt").exists():
            assert proc.poll() is None, "worker exited before spawning a tool"
            if _time.perf_counter() > deadline:
                raise TimeoutError("tool never started")
            _time.sleep(0.5)
        tool_pids = _read_pids(work)
        assert tool_pids, "tool PID must be recorded at spawn"
        assert all(_alive(pid) for pid in tool_pids)
        _stop(proc)
        assert _confirm(proc.pid, None, tool_pids, job) is True
        assert not any(_alive(pid) for pid in tool_pids)
        assert not _alive(proc.pid)
    finally:
        _close(proc)
        if proc.poll() is None:
            _stop(proc)


def test_job_handle_not_inheritable_and_stdio_are(tmp_path):
    import ctypes
    from app.supervisor import _kernel32, create_job
    if sys.platform != "win32":
        pytest.skip("job handles are Windows-only")
    kernel32 = _kernel32()
    kernel32.GetHandleInformation.restype = ctypes.c_bool
    kernel32.GetHandleInformation.argtypes = [ctypes.c_void_p,
                                              ctypes.POINTER(ctypes.c_uint32)]
    job = create_job()
    assert job is not None
    try:
        handle, _ = job
        flags = ctypes.c_uint32(0)
        assert kernel32.GetHandleInformation(handle, ctypes.byref(flags))
        assert flags.value & 0x00000001 == 0, "job handle must not inherit"
    finally:
        from app.supervisor import close_job
        close_job(job)


def test_stdio_routing_stdin_eof_no_console(tmp_path):
    from app.supervisor import _resume, _spawn_suspended, close_proc_handle, worker_env
    from pathlib import Path as _Path
    service_dir = _Path(__file__).resolve().parent.parent
    work = tmp_path / "work"
    work.mkdir()
    env = worker_env(service_dir, ())
    script = ("import sys;"
              "data = sys.stdin.read();"
              "sys.stdout.write('OUT-MARKER:' + str(len(data)));"
              "sys.stderr.write('ERR-MARKER')")
    proc = _spawn_suspended([sys.executable, "-c", script], work, env)
    try:
        _resume(proc)
        deadline = time.perf_counter() + 30.0
        while proc.poll() is None and time.perf_counter() < deadline:
            time.sleep(0.1)
        assert proc.poll() is not None, "child must exit (stdin EOF, no hang)"
        out = (work / "worker.log").read_text(encoding="utf-8")
        err = (work / "worker.err").read_text(encoding="utf-8")
        assert "OUT-MARKER:0" in out
        assert "ERR-MARKER" in err
        assert "ERR-MARKER" not in out
        assert "OUT-MARKER" not in err
    finally:
        from app.process_tree import pid_alive as _alive
        from app.supervisor import kill_tree as _kill
        if _alive(proc.pid):
            _kill(proc.pid, force=True)
        close_proc_handle(proc)
