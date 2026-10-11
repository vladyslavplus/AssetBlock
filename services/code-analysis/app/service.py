"""Authenticated bounded analysis endpoint over loopback.

Ingress order per request: authenticate, claim the single execution gate,
enforce the parsed reservation deadline, stream the upload to a private
workspace file, validate and hash-bind every input, then run the pipeline
in a supervised child process. Capacity is released only after confirmed
worker termination and workspace cleanup.
"""

from __future__ import annotations

import asyncio
import json
import logging
import sys
import threading
import time
from contextlib import asynccontextmanager
from pathlib import Path

from fastapi import FastAPI, Request
from fastapi.responses import JSONResponse

from . import contracts, policy as policy_module, transport, workspace as workspace_module
from .contracts import ContractError
from .reference_index import load_manifest
from .stages import ServiceConfig
from .supervisor import (await_worker, close_job_handle,
                         confirm_containment,
                         read_tool_pids, spawn_worker, stop_containment,
                         worker_env)

LOG = logging.getLogger("code-analysis.service")

_STATUS_BY_CODE = {
    "AUTH_MISSING": 401,
    "AUTH_INVALID": 401,
    "BODY_TOO_LARGE": 413,
    "SERVICE_BUSY": 503,
    "STALE_RUN_CONFLICT": 409,
    "DIGEST_MISMATCH": 409,
    "POLICY_MISMATCH": 409,
    "RESERVATION_INVALID": 409,
    "DEADLINE_EXCEEDED": 408,
}


def _status_for(code: str) -> int:
    return _STATUS_BY_CODE.get(code, 400)


def create_app(config: ServiceConfig) -> FastAPI:
    if len(config.secret.encode("utf-8")) < 32:
        raise ValueError("analysis secret must be at least 32 bytes")
    config.workspace_root.mkdir(parents=True, exist_ok=True)
    state = {"busy": False, "active": {}, "sweeper": None,
             "pending_cleanups": set(), "active_workspaces": set(),
             "ingress_busy": False, "unconfirmed": set()}

    @asynccontextmanager
    async def lifespan(app: FastAPI):
        workspace_module.sweep_stale(config.workspace_root, set())
        state["sweeper"] = asyncio.create_task(_sweeper(config, state))
        yield
        task = state["sweeper"]
        if task is not None:
            task.cancel()

    app = FastAPI(title="Code Analysis Service", version=contracts.SERVICE_VERSION,
                  lifespan=lifespan)
    app.state.service_config = config
    app.state.pipeline_state = state
    app.state.policy = policy_module.current_policy()

    @app.get("/internal/health")
    async def health():
        import os
        snapshot_state = _snapshot_state(config)
        return {"schema": contracts.SCHEMA,
                "serviceVersion": contracts.SERVICE_VERSION,
                "processId": os.getpid(),
                "policyDigest": app.state.policy["policyDigest"],
                "verifierMode": "PENDING_RESEARCH_EVALUATION",
                "canAuthorizePublication": False,
                "activeRequests": len(state["active"]),
                "tools": {"token": "dolos" in config.observed_tool_versions,
                          "license": "scancode" in config.observed_tool_versions,
                          "encoder": config.model_dir is not None,
                          "reference": snapshot_state}}

    @app.post("/internal/analysis")
    async def analyze(request: Request):
        try:
            transport.check_secret(request.headers.get(transport.AUTH_HEADER),
                                   config.secret)
        except ContractError as exc:
            return _error(exc)
        length = request.headers.get("content-length")
        if length is not None:
            try:
                if int(length) > contracts.MAX_BODY_BYTES:
                    return _error(ContractError("BODY_TOO_LARGE"))
            except ValueError:
                return _error(ContractError("MALFORMED_MULTIPART", "content length"))
        if state["pending_cleanups"] or state["unconfirmed"]:
            return _error(ContractError("SERVICE_BUSY"))
        if state["ingress_busy"]:
            return _error(ContractError("SERVICE_BUSY"))
        state["ingress_busy"] = True
        try:
            try:
                return await _handle_upload(request, config, state, app)
            except ContractError as exc:
                return _error(exc)
        finally:
            state["ingress_busy"] = False

    async def _handle_upload(request: Request, config: ServiceConfig,
                             state: dict, app: FastAPI):
        started = time.time()
        try:
            content_type = request.headers.get("content-type", "")
            _peek_boundary(content_type)
        except ContractError as exc:
            return _error(exc)
        try:
            run_workspace = workspace_module.Workspace(config.workspace_root,
                                                       "ingress")
        except RuntimeError:
            LOG.error("workspace create failed")
            return JSONResponse(status_code=500,
                                content={"schema": contracts.SCHEMA,
                                         "errorCode": "INTERNAL_ERROR"})
        state["active_workspaces"].add(run_workspace.path)
        try:
            staged_body = run_workspace.path / "body.bin"
            intake = await _receive_intake(request, config, run_workspace,
                                           staged_body, content_type, started,
                                           app.state.policy["policyDigest"])
            try:
                staged = transport.parse_multipart_file(staged_body, content_type,
                                                        run_workspace.path)
            finally:
                staged_body.unlink(missing_ok=True)
            metadata = contracts.validate_metadata(staged.metadata)
            _check_intake_binding(intake, staged.metadata)
            if staged.archive_sha256 != metadata.content_sha256:
                return _error(ContractError("DIGEST_MISMATCH", "archive bytes"))
            _intake_limit, execution_deadline = split_deadlines(
                started, intake["reservation_deadline"], config)
            if execution_deadline <= time.time():
                return _error(ContractError("RESERVATION_INVALID"))
            snapshot = _load_snapshot(config)
            reference = _reference_identity(snapshot)
            recomputed = contracts.compute_analysis_digest(
                metadata, app.state.policy["policyDigest"], reference)
            if recomputed != metadata.input_digest:
                return _error(ContractError("DIGEST_MISMATCH", "analysis digest"))
            claimed, conflict = _enter_run(state, metadata.run_id, metadata.input_digest)
            if conflict is not None:
                return _error(conflict)
            if not claimed:  # pragma: no cover - guarded by ingress claim
                return _error(ContractError("SERVICE_BUSY"))
            try:
                return await _run_supervised(request, config, state, app,
                                             metadata, run_workspace, staged,
                                             snapshot, execution_deadline)
            except _Wedged:
                return JSONResponse(status_code=500,
                                    content={"schema": contracts.SCHEMA,
                                             "errorCode": "INTERNAL_ERROR"})
            finally:
                if not state["unconfirmed"]:
                    _exit_run(state, metadata.run_id)
        finally:
            state["active_workspaces"].discard(run_workspace.path)
            if run_workspace.live and run_workspace.path not in state["unconfirmed"]:
                try:
                    run_workspace.cleanup()
                except RuntimeError:
                    LOG.error("workspace cleanup failed")
                    state["pending_cleanups"].add(run_workspace.path)

    return app


