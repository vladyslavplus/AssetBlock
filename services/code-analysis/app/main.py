"""Process entry point: uvicorn serving the analysis app on loopback."""

from __future__ import annotations

import os
import re
import tempfile
from pathlib import Path

import uvicorn

from .service import ServiceConfig, create_app


def _optional_path(name: str) -> Path | None:
    raw = os.environ.get(name)
    if not raw:
        return None
    candidate = Path(raw)
    if not candidate.exists():
        raise ValueError(f"{name} points at a missing path")
    # Workers run with the run workspace as CWD: every configured path must
    # be absolute so it resolves identically in parent and child.
    return candidate.absolute()


def build_config() -> tuple:
    secret = os.environ.get("CODE_ANALYSIS_SECRET", "")
    if len(secret.encode("utf-8")) < 32:
        raise ValueError("CODE_ANALYSIS_SECRET must be set with at least 32 bytes")
    bind = os.environ.get("CODE_ANALYSIS_BIND", "127.0.0.1")
    if bind not in ("127.0.0.1", "localhost", "::1"):
        if os.environ.get("CODE_ANALYSIS_ALLOW_NON_LOOPBACK") != "1":
            raise ValueError("non-loopback bind requires CODE_ANALYSIS_ALLOW_NON_LOOPBACK=1 "
                             "with externally terminated TLS")
    workspace = os.environ.get("CODE_ANALYSIS_WORKSPACE_ROOT")
    workspace_root = Path(workspace).absolute() if workspace else Path(tempfile.gettempdir()) / "code-analysis-workspace"
    node_exe = _optional_path("CODE_ANALYSIS_NODE_EXE")
    dolos_cli = _optional_path("CODE_ANALYSIS_DOLOS_CLI")
    scancode_exe = _optional_path("CODE_ANALYSIS_SCANCODE_EXE")
    service_dir = Path(__file__).resolve().parent.parent
    index_runtime_compatibility = os.environ.get("CODE_ANALYSIS_INDEX_RUNTIME_COMPATIBILITY")
    if index_runtime_compatibility not in (None, "pg18-owned-sandbox-v1"):
        raise ValueError("unsupported CODE_ANALYSIS_INDEX_RUNTIME_COMPATIBILITY")
    worker_entries = tuple(p for p in
                           os.environ.get("CODE_ANALYSIS_WORKER_PATHS", "").split(os.pathsep)
                           if p)
    observed = _probe_tools(service_dir, node_exe, dolos_cli, scancode_exe)
    return ServiceConfig(
        secret=secret,
        workspace_root=workspace_root,
        node_exe=node_exe,
        dolos_cli=dolos_cli,
        scancode_exe=scancode_exe,
        model_dir=_optional_path("CODE_ANALYSIS_MODEL_DIR"),
        model_manifest=_optional_path("CODE_ANALYSIS_MODEL_MANIFEST"),
        reference_manifest=_optional_path("CODE_ANALYSIS_REFERENCE_MANIFEST"),
        gallery_root=_optional_path("CODE_ANALYSIS_GALLERY_ROOT"),
        index_dsn=os.environ.get("CODE_ANALYSIS_INDEX_DSN"),
        index_runtime_compatibility=index_runtime_compatibility,
        overall_deadline_seconds=_clamped("CODE_ANALYSIS_OVERALL_DEADLINE_SECONDS", 18 * 60, 60, 18 * 60),
        token_deadline_seconds=_clamped("CODE_ANALYSIS_TOKEN_DEADLINE_SECONDS", 3 * 60, 30, 3 * 60),
        scancode_deadline_seconds=_clamped("CODE_ANALYSIS_SCANCODE_DEADLINE_SECONDS", 12 * 60, 60, 12 * 60),
        upload_deadline_seconds=_clamped("CODE_ANALYSIS_UPLOAD_DEADLINE_SECONDS", 300, 5, 300),
        service_dir=service_dir,
        worker_path_entries=worker_entries,
        observed_tool_versions=observed,
    ), bind


def _probe_tools(service_dir: Path, node_exe, dolos_cli, scancode_exe) -> dict:
    """Observe actual configured tool versions; never trust hardcoded strings."""
    from .process_tree import minimal_env, run_native
    observed: dict = {}
    if node_exe is not None and dolos_cli is not None:
        try:
            result = run_native([str(node_exe), str(dolos_cli), "--version"],
                                cwd=service_dir, timeout_seconds=30,
                                max_output_bytes=65536, env=minimal_env())
            if not result.timed_out and result.returncode == 0:
                text = result.stdout.decode("utf-8", errors="replace")
                match = re.search(r"v(\d+\.\d+\.\d+)", text)
                if match:
                    observed["dolos"] = match.group(1)
        except (OSError, ValueError):
            pass
    if scancode_exe is not None:
        try:
            result = run_native([str(scancode_exe), "--version"],
                                cwd=service_dir, timeout_seconds=30,
                                max_output_bytes=65536, env=minimal_env())
            if not result.timed_out and result.returncode == 0:
                text = result.stdout.decode("utf-8", errors="replace")
                match = re.search(r"version:\s*([0-9]+\.[0-9]+\.[0-9]+)", text)
                if match:
                    observed["scancode"] = match.group(1)
        except (OSError, ValueError):
            pass
    return observed


def _clamped(name: str, default: float, low: float, high: float) -> float:
    raw = os.environ.get(name)
    if raw is None:
        return float(default)
    try:
        value = float(raw)
    except ValueError:
        raise ValueError(f"{name} must be numeric")
    return max(low, min(high, value))


def main() -> None:
    config, bind = build_config()
    port = int(os.environ.get("CODE_ANALYSIS_PORT", "8081"))
    app = create_app(config)
    uvicorn.run(app, host=bind, port=port, log_level="info")


if __name__ == "__main__":
    main()
