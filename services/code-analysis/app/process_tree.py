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
_WINDOWS_PROCESS_JOBS: dict[int, tuple] = {}


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
        kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
        kernel32.OpenProcess.restype = ctypes.c_void_p
        kernel32.OpenProcess.argtypes = [ctypes.c_uint32, ctypes.c_bool,
                                         ctypes.c_uint32]
        kernel32.GetExitCodeProcess.restype = ctypes.c_bool
        kernel32.GetExitCodeProcess.argtypes = [ctypes.c_void_p,
                                                ctypes.POINTER(ctypes.c_ulong)]
        kernel32.CloseHandle.restype = ctypes.c_bool
        kernel32.CloseHandle.argtypes = [ctypes.c_void_p]
        ctypes.set_last_error(0)
        handle = kernel32.OpenProcess(0x1000, False, pid)
        if not handle:
            # Only the documented invalid-PID error proves absence. Access
            # denied and all other failures are unknown, so fail closed.
            return ctypes.get_last_error() != 87
        try:
            code = ctypes.c_ulong()
            if not kernel32.GetExitCodeProcess(handle, ctypes.byref(code)):
                return True
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
        # The child must join a kill-on-close Job Object before its first
        # instruction. PID-only taskkill is denied in the service runtime and
        # cannot provide an affirmative tree-stop result.
        proc, windows_job = _spawn_windows_job(argv, cwd, kwargs.get("env"))
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
        if sys.platform == "win32":
            if not _confirm_windows_job_empty(windows_job, GRACE_SECONDS):
                kill_tree(proc.pid, force=True)
                if not _confirm_windows_job_empty(windows_job, GRACE_SECONDS):
                    # Keep the handle open and fail closed. Its owner (the
                    # supervised worker) will close it on exit, enforcing the
                    # configured KILL_ON_JOB_CLOSE policy.
                    raise RuntimeError("native process tree termination unconfirmed")
            _close_windows_process_job(proc.pid, windows_job)
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
        job = _WINDOWS_PROCESS_JOBS.get(pid)
        if job is not None:
            try:
                handle, kernel32 = job
                kernel32.TerminateJobObject.restype = ctypes.c_bool
                kernel32.TerminateJobObject.argtypes = [ctypes.c_void_p,
                                                        ctypes.c_uint]
                return bool(kernel32.TerminateJobObject(handle, 1))
            except (OSError, AttributeError, ValueError):
                return False
        # This checked fallback is useful only where the caller's token can
        # terminate the target. Never treat invocation as proof; callers must
        # still confirm process/job state before cleanup or capacity release.
        system_root = os.environ.get("SystemRoot") or os.environ.get("WINDIR")
        executable = Path(system_root, "System32", "taskkill.exe") if system_root else None
        if executable is None or not executable.is_file():
            return False
        args = [str(executable), "/PID", str(pid), "/T"]
        if force:
            args.append("/F")
        try:
            result = subprocess.run(args, capture_output=True, timeout=30,
                                    shell=False, creationflags=0x08000000)
            return result.returncode == 0
        except (OSError, subprocess.SubprocessError):
            return False
    import signal
    try:
        sig = signal.SIGKILL if force else signal.SIGTERM
        os.killpg(os.getpgid(pid), sig)
    except (OSError, ProcessLookupError):
        pass


def _spawn_windows_job(argv: list[str], cwd: Path, env: dict | None):
    """Start a Windows process suspended, attach its private job, then resume."""
    from .supervisor import assign_to_job, close_job, create_job

    job = create_job()
    if job is None:
        raise RuntimeError("native process containment unavailable")
    flags = (subprocess.CREATE_NEW_PROCESS_GROUP | 0x00000004
             | 0x08000000)  # CREATE_SUSPENDED | CREATE_NO_WINDOW
    proc = None
    try:
        proc = subprocess.Popen(argv, cwd=str(cwd), shell=False,
                                stdout=subprocess.PIPE,
                                stderr=subprocess.PIPE, env=env,
                                creationflags=flags)
        if not assign_to_job(job, proc.pid):
            raise RuntimeError("native process containment assignment denied")
        _resume_primary_thread(proc.pid)
        _WINDOWS_PROCESS_JOBS[proc.pid] = job
        return proc, job
    except Exception:
        if proc is not None:
            try:
                proc.kill()
                proc.wait(timeout=GRACE_SECONDS)
            except (OSError, subprocess.SubprocessError):
                pass
        close_job(job)
        raise