def _peek_boundary(content_type: str) -> None:
    main, params = transport._split_header(content_type or "")
    if main != "multipart/form-data" or "boundary" not in params:
        raise ContractError("MALFORMED_MULTIPART", "content type")


def split_deadlines(started: float, reservation_epoch: float,
                    config: ServiceConfig) -> tuple:
    """Separate intake and execution budgets from one request start.

    Intake is bounded by the reservation, the upload budget, and the
    overall budget. Execution is bounded by the reservation and the
    overall budget only: upload time counts into the overall budget, but
    the standalone upload limit never caps a successfully started run.
    """
    intake = min(reservation_epoch,
                 started + config.upload_deadline_seconds,
                 started + config.overall_deadline_seconds)
    execution = min(reservation_epoch, started + config.overall_deadline_seconds)
    return intake, execution


async def _receive_intake(request: Request, config: ServiceConfig,
                          run_workspace, staged_body: Path, content_type: str,
                          started: float, policy_digest: str) -> dict:
    """Two-phase intake: bounded metadata first, then bulk archive.

    Phase A accumulates chunks into a bounded RAM prefix (never bulk) until
    the complete leading metadata part arrives or the prefix cap is hit;
    incomplete-but-valid framing means NEED_MORE, a complete non-metadata
    first part or a full cap without metadata means failure. Phase B streams
    the prefix plus the remaining chunks straight to the staging file under
    the remaining budget of the accepted reservation deadline. Every chunk
    wait carries a real async timeout, so a stalled sender cannot hold the
    ingress gate.
    """
    upload_limit = started + config.upload_deadline_seconds
    effective = upload_limit
    agen = request.stream().__aiter__()
    prefix = bytearray()
    early_metadata = None
    total = 0

    async def next_chunk():
        nonlocal total
        remaining = min(upload_limit, effective) - time.time()
        if remaining <= 0:
            raise ContractError("DEADLINE_EXCEEDED")
        try:
            chunk = await asyncio.wait_for(agen.__anext__(), remaining)
        except StopAsyncIteration:
            raise _EndOfBody()
        except asyncio.TimeoutError:
            raise ContractError("DEADLINE_EXCEEDED")
        total += len(chunk)
        if total > contracts.MAX_BODY_BYTES:
            raise ContractError("BODY_TOO_LARGE")
        return chunk

    # Phase A: bounded prefix, no bulk materialization.
    carry = b""
    while early_metadata is None:
        if len(prefix) >= transport.SCAN_PREFIX_CAP:
            raise ContractError("MALFORMED_METADATA", "metadata cap")
        try:
            chunk = await next_chunk()
        except _EndOfBody:
            break
        need = transport.SCAN_PREFIX_CAP - len(prefix)
        prefix += chunk[:need]
        carry = chunk[need:]
        early_metadata = transport.scan_prefix_metadata(bytes(prefix), content_type)
    if early_metadata is None:
        raise ContractError("MALFORMED_MULTIPART", "metadata missing")
    validated = contracts.validate_metadata(early_metadata)
    reservation_deadline = contracts.parse_reservation_deadline(
        validated.reservation.hard_deadline_utc)
    effective, _execution = split_deadlines(started, reservation_deadline, config)
    if effective <= time.time():
        raise ContractError("RESERVATION_INVALID")
    # Phase B: prefix plus remainder stream to disk under the accepted budget.
    with open(staged_body, "wb") as stream:
        stream.write(prefix)
        stream.write(carry)
        stream.flush()
        while True:
            remaining = min(upload_limit, effective) - time.time()
            if remaining <= 0:
                raise ContractError("DEADLINE_EXCEEDED")
            try:
                chunk = await asyncio.wait_for(agen.__anext__(), remaining)
            except StopAsyncIteration:
                break
            except asyncio.TimeoutError:
                raise ContractError("DEADLINE_EXCEEDED")
            total += len(chunk)
            if total > contracts.MAX_BODY_BYTES:
                raise ContractError("BODY_TOO_LARGE")
            stream.write(chunk)
            stream.flush()
    if total == 0:
        raise ContractError("MALFORMED_MULTIPART", "empty body")
    return {"early_metadata": early_metadata,
            "effective_deadline": effective,
            "reservation_deadline": reservation_deadline,
            "total_bytes": total}


