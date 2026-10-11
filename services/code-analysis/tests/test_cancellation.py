"""Cancellation: deadlines terminate the native process tree and clean up."""

import ctypes
import os
import subprocess
import sys
import tempfile
import time
from pathlib import Path

from app import workspace as workspace_module
from app.process_tree import kill_tree, run_native


def _pid_alive(pid: int) -> bool:
    if sys.platform == "win32":
        kernel32 = ctypes.windll.kernel32
        handle = kernel32.OpenProcess(0x1000, False, pid)
        if not handle:
            return False
        try:
            code = ctypes.c_ulong()
            if not kernel32.GetExitCodeProcess(handle, ctypes.byref(code)):
                return False
            return code.value == 259  # STILL_ACTIVE
        finally:
            kernel32.CloseHandle(handle)
    try:
        os.kill(pid, 0)
    except ProcessLookupError:
        return False
    except PermissionError:
        return True
    return True


def test_deadline_kills_process_tree(tmp_path):
    pid_file = tmp_path / "grandchild.pid"
    script = (
        "import subprocess, sys, time\n"
        "grandchild = subprocess.Popen([sys.executable, '-c', "
        "'import time; time.sleep(120)'])\n"
        f"open(r'{pid_file}', 'w').write(str(grandchild.pid))\n"
        "time.sleep(120)\n"
    )
    started = time.perf_counter()
    result = run_native([sys.executable, "-c", script], cwd=tmp_path, timeout_seconds=5)
    assert result.timed_out is True
    assert time.perf_counter() - started < 60
    grandchild_pid = int(pid_file.read_text())
    for _ in range(100):
        if not _pid_alive(grandchild_pid):
            break
        time.sleep(0.2)
    assert not _pid_alive(grandchild_pid)


def test_cancel_flag_kills_tree(tmp_path):
    cancelled = {"flag": False}

    def stop_after_start():
        time.sleep(2)
        cancelled["flag"] = True

    import threading
    watcher = threading.Thread(target=stop_after_start, daemon=True)
    watcher.start()
    result = run_native(
        [sys.executable, "-c", "import time; time.sleep(120)"],
        cwd=tmp_path, timeout_seconds=120,
        cancelled=lambda: cancelled["flag"])
    assert result.was_cancelled is True
    watcher.join(timeout=10)


def test_workspace_cleanup_removes_plaintext(tmp_path):
    root = tmp_path / "ws"
    root.mkdir()
    space = workspace_module.Workspace(root, "11111111-2222-3333-4444-555555555555")
    secret = space.stage("payload.bin", b"plaintext-bytes")
    assert secret.exists()
    space.cleanup()
    assert not space.path.exists()
    assert list(root.iterdir()) == []


def test_sweeper_removes_only_stale(tmp_path):
    root = tmp_path / "ws"
    root.mkdir()
    live = root / "run-live-abc"
    live.mkdir()
    stale = root / "run-stale-def"
    stale.mkdir()
    old = time.time() - 7200
    os.utime(stale, (old, old))
    result = workspace_module.sweep_stale(root, {live})
    assert result["removed"] == 1
    assert live.exists()
    assert not stale.exists()
