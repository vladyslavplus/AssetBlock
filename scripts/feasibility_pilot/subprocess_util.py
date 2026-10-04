"""Bounded subprocess execution for local tools only."""

from __future__ import annotations

import os
import subprocess
import time
from dataclasses import dataclass
from pathlib import Path
from typing import Mapping, Sequence

from .paths import PathEscapeError, ensure_inside


@dataclass
class CommandResult:
    argv: list[str]
    returncode: int
    stdout: str
    stderr: str
    duration_s: float
    timed_out: bool
    output_overflow: bool


def run_bounded(
    argv: Sequence[str],
    *,
    cwd: Path,
    allowed_roots: Sequence[Path],
    timeout_s: float,
    max_output_bytes: int = 256 * 1024 * 1024,
    env: Mapping[str, str] | None = None,
    extra_env_allow: Sequence[str] = (),
) -> CommandResult:
    if not argv:
        raise ValueError("empty command")
    executable = Path(argv[0])
    ensure_inside(executable if executable.is_absolute() else cwd / executable, *allowed_roots)
    ensure_inside(cwd, *allowed_roots)
    for arg in argv[1:]:
        candidate = Path(arg)
        if candidate.is_absolute() or (len(arg) >= 2 and arg[1] == ":"):
            ensure_inside(Path(arg), *allowed_roots)
    merged = _tool_env(env, extra_env_allow)
    start = time.perf_counter()
    try:
        completed = subprocess.run(
            list(argv),
            cwd=str(cwd),
            env=merged,
            capture_output=True,
            timeout=timeout_s,
            check=False,
            shell=False,
        )
    except subprocess.TimeoutExpired as exc:
        stdout = _decode_cap(exc.stdout, max_output_bytes)
        stderr = _decode_cap(exc.stderr, max_output_bytes)
        overflow = _overflowed(exc.stdout, exc.stderr, max_output_bytes)
        return CommandResult(
            argv=list(argv),
            returncode=124,
            stdout=stdout,
            stderr=stderr,
            duration_s=time.perf_counter() - start,
            timed_out=True,
            output_overflow=overflow,
        )
    overflow = _overflowed(completed.stdout, completed.stderr, max_output_bytes)
    return CommandResult(
        argv=list(argv),
        returncode=completed.returncode,
        stdout=_decode_cap(completed.stdout, max_output_bytes),
        stderr=_decode_cap(completed.stderr, max_output_bytes),
        duration_s=time.perf_counter() - start,
        timed_out=False,
        output_overflow=overflow,
    )


def _decode_cap(data: bytes | None, max_output_bytes: int) -> str:
    if not data:
        return ""
    return data[:max_output_bytes].decode("utf-8", errors="replace")


def _overflowed(stdout: bytes | None, stderr: bytes | None, max_output_bytes: int) -> bool:
    total = len(stdout or b"") + len(stderr or b"")
    return total > max_output_bytes


def _tool_env(env: Mapping[str, str] | None, extra_env_allow: Sequence[str]) -> dict[str, str]:
    keep = {
        "PATH",
        "SYSTEMROOT",
        "WINDIR",
        "COMSPEC",
        "PATHEXT",
        "TEMP",
        "TMP",
        "USERNAME",
        "USERPROFILE",
        "HOMEDRIVE",
        "HOMEPATH",
        "NUMBER_OF_PROCESSORS",
        "PROCESSOR_ARCHITECTURE",
        "PROGRAMFILES",
        "PROGRAMFILES(X86)",
        "PROGRAMDATA",
        "LOCALAPPDATA",
        "APPDATA",
        "OS",
        "PYTHONUTF8",
        "PYTHONIOENCODING",
        "VIRTUAL_ENV",
        "VIRTUAL_ENV_PROMPT",
    }
    keep.update(name.upper() for name in extra_env_allow)
    merged: dict[str, str] = {}
    for key, value in os.environ.items():
        if key.upper() in keep or key.upper().startswith("CUDA") or key.upper().startswith("NVIDIA"):
            merged[key] = value
    if env:
        merged.update(env)
    merged["PYTHONUTF8"] = "1"
    merged["PYTHONIOENCODING"] = "utf-8"
    return merged


def reject_npx(argv: Sequence[str]) -> None:
    if argv and Path(argv[0]).name.lower() in {"npx", "npx.cmd", "npx.exe"}:
        raise PathEscapeError("npx implicit download is not allowed")
