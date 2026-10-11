"""Stage orchestration: inventory, extraction, token, semantic, license,
union, verifier record, and immutable report with a sanitized projection.

Every mandatory stage failure stays a failure: missing tools, models, or
reference evidence never becomes an empty success. Foreign private bytes,
paths, names, vectors, and raw outputs never enter logs or the seller-safe
projection.
"""

from __future__ import annotations

import csv
import json
import logging
import time
from dataclasses import dataclass, field
from io import StringIO
from pathlib import Path

from . import archive_safety, contracts, mechanisms, policy as policy_module, workspace as workspace_module
from .archive_safety import InventoriedFile
from .contracts import (APPLICABILITY, COVERAGE, FINDING_TYPE, RUN_OUTCOME,
                        SENSITIVITY, STAGE, STAGE_OUTCOME, VERIFIER_MODE,
                        ContractError, StageRecord, compute_analysis_digest)
from .process_tree import minimal_env, run_native
from .reference_index import ReferenceSnapshot, load_manifest, read_gallery_bytes

LOG = logging.getLogger("code-analysis.stages")

VENDOR_SEGMENTS = frozenset({"vendor", "node_modules", "third_party", ".git"})

_DIALECT_SUFFIX = {
    "javascript": ".js",
    "typescript": ".ts",
    "tsx": ".tsx",
    "python": ".py",
    "java": ".java",
    "csharp": ".cs",
}


@dataclass
class ServiceConfig:
    secret: str
    workspace_root: Path
    node_exe: Path | None = None
    dolos_cli: Path | None = None
    scancode_exe: Path | None = None
    model_dir: Path | None = None
    model_manifest: Path | None = None
    reference_manifest: Path | None = None
    gallery_root: Path | None = None
    index_dsn: str | None = None
    overall_deadline_seconds: float = 18 * 60
    token_deadline_seconds: float = 3 * 60
    scancode_deadline_seconds: float = 12 * 60
    upload_deadline_seconds: float = 300.0
    service_dir: Path | None = None
    worker_path_entries: tuple = ()
    observed_tool_versions: dict = field(default_factory=dict)


@dataclass
class AnalyzedFile:
    file_id: str
    canonical_path: str
    sha256: str
    language_group: str
    dialect: str
    size: int
    fragments: list = field(default_factory=list)
    extraction_sha: str = ""
    searchable: bool = False


@dataclass
class RunContext:
    metadata: contracts.AnalysisMetadata
    archive: bytes
    workspace: workspace_module.Workspace
    config: ServiceConfig
    policy: dict
    snapshot: ReferenceSnapshot | None = None
    cancelled: object = None
    containment_pgid: int | None = None

    def check_cancelled(self) -> None:
        if self.cancelled is not None and self.cancelled():
            raise ContractError("CANCELLED")


@dataclass
class Pipeline:
    context: RunContext
    stages: list = field(default_factory=list)
    omissions: list = field(default_factory=list)
    omission_overflow: int = 0
    inventory: object = None
    analyzed: list = field(default_factory=list)
    token_ranked: dict = field(default_factory=dict)
    semantic_ranked: dict = field(default_factory=dict)
    license_rows: list = field(default_factory=list)
    findings: list = field(default_factory=list)
    finding_overflow: int = 0

    def omit(self, reason: str, path: str | None = None,
             file_id: str | None = None, detail: str = "") -> None:
        if reason not in contracts.OMISSION_REASONS:
            raise ValueError(f"unknown omission reason {reason}")
        if len(self.omissions) >= contracts.MAX_OMISSIONS:
            self.omission_overflow += 1
            return
        entry: dict = {"reason": reason}
        if path is not None:
            entry["path"] = path[:512]
        if file_id is not None:
            entry["fileId"] = file_id
        if detail:
            entry["detail"] = detail[:256]
        self.omissions.append(entry)

    def record(self, stage: StageRecord) -> None:
        self.stages.append(stage)

    def add_finding(self, ftype: str, path: str | None, file_id: str | None,
                    reason: str, foreign_id: str | None = None,
                    sensitivity: str | None = None, origin: str | None = None,
                    attribution: dict | None = None, score=None) -> None:
        _append_finding(self, ftype, path, file_id, reason, foreign_id,
                        sensitivity, origin, attribution, score)


def _retryable(code: str) -> bool:
    return code in {"TOOL_TIMEOUT", "SERVICE_BUSY", "DEADLINE_EXCEEDED",
                    "INDEX_UNAVAILABLE", "TOOL_MISSING"}


def run_pipeline(ctx: RunContext) -> dict:
    # Independent stages run even after a mandatory failure so the envelope
    # carries complete evidence; the outcome still stays FAILED. Union needs
    # both retrieval channels and is blocked otherwise.
    pipe = Pipeline(context=ctx)
    _stage_inventory(pipe)
    if not _failed(pipe):
        ctx.check_cancelled()
        _stage_extraction(pipe)
    token_ok = semantic_ok = False
    if not _failed(pipe):
        ctx.check_cancelled()
        _stage_token(pipe)
    token_ok = _succeeded(pipe, STAGE.TOKEN.value)
    if not _failed(pipe):
        ctx.check_cancelled()
        _stage_semantic(pipe)
    elif _succeeded(pipe, STAGE.EXTRACTION.value):
        _stage_semantic(pipe)
    semantic_ok = _succeeded(pipe, STAGE.SEMANTIC.value)
    if _succeeded(pipe, STAGE.EXTRACTION.value):
        ctx.check_cancelled()
        _stage_license(pipe)
    if token_ok and semantic_ok:
        ctx.check_cancelled()
        _stage_union(pipe)
    elif _succeeded(pipe, STAGE.EXTRACTION.value):
        _block_union(pipe)
    _stage_verifier(pipe)
    if _failed(pipe):
        return _failure_envelope(pipe)
    try:
        body = _success_envelope(pipe)
    except ContractError as exc:
        if not any(s.stage == STAGE.REPORT.value
                   and s.outcome == STAGE_OUTCOME.FAILED.value for s in pipe.stages):
            pipe.record(StageRecord(
                stage=STAGE.REPORT.value, applicability=APPLICABILITY.MANDATORY.value,
                outcome=STAGE_OUTCOME.FAILED.value, error_code=exc.code,
                retryable=False, duration_seconds=0.0))
        return _failure_envelope(pipe)
    if not _full_matrix(pipe):
        pipe.record(StageRecord(
            stage=STAGE.REPORT.value, applicability=APPLICABILITY.MANDATORY.value,
            outcome=STAGE_OUTCOME.FAILED.value, error_code="INTERNAL_ERROR",
            retryable=False, duration_seconds=0.0))
        return _failure_envelope(pipe)
    return body


def _failed(pipe: Pipeline) -> bool:
    return any(s.outcome == STAGE_OUTCOME.FAILED.value and s.applicability == APPLICABILITY.MANDATORY.value
               for s in pipe.stages)


