"""Path allowlisting for fixture and artifact roots."""

from __future__ import annotations

import stat
from pathlib import Path

PILOT_ROOT = Path(__file__).absolute().parent
REPO_ROOT = PILOT_ROOT.parents[1]
FIXTURE_ROOT = PILOT_ROOT / "fixtures"
CONFIG_ROOT = PILOT_ROOT / "config"
ARTIFACT_ROOT = REPO_ROOT / "artifacts" / "feasibility_pilot"


class PathEscapeError(ValueError):
    pass


def _is_reparse_point(path: Path) -> bool:
    try:
        st = path.lstat()
    except FileNotFoundError:
        return False
    if stat.S_ISLNK(st.st_mode) or path.is_symlink():
        return True
    # Windows reparse points (symlinks, junctions, mount points).
    return bool(getattr(st, "st_file_attributes", 0) & 0x400)


def ensure_inside(path: Path, *roots: Path) -> Path:
    def inspect_original(supplied: Path) -> Path:
        supplied = Path(supplied)
        if ".." in supplied.parts:
            raise PathEscapeError(f"traversal rejected: {supplied}")
        # absolute() retains links; reject traversal in cwd as well.
        lexical = supplied.absolute()
        if ".." in lexical.parts:
            raise PathEscapeError(f"traversal rejected: {supplied}")
        current = Path(lexical.anchor)
        for part in ("", *lexical.parts[1:]):
            if part:
                current /= part
            if _is_reparse_point(current):
                raise PathEscapeError(f"reparse point or symlink rejected: {current}")
        return lexical

    original = inspect_original(path)
    checked_roots = [inspect_original(root) for root in roots]
    resolved = original.resolve()
    candidates = [root.resolve() for root in checked_roots]
    if not any(resolved == root or root in resolved.parents for root in candidates):
        raise PathEscapeError(f"path escapes allowed roots: {path}")
    return resolved


def run_dir(run_id: str) -> Path:
    if not run_id or any(ch in run_id for ch in r'\/:*?"<>|') or run_id in {".", ".."}:
        raise PathEscapeError("invalid run id")
    return ensure_inside(ARTIFACT_ROOT / "runs" / run_id, ARTIFACT_ROOT)


def env_dir(name: str) -> Path:
    return ensure_inside(ARTIFACT_ROOT / "env" / name, ARTIFACT_ROOT)


def cache_dir() -> Path:
    return ensure_inside(ARTIFACT_ROOT / "cache" / "models", ARTIFACT_ROOT)