def _resume_primary_thread(pid: int) -> None:
    """Resume the sole primary thread of a newly created suspended process."""
    import ctypes

    class ThreadEntry32(ctypes.Structure):
        _fields_ = [("dwSize", ctypes.c_uint32), ("cntUsage", ctypes.c_uint32),
                    ("th32ThreadID", ctypes.c_uint32),
                    ("th32OwnerProcessID", ctypes.c_uint32),
                    ("tpBasePri", ctypes.c_int32), ("tpDeltaPri", ctypes.c_int32),
                    ("dwFlags", ctypes.c_uint32)]

    kernel32 = ctypes.windll.kernel32
    kernel32.CreateToolhelp32Snapshot.restype = ctypes.c_void_p
    kernel32.CreateToolhelp32Snapshot.argtypes = [ctypes.c_uint32, ctypes.c_uint32]
    kernel32.Thread32First.restype = ctypes.c_bool
    kernel32.Thread32First.argtypes = [ctypes.c_void_p,
                                       ctypes.POINTER(ThreadEntry32)]
    kernel32.Thread32Next.restype = ctypes.c_bool
    kernel32.Thread32Next.argtypes = [ctypes.c_void_p,
                                      ctypes.POINTER(ThreadEntry32)]
    kernel32.OpenThread.restype = ctypes.c_void_p
    kernel32.OpenThread.argtypes = [ctypes.c_uint32, ctypes.c_bool, ctypes.c_uint32]
    kernel32.ResumeThread.restype = ctypes.c_uint32
    kernel32.ResumeThread.argtypes = [ctypes.c_void_p]
    kernel32.CloseHandle.restype = ctypes.c_bool
    kernel32.CloseHandle.argtypes = [ctypes.c_void_p]
    snapshot = kernel32.CreateToolhelp32Snapshot(0x00000004, 0)
    if not snapshot or snapshot == ctypes.c_void_p(-1).value:
        raise OSError(ctypes.get_last_error(), "thread snapshot unavailable")
    try:
        entry = ThreadEntry32()
        entry.dwSize = ctypes.sizeof(entry)
        matches = []
        if kernel32.Thread32First(snapshot, ctypes.byref(entry)):
            while True:
                if entry.th32OwnerProcessID == pid:
                    matches.append(entry.th32ThreadID)
                if not kernel32.Thread32Next(snapshot, ctypes.byref(entry)):
                    break
        if len(matches) != 1:
            raise OSError("suspended process primary thread is ambiguous")
        thread = kernel32.OpenThread(0x0002, False, matches[0])
        if not thread:
            raise OSError(ctypes.get_last_error(), "primary thread unavailable")
        try:
            if kernel32.ResumeThread(thread) == 0xFFFFFFFF:
                raise OSError(ctypes.get_last_error(), "primary thread resume failed")
        finally:
            kernel32.CloseHandle(thread)
    finally:
        kernel32.CloseHandle(snapshot)


def _confirm_windows_job_empty(job, timeout: float) -> bool:
    from .supervisor import job_active_processes

    deadline = time.perf_counter() + timeout
    while time.perf_counter() < deadline:
        if job_active_processes(job) is True:
            return True
        time.sleep(0.1)
    return job_active_processes(job) is True


def _close_windows_process_job(pid: int, job) -> None:
    from .supervisor import close_job

    _WINDOWS_PROCESS_JOBS.pop(pid, None)
    close_job(job)


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