def _succeeded(pipe: Pipeline, stage: str) -> bool:
    return any(s.stage == stage and s.outcome == STAGE_OUTCOME.SUCCEEDED.value
               for s in pipe.stages)


def _block_union(pipe: Pipeline) -> None:
    blocker = next((s.error_code for s in pipe.stages
                    if s.outcome == STAGE_OUTCOME.FAILED.value), "INTERNAL_ERROR")
    pipe.record(StageRecord(
        stage=STAGE.UNION.value, applicability=APPLICABILITY.MANDATORY.value,
        outcome=STAGE_OUTCOME.FAILED.value, error_code=blocker,
        retryable=_retryable(blocker or "")))


# ---------------------------------------------------------------- inventory

def _stage_inventory(pipe: Pipeline) -> None:
    ctx = pipe.context
    started = time.perf_counter()
    try:
        inventory = archive_safety.inspect_zip(ctx.archive)
        actual = contracts.sha256_hex(ctx.archive)
        if actual != ctx.metadata.content_sha256:
            raise ContractError("DIGEST_MISMATCH", "archive bytes differ from request")
        if ctx.metadata.security_evidence.content_sha256 != actual:
            raise ContractError("SECURITY_EVIDENCE_INVALID", "evidence hash mismatch")
        reference = _reference_identity(ctx.snapshot)
        expected = contracts.compute_analysis_digest(
            ctx.metadata, ctx.policy["policyDigest"], reference)
        if expected != ctx.metadata.input_digest:
            raise ContractError("DIGEST_MISMATCH", "analysis digest mismatch")
        pipe.inventory = inventory
        record = StageRecord(
            stage=STAGE.INVENTORY.value, applicability=APPLICABILITY.MANDATORY.value,
            outcome=STAGE_OUTCOME.SUCCEEDED.value,
            duration_seconds=time.perf_counter() - started,
            input_sha256=actual,
            output_sha256=contracts.sha256_hex(contracts.canonical(
                [f.canonical_path for f in inventory.files])),
            counts={"entries": inventory.entry_count,
                    "files": len(inventory.files),
                    "expandedBytes": inventory.total_expanded_bytes})
    except ContractError as exc:
        record = StageRecord(
            stage=STAGE.INVENTORY.value, applicability=APPLICABILITY.MANDATORY.value,
            outcome=STAGE_OUTCOME.FAILED.value, error_code=exc.code,
            retryable=_retryable(exc.code),
            duration_seconds=time.perf_counter() - started)
    pipe.record(record)
    LOG.info("run inventory outcome=%s files=%d", record.outcome,
             len(pipe.inventory.files) if pipe.inventory else 0)


# --------------------------------------------------------------- extraction

def _classify_file(path: str, size: int) -> tuple[str | None, str | None]:
    lowered = path.lower()
    if lowered.endswith((".zip", ".tar", ".tgz", ".tar.gz", ".gz", ".rar", ".7z")):
        return None, "NESTED_ARCHIVE"
    suffix = "." + path.rsplit(".", 1)[-1].lower() if "." in path.rsplit("/", 1)[-1] else ""
    dialect = contracts.EXTENSION_DIALECTS.get(suffix)
    if dialect is None:
        return None, "UNSUPPORTED_EXTENSION"
    lowered_slash = "/" + lowered
    if any(f"/{seg}/" in lowered_slash + "/" or lowered_slash.endswith(f"/{seg}") for seg in VENDOR_SEGMENTS):
        return None, "VENDOR_EXCLUDED"
    if size > contracts.MAX_SOURCE_BYTES:
        return None, "FILE_TOO_LARGE"
    return dialect, None


def _stage_extraction(pipe: Pipeline) -> None:
    ctx = pipe.context
    started = time.perf_counter()
    parser_ok = True
    total_units = 0
    coverage_by_dialect: dict[str, dict] = {}
    try:
        staged: dict[str, bytes] = {}
        for item in pipe.inventory.files:
            data = _entry_bytes(ctx, item)
            ctx.workspace.stage(item.staged_name, data)
            staged[item.canonical_path] = data
            raw = data
            dialect, omission = _classify_file(item.canonical_path, item.size)
            bucket = coverage_by_dialect.setdefault(
                dialect or "unknown", {"attempted": 0, "extracted": 0, "omitted": 0})
            if omission is not None:
                bucket["omitted"] += 1
                pipe.omit(omission, path=item.canonical_path)
                continue
            signals = mechanisms.generator_header_signals(raw)
            group = next(g for g, ds in contracts.LANGUAGE_GROUPS.items() if dialect in ds)
            if signals:
                bucket["omitted"] += 1
                pipe.omit("GENERATED_EXCLUDED", path=item.canonical_path)
                continue
            bucket["attempted"] += 1
            file_id = contracts.sha256_hex(contracts.canonical(
                [item.sha256, item.canonical_path]))
            record = {"fileId": file_id, "sha256": item.sha256,
                      "language": group, "dialect": dialect}
            try:
                result = mechanisms.extract_source(raw, record)
            except ValueError as exc:
                raise ContractError("EXTRACTION_FAILED", str(exc)[:200])
            outcome = result.get("actualOutcome")
            if outcome == "UNSUPPORTED_DIALECT":
                bucket["omitted"] += 1
                pipe.omit("UNSUPPORTED_DIALECT", path=item.canonical_path, file_id=file_id)
                continue
            if result.get("operationalFailure") or outcome == "FAILED":
                raise ContractError("EXTRACTION_FAILED", item.canonical_path[:200])
            if outcome != "VALID_EXTRACTED":
                bucket["omitted"] += 1
                pipe.omit("PARSE_INVALID", path=item.canonical_path, file_id=file_id)
                continue
            fragments = result.get("fragments") or []
            if not fragments or not result.get("searchableExecutable"):
                bucket["omitted"] += 1
                pipe.omit("EMPTY_SHORT_CODE", path=item.canonical_path, file_id=file_id)
                continue
            total_units += len(fragments)
            if total_units > contracts.MAX_UNITS:
                bucket["omitted"] += 1
                pipe.omit("UNIT_BUDGET_EXCEEDED", path=item.canonical_path, file_id=file_id)
                total_units -= len(fragments)
                continue
            bucket["extracted"] += 1
            pipe.analyzed.append(AnalyzedFile(
                file_id=file_id, canonical_path=item.canonical_path,
                sha256=item.sha256, language_group=group, dialect=dialect,
                size=item.size, fragments=fragments,
                extraction_sha=result.get("extractionSha256", ""),
                searchable=True))
        try:
            parsers = mechanisms.parser_identity()
        except Exception:
            parsers = {"unavailable": "unknown"}
        record = StageRecord(
            stage=STAGE.EXTRACTION.value, applicability=APPLICABILITY.MANDATORY.value,
            outcome=STAGE_OUTCOME.SUCCEEDED.value,
            duration_seconds=time.perf_counter() - started,
            output_sha256=contracts.sha256_hex(contracts.canonical(
                [f.file_id for f in pipe.analyzed])),
            tool_identity={"parsers": parsers},
            counts={"analyzedFiles": len(pipe.analyzed),
                    "extractedUnits": total_units,
                    "coverageByDialect": coverage_by_dialect,
                    "omissions": len(pipe.omissions)})
    except ContractError as exc:
        record = StageRecord(
            stage=STAGE.EXTRACTION.value, applicability=APPLICABILITY.MANDATORY.value,
            outcome=STAGE_OUTCOME.FAILED.value, error_code=exc.code,
            retryable=_retryable(exc.code),
            duration_seconds=time.perf_counter() - started)
    pipe.record(record)
    LOG.info("run extraction outcome=%s analyzed=%d omissions=%d",
             record.outcome, len(pipe.analyzed),
             len(pipe.omissions) + pipe.omission_overflow)


