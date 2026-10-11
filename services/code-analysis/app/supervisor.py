"""Supervised worker execution: the analysis pipeline runs in a child process.

The parent never awaits worker completion without a bound. Containment is
shared across the whole run: on POSIX every native tool joins the
supervisor-owned process group (no separate sessions), on Windows the
worker tree additionally runs in a Job Object with kill-on-close. On
deadline, disconnect, or task cancellation the whole containment is
stopped, worker and recorded native descendants are confirmed dead, and
only then does cleanup and capacity release happen. An unconfirmed run
keeps the capacity gate held instead of overlapping the next run.
"""

from __future__ import annotations

import asyncio
import logging
import subprocess
import sys
import time
from pathlib import Path

from . import mechanisms
from .process_tree import GROUP_ENV_VAR, kill_tree, minimal_env, pid_alive

LOG = logging.getLogger("code-analysis.supervisor")

POLL_SECONDS = 0.5
GRACE_SECONDS = 10.0
CONFIRM_SECONDS = 10.0


def worker_env(service_dir, extra_path_entries: tuple = ()) -> dict:
    """Child import path is explicit: service package first, then layered runtimes."""
    service_dir = mechanisms.ensure_inside(Path(service_dir), Path(service_dir))
    entries = [str(service_dir)]
    entries.extend(str(Path(p)) for p in extra_path_entries if p)
    env = minimal_env()
    env["PYTHONPATH"] = ";".join(entries) if sys.platform == "win32" else ":".join(entries)
    return env


class _SuspendedProc:
    """Minimal Popen-compatible handle for a suspended-spawned worker."""

    def __init__(self, pid: int, handle):
        self.pid = pid
        self._handle = handle
        self.returncode = None
        self.service_owns_handle = True

    def poll(self):
        import ctypes
        code = ctypes.c_uint32(0)
        kernel32 = ctypes.windll.kernel32
        kernel32.GetExitCodeProcess.restype = ctypes.c_bool
        kernel32.GetExitCodeProcess.argtypes = [ctypes.c_void_p,
                                                ctypes.POINTER(ctypes.c_uint32)]
        if not kernel32.GetExitCodeProcess(self._handle, ctypes.byref(code)):
            return None
        if code.value == 259:  # STILL_ACTIVE
            return None
        self.returncode = code.value
        return code.value


def spawn_worker(service_dir: Path, workspace_dir: Path, env: dict) -> tuple:
    """Spawn the worker inside our own managed run job.

    Windows: the worker starts suspended, is assigned to a fresh
    kill-on-close Job Object, and only then resumes — assignment precedes
    the first executed instruction, so no race exists. Any containment
    failure terminates the worker before returning: the pipeline never
    starts without its job. Returns (proc, mode, job).
    """
    service_dir = mechanisms.ensure_inside(service_dir, service_dir)
    workspace_dir = mechanisms.ensure_inside(workspace_dir, workspace_dir)
    script = service_dir / "app" / "worker.py"
    if not script.is_file():
        raise ValueError("worker script missing")
    argv = [sys.executable, str(script), str(workspace_dir)]
    if sys.platform != "win32":
        kwargs: dict = {"cwd": str(workspace_dir), "shell": False,
                         "stdout": open(workspace_dir / "worker.log", "wb"),
                         "stderr": open(workspace_dir / "worker.err", "wb"),
                         "env": env, "start_new_session": True}
        proc = subprocess.Popen(argv, **kwargs)
        proc.service_job_handle = None  # type: ignore[attr-defined]
        proc.service_containment_mode = "group"  # type: ignore[attr-defined]
        return proc, "group", None
    job = create_job()
    if job is None:
        raise RuntimeError("worker containment unavailable (no job)")
    try:
        proc = _spawn_suspended(argv, workspace_dir, env)
    except Exception:
        close_job(job)
        raise
    try:
        if not assign_to_job(job, proc.pid):
            raise RuntimeError(f"worker containment unavailable (pid {proc.pid})")
        _resume(proc)
    except Exception:
        _destroy_suspended(proc)
        close_job(job)
        raise
    proc.service_job_handle = job  # type: ignore[attr-defined]
    proc.service_containment_mode = "job"  # type: ignore[attr-defined]
    return proc, "job", job


