"""Supervised analysis worker: runs one pipeline inside the run workspace.

Invoked as `python -m app.worker <workspace-dir>` with the service
directory on the import path. Reads request.json, runs the pipeline over
the staged archive file, writes envelope.json. Standard output stays
silent; operational details go to the workspace log files only, which the
parent removes with the workspace.
"""

from __future__ import annotations

import json
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))


def main() -> int:
    if len(sys.argv) != 2:
        return 2
    workdir = Path(sys.argv[1])
    try:
        import os
        containment = None
        if sys.platform != "win32":
            # This process leads its own session (parent spawns it that way);
            # native tools join this group instead of forming new sessions,
            # so one group signal reaches every descendant.
            from app.process_tree import GROUP_ENV_VAR
            containment = os.getpid()
            os.environ[GROUP_ENV_VAR] = str(containment)
        from app import contracts, mechanisms, policy as policy_module
        from app.reference_index import load_manifest
        from app.stages import RunContext, ServiceConfig, run_pipeline
    except ImportError:
        _note(workdir, "import-error")
        return 2
    try:
        request = _read_request(workdir)
    except (OSError, ValueError):
        _note(workdir, "request-unreadable")
        return 2
    started = time.perf_counter()
    try:
        _verify_containment()
    except RuntimeError:
        _note(workdir, "containment-unverified")
        return 2
    try:
        metadata = contracts.validate_metadata(request["metadata"])
        policy = policy_module.current_policy()
        snapshot = None
        if request.get("referenceManifest") and request.get("galleryRoot"):
            snapshot = load_manifest(Path(request["referenceManifest"]),
                                     Path(request["galleryRoot"]))
        archive = (workdir / request["archiveRel"]).read_bytes()
        config = _service_config(request["config"])
        context = RunContext(metadata=metadata, archive=archive,
                             workspace=_workspace(workdir, metadata.run_id),
                             config=config, policy=policy, snapshot=snapshot,
                             containment_pgid=containment)
        envelope = run_pipeline(context)
    except Exception as exc:
        envelope = _crash_envelope(request, exc)
    try:
        (workdir / "envelope.json").write_bytes(contracts.canonical(envelope))
    except OSError:
        _note(workdir, "envelope-unwritable")
        return 2
    try:
        _write_log(workdir, envelope, time.perf_counter() - started)
    except OSError:
        _note(workdir, "log-unwritable")
    # Workspace removal belongs to the supervising parent, which confirms
    # worker termination first; this process must not delete evidence early.
    return 0


def _read_request(workdir: Path) -> dict:
    """Read the parent-written request, polling briefly past spawn.

    The parent writes request.json right after spawn; the worker startup
    (interpreter plus imports) always wins that race, but poll instead of
    assuming it so a slow parent cannot cause a misread.
    """
    deadline = time.perf_counter() + 5.0
    while True:
        try:
            return json.loads((workdir / "request.json").read_text(encoding="utf-8"))
        except (OSError, ValueError):
            if time.perf_counter() >= deadline:
                raise
            time.sleep(0.05)


def _verify_containment() -> None:
    """Refuse to start the pipeline outside the supervisor containment.

    Assignment precedes the first resumed instruction, so on Windows the
    parent-side ordering is the guarantee; on POSIX this process must lead
    its own session for the shared group signal to reach every descendant.
    Refusal is fail-closed and marker-only.
    """
    if sys.platform != "win32":
        import os
        if os.getpgid(0) != os.getpid():
            raise RuntimeError("worker containment unverified")


def _service_config(raw: dict):
    from app.stages import ServiceConfig
    return ServiceConfig(
        secret="worker-side-unused",
        workspace_root=Path(raw["workspaceRoot"]),
        node_exe=Path(raw["nodeExe"]) if raw.get("nodeExe") else None,
        dolos_cli=Path(raw["dolosCli"]) if raw.get("dolosCli") else None,
        scancode_exe=Path(raw["scancodeExe"]) if raw.get("scancodeExe") else None,
        model_dir=Path(raw["modelDir"]) if raw.get("modelDir") else None,
        model_manifest=Path(raw["modelManifest"]) if raw.get("modelManifest") else None,
        reference_manifest=Path(raw["referenceManifest"]) if raw.get("referenceManifest") else None,
        gallery_root=Path(raw["galleryRoot"]) if raw.get("galleryRoot") else None,
        index_dsn=raw.get("indexDsn"),
        index_runtime_compatibility=raw.get("indexRuntimeCompatibility"),
        overall_deadline_seconds=float(raw.get("overallDeadlineSeconds", 1080)),
        token_deadline_seconds=float(raw.get("tokenDeadlineSeconds", 180)),
        scancode_deadline_seconds=float(raw.get("scancodeDeadlineSeconds", 720)),
        observed_tool_versions=dict(raw.get("observedToolVersions") or {}),
    )


def _workspace(workdir: Path, run_id: str):
    from app import workspace as workspace_module
    return workspace_module.Workspace.attach(workdir.parent, workdir)


def _crash_envelope(request: dict, exc: Exception) -> dict:
    from app import contracts as contracts_module
    metadata = request.get("metadata", {}) if isinstance(request, dict) else {}
    return {
        "schema": "code-analysis-v1",
        "serviceVersion": contracts_module.SERVICE_VERSION,
        "outcome": "FAILED",
        "runId": metadata.get("runId"),
        "inputDigest": metadata.get("inputDigest"),
        "contentSha256": metadata.get("contentSha256"),
        "assetId": metadata.get("assetId"),
        "versionId": metadata.get("versionId"),
        "stages": [],
        "coverage": {"level": "UNSUPPORTED", "analyzedFiles": 0,
                     "omissions": [], "omissionOverflow": 0},
        "findings": [],
        "errorCode": "INTERNAL_ERROR",
        "crashField": (
            exc.args[0]
            if isinstance(exc, KeyError)
            and exc.args
            and isinstance(exc.args[0], str)
            and exc.args[0] in {
                "fileId", "canonicalPath", "sha256", "size", "languageGroup", "dialect",
                "fragments", "extractionSha", "omissions", "omissionOverflow",
                "coverageByDialect", "toolIdentity", "ranked", "counts", "windows",
                "vectorsF32LeBase64", "vectors", "sourceRunId", "sourceInputDigest",
                "sourceReportId", "sourceStageId", "artifactDigest", "stage",
                "reuseParentRunId", "reuseParentReportId", "reuseParentStageId",
            }
            else None
        ),
        "crashType": type(exc).__name__,
        "retryable": False,
        "failedStage": None,
        "admittedBy": None,
        "cleanupError": None,
    }


def _note(workdir: Path, code: str) -> None:
    """Static failure marker; never carries exception content."""
    try:
        (workdir / "worker-exit.txt").write_text(code + "\n", encoding="utf-8")
    except OSError:
        pass


def _write_log(workdir: Path, envelope: dict, duration: float) -> None:
    stages = envelope.get("stages", [])
    lines = [f"outcome={envelope.get('outcome')} error={envelope.get('errorCode')} "
             f"duration={duration:.1f}"]
    for stage in stages:
        lines.append(f"stage={stage.get('stage')} outcome={stage.get('outcome')} "
                     f"error={stage.get('errorCode')}")
    (workdir / "worker.log").write_text("\n".join(lines) + "\n", encoding="utf-8")


if __name__ == "__main__":
    sys.exit(main())