def _entry_bytes(ctx: RunContext, item: InventoriedFile) -> bytes:
    import io
    import zipfile
    with zipfile.ZipFile(io.BytesIO(ctx.archive)) as archive:
        with archive.open(item.canonical_path.replace("/", "/")) as stream:
            return stream.read()


# -------------------------------------------------------------------- token

def _require_snapshot(pipe: Pipeline) -> ReferenceSnapshot:
    snapshot = pipe.context.snapshot
    if snapshot is None:
        raise ContractError("REFERENCE_GRANT_MISSING", "no admitted reference snapshot")
    return snapshot


def _reference_identity(snapshot) -> dict | None:
    if snapshot is None:
        return None
    return {"snapshotId": snapshot.snapshot_id,
            "modelKey": snapshot.model_key,
            "indexKey": snapshot.index_key}


def _stage_token(pipe: Pipeline) -> None:
    ctx = pipe.context
    started = time.perf_counter()
    try:
        snapshot = _require_snapshot(pipe)
        if not _tool_available(ctx, "dolos"):
            raise ContractError("TOOL_MISSING", "token runner unavailable")
        gallery_by_group: dict[str, list] = {}
        for entry in snapshot.files:
            gallery_by_group.setdefault(entry.language_group, []).append(entry)
        ranked: dict[str, list] = {}
        query_files = [f for f in pipe.analyzed if f.searchable]
        batches = 0
        compared, uncompared = [], []
        for group in contracts.LANGUAGE_GROUPS:
            group_queries = [f for f in query_files if f.language_group == group]
            gallery = gallery_by_group.get(group, [])
            if not group_queries:
                continue
            if not gallery:
                uncompared.append(group)
                continue
            compared.append(group)
            for dialect in sorted({f.dialect for f in group_queries}):
                ranked.update(_token_batch(pipe, snapshot, group, dialect,
                                           [f for f in group_queries if f.dialect == dialect],
                                           [e for e in gallery if e.dialect == dialect] or gallery))
                batches += 1
        if not compared:
            raise ContractError("REFERENCE_GRANT_MISSING", "empty gallery for query languages")
        pipe.token_ranked = ranked
        record = StageRecord(
            stage=STAGE.TOKEN.value, applicability=APPLICABILITY.MANDATORY.value,
            outcome=STAGE_OUTCOME.SUCCEEDED.value,
            duration_seconds=time.perf_counter() - started,
            tool_identity=_tool_identity("dolos", ctx),
            counts={"queryFiles": len(query_files), "batches": batches,
                    "rankedFiles": len(ranked),
                    "comparedGroups": compared, "uncomparedGroups": uncompared})
    except ContractError as exc:
        record = StageRecord(
            stage=STAGE.TOKEN.value, applicability=APPLICABILITY.MANDATORY.value,
            outcome=STAGE_OUTCOME.FAILED.value, error_code=exc.code,
            retryable=_retryable(exc.code),
            duration_seconds=time.perf_counter() - started,
            tool_identity=_tool_identity("dolos", ctx))
    pipe.record(record)
    LOG.info("run token outcome=%s ranked=%d", record.outcome, len(pipe.token_ranked))


def _observed_versions(ctx: RunContext) -> dict:
    return ctx.config.observed_tool_versions or {}


def _tool_available(ctx: RunContext, name: str) -> bool:
    observed = _observed_versions(ctx)
    if name == "dolos":
        return (ctx.config.node_exe is not None and ctx.config.dolos_cli is not None
                and "dolos" in observed)
    if name == "scancode":
        return ctx.config.scancode_exe is not None and "scancode" in observed
    return False


def _tool_identity(name: str, ctx: RunContext) -> dict:
    observed = _observed_versions(ctx)
    if name == "dolos":
        return {"name": "dolos", "version": observed.get("dolos", "unavailable"),
                "k": contracts.TOKEN_K, "window": contracts.TOKEN_WINDOW,
                "available": ctx.config.node_exe is not None and ctx.config.dolos_cli is not None
                and "dolos" in observed}
    if name == "scancode":
        return {"name": "scancode", "version": observed.get("scancode", "unavailable"),
                "available": ctx.config.scancode_exe is not None and "scancode" in observed}
    return {"name": name}


def _record_tool_pid(ctx: RunContext, pid: int) -> None:
    """Record a native descendant so the supervisor can confirm its death."""
    if not isinstance(pid, int) or pid <= 0:
        return
    try:
        with open(ctx.workspace.path / "tool-pids.txt", "a", encoding="utf-8") as stream:
            stream.write(f"{pid}\n")
    except OSError:
        pass