def _spawn_suspended(argv: list, cwd: Path, env: dict):
    """CreateProcessW suspended with an explicit stdio handle allowlist.

    Only stdin (read-only NUL), stdout, and stderr handles are inheritable
    by the child via PROC_THREAD_ATTRIBUTE_HANDLE_LIST; every other parent
    handle — including the Job Object handle — stays private. Parent copies
    close before return. Exact signatures throughout.
    """
    import ctypes
    import subprocess as _sp
    kernel32 = _kernel32()

    class Security(ctypes.Structure):
        _fields_ = [("nLength", ctypes.c_uint32),
                    ("lpSecurityDescriptor", ctypes.c_void_p),
                    ("bInheritHandle", ctypes.c_bool)]

    class StartupInfo(ctypes.Structure):
        _fields_ = [("cb", ctypes.c_uint32),
                    ("lpReserved", ctypes.c_wchar_p),
                    ("lpDesktop", ctypes.c_wchar_p),
                    ("lpTitle", ctypes.c_wchar_p),
                    ("dwX", ctypes.c_uint32),
                    ("dwY", ctypes.c_uint32),
                    ("dwXSize", ctypes.c_uint32),
                    ("dwYSize", ctypes.c_uint32),
                    ("dwXCountChars", ctypes.c_uint32),
                    ("dwYCountChars", ctypes.c_uint32),
                    ("dwFillAttribute", ctypes.c_uint32),
                    ("dwFlags", ctypes.c_uint32),
                    ("wShowWindow", ctypes.c_uint16),
                    ("cbReserved2", ctypes.c_uint16),
                    ("lpReserved2", ctypes.c_void_p),
                    ("hStdInput", ctypes.c_void_p),
                    ("hStdOutput", ctypes.c_void_p),
                    ("hStdError", ctypes.c_void_p)]

    class StartupInfoEx(ctypes.Structure):
        _fields_ = [("StartupInfo", StartupInfo),
                    ("lpAttributeList", ctypes.c_void_p)]

    class ProcessInfo(ctypes.Structure):
        _fields_ = [("hProcess", ctypes.c_void_p),
                    ("hThread", ctypes.c_void_p),
                    ("dwProcessId", ctypes.c_uint32),
                    ("dwThreadId", ctypes.c_uint32)]

    kernel32.CreateProcessW.restype = ctypes.c_bool
    kernel32.CreateProcessW.argtypes = [ctypes.c_wchar_p, ctypes.c_wchar_p,
                                        ctypes.c_void_p, ctypes.c_void_p,
                                        ctypes.c_bool, ctypes.c_uint32,
                                        ctypes.c_void_p, ctypes.c_wchar_p,
                                        ctypes.c_void_p, ctypes.c_void_p]
    kernel32.CreateFileW.restype = ctypes.c_void_p
    kernel32.CreateFileW.argtypes = [ctypes.c_wchar_p, ctypes.c_uint32,
                                     ctypes.c_uint32, ctypes.c_void_p,
                                     ctypes.c_uint32, ctypes.c_uint32,
                                     ctypes.c_void_p]
    kernel32.InitializeProcThreadAttributeList.restype = ctypes.c_bool
    kernel32.InitializeProcThreadAttributeList.argtypes = [
        ctypes.c_void_p, ctypes.c_uint32, ctypes.c_uint32,
        ctypes.POINTER(ctypes.c_size_t)]
    kernel32.UpdateProcThreadAttribute.restype = ctypes.c_bool
    kernel32.UpdateProcThreadAttribute.argtypes = [
        ctypes.c_void_p, ctypes.c_uint32, ctypes.c_size_t,
        ctypes.c_void_p, ctypes.c_size_t, ctypes.c_void_p,
        ctypes.POINTER(ctypes.c_size_t)]
    kernel32.DeleteProcThreadAttributeList.restype = None
    kernel32.DeleteProcThreadAttributeList.argtypes = [ctypes.c_void_p]
    inheritable = Security(ctypes.sizeof(Security), None, True)
    created = []
    attribute_list = None

    def std_handle(path: str, access: int, disposition: int):
        handle = kernel32.CreateFileW(
            path, access, 0x00000001 | 0x00000002 | 0x00000004,
            ctypes.byref(inheritable), disposition, 0x00000080, None)
        if not handle or handle == ctypes.c_void_p(-1).value:
            raise OSError(f"worker stdio unavailable ({path})")
        created.append(handle)
        return handle

    try:
        h_in = std_handle("NUL", 0x80000000, 3)
        h_out = std_handle(str(cwd / "worker.log"), 0x40000000, 2)
        h_err = std_handle(str(cwd / "worker.err"), 0x40000000, 2)
        handles = (ctypes.c_void_p * 3)(h_in, h_out, h_err)
        size = ctypes.c_size_t(0)
        kernel32.InitializeProcThreadAttributeList(None, 1, 0, ctypes.byref(size))
        attribute_list = (ctypes.c_ubyte * size.value)()
        if not kernel32.InitializeProcThreadAttributeList(
                attribute_list, 1, 0, ctypes.byref(size)):
            raise OSError("worker attribute list unavailable")
        if not kernel32.UpdateProcThreadAttribute(
                attribute_list, 0, _PROC_THREAD_ATTRIBUTE_HANDLE_LIST,
                handles, ctypes.sizeof(handles), None, None):
            raise OSError("worker handle allowlist unavailable")
        startup = StartupInfoEx()
        startup.StartupInfo.cb = ctypes.sizeof(StartupInfoEx)
        startup.StartupInfo.dwFlags = 0x00000100
        startup.StartupInfo.hStdInput = h_in
        startup.StartupInfo.hStdOutput = h_out
        startup.StartupInfo.hStdError = h_err
        startup.lpAttributeList = ctypes.cast(attribute_list, ctypes.c_void_p).value
        info = ProcessInfo()
        command = ctypes.create_unicode_buffer(_sp.list2cmdline(
            [str(part) for part in argv]))
        environment = _environment_block(env)
        flags = 0x00000004 | 0x08000000 | 0x00000400 | 0x00000200 | 0x00080000
        ok = kernel32.CreateProcessW(
            None, command, None, None, True, flags, environment,
            str(cwd), ctypes.byref(startup), ctypes.byref(info))
        if not ok:
            raise OSError("worker process unavailable")
        proc = _SuspendedProc(info.dwProcessId, info.hProcess)
        proc.service_thread_handle = info.hThread  # type: ignore[attr-defined]
    finally:
        if attribute_list is not None:
            try:
                kernel32.DeleteProcThreadAttributeList(attribute_list)
            except OSError:
                pass
        for handle in created:
            try:
                kernel32.CloseHandle(handle)
            except OSError:
                pass
    return proc


