"""Bounded native-process runner with incremental caps and tree termination.

Fixed argv, no shell. Both streams are drained incrementally with a shared
byte cap: on overflow the whole tree is stopped immediately and the stage
must treat the run as a failure. All waits are bounded; nothing here blocks
forever, including the final reaps after a force kill.
"""

from __future__ import annotations

import ctypes
import os
import subprocess
import sys
import threading
import time
from dataclasses import dataclass
from pathlib import Path

from . import mechanisms

GRACE_SECONDS = 10.0
POLL_SECONDS = 0.5


@dataclass
class NativeResult:
    returncode: int
    stdout: bytes
    stderr: bytes
    duration_seconds: float
    timed_out: bool
    output_overflow: bool
    was_cancelled: bool = False
    pid: int = 0


GROUP_ENV_VAR = "CODE_ANALYSIS_PGID"


def _join_group_id(env: dict | None) -> int | None:
    if sys.platform == "win32" or not env:
        return None
    raw = env.get(GROUP_ENV_VAR)
    if raw is None:
        return None
    try:
        pgid = int(raw)
    except (TypeError, ValueError):
        raise ValueError("invalid process group join id")
    if pgid <= 0:
        raise ValueError("invalid process group join id")
    return pgid


def _set_group(pgid: int):
    def join() -> None:
        import os
        os.setpgid(0, pgid)
    return join


def pid_alive(pid: int) -> bool:
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


def run_native(argv: list[str], cwd: Path, timeout_seconds: float,
               max_output_bytes: int = 4 * 1024 * 1024,
               env: dict | None = None, cancelled=None,
               containment_pgid: int | None = None,
               pid_callback=None) -> NativeResult:
    """Run a fixed native command inside shared run containment.

    containment_pgid is passed explicitly by the caller (never sniffed from
    ambient state): on POSIX the child joins that group instead of forming
    a new session. pid_callback fires immediately after spawn, before any
    waiting, so the supervisor can track the native PID even if the caller
    is killed mid-execution.
    """
    if not argv:
        raise ValueError("empty argv")
    if timeout_seconds <= 0:
        raise ValueError("positive timeout required")
    cwd = mechanisms.ensure_inside(cwd, cwd)
    for arg in argv:
        if not isinstance(arg, str) or not arg:
            raise ValueError("argv must be nonempty strings")
    start = time.perf_counter()
    kwargs: dict = {"cwd": str(cwd), "shell": False,
                     "stdout": subprocess.PIPE, "stderr": subprocess.PIPE}
    if env is not None:
        kwargs["env"] = dict(env)
    if sys.platform == "win32":
        # No console windows for native tools: NEW_PROCESS_GROUP keeps the
        # tree killable, NO_WINDOW keeps the desktop quiet.
        kwargs["creationflags"] = (subprocess.CREATE_NEW_PROCESS_GROUP
                                   | 0x08000000)
    else:
        join_pgid = containment_pgid if containment_pgid is not None else _join_group_id(env)
        if join_pgid is not None:
            if join_pgid <= 0:
                raise ValueError("invalid process group join id")
            # Join the supervisor-owned containment instead of forming a new
            # session, so one group signal reaches every native descendant.
            kwargs["preexec_fn"] = _set_group(join_pgid)
        else:
            kwargs["start_new_session"] = True
    proc = subprocess.Popen(argv, **kwargs)
    pid = proc.pid
    if pid_callback is not None:
        try:
            pid_callback(pid)
        except Exception:
            pass
    captured = _StreamCap(max_output_bytes)
    readers = [threading.Thread(target=captured.drain, args=(proc.stdout, True),
                                daemon=True),
               threading.Thread(target=captured.drain, args=(proc.stderr, False),
                                daemon=True)]
    for thread in readers:
        thread.start()
    timed_out = False
    was_cancelled = False
    try:
        while True:
            elapsed = time.perf_counter() - start
            if cancelled is not None and cancelled():
                was_cancelled = True
                break
            if captured.overflow:
                break
            if proc.poll() is not None:
                break
            if elapsed >= timeout_seconds:
                timed_out = True
                break
            time.sleep(min(POLL_SECONDS, max(0.05, timeout_seconds - elapsed)))
    finally:
        if was_cancelled or timed_out or captured.overflow:
            kill_tree(proc.pid)
        _bounded_reap(proc, readers)
    if captured.overflow and proc.poll() is None:
        kill_tree(proc.pid, force=True)
        _bounded_reap(proc, readers)
    duration = time.perf_counter() - start
    return NativeResult(
        returncode=proc.returncode if proc.returncode is not None else 124,
        stdout=captured.stdout_bytes(),
        stderr=captured.stderr_bytes(),
        duration_seconds=duration,
        timed_out=timed_out,
        output_overflow=captured.overflow,
        was_cancelled=was_cancelled,
        pid=pid,
    )


class _StreamCap:
    """Incremental dual-stream drain with a shared total cap."""

    def __init__(self, cap: int):
        self._cap = cap
        self._lock = threading.Lock()
        self._total = 0
        self.overflow = False
        self._out: list[bytes] = []
        self._err: list[bytes] = []
        self._out_len = 0
        self._err_len = 0

    def drain(self, stream, is_stdout: bool) -> None:
        try:
            while True:
                block = stream.read(65536)
                if not block:
                    return
                with self._lock:
                    if self.overflow:
                        return
                    self._total += len(block)
                    if self._total > self._cap:
                        self.overflow = True
                        return
                    if is_stdout:
                        self._out.append(block)
                        self._out_len += len(block)
                    else:
                        self._err.append(block)
                        self._err_len += len(block)
        except (OSError, ValueError):
            return

    def stdout_bytes(self) -> bytes:
        with self._lock:
            return b"".join(self._out)

    def stderr_bytes(self) -> bytes:
        with self._lock:
            return b"".join(self._err)


def _bounded_reap(proc: subprocess.Popen, readers: list) -> None:
    for thread in readers:
        thread.join(timeout=GRACE_SECONDS)
    if proc.poll() is None:
        kill_tree(proc.pid, force=True)
        deadline = time.perf_counter() + GRACE_SECONDS
        while proc.poll() is None and time.perf_counter() < deadline:
            time.sleep(0.2)


def kill_tree(pid: int, force: bool = False) -> None:
    if sys.platform == "win32":
        args = ["taskkill", "/PID", str(pid), "/T"]
        if force:
            args.append("/F")
        try:
            subprocess.run(args, capture_output=True, timeout=30, shell=False,
                           creationflags=0x08000000)
        except (OSError, subprocess.SubprocessError):
            pass
        return
    import signal
    try:
        sig = signal.SIGKILL if force else signal.SIGTERM
        os.killpg(os.getpgid(pid), sig)
    except (OSError, ProcessLookupError):
        pass


def minimal_env(extra: dict | None = None) -> dict:
    keep = {"PATH", "SYSTEMROOT", "WINDIR", "COMSPEC", "PATHEXT", "TEMP",
            "TMP", "NUMBER_OF_PROCESSORS", "PROCESSOR_ARCHITECTURE",
            "PROGRAMFILES", "PROGRAMDATA", "LOCALAPPDATA", "APPDATA", "OS",
            "PYTHONUTF8", "PYTHONIOENCODING", "NODE_PATH", GROUP_ENV_VAR}
    merged = {k: v for k, v in os.environ.items() if k.upper() in keep}
    merged["PYTHONUTF8"] = "1"
    merged["PYTHONIOENCODING"] = "utf-8"
    if extra:
        merged.update(extra)
    return merged