def _token_batch(pipe: Pipeline, snapshot: ReferenceSnapshot, group: str,
                 dialect: str, queries: list, gallery: list) -> dict:
    ctx = pipe.context
    work = ctx.workspace.subdir(f"token-{group}-{dialect}")
    inputs = work / "inputs"
    inputs.mkdir(exist_ok=True)
    ext = _DIALECT_SUFFIX[dialect]
    staged: list[Path] = []
    allowlist = []
    staged_names: dict[str, str] = {}
    for entry in gallery:
        raw = read_gallery_bytes(snapshot, entry)
        name = entry.file_id + ext
        (inputs / name).write_bytes(raw)
        staged.append(inputs / name)
        allowlist.append({"fileId": entry.file_id, "path": name, "sha256": entry.sha256})
        staged_names[entry.file_id] = name
    query_names: dict[str, AnalyzedFile] = {}
    for item in queries:
        raw = (ctx.workspace.path / _staged_name_for(pipe, item)).read_bytes()
        name = item.file_id + ext
        (inputs / name).write_bytes(raw)
        staged.append(inputs / name)
        allowlist.append({"fileId": item.file_id, "path": name, "sha256": item.sha256})
        query_names[item.file_id] = item
    outdir = work / "dolos"
    cli_path = Path(ctx.config.dolos_cli)
    node_modules = cli_path.parent.parent.parent
    token_env = minimal_env({"NODE_PATH": str(node_modules)})
    argv = [str(ctx.config.node_exe), str(ctx.config.dolos_cli), "run",
            "--language", contracts.DOLOS_LANGUAGES[dialect],
            "-k", str(contracts.TOKEN_K), "-w", str(contracts.TOKEN_WINDOW),
            "-f", "csv", "--no-open", "-o", str(outdir),
            *[str(p) for p in staged]]
    result = run_native(argv, cwd=work,
                        timeout_seconds=ctx.config.token_deadline_seconds,
                        env=token_env, cancelled=ctx.cancelled,
                        containment_pgid=ctx.containment_pgid,
                        pid_callback=lambda pid: _record_tool_pid(ctx, pid))
    if result.was_cancelled:
        raise ContractError("CANCELLED")
    if result.output_overflow:
        raise ContractError("TOOL_FAILED", "token output overflow")
    if result.timed_out:
        raise ContractError("TOOL_TIMEOUT", "token runner deadline")
    if result.returncode != 0:
        raise ContractError("TOOL_FAILED", "token runner exit")
    pairs_csv = next(iter(sorted(outdir.rglob("*pairs*.csv"))), None)
    if pairs_csv is None:
        raise ContractError("TOOL_OUTPUT_INVALID", "token pairs missing")
    raw_pairs = _read_pairs_csv(pairs_csv)
    for row in raw_pairs:
        for key in ("similarity", "score", "similarityScore"):
            if row.get(key):
                try:
                    value = float(row[key])
                except ValueError:
                    raise ContractError("TOOL_OUTPUT_INVALID", "token score")
                import math
                if not math.isfinite(value):
                    raise ContractError("TOOL_OUTPUT_INVALID", "token score")
    try:
        bound = mechanisms.bind_token_pairs(raw_pairs, allowlist)
    except (ValueError, KeyError) as exc:
        raise ContractError("TOOL_OUTPUT_INVALID", str(exc)[:200])
    gallery_ids = [e.file_id for e in gallery]
    ranked: dict[str, list] = {}
    for item in queries:
        rows = mechanisms.rank_token_files(bound, item.file_id, gallery_ids,
                                           contracts.TOKEN_TOP)
        ranked[item.file_id] = [
            {"fileId": r["fileId"], "score": float(r["score"]), "rank": int(r["rank"])}
            for r in rows]
    return ranked


def _read_pairs_csv(path: Path) -> list:
    text = path.read_text(encoding="utf-8")
    reader = csv.DictReader(StringIO(text))
    if not reader.fieldnames:
        raise ContractError("TOOL_OUTPUT_INVALID", "token csv header")
    return [{k: (v or "") for k, v in row.items()} for row in reader]


def _staged_name_for(pipe: Pipeline, item: AnalyzedFile) -> str:
    for entry in pipe.inventory.files:
        if entry.canonical_path == item.canonical_path:
            return entry.staged_name
    raise ContractError("INTERNAL_ERROR", "staged bytes missing")


# ----------------------------------------------------------------- semantic

def _stage_semantic(pipe: Pipeline) -> None:
    ctx = pipe.context
    started = time.perf_counter()
    encoded_info: dict = {}
    try:
        snapshot = _require_snapshot(pipe)
        tokenizer, model, loaded = _load_encoder(ctx)
        _verify_encoder_identity(loaded, snapshot)
        windows, _omitted_windows = _map_windows(pipe, tokenizer)
        vectors, vector_sha = _encode_windows(ctx, model, tokenizer, windows)
        encoded_info = {"encodedWindows": len(windows),
                        "encodedRows": len(vectors),
                        "vectorSha256": vector_sha,
                        "loadedCheckpointSha256": loaded["checkpointSha256"],
                        "loadedStateSha256": loaded["loadedFullStateSha256"]}
        ranked = _query_index(ctx, snapshot, windows, vectors)
        pipe.semantic_ranked = ranked
        record = StageRecord(
            stage=STAGE.SEMANTIC.value, applicability=APPLICABILITY.MANDATORY.value,
            outcome=STAGE_OUTCOME.SUCCEEDED.value,
            duration_seconds=time.perf_counter() - started,
            output_sha256=contracts.sha256_hex(contracts.canonical(ranked)),
            tool_identity={"encoder": "unixcoder-base-nine",
                           "modelKey": snapshot.model_key,
                           "indexKey": snapshot.index_key,
                           "loadedCheckpointSha256": loaded["checkpointSha256"],
                           "loadedStateSha256": loaded["loadedFullStateSha256"],
                           "framing": loaded["framing"],
                           "pooling": loaded["pooling"],
                           "device": loaded["device"]},
            counts={"queryWindows": len(windows), "rankedFiles": len(ranked)})
    except ContractError as exc:
        record = StageRecord(
            stage=STAGE.SEMANTIC.value, applicability=APPLICABILITY.MANDATORY.value,
            outcome=STAGE_OUTCOME.FAILED.value, error_code=exc.code,
            retryable=_retryable(exc.code),
            duration_seconds=time.perf_counter() - started,
            counts=encoded_info)
    pipe.record(record)
    LOG.info("run semantic outcome=%s ranked=%d", record.outcome, len(pipe.semantic_ranked))


def _load_encoder(ctx: RunContext):
    try:
        import transformers  # noqa: F401
    except ImportError:
        raise ContractError("TOKENIZER_UNAVAILABLE")
    if ctx.config.model_dir is None or ctx.config.model_manifest is None:
        raise ContractError("MODEL_UNAVAILABLE")
    try:
        expected = json.loads(ctx.config.model_manifest.read_text(encoding="utf-8"))
    except (OSError, ValueError) as exc:
        raise ContractError("MODEL_UNAVAILABLE", str(exc)[:200])
    if not isinstance(expected, dict) or not expected:
        raise ContractError("MODEL_UNAVAILABLE", "manifest")
    try:
        tokenizer, model, loaded = mechanisms.load_frozen_encoder(
            ctx.config.model_dir, ctx.config.model_dir.parent, expected, device="cpu")
    except ImportError as exc:
        raise ContractError("MODEL_UNAVAILABLE", str(exc)[:200])
    except (ValueError, OSError) as exc:
        raise ContractError("MODEL_UNAVAILABLE", str(exc)[:200])
    loaded = {**loaded, "framing": "encoder-only",
              "pooling": "padding-masked mean then L2"}
    return tokenizer, model, loaded


def _verify_encoder_identity(loaded: dict, snapshot: ReferenceSnapshot) -> None:
    """Bind the actually loaded encoder to the admitted model identity.

    A separately hash-valid model (or tokenizer, framing, pooling, mask,
    preprocessing, chunking change) never encodes for another index: the
    manifest assertion is not substituted for measured loader identity.
    """
    identity = snapshot.model_identity
    if not identity:
        raise ContractError("REFERENCE_MANIFEST_INVALID", "modelIdentity")
    try:
        actual = mechanisms.actual_representation()
    except ImportError as exc:
        raise ContractError("MODEL_UNAVAILABLE", type(exc).__name__)
    if (loaded.get("checkpointSha256") != identity.get("checkpointSha256")
            or loaded.get("tokenizerHashes") != identity.get("tokenizerHashes")
            or loaded.get("dimension") != identity.get("dimension")
            or loaded.get("dimension") != 768
            or loaded.get("device") != "cpu"):
        raise ContractError("MODEL_UNAVAILABLE", "loaded encoder mismatch")
    for name in ("framing", "pooling", "mask", "preprocessing", "chunking"):
        if actual.get(name) != identity.get(name) or loaded.get(name) not in (
                None, identity.get(name)):
            raise ContractError("MODEL_UNAVAILABLE", "representation mismatch")