def _environment_block(env: dict):
    import ctypes
    items = []
    for key, value in sorted(env.items()):
        if not isinstance(key, str) or not isinstance(value, str):
            raise ValueError("worker environment must be strings")
        if not key or "=" in key or "\0" in key or "\0" in value:
            raise ValueError("worker environment must be strings")
        items.append(f"{key}={value}")
    return ctypes.create_unicode_buffer("\0".join(items) + "\0\0")


def _resume(proc) -> None:
    import ctypes
    kernel32 = ctypes.windll.kernel32
    kernel32.ResumeThread.restype = ctypes.c_uint32
    kernel32.ResumeThread.argtypes = [ctypes.c_void_p]
    thread = getattr(proc, "service_thread_handle", None)
    if thread is not None:
        resumed = kernel32.ResumeThread(thread)
        kernel32.CloseHandle(thread)
        proc.service_thread_handle = None  # type: ignore[attr-defined]
        if resumed == 0xFFFFFFFF:
            raise OSError("worker resume unavailable")


def _destroy_suspended(proc) -> None:
    import ctypes
    kernel32 = ctypes.windll.kernel32
    kernel32.TerminateProcess.restype = ctypes.c_bool
    kernel32.TerminateProcess.argtypes = [ctypes.c_void_p, ctypes.c_uint]
    kernel32.TerminateProcess(proc._handle, 1)
    kernel32.CloseHandle(proc._handle)


