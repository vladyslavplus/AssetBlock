"""Code and environment identity recorded with every stage output.

The executed source closure is every loaded module of this repository's scripts and
analysis service; its bytes are hashed, so a helper edit changes the identity even
when the working tree is dirty. Git is queried through the hidden bounded runner.
"""

import hashlib
import importlib.metadata
import platform
import shutil
import sys
from pathlib import Path

from scripts.code_analysis.contracts import canonical, digest
from scripts.feasibility_pilot.paths import REPO_ROOT
from scripts.feasibility_pilot.subprocess_util import run_bounded

SOURCE_ROOTS = ("scripts", "services/code-analysis/app")
GIT_TIMEOUT_SECONDS = 60


def _git(arguments, repo_root):
    executable = shutil.which("git")
    if executable is None:
        raise OSError("git is not available")
    result = run_bounded([executable, *arguments], cwd=repo_root,
                         allowed_roots=[repo_root, Path(executable).parent], timeout_s=GIT_TIMEOUT_SECONDS)
    if result.returncode != 0 or result.timed_out:
        raise OSError("git %s failed" % arguments[0])
    return result.stdout


def source_closure(repo_root=REPO_ROOT, modules=None):
    roots = [(Path(repo_root) / root).resolve() for root in SOURCE_ROOTS]
    rows = {}
    for module in list((sys.modules if modules is None else modules).values()):
        name = getattr(module, "__file__", None)
        if not name or not name.endswith(".py"):
            continue
        path = Path(name).resolve()
        if any(root in path.parents for root in roots):
            rows[path.relative_to(Path(repo_root).resolve()).as_posix()] = hashlib.sha256(path.read_bytes()).hexdigest()
    return [{"path": path, "sha256": rows[path]} for path in sorted(rows)]


def environment_identity():
    packages = sorted((dist.metadata["Name"] or "", dist.version) for dist in importlib.metadata.distributions())
    return {"python": platform.python_version(), "implementation": platform.python_implementation(),
            "platform": platform.platform(), "packageCount": len(packages),
            "packageListSha256": digest(canonical([list(item) for item in packages]))}


def capture(*, repo_root=REPO_ROOT, git=_git, modules=None):
    """Commit, dirty state, executed source closure and environment identity."""
    commit = git(["rev-parse", "HEAD"], repo_root).strip()
    dirty_paths = sorted(line[3:] for line in git(["status", "--porcelain"], repo_root).splitlines() if line)
    closure = source_closure(repo_root, modules)
    return {"commit": commit, "dirty": bool(dirty_paths), "dirtyPathCount": len(dirty_paths),
            "dirtyPathsSha256": digest(canonical(dirty_paths)),
            "sourceClosure": closure, "sourceClosureSha256": digest(canonical(closure)),
            "environment": environment_identity()}
