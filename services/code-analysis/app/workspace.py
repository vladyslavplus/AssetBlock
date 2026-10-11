"""Per-run workspace lifecycle with privacy, ownership, and strict cleanup.

Workspaces are private to the service account (POSIX 0700 or a restricted
Windows ACL applied fail-closed). Every workspace carries an owner record;
the sweeper never removes a workspace whose owner process is alive. Cleanup
failure is an operational failure, never a silent skip.
"""

from __future__ import annotations

import getpass
import json
import logging
import os
import secrets
import shutil
import subprocess
import sys
import time
from pathlib import Path

from . import mechanisms

LOG = logging.getLogger("code-analysis.workspace")

STALE_SECONDS = 3600


class Workspace:
    def __init__(self, root: Path, run_id: str):
        self.root = mechanisms.ensure_inside(root, root)
        token = secrets.token_hex(16)
        candidate = self.root / f"run-{run_id}-{token}"
        self.path = mechanisms.ensure_inside(candidate, self.root)
        self.path.mkdir(parents=True, exist_ok=False)
        _restrict_private(self.path)
        self._write_owner()
        self.live = True

    def _write_owner(self) -> None:
        record = {"pid": os.getpid(), "started": time.time()}
        owner = mechanisms.ensure_inside(self.path / ".owner", self.path)
        owner.write_text(json.dumps(record), encoding="utf-8")

    @classmethod
    def attach(cls, root: Path, path: Path) -> "Workspace":
        """Adopt an existing workspace created by the supervising parent."""
        instance = cls.__new__(cls)
        instance.root = mechanisms.ensure_inside(root, root)
        instance.path = mechanisms.ensure_inside(path, instance.root)
        instance.live = True
        return instance

    def stage(self, name: str, data: bytes) -> Path:
        if not self.live:
            raise RuntimeError("workspace closed")
        target = mechanisms.ensure_inside(self.path / name, self.path)
        target.write_bytes(data)
        return target

    def subdir(self, name: str) -> Path:
        if not self.live:
            raise RuntimeError("workspace closed")
        target = mechanisms.ensure_inside(self.path / name, self.path)
        target.mkdir(parents=True, exist_ok=True)
        return target

    def cleanup(self) -> None:
        self.live = False
        shutil.rmtree(self.path, ignore_errors=True)
        if self.path.exists():
            raise RuntimeError("workspace cleanup failed")


def _restrict_private(path: Path) -> None:
    if sys.platform == "win32":
        _restrict_windows(path)
        return
    os.chmod(path, 0o700)
    if (path.stat().st_mode & 0o777) != 0o700:
        raise RuntimeError("workspace privacy could not be enforced")


def _restrict_windows(path: Path) -> None:
    user = getpass.getuser()
    commands = [
        ["icacls", str(path), "/inheritance:r"],
        ["icacls", str(path), "/grant:r", f"{user}:(OI)(CI)F"],
    ]
    for argv in commands:
        try:
            completed = subprocess.run(argv, capture_output=True, timeout=60,
                                       shell=False, creationflags=0x08000000)
        except (OSError, subprocess.SubprocessError) as exc:
            raise RuntimeError("workspace ACL restriction failed") from exc
        if completed.returncode != 0:
            raise RuntimeError("workspace ACL restriction failed")
    try:
        shown = subprocess.run(["icacls", str(path)], capture_output=True,
                               timeout=60, shell=False, creationflags=0x08000000)
    except (OSError, subprocess.SubprocessError) as exc:
        raise RuntimeError("workspace ACL verification failed") from exc
    text = shown.stdout.decode("utf-8", errors="replace")
    for forbidden in ("Everyone", "BUILTIN\\Users", "NT AUTHORITY\\Authenticated Users"):
        if forbidden.lower() in text.lower():
            raise RuntimeError("workspace ACL verification failed")


def owner_alive(path: Path) -> bool:
    """True when the workspace owner record points at a live process."""
    from .process_tree import pid_alive
    try:
        record = json.loads((path / ".owner").read_text(encoding="utf-8"))
        pid = record.get("pid")
    except (OSError, ValueError, AttributeError):
        return False
    if type(pid) is not int:
        return False
    return pid_alive(pid)


def sweep_stale(root: Path, live_paths: set[Path]) -> dict:
    """Remove abandoned run workspaces; never touch live or owned ones.

    A workspace survives when it is active in this process, when its owner
    process is alive, or when it is younger than the stale budget. Removals
    are retried by later sweeps; persistent failures are reported, not hidden.
    """
    root = mechanisms.ensure_inside(root, root)
    removed, kept, failed = 0, 0, []
    now = time.time()
    for child in sorted(root.iterdir()):
        if not child.is_dir() or not child.name.startswith("run-"):
            continue
        if child in live_paths or owner_alive(child):
            kept += 1
            continue
        try:
            age = now - child.stat().st_mtime
        except OSError:
            continue
        if age < STALE_SECONDS:
            kept += 1
            continue
        try:
            resolved = mechanisms.ensure_inside(child, root)
        except ValueError:
            failed.append(child.name)
            continue
        shutil.rmtree(resolved, ignore_errors=True)
        if resolved.exists():
            failed.append(child.name)
        else:
            removed += 1
    result = {"removed": removed, "kept": kept, "failed": failed}
    if failed:
        LOG.warning("workspace sweep failed=%d", len(failed))
    else:
        LOG.info("workspace sweep removed=%d kept=%d", removed, kept)
    return result