async def await_worker(proc: subprocess.Popen, deadline_epoch: float,
                       cancelled, disconnected) -> str:
    """Wait for the worker; stop its containment on breach. Returns DONE/TIMEOUT/CANCELLED."""
    while True:
        if proc.poll() is not None:
            return "DONE"
        now = time.time()
        if cancelled() or await disconnected():
            stop_containment(proc)
            return "CANCELLED"
        if now >= deadline_epoch:
            stop_containment(proc)
            return "TIMEOUT"
        await asyncio.sleep(min(POLL_SECONDS, max(0.05, deadline_epoch - now)))


def worker_pgid(proc: subprocess.Popen) -> int:
    """Supervisor-owned containment id: the worker leads its own session."""
    return proc.pid


def stop_containment(proc: subprocess.Popen) -> None:
    """Stop worker plus every native descendant in shared containment."""
    job = getattr(proc, "service_job_handle", None)
    if sys.platform != "win32":
        _stop_group(worker_pgid(proc))
    if job is not None:
        terminate_job(job)
    kill_tree(proc.pid)
    deadline = time.perf_counter() + GRACE_SECONDS
    while pid_alive(proc.pid) and time.perf_counter() < deadline:
        proc.poll()
        time.sleep(0.2)
    if pid_alive(proc.pid):
        if sys.platform != "win32":
            _stop_group(worker_pgid(proc), force=True)
        if job is not None:
            terminate_job(job)
        kill_tree(proc.pid, force=True)
    proc.poll()


def _stop_group(pgid: int, force: bool = False) -> None:
    if sys.platform == "win32":
        return
    import os
    import signal
    try:
        os.killpg(pgid, signal.SIGKILL if force else signal.SIGTERM)
    except (OSError, ProcessLookupError):
        pass


def confirm_stopped(pid: int, extra_pids: tuple = ()) -> bool:
    """Confirm the worker and every recorded native descendant is dead."""
    deadline = time.perf_counter() + CONFIRM_SECONDS
    targets = [pid, *[p for p in extra_pids if isinstance(p, int) and p > 0]]
    while any(pid_alive(target) for target in targets):
        if time.perf_counter() >= deadline:
            return False
        time.sleep(0.2)
    return True


def confirm_containment(worker_pid: int, pgid: int | None,
                        extra_pids: tuple = (), job=None) -> bool:
    """Confirm the whole containment is empty, not just recorded PIDs.

    POSIX: the supervisor-owned group must have no members left (signal 0
    probe raises), which covers unrecorded grandchildren. Windows: the Job
    Object must report zero active processes. Recorded PIDs are always
    checked individually on both platforms.
    """
    if not confirm_stopped(worker_pid, extra_pids):
        return False
    if pgid is not None and sys.platform != "win32":
        if not _group_empty(pgid):
            return False
    if job is not None and sys.platform == "win32":
        if job_active_processes(job) is not True:
            return False
    return True


def _group_empty(pgid: int) -> bool:
    import os
    deadline = time.perf_counter() + CONFIRM_SECONDS
    while True:
        try:
            os.killpg(pgid, 0)
        except ProcessLookupError:
            return True
        except PermissionError:
            return False
        if time.perf_counter() >= deadline:
            return False
        time.sleep(0.2)