class _EndOfBody(Exception):
    pass


def _check_intake_binding(intake: dict, staged_metadata: dict) -> None:
    early = intake["early_metadata"]
    if early is not None and contracts.canonical(early) != contracts.canonical(staged_metadata):
        # Metadata validated from the prefix must equal the fully parsed one.
        raise ContractError("DIGEST_MISMATCH", "metadata binding")


async def _run_supervised(request: Request, config: ServiceConfig, state: dict,
                          app: FastAPI, metadata, run_workspace,
                          staged: transport.StagedUpload, snapshot,
                           effective_deadline: float):
    env = worker_env(config.service_dir, config.worker_path_entries)
    try:
        proc, mode, job = spawn_worker(config.service_dir, run_workspace.path, env)
    except (ValueError, RuntimeError, OSError):
        LOG.error("worker containment unavailable")
        return JSONResponse(status_code=500,
                            content={"schema": contracts.SCHEMA,
                                     "errorCode": "INTERNAL_ERROR"})
    request_doc = {
        "metadata": json.loads(staged.metadata_raw.decode("utf-8")),
        "archiveRel": staged.archive_path.name,
        "referenceManifest": str(config.reference_manifest) if config.reference_manifest else None,
        "galleryRoot": str(config.gallery_root) if config.gallery_root else None,
        "config": {
            "workspaceRoot": str(config.workspace_root),
            "nodeExe": str(config.node_exe) if config.node_exe else None,
            "dolosCli": str(config.dolos_cli) if config.dolos_cli else None,
            "scancodeExe": str(config.scancode_exe) if config.scancode_exe else None,
            "modelDir": str(config.model_dir) if config.model_dir else None,
            "modelManifest": str(config.model_manifest) if config.model_manifest else None,
            "referenceManifest": str(config.reference_manifest) if config.reference_manifest else None,
            "galleryRoot": str(config.gallery_root) if config.gallery_root else None,
            "indexDsn": config.index_dsn,
            "overallDeadlineSeconds": config.overall_deadline_seconds,
            "tokenDeadlineSeconds": config.token_deadline_seconds,
            "scancodeDeadlineSeconds": config.scancode_deadline_seconds,
            "observedToolVersions": dict(config.observed_tool_versions or {}),
        },
    }
    cancel = _CancelFlag()

    async def disconnected() -> bool:
        return await request.is_disconnected()

    watcher = asyncio.create_task(_watch_disconnect(disconnected, cancel))
    cleanup_failed = False
    cancelled_exc = None
    bootstrapped = False
    try:
        try:
            (run_workspace.path / "request.json").write_text(
                json.dumps(request_doc), encoding="utf-8")
            bootstrapped = True
        except OSError:
            # Bootstrap failure after a successful spawn fences through the
            # same bounded finalizer instead of leaking a live worker.
            stop_containment(proc)
            envelope = _exception_envelope(metadata, "INTERNAL_ERROR")
        if bootstrapped:
            try:
                reason = await await_worker(proc, effective_deadline, cancel.is_set,
                                            disconnected)
            except asyncio.CancelledError as exc:
                # The HTTP task went away: fence the run first through the same
                # bounded finalizer, then re-raise only after release decisions.
                cancelled_exc = exc
                cancel.set()
                stop_containment(proc)
                reason = "CANCELLED"
            if reason != "DONE":
                code = "DEADLINE_EXCEEDED" if reason == "TIMEOUT" else "CANCELLED"
                envelope = _cancelled_envelope(metadata, code)
            elif proc.returncode != 0:
                LOG.error("worker exited code=%s marker=%s", proc.returncode,
                          _worker_exit_marker(run_workspace.path))
                envelope = _exception_envelope(metadata, "INTERNAL_ERROR")
            else:
                envelope = _read_envelope(run_workspace.path, metadata)
        tool_pids = read_tool_pids(run_workspace.path)
        job = getattr(proc, "service_job_handle", None)
        pgid = proc.pid if sys.platform != "win32" else None
        if not confirm_containment(proc.pid, pgid, tool_pids, job):
            # Unconfirmed execution is fenced, never cleaned: the workspace
            # stays, the sweeper must not clear the marker, and capacity is
            # not released. Only a confirmed stop unlocks cleanup.
            state["unconfirmed"].add(run_workspace.path)
            LOG.error("worker termination unconfirmed tools=%d", len(tool_pids))
            if cancelled_exc is not None:
                raise cancelled_exc
            raise _Wedged()
        close_job_handle(proc)
        if cancelled_exc is None and await request.is_disconnected():
            envelope = _cancelled_envelope(metadata, "CANCELLED")
    finally:
        watcher.cancel()
        close_job_handle(proc)
        if run_workspace.path not in state["unconfirmed"]:
            try:
                run_workspace.cleanup()
            except RuntimeError:
                LOG.error("workspace cleanup failed")
                cleanup_failed = True
                state["pending_cleanups"].add(run_workspace.path)
    if cleanup_failed:
        envelope = dict(envelope)
        envelope["cleanupError"] = "CLEANUP_FAILED"
    if cleanup_failed:
        envelope = _cleanup_failed_envelope(metadata, envelope)
    if cancelled_exc is not None:
        raise cancelled_exc
    return JSONResponse(status_code=200, content=json.loads(
        contracts.canonical(envelope).decode("utf-8")))