def _map_windows(pipe: Pipeline, tokenizer) -> tuple[list, int]:
    windows: list = []
    omitted = 0
    for item in pipe.analyzed:
        raw = (pipe.context.workspace.path / _staged_name_for(pipe, item)).read_bytes()
        for fragment in item.fragments:
            try:
                mapped = mechanisms.mapped_chunks(raw, fragment, tokenizer)
            except ValueError:
                pipe.omit("WINDOW_BUDGET_EXCEEDED", path=item.canonical_path,
                          file_id=item.file_id)
                omitted += 1
                continue
            for chunk in mapped.get("chunks", []):
                if len(windows) >= contracts.MAX_WINDOWS:
                    pipe.omit("WINDOW_BUDGET_EXCEEDED", path=item.canonical_path,
                              file_id=item.file_id)
                    omitted += 1
                    continue
                windows.append({**chunk, "queryFileId": item.file_id,
                                "queryGroup": item.language_group})
            if mapped.get("partialMLCoverage"):
                pipe.omit("WINDOW_TRUNCATED", path=item.canonical_path,
                          file_id=item.file_id)
    return windows, omitted


def _encode_windows(ctx: RunContext, model, tokenizer, windows: list) -> tuple[list, str]:
    if not windows:
        return [], contracts.sha256_hex(b"")
    chunks = [{"inputIds": w["inputIds"], "framingTokens": w.get("framingTokens", 4)}
              for w in windows]
    try:
        vectors, _receipt = mechanisms.encode_frozen_chunks(
            model, tokenizer, chunks, batch_size=8)
    except (ValueError, RuntimeError) as exc:
        raise ContractError("MODEL_UNAVAILABLE", str(exc)[:200])
    raw = mechanisms.check_vector_bytes(vectors)
    return vectors, contracts.sha256_hex(raw)


def _query_index(ctx: RunContext, snapshot: ReferenceSnapshot,
                 windows: list, vectors: list) -> dict:
    """Exact read-only retrieval over the admitted reference snapshot.

    Verifies READY identity, ModelKey/IndexKey, dimension, snapshot,
    partition, and allowlisted gallery IDs before any distance is trusted.
    Aggregation covers the full admitted gallery; chunk results are never
    truncated before file aggregation. The DSN is never logged.
    """
    if not windows:
        return {}
    identity = snapshot.model_identity
    if not identity:
        raise ContractError("REFERENCE_MANIFEST_INVALID", "modelIdentity")
    try:
        import psycopg  # noqa: F401
    except ImportError:
        raise ContractError("INDEX_UNAVAILABLE")
    if not ctx.config.index_dsn:
        raise ContractError("INDEX_UNAVAILABLE", "no reference DSN configured")
    try:
        expected_model = mechanisms.reference_model_key(snapshot.model_identity)
    except ImportError as exc:
        raise ContractError("INDEX_UNAVAILABLE", type(exc).__name__)
    if expected_model != snapshot.model_key:
        raise ContractError("REFERENCE_MANIFEST_INVALID", "modelKey")
    connection = _connect_readonly(ctx.config.index_dsn)
    try:
        _verify_index_row(connection, snapshot)
        return _exact_retrieval(connection, snapshot, windows, vectors)
    finally:
        try:
            connection.close()
        except Exception:
            pass


def _connect_readonly(dsn: str):
    import psycopg
    try:
        connection = psycopg.connect(dsn, connect_timeout=10, autocommit=True,
                                     options="-c default_transaction_read_only=on")
    except Exception as exc:
        raise ContractError("INDEX_UNAVAILABLE", type(exc).__name__)
    return connection


def _verify_index_row(connection, snapshot: ReferenceSnapshot) -> None:
    try:
        from scripts.code_analysis import code_index as research_index
    except ImportError as exc:
        raise ContractError("INDEX_UNAVAILABLE", type(exc).__name__)
    try:
        research_index.preflight(connection)
    except (ValueError, Exception) as exc:
        raise ContractError("INDEX_UNAVAILABLE", type(exc).__name__)
    try:
        with connection.cursor() as cursor:
            cursor.execute(
                "SELECT state, partition, expected_rows, dimension, model_key,"
                " snapshot_id FROM code_lab.code_indexes WHERE index_key=%s",
                (snapshot.index_key,))
            row = cursor.fetchone()
    except Exception as exc:
        raise ContractError("INDEX_UNAVAILABLE", type(exc).__name__)
    if row is None:
        raise ContractError("INDEX_NOT_READY")
    state, partition, _expected, dimension, model_key, snapshot_id = row
    if (state != "READY" or partition != snapshot.partition
            or dimension != 768 or model_key != snapshot.model_key
            or snapshot_id != snapshot.snapshot_id):
        raise ContractError("INDEX_NOT_READY")


def _exact_retrieval(connection, snapshot: ReferenceSnapshot,
                     windows: list, vectors: list) -> dict:
    try:
        from scripts.code_analysis import code_index as research_index
    except ImportError as exc:
        raise ContractError("INDEX_UNAVAILABLE", type(exc).__name__)
    gallery_by_group: dict[str, list] = {}
    for entry in snapshot.files:
        gallery_by_group.setdefault(entry.language_group, []).append(entry.file_id)
    by_file: dict[str, list] = {}
    groups: dict[str, str] = {}
    for window, vector in zip(windows, vectors):
        by_file.setdefault(window["queryFileId"], []).append(vector)
        groups[window["queryFileId"]] = window["queryGroup"]
    ranked: dict[str, list] = {}
    for query_id, query_vectors in by_file.items():
        group = groups[query_id]
        gallery_ids = gallery_by_group.get(group, [])
        if not gallery_ids:
            continue
        best: dict[str, float] = {}
        for offset in range(0, len(query_vectors), 80):
            batch = query_vectors[offset:offset + 80]
            try:
                rows = research_index.exact_query(
                    connection, snapshot.index_key, batch, gallery_ids,
                    group, partition=snapshot.partition, limit=contracts.SEMANTIC_TOP)
            except (ValueError, Exception) as exc:
                raise ContractError("INDEX_UNAVAILABLE", type(exc).__name__)
            for row in rows:
                distance = float(row["distance"])
                if row["fileId"] not in best or distance < best[row["fileId"]]:
                    best[row["fileId"]] = distance
        ordered = sorted(best, key=lambda fid: (best[fid], fid))[:contracts.SEMANTIC_TOP]
        ranked[query_id] = [{"fileId": fid,
                             "score": max(0.0, 1.0 - best[fid]),
                             "rank": rank}
                            for rank, fid in enumerate(ordered, 1)]
    return ranked


# ------------------------------------------------------------------ license