def job_active_processes(job):
    """True only when the job verifiably reports zero active processes."""
    if sys.platform != "win32" or job is None:
        return None
    try:
        import ctypes
        handle, kernel32 = job

        class Accounting(ctypes.Structure):
            _fields_ = [("TotalUserTime", ctypes.c_int64),
                        ("TotalKernelTime", ctypes.c_int64),
                        ("ThisPeriodTotalUserTime", ctypes.c_int64),
                        ("ThisPeriodTotalKernelTime", ctypes.c_int64),
                        ("TotalPageFaultCount", ctypes.c_uint32),
                        ("TotalProcesses", ctypes.c_uint32),
                        ("ActiveProcesses", ctypes.c_uint32),
                        ("TotalTerminatedProcesses", ctypes.c_uint32)]

        kernel32.QueryInformationJobObject.restype = ctypes.c_bool
        kernel32.QueryInformationJobObject.argtypes = [
            ctypes.c_void_p, ctypes.c_int, ctypes.c_void_p,
            ctypes.c_uint32, ctypes.POINTER(ctypes.c_uint32)]
        info = Accounting()
        returned = ctypes.c_uint32(0)
        ok = kernel32.QueryInformationJobObject(
            handle, 1, ctypes.byref(info), ctypes.sizeof(info),
            ctypes.byref(returned))
        if not ok:
            return None
        return info.ActiveProcesses == 0
    except (OSError, AttributeError, ValueError):
        return None


def read_tool_pids(workspace_dir: Path, limit: int = 64) -> tuple:
    """Native descendant PIDs recorded by the worker; bounded and validated."""
    try:
        lines = (workspace_dir / "tool-pids.txt").read_text(encoding="utf-8").splitlines()
    except OSError:
        return ()
    pids = []
    for line in lines[:limit]:
        try:
            pid = int(line.strip())
        except ValueError:
            continue
        if pid > 0:
            pids.append(pid)
    return tuple(pids)


def close_job_handle(proc: subprocess.Popen) -> None:
    job = getattr(proc, "service_job_handle", None)
    if job is not None:
        close_job(job)
        proc.service_job_handle = None  # type: ignore[attr-defined]
    close_proc_handle(proc)


def close_proc_handle(proc) -> None:
    # Only suspended-spawned handles need this: regular Popen manages its
    # own handle lifetime, and closing it early would break polling.
    if not getattr(proc, "service_owns_handle", False):
        return
    handle = getattr(proc, "_handle", None)
    if handle is not None:
        try:
            import ctypes
            kernel32 = ctypes.windll.kernel32
            kernel32.CloseHandle.restype = ctypes.c_bool
            kernel32.CloseHandle.argtypes = [ctypes.c_void_p]
            kernel32.CloseHandle(handle)
        except (OSError, AttributeError):
            pass
        try:
            proc._handle = None
        except AttributeError:
            pass


# --------------------------------------- Windows Job Object containment

_JOB_KILL_ON_CLOSE = 0x2000
_JOB_EXTENDED_LIMIT_INFORMATION = 9
# ProcThreadAttributeValue(2, FALSE, TRUE, FALSE): handle allowlist input.
_PROC_THREAD_ATTRIBUTE_HANDLE_LIST = 0x20002


def _kernel32():
    import ctypes
    kernel32 = ctypes.windll.kernel32
    kernel32.CreateJobObjectW.restype = ctypes.c_void_p
    kernel32.CreateJobObjectW.argtypes = [ctypes.c_void_p, ctypes.c_wchar_p]
    kernel32.SetInformationJobObject.restype = ctypes.c_bool
    kernel32.SetInformationJobObject.argtypes = [ctypes.c_void_p, ctypes.c_int,
                                                 ctypes.c_void_p, ctypes.c_uint32]
    kernel32.AssignProcessToJobObject.restype = ctypes.c_bool
    kernel32.AssignProcessToJobObject.argtypes = [ctypes.c_void_p, ctypes.c_void_p]
    kernel32.TerminateJobObject.restype = ctypes.c_bool
    kernel32.TerminateJobObject.argtypes = [ctypes.c_void_p, ctypes.c_uint]
    kernel32.OpenProcess.restype = ctypes.c_void_p
    kernel32.OpenProcess.argtypes = [ctypes.c_uint32, ctypes.c_bool, ctypes.c_uint32]
    kernel32.CloseHandle.restype = ctypes.c_bool
    kernel32.CloseHandle.argtypes = [ctypes.c_void_p]
    return kernel32