class _Wedged(Exception):
    """Unconfirmed worker termination: gate stays held, no cleanup, no release."""


class _CancelFlag:
    def __init__(self):
        self._flag = False

    def set(self):
        self._flag = True

    def is_set(self):
        return self._flag


async def _watch_disconnect(disconnected, cancel: _CancelFlag) -> None:
    try:
        while True:
            await asyncio.sleep(0.5)
            if await disconnected():
                cancel.set()
                return
    except asyncio.CancelledError:
        pass


def _worker_exit_marker(workspace_path: Path) -> str:
    try:
        text = (workspace_path / "worker-exit.txt").read_text(encoding="utf-8")
    except OSError:
        return "unknown"
    marker = text.strip()
    return marker if marker in {"import-error", "request-unreadable",
                                "envelope-unwritable", "log-unwritable",
                                "containment-unverified"} else "unknown"


def _read_envelope(workspace_path: Path, metadata) -> dict:
    envelope_path = workspace_path / "envelope.json"
    try:
        raw = envelope_path.read_bytes()
    except OSError:
        return _exception_envelope(metadata, "INTERNAL_ERROR")
    if len(raw) > contracts.MAX_RESPONSE_BYTES + 1024 * 1024:
        return _exception_envelope(metadata, "REPORT_TOO_LARGE")
    try:
        envelope = json.loads(raw.decode("utf-8"))
    except (UnicodeDecodeError, ValueError):
        return _exception_envelope(metadata, "INTERNAL_ERROR")
    if not isinstance(envelope, dict) or envelope.get("schema") != contracts.SCHEMA:
        return _exception_envelope(metadata, "INTERNAL_ERROR")
    return envelope


def _enter_run(state: dict, run_id: str, digest: str) -> tuple[bool, ContractError | None]:
    # Synchronous on the event loop: check-and-reserve is atomic, no await inside.
    active = state["active"]
    if run_id in active:
        if active[run_id] != digest:
            return False, ContractError("STALE_RUN_CONFLICT")
        return False, ContractError("SERVICE_BUSY")
    if state["busy"]:
        return False, ContractError("SERVICE_BUSY")
    state["busy"] = True
    active[run_id] = digest
    return True, None