def _normalize_expression(detection: dict) -> str:
    raw = (detection.get("license_expression_spdx")
           or detection.get("license_expression")
           or detection.get("key") or "")
    text = str(raw).strip()
    if not text:
        return "UNKNOWN"
    lowered = text.lower()
    if lowered == "mit":
        return "MIT"
    if lowered in ("apache-2.0", "apache 2.0"):
        return "Apache-2.0"
    if lowered == "bsd-3-clause":
        return "BSD-3-Clause"
    return text[:128]


def _stage_license(pipe: Pipeline) -> None:
    ctx = pipe.context
    started = time.perf_counter()
    try:
        if not _tool_available(ctx, "scancode"):
            raise ContractError("TOOL_MISSING", "license runner unavailable")
        work = ctx.workspace.subdir("license")
        sources = work / "sources"
        sources.mkdir(exist_ok=True)
        staged_map: dict[str, str] = {}
        for item in pipe.inventory.files:
            raw = (ctx.workspace.path / item.staged_name).read_bytes()
            target = sources / item.staged_name
            target.write_bytes(raw)
            staged_map[item.staged_name] = item.canonical_path
        output = work / "scancode.json"
        argv = [str(ctx.config.scancode_exe), "--license", "--copyright",
                "--package", "--info", "--processes", "1", "--timeout", "60",
                "--json-pp", str(output), str(sources)]
        result = run_native(argv, cwd=work,
                            timeout_seconds=ctx.config.scancode_deadline_seconds,
                            env=minimal_env(), cancelled=ctx.cancelled,
                            containment_pgid=ctx.containment_pgid,
                            pid_callback=lambda pid: _record_tool_pid(ctx, pid))
        if result.was_cancelled:
            raise ContractError("CANCELLED")
        if result.output_overflow:
            raise ContractError("TOOL_FAILED", "license output overflow")
        if result.timed_out:
            raise ContractError("TOOL_TIMEOUT", "license runner deadline")
        if result.returncode != 0 or not output.exists():
            raise ContractError("TOOL_FAILED", "license runner exit")
        try:
            payload = json.loads(output.read_text(encoding="utf-8"))
        except (OSError, ValueError) as exc:
            raise ContractError("TOOL_OUTPUT_INVALID", str(exc)[:200])
        rows, scan_errors = _normalize_scancode(payload, staged_map)
        if scan_errors:
            raise ContractError("SCAN_ERROR", scan_errors[0][:200])
        pipe.license_rows = rows
        _reconcile_declaration(pipe)
        record = StageRecord(
            stage=STAGE.LICENSE.value, applicability=APPLICABILITY.MANDATORY.value,
            outcome=STAGE_OUTCOME.SUCCEEDED.value,
            duration_seconds=time.perf_counter() - started,
            output_sha256=contracts.sha256_hex(contracts.canonical(rows)),
            tool_identity=_tool_identity("scancode", ctx),
            counts={"scannedFiles": len(rows),
                    "unknownDetections": sum(1 for r in rows if r["expression"] == "UNKNOWN")})
    except ContractError as exc:
        record = StageRecord(
            stage=STAGE.LICENSE.value, applicability=APPLICABILITY.MANDATORY.value,
            outcome=STAGE_OUTCOME.FAILED.value, error_code=exc.code,
            retryable=_retryable(exc.code),
            duration_seconds=time.perf_counter() - started,
            tool_identity=_tool_identity("scancode", ctx))
    pipe.record(record)
    LOG.info("run license outcome=%s scanned=%d", record.outcome, len(pipe.license_rows))


def _normalize_scancode(payload: dict, staged_map: dict) -> tuple[list, list]:
    if not isinstance(payload, dict) or not isinstance(payload.get("files"), list):
        raise ContractError("TOOL_OUTPUT_INVALID", "license payload shape")
    rows = []
    scan_errors = []
    for item in payload["files"]:
        if not isinstance(item, dict):
            continue
        staged = str(item.get("path", "")).replace("\\", "/").split("/")[-1]
        canonical = staged_map.get(staged, staged[:512])
        errors = item.get("scan_errors") or []
        if errors:
            scan_errors.append(canonical)
            continue
        detections = item.get("license_detections") or item.get("licenses") or []
        expressions = sorted({_normalize_expression(d) for d in detections
                              if isinstance(d, dict)})
        copyrights = item.get("copyrights") or []
        rows.append({"path": canonical,
                     "expression": " AND ".join(expressions) if expressions else "UNKNOWN",
                     "copyrightStatements": len(copyrights)})
    rows.sort(key=lambda r: r["path"])
    return rows, sorted(set(scan_errors))


def _reconcile_declaration(pipe: Pipeline) -> None:
    declared = pipe.context.metadata.declared_licenses
    if not declared:
        return
    by_path = {r["path"]: r for r in pipe.license_rows}
    for entry in declared:
        scope = entry.scope_path.strip("/")
        matched = [r for p, r in by_path.items()
                   if p == scope or p.startswith(scope.rstrip("/") + "/")]
        if not matched:
            pipe.add_finding(FINDING_TYPE.NOTICE_MISSING.value, None, None,
                             f"Declared license scope has no scanned files: {scope[:128]}")
            continue
        detected = {r["expression"] for r in matched} - {"UNKNOWN"}
        if not any(entry.spdx.lower() in expr.lower() for expr in detected):
            if all(r["expression"] == "UNKNOWN" for r in matched):
                for row in matched:
                    pipe.add_finding(
                        FINDING_TYPE.LICENSE_UNKNOWN.value, row["path"], None,
                        "No license signal detected; a declaration alone is not a detection.")
            else:
                pipe.add_finding(
                    FINDING_TYPE.LICENSE_CONTRADICTION.value, None, None,
                    f"Declared {entry.spdx[:64]} is not among detected signals "
                    f"under scope {scope[:128]}.")


# -------------------------------------------------------------------- union