def create_job():
    """Job Object with kill-on-close via the extended limit class.

    The handle stays in the supervisor. The worker starts suspended and
    resumes only after assignment. Returns a handle/kernel32 pair or None.
    """
    if sys.platform != "win32":
        return None
    try:
        import ctypes
        import struct
        kernel32 = _kernel32()

        class Security(ctypes.Structure):
            _fields_ = [("nLength", ctypes.c_uint32),
                        ("lpSecurityDescriptor", ctypes.c_void_p),
                        ("bInheritHandle", ctypes.c_bool)]

        security = Security(ctypes.sizeof(Security), None, False)
        handle = kernel32.CreateJobObjectW(ctypes.byref(security), None)
        if not handle:
            return None
        # JOBOBJECT_EXTENDED_LIMIT_INFORMATION is 144 bytes here; only the
        # LimitFlags DWORD at offset 16 is set (KILL_ON_JOB_CLOSE), every
        # other limit stays zero (no limits imposed). The setting is read
        # back before the handle is trusted.
        buffer = bytearray(144)
        struct.pack_into("<I", buffer, 16, _JOB_KILL_ON_CLOSE)
        raw = bytes(buffer)
        ok = kernel32.SetInformationJobObject(
            handle, _JOB_EXTENDED_LIMIT_INFORMATION, raw, len(raw))
        if not ok or _read_limit_flags(handle, kernel32) != _JOB_KILL_ON_CLOSE:
            kernel32.CloseHandle(handle)
            return None
        return (handle, kernel32)
    except (OSError, AttributeError, ValueError):
        return None


def _read_limit_flags(handle, kernel32):
    import ctypes
    import struct
    query = (ctypes.c_ubyte * 144)()
    returned = ctypes.c_uint32(0)
    kernel32.QueryInformationJobObject.restype = ctypes.c_bool
    kernel32.QueryInformationJobObject.argtypes = [
        ctypes.c_void_p, ctypes.c_int, ctypes.c_void_p,
        ctypes.c_uint32, ctypes.POINTER(ctypes.c_uint32)]
    ok = kernel32.QueryInformationJobObject(
        handle, _JOB_EXTENDED_LIMIT_INFORMATION,
        query, ctypes.sizeof(query), ctypes.byref(returned))
    if not ok:
        return None
    return struct.unpack_from("<I", bytes(query), 16)[0]


def assign_to_job(job, pid: int) -> bool:
    handle, kernel32 = job
    try:
        process_set_quota = 0x0100
        process_terminate = 0x0001
        proc_handle = kernel32.OpenProcess(
            process_set_quota | process_terminate, False, pid)
        if not proc_handle:
            return False
        try:
            return bool(kernel32.AssignProcessToJobObject(handle, proc_handle))
        finally:
            kernel32.CloseHandle(proc_handle)
    except (OSError, AttributeError, ValueError):
        return False


def terminate_job(job) -> None:
    handle, kernel32 = job
    try:
        kernel32.TerminateJobObject(handle, 1)
    except (OSError, AttributeError):
        pass


def close_job(job) -> None:
    handle, kernel32 = job
    try:
        kernel32.CloseHandle(handle)
    except (OSError, AttributeError):
        pass


def job_handle_value(job) -> int | None:
    if job is None:
        return None
    handle, _kernel32 = job
    return int(handle)


_probe_cache = {}


def probe_containment() -> bool:
    """True only if this host permits our own managed job scope.

    Never runs any pipeline: a suspended trivial process is assigned and
    destroyed. Cached per process. POSIX session groups need no privilege
    and report True.
    """
    if sys.platform != "win32":
        return True
    if "capable" in _probe_cache:
        return _probe_cache["capable"]
    capable = False
    job = create_job()
    if job is not None:
        try:
            import tempfile
            proc = _spawn_suspended(
                [sys.executable, "-c", "import time; time.sleep(60)"],
                Path(tempfile.gettempdir()),
                minimal_env())
            try:
                capable = assign_to_job(job, proc.pid)
            finally:
                _destroy_suspended(proc)
                close_proc_handle(proc)
        finally:
            close_job(job)
    _probe_cache["capable"] = capable
    return capable