def _exit_run(state: dict, run_id: str) -> None:
    state["active"].pop(run_id, None)
    if not state["active"]:
        state["busy"] = False


def _load_snapshot(config: ServiceConfig):
    if config.reference_manifest is None or config.gallery_root is None:
        return None
    try:
        return load_manifest(config.reference_manifest, config.gallery_root)
    except ContractError as exc:
        LOG.warning("reference manifest rejected code=%s", exc.code)
        return None


def _reference_identity(snapshot) -> dict | None:
    if snapshot is None:
        return None
    return {"snapshotId": snapshot.snapshot_id,
            "modelKey": snapshot.model_key,
            "indexKey": snapshot.index_key}


def _snapshot_state(config: ServiceConfig) -> bool:
    if config.reference_manifest is None or config.gallery_root is None:
        return False
    try:
        load_manifest(config.reference_manifest, config.gallery_root)
        return True
    except ContractError:
        return False


def _cancelled_envelope(metadata, code: str) -> dict:
    return {
        "schema": contracts.SCHEMA,
        "serviceVersion": contracts.SERVICE_VERSION,
        "outcome": "CANCELLED",
        "runId": metadata.run_id,
        "inputDigest": metadata.input_digest,
        "contentSha256": metadata.content_sha256,
        "assetId": metadata.asset_id,
        "versionId": metadata.version_id,
        "stages": [],
        "coverage": {"level": "UNSUPPORTED", "analyzedFiles": 0,
                     "omissions": [], "omissionOverflow": 0},
        "findings": [],
        "errorCode": code,
        "retryable": code != "CANCELLED",
        "admittedBy": None,
        "cleanupError": None,
    }


def _exception_envelope(metadata, code: str) -> dict:
    return {
        "schema": contracts.SCHEMA,
        "serviceVersion": contracts.SERVICE_VERSION,
        "outcome": "FAILED",
        "runId": metadata.run_id,
        "inputDigest": metadata.input_digest,
        "contentSha256": metadata.content_sha256,
        "assetId": metadata.asset_id,
        "versionId": metadata.version_id,
        "stages": [],
        "coverage": {"level": "UNSUPPORTED", "analyzedFiles": 0,
                     "omissions": [], "omissionOverflow": 0},
        "findings": [],
        "errorCode": code,
        "retryable": False,
        "failedStage": None,
        "admittedBy": None,
        "cleanupError": None,
    }


def _cleanup_failed_envelope(metadata, completed: dict) -> dict:
    """A run whose plaintext could not be removed is a failure, not a success."""
    return {
        "schema": contracts.SCHEMA,
        "serviceVersion": contracts.SERVICE_VERSION,
        "outcome": "FAILED",
        "runId": metadata.run_id,
        "inputDigest": metadata.input_digest,
        "contentSha256": metadata.content_sha256,
        "assetId": metadata.asset_id,
        "versionId": metadata.version_id,
        "stages": completed.get("stages", []),
        "coverage": completed.get("coverage", {}),
        "findings": [],
        "findingOverflow": completed.get("findingOverflow", 0),
        "errorCode": "CLEANUP_FAILED",
        "retryable": False,
        "failedStage": "REPORT",
        "admittedBy": None,
        "cleanupError": None,
    }


def _error(exc: ContractError) -> JSONResponse:
    return JSONResponse(status_code=_status_for(exc.code),
                        content={"schema": contracts.SCHEMA,
                                 "errorCode": exc.code})


async def _sweeper(config: ServiceConfig, state: dict) -> None:
    try:
        while True:
            await asyncio.sleep(900)
            live = set(state["active_workspaces"]) | set(state["unconfirmed"])
            result = workspace_module.sweep_stale(config.workspace_root, live)
            for path in sorted(state["pending_cleanups"]):
                if path in state["unconfirmed"]:
                    # Fenced execution evidence: never cleared by sweeping.
                    continue
                try:
                    import shutil
                    shutil.rmtree(path, ignore_errors=True)
                    if not Path(path).exists():
                        state["pending_cleanups"].discard(path)
                except OSError:
                    pass
            if state["pending_cleanups"]:
                LOG.warning("pending cleanups remain=%d",
                            len(state["pending_cleanups"]))
            if state["unconfirmed"]:
                LOG.warning("unconfirmed runs hold the gate=%d",
                            len(state["unconfirmed"]))
    except asyncio.CancelledError:
        pass