def _stage_union(pipe: Pipeline) -> None:
    started = time.perf_counter()
    try:
        snapshot = _require_snapshot(pipe)
        by_id = {e.file_id: e for e in snapshot.files}
        for item in pipe.analyzed:
            token_ids = [r["fileId"] for r in pipe.token_ranked.get(item.file_id, [])]
            semantic_ids = [r["fileId"] for r in pipe.semantic_ranked.get(item.file_id, [])]
            union = mechanisms.union_candidates(token_ids, semantic_ids,
                                                contracts.UNION_BUDGET)
            ordering = mechanisms.reciprocal_rank_order(token_ids, semantic_ids,
                                                        contracts.UNION_BUDGET)
            token_set, semantic_set = set(token_ids), set(semantic_ids)
            for foreign_id in union:
                entry = by_id.get(foreign_id)
                if entry is None:
                    raise ContractError("TOOL_OUTPUT_INVALID", "unknown union member")
                channel = ("TOKEN" if foreign_id in token_set else "") + \
                          ("+SEMANTIC" if foreign_id in semantic_ids else "")
                channel = channel.lstrip("+") if channel else "SEMANTIC"
                ftype = (FINDING_TYPE.SIMILARITY_TOKEN.value
                         if foreign_id in token_set else FINDING_TYPE.SIMILARITY_SEMANTIC.value)
                pipe.add_finding(
                    ftype, item.canonical_path, item.file_id,
                    _similarity_reason(entry, channel),
                    foreign_id=foreign_id, sensitivity=entry.sensitivity,
                    origin=entry.origin,
                    attribution=dict(entry.attribution),
                    score=_channel_score(pipe, item.file_id, foreign_id, token_set))
        record = StageRecord(
            stage=STAGE.UNION.value, applicability=APPLICABILITY.MANDATORY.value,
            outcome=STAGE_OUTCOME.SUCCEEDED.value,
            duration_seconds=time.perf_counter() - started,
            output_sha256=contracts.sha256_hex(contracts.canonical(
                [f["findingId"] for f in pipe.findings])),
            counts={"unionFindings": len(pipe.findings)})
    except ContractError as exc:
        record = StageRecord(
            stage=STAGE.UNION.value, applicability=APPLICABILITY.MANDATORY.value,
            outcome=STAGE_OUTCOME.FAILED.value, error_code=exc.code,
            retryable=_retryable(exc.code),
            duration_seconds=time.perf_counter() - started)
    pipe.record(record)
    LOG.info("run union outcome=%s findings=%d", record.outcome, len(pipe.findings))


def _channel_score(pipe: Pipeline, query_id: str, foreign_id: str, token_set: set):
    if foreign_id in token_set:
        for row in pipe.token_ranked.get(query_id, []):
            if row["fileId"] == foreign_id:
                return float(row["score"])
    for row in pipe.semantic_ranked.get(query_id, []):
        if row["fileId"] == foreign_id:
            return float(row.get("score", 0.0))
    return 0.0


def _similarity_reason(entry, channel: str) -> str:
    if entry.origin == "OWN":
        return (f"Overlaps the seller's own prior {channel} match. "
                "Own reuse is informative and is not infringement evidence.")
    if entry.sensitivity == SENSITIVITY.PRIVATE.value:
        return ("Similar code was found in another private asset. Its code is not "
                "shared. A reviewer decides what follows; similarity alone proves nothing.")
    label = entry.attribution.get("label", "a public reference")
    return (f"Overlaps public reference {label[:160]} ({channel}). "
            "Similarity alone proves nothing; review before acting.")


# ----------------------------------------------------------------- verifier

def _stage_verifier(pipe: Pipeline) -> None:
    started = time.perf_counter()
    pipe.record(StageRecord(
        stage=STAGE.VERIFIER.value, applicability=APPLICABILITY.MANDATORY.value,
        outcome=STAGE_OUTCOME.SUCCEEDED.value,
        duration_seconds=time.perf_counter() - started,
        tool_identity={"mode": VERIFIER_MODE.PENDING_RESEARCH_EVALUATION.value},
        counts={"outputs": 0}))


# ------------------------------------------------------------------ report

def _coverage_level(pipe: Pipeline) -> str:
    if not pipe.analyzed:
        return COVERAGE.UNSUPPORTED.value
    if pipe.omissions or pipe.omission_overflow:
        return COVERAGE.PARTIAL.value
    return COVERAGE.FULL.value


def _success_envelope(pipe: Pipeline) -> dict:
    ctx = pipe.context
    coverage = _coverage_level(pipe)
    findings = sorted(pipe.findings, key=lambda f: (f["type"], f.get("path") or "",
                                                    f["findingId"]))
    total = len(findings)
    kept = findings[:contracts.MAX_FINDINGS]
    overflow = total - len(kept)
    body = {
        "schema": contracts.SCHEMA,
        "serviceVersion": contracts.SERVICE_VERSION,
        "outcome": RUN_OUTCOME.COMPLETED.value,
        "runId": ctx.metadata.run_id,
        "inputDigest": ctx.metadata.input_digest,
        "contentSha256": ctx.metadata.content_sha256,
        "assetId": ctx.metadata.asset_id,
        "versionId": ctx.metadata.version_id,
        "policy": {"policyVersion": ctx.policy["policyVersion"],
                   "policyDigest": ctx.policy["policyDigest"],
                   "verifierMode": VERIFIER_MODE.PENDING_RESEARCH_EVALUATION.value,
                   "canAuthorizePublication": False},
        "securityEvidence": {"archiveEvidenceId": ctx.metadata.security_evidence.archive_evidence_id,
                             "malwareEvidenceId": ctx.metadata.security_evidence.malware_evidence_id,
                             "securityGeneration": ctx.metadata.security_evidence.security_generation,
                             "contentSha256": ctx.metadata.security_evidence.content_sha256},
        "reservation": {"serviceKey": ctx.metadata.reservation.service_key,
                        "slotNumber": ctx.metadata.reservation.slot_number,
                        "generation": ctx.metadata.reservation.generation},
        "stages": [s.as_dict() for s in pipe.stages],
        "coverage": {"level": coverage,
                     "analyzedFiles": len(pipe.analyzed),
                     "omissions": pipe.omissions,
                     "omissionOverflow": pipe.omission_overflow},
        "findings": kept,
        "findingOverflow": overflow,
        "comparedScope": {"snapshotId": ctx.snapshot.snapshot_id if ctx.snapshot else None,
                          "revision": ctx.snapshot.revision if ctx.snapshot else None,
                          "comparedFiles": len(ctx.snapshot.files) if ctx.snapshot else 0},
        "admittedBy": None,
        "cleanupError": None,
    }
    # The report digest binds the pre-REPORT payload; the REPORT record then
    # binds findings/coverage/stage evidence to that digest. This avoids
    # self-referential hashing while keeping all 8 mandatory records.
    body["reportId"] = contracts.sha256_hex(contracts.canonical(body))
    report_record = StageRecord(
        stage=STAGE.REPORT.value, applicability=APPLICABILITY.MANDATORY.value,
        outcome=STAGE_OUTCOME.SUCCEEDED.value,
        duration_seconds=0.0,
        input_sha256=contracts.sha256_hex(contracts.canonical(
            {"findings": [f["findingId"] for f in kept],
             "coverage": body["coverage"],
             "stages": [s["stage"] for s in body["stages"]]})),
        output_sha256=body["reportId"],
        counts={"findings": len(kept), "findingOverflow": overflow,
                "omissions": len(pipe.omissions),
                "omissionOverflow": pipe.omission_overflow})
    pipe.record(report_record)
    body["stages"] = [s.as_dict() for s in pipe.stages]
    body["sellerSafe"] = build_seller_safe(body)
    _validate_report_body(pipe, body)
    encoded = contracts.canonical(body)
    if len(encoded) > contracts.MAX_RESPONSE_BYTES:
        report_record.outcome = STAGE_OUTCOME.FAILED.value
        report_record.error_code = "REPORT_TOO_LARGE"
        report_record.retryable = False
        body["stages"] = [s.as_dict() for s in pipe.stages]
        raise ContractError("REPORT_TOO_LARGE")
    return body


def _validate_report_body(pipe: Pipeline, body: dict) -> None:
    """Schema, binding, projection, and budget checks before admission to COMPLETED."""
    if body.get("schema") != contracts.SCHEMA:
        raise ContractError("INTERNAL_ERROR", "report schema")
    if body.get("runId") != pipe.context.metadata.run_id:
        raise ContractError("INTERNAL_ERROR", "report binding")
    if body.get("inputDigest") != pipe.context.metadata.input_digest:
        raise ContractError("INTERNAL_ERROR", "report binding")
    if body.get("admittedBy") is not None:
        raise ContractError("INTERNAL_ERROR", "report authority")
    if body.get("policy", {}).get("canAuthorizePublication") is not False:
        raise ContractError("INTERNAL_ERROR", "report authority")
    safe = body.get("sellerSafe") or {}
    if safe.get("reportId") != body.get("reportId"):
        raise ContractError("INTERNAL_ERROR", "report projection")
    if len(body.get("findings", [])) > contracts.MAX_FINDINGS:
        raise ContractError("INTERNAL_ERROR", "report budget")
    if len(body.get("coverage", {}).get("omissions", [])) > contracts.MAX_OMISSIONS:
        raise ContractError("INTERNAL_ERROR", "report budget")


def _full_matrix(pipe: Pipeline) -> bool:
    succeeded = {s.stage for s in pipe.stages
                 if s.outcome == STAGE_OUTCOME.SUCCEEDED.value}
    return {stage.value for stage in STAGE} <= succeeded


def _failure_envelope(pipe: Pipeline) -> dict:
    ctx = pipe.context
    failed = next(s for s in pipe.stages if s.outcome == STAGE_OUTCOME.FAILED.value)
    return {
        "schema": contracts.SCHEMA,
        "serviceVersion": contracts.SERVICE_VERSION,
        "outcome": RUN_OUTCOME.FAILED.value,
        "runId": ctx.metadata.run_id,
        "inputDigest": ctx.metadata.input_digest,
        "contentSha256": ctx.metadata.content_sha256,
        "assetId": ctx.metadata.asset_id,
        "versionId": ctx.metadata.version_id,
        "policy": {"policyVersion": ctx.policy["policyVersion"],
                   "policyDigest": ctx.policy["policyDigest"],
                   "verifierMode": VERIFIER_MODE.PENDING_RESEARCH_EVALUATION.value,
                   "canAuthorizePublication": False},
        "stages": [s.as_dict() for s in pipe.stages],
        "coverage": {"level": _coverage_level(pipe),
                     "analyzedFiles": len(pipe.analyzed),
                     "omissions": pipe.omissions,
                     "omissionOverflow": pipe.omission_overflow},
        "findings": [],
        "errorCode": failed.error_code,
        "retryable": failed.retryable,
        "failedStage": failed.stage,
        "admittedBy": None,
        "cleanupError": None,
    }


def build_seller_safe(report: dict) -> dict:
    """Project the immutable report to owner-safe fields only.

    Foreign private names, paths, snippets, vectors, and scores are removed.
    Counts and generic wording remain.
    """
    safe_findings = []
    for item in report.get("findings", []):
        foreign = item.get("foreign") or {}
        if foreign.get("sensitivity") == SENSITIVITY.PRIVATE.value:
            basis = ("Similar code was found in another private asset. Its code is "
                     "not shared with you. A moderator will review the match.")
        else:
            basis = f"Overlaps public reference {(foreign.get('attribution') or {}).get('label', '')[:160]}.".strip()
        safe_findings.append({
            "findingId": item["findingId"],
            "type": item["type"],
            "path": item.get("path"),
            "startLine": item.get("startLine"),
            "endLine": item.get("endLine"),
            "ownExcerpt": (item.get("ownExcerpt") or "")[:320],
            "basis": basis,
            "reason": item.get("reason"),
            "requestedAction": item.get("requestedAction"),
        })
    return {
        "reportId": report.get("reportId"),
        "runId": report.get("runId"),
        "outcome": report.get("outcome"),
        "coverage": report.get("coverage", {}).get("level"),
        "omissionCount": len(report.get("coverage", {}).get("omissions", [])),
        "findings": safe_findings,
        "findingOverflow": report.get("findingOverflow", 0),
        "canAuthorizePublication": False,
    }


def _finding_id(ftype: str, path: str | None, file_id: str | None,
                foreign_id: str | None, reason: str) -> str:
    return contracts.sha256_hex(contracts.canonical(
        [ftype, path or "", file_id or "", foreign_id or "", reason]))


def _append_finding(pipe: Pipeline, ftype: str, path: str | None, file_id: str | None,
                    reason: str, foreign_id: str | None = None, sensitivity: str | None = None,
                    origin: str | None = None, attribution: dict | None = None,
                    score=None) -> None:
    if len(pipe.findings) >= 10 * contracts.MAX_FINDINGS:
        pipe.finding_overflow += 1
        return
    excerpt = ""
    start_line = end_line = None
    if file_id is not None:
        excerpt, start_line, end_line = _own_excerpt(pipe, file_id)
    pipe.findings.append({
        "findingId": _finding_id(ftype, path, file_id, foreign_id, reason),
        "type": ftype,
        "path": path[:512] if path else None,
        "fileId": file_id,
        "startLine": start_line,
        "endLine": end_line,
        "ownExcerpt": excerpt,
        "foreign": ({"fileId": foreign_id, "sensitivity": sensitivity,
                      "origin": origin,
                      "attribution": attribution or {}} if foreign_id else None),
        "channelScore": score,
        "reason": reason[:1024],
        "requestedAction": _requested_action(ftype),
    })


def _requested_action(ftype: str) -> str:
    return {
        FINDING_TYPE.SIMILARITY_TOKEN.value:
            "Review the overlapping code and rewrite it in your own words if it is not yours.",
        FINDING_TYPE.SIMILARITY_SEMANTIC.value:
            "Review the overlapping code and rewrite it in your own words if it is not yours.",
        FINDING_TYPE.LICENSE_UNKNOWN.value:
            "Add an explicit license header or notice file covering this file.",
        FINDING_TYPE.LICENSE_CONTRADICTION.value:
            "Align the declared license with the detected license evidence, or remove the conflicting file.",
        FINDING_TYPE.NOTICE_MISSING.value:
            "Provide the missing license notice or narrow the declaration scope.",
    }.get(ftype, "Review the reported concern.")


def _own_excerpt(pipe: Pipeline, file_id: str) -> tuple[str, int | None, int | None]:
    for item in pipe.analyzed:
        if item.file_id == file_id and item.fragments:
            first = min(item.fragments, key=lambda f: (f["startByte"], f["endByte"]))
            raw = (pipe.context.workspace.path / _staged_name_for(pipe, item)).read_bytes()
            excerpt = raw[first["startByte"]:first["endByte"]].decode("utf-8", errors="replace")
            return excerpt[:320], first.get("startLine"), first.get("endLine")
    return "", None, None
