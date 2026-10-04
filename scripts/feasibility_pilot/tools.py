"""Bounded local Dolos and ScanCode runners."""

from __future__ import annotations

import csv
import json
import shutil
from io import StringIO
from pathlib import Path
from typing import Any

from .inventory import sha256_file
from .paths import ARTIFACT_ROOT, FIXTURE_ROOT, env_dir
from .subprocess_util import reject_npx, run_bounded


DOLOS_LANGUAGES = {
    "javascript": "javascript",
    "typescript": "typescript",
    "tsx": "tsx",
    "python": "python",
    "java": "java",
    "csharp": "c-sharp",
}


def dolos_cli() -> tuple[Path, Path] | None:
    prefix = env_dir("dolos")
    cli = prefix / "node_modules" / "@dodona" / "dolos" / "dist" / "cli.js"
    node = shutil.which("node")
    if cli.exists() and node:
        return Path(node), cli
    return None


def scancode_executable() -> Path | None:
    prefix = env_dir("scancode")
    for relative in ("Scripts/scancode.exe", "bin/scancode"):
        candidate = prefix / relative
        if candidate.exists():
            return candidate
    return None


def run_dolos_batch(
    *,
    language: str,
    files: list[Path],
    output_dir: Path,
    k: int,
    window: int,
    timeout_s: float,
) -> dict[str, Any]:
    resolved = dolos_cli()
    if resolved is None:
        return {
            "language": language,
            "outcome": "FAILED",
            "executionState": "SKIPPED_DEPENDENCY",
            "reason": "Dolos CLI or host node executable not available in the isolated prefix",
        }
    node, cli = resolved
    prefix = env_dir("dolos")
    output_dir.parent.mkdir(parents=True, exist_ok=True)
    if output_dir.exists():
        shutil.rmtree(output_dir)
    argv = [
        str(node),
        str(cli),
        "run",
        "--language",
        DOLOS_LANGUAGES[language],
        "-k",
        str(k),
        "-w",
        str(window),
        "-f",
        "csv",
        "--no-open",
        "-o",
        str(output_dir),
        *[str(path) for path in files],
    ]
    reject_npx(argv)
    result = run_bounded(
        argv,
        cwd=output_dir.parent,
        allowed_roots=[ARTIFACT_ROOT, FIXTURE_ROOT, node.parent],
        timeout_s=timeout_s,
        extra_env_allow=("NODE_PATH",),
        env={"NODE_PATH": str(prefix / "node_modules")},
    )
    pairs_csv = _find_csv(output_dir, "pairs")
    parsed = _parse_pairs_csv(pairs_csv) if pairs_csv else []
    csv_exists = bool(pairs_csv and pairs_csv.exists())
    csv_sha = sha256_file(pairs_csv) if csv_exists else None
    input_binding = {path.as_posix(): sha256_file(path) for path in files}
    outcome = "SUCCEEDED"
    reason = "dolos csv written"
    if result.timed_out or result.output_overflow or result.returncode != 0:
        outcome = "FAILED"
        reason = "dolos process failed"
    elif not csv_exists:
        outcome = "FAILED"
        reason = "dolos produced no pairs csv"
    elif not _csv_has_header(pairs_csv):
        outcome = "FAILED"
        reason = "dolos csv is missing a header"
    return {
        "language": language,
        "cliLanguage": DOLOS_LANGUAGES[language],
        "k": k,
        "window": window,
        "kFlag": "-k/--kgram-length",
        "windowFlag": "-w/--kgrams-in-window",
        "kDefaultDocumented": 23,
        "windowDefaultDocumented": 17,
        "outcome": outcome,
        "executionState": "COMPLETED",
        "reason": reason,
        "returncode": result.returncode,
        "timedOut": result.timed_out,
        "outputOverflow": result.output_overflow,
        "stderr": result.stderr[-4000:],
        "stdoutTail": result.stdout[-2000:],
        "pairs": parsed,
        "rawCsv": str(pairs_csv) if csv_exists else None,
        "csvExists": csv_exists,
        "csvSha256": csv_sha,
        "inputBinding": input_binding,
        "durationSeconds": result.duration_s,
        "invocation": "host-node + isolated dist/cli.js",
    }


def run_scancode(input_dir: Path, output_json: Path, timeout_s: float) -> dict[str, Any]:
    exe = scancode_executable()
    if exe is None:
        return {
            "outcome": "FAILED",
            "executionState": "SKIPPED_DEPENDENCY",
            "reason": "ScanCode is not installed; license terms need a reviewed exception before install",
        }
    output_json.parent.mkdir(parents=True, exist_ok=True)
    argv = [
        str(exe),
        "--license",
        "--copyright",
        "--package",
        "--info",
        "--processes",
        "1",
        "--timeout",
        "60",
        "--json-pp",
        str(output_json),
        str(input_dir),
    ]
    result = run_bounded(
        argv,
        cwd=output_json.parent,
        allowed_roots=[ARTIFACT_ROOT, FIXTURE_ROOT, env_dir("scancode")],
        timeout_s=timeout_s,
    )
    payload: dict[str, Any] = {}
    if output_json.exists():
        payload = json.loads(output_json.read_text(encoding="utf-8"))
    outcome = "SUCCEEDED"
    if result.timed_out or result.returncode != 0 or result.output_overflow or not output_json.exists():
        outcome = "FAILED"
    reason = "scancode completed"
    if result.timed_out:
        reason = "scancode outer process timed out"
    elif result.returncode != 0:
        reason = f"scancode exited {result.returncode}"
    elif not output_json.exists():
        reason = "scancode produced no json"
    files = []
    for item in payload.get("files", []):
        files.append(
            {
                "path": item.get("path"),
                "scanErrors": item.get("scan_errors") or [],
                "licenseDetections": item.get("license_detections") or item.get("licenses") or [],
                "copyrights": item.get("copyrights") or [],
                "packageData": item.get("package_data") or [],
            }
        )
    files_with_errors = [item["path"] for item in files if item.get("scanErrors")]
    if files_with_errors and outcome == "SUCCEEDED":
        outcome = "FAILED"
        reason = "scancode reported per-file scan errors"
    return {
        "outcome": outcome,
        "executionState": "COMPLETED",
        "reason": reason,
        "returncode": result.returncode,
        "timedOut": result.timed_out,
        "outputOverflow": result.output_overflow,
        "files": files,
        "filesWithScanErrors": files_with_errors,
        "stderr": result.stderr[-4000:],
        "durationSeconds": result.duration_s,
        "toolVersion": payload.get("headers", [{}])[0].get("tool_version") if payload.get("headers") else None,
        "processes": 1,
        "perFileTimeoutSeconds": 60,
        "outerTimeoutSeconds": timeout_s,
    }


def dolos_reuse_allowed(item: dict[str, Any], current_files: list[Path]) -> bool:
    if item.get("outcome") != "SUCCEEDED":
        return False
    raw = item.get("rawCsv")
    if not raw:
        return False
    csv_path = Path(raw)
    if not csv_path.exists() or not csv_path.is_file():
        return False
    if not item.get("csvSha256") or sha256_file(csv_path) != item["csvSha256"]:
        return False
    if item.get("k") is None or item.get("window") is None:
        return False
    stored = item.get("inputBinding") or {}
    current = {path.as_posix(): sha256_file(path) for path in current_files}
    return stored == current


def evaluate_scancode_report(report: dict[str, Any], packages: list[dict[str, Any]]) -> dict[str, Any]:
    files = report.get("files") or []
    by_path = {_norm_scan_path(item.get("path")): item for item in files if item.get("path")}
    mismatches: list[dict[str, Any]] = []
    if report.get("filesWithScanErrors"):
        mismatches.append({"reason": "per-file scan errors", "paths": report.get("filesWithScanErrors")})
    evaluations = []
    for package in packages:
        expected = package.get("expectedSignals") or {}
        pkg_files = package.get("resolvedFiles") or package.get("files") or []
        rows = []
        for meta in pkg_files:
            rel = _norm_scan_path(meta.get("relativePath") or meta.get("path"))
            rows.append(_lookup_scan_row(rel, by_path))
        detections = []
        copyrights = []
        scan_errors = []
        for row in rows:
            if not row:
                continue
            detections.extend(row.get("licenseDetections") or [])
            copyrights.extend(row.get("copyrights") or [])
            scan_errors.extend(row.get("scanErrors") or [])
        detected_ids = {_license_id(item) for item in detections}
        required = expected.get("license")
        diagnostic_unknown = required in {"UNKNOWN", "UNKNOWN_OR_CUSTOM"}
        ok = True
        detail = {"id": package.get("id"), "expected": expected, "detected": sorted(x for x in detected_ids if x)}
        if scan_errors:
            ok = False
            detail["reason"] = "scan error"
        elif required in {"MIT", "Apache-2.0", "BSD-3-Clause"} and not _signal_present(required, detected_ids):
            ok = False
            detail["reason"] = f"missing required {required} signal"
        elif diagnostic_unknown:
            detail["reason"] = "expected UNKNOWN diagnostic"
        if expected.get("noticePresent") is True:
            notice_files = [
                meta
                for meta in pkg_files
                if str(meta.get("relativePath") or "").replace("\\", "/").endswith("NOTICE")
            ]
            if notice_files and not any(_lookup_scan_row(_norm_scan_path(meta.get("relativePath")), by_path) for meta in notice_files):
                ok = False
                detail["reason"] = "missing required NOTICE file in ScanCode output"
        detail["ok"] = ok
        evaluations.append(detail)
        if not ok:
            mismatches.append(detail)
    return {"ok": not mismatches, "evaluations": evaluations, "mismatches": mismatches}


def _norm_scan_path(value: str | None) -> str:
    return str(value or "").replace("\\", "/").lstrip("./")


def _lookup_scan_row(rel: str, by_path: dict[str, Any]) -> dict[str, Any] | None:
    candidates = [rel]
    if rel.startswith("licenses/"):
        candidates.append(rel[len("licenses/") :])
    candidates.append(rel.split("/")[-1])
    for key in list(by_path):
        if key in candidates or key.endswith("/" + rel.split("/")[-1]) or any(key.endswith(item) for item in candidates):
            return by_path[key]
    return None


def _license_id(detection: dict[str, Any]) -> str:
    if not isinstance(detection, dict):
        return ""
    raw = detection.get("license_expression_spdx") or detection.get("license_expression") or detection.get("key") or ""
    return str(raw).replace("apache-2.0", "Apache-2.0").replace("mit", "MIT") if raw else ""


def _signal_present(required: str, detected: set[str]) -> bool:
    required_norm = required.lower()
    for item in detected:
        blob = item.lower()
        if required_norm in blob:
            return True
        if required == "MIT" and "mit" in blob:
            return True
        if required == "Apache-2.0" and "apache-2.0" in blob:
            return True
        if required == "BSD-3-Clause" and "bsd-3-clause" in blob:
            return True
    return False


def _notice_file_present(pkg_files: list[dict[str, Any]], rows: list[dict[str, Any] | None]) -> bool:
    for meta in pkg_files:
        rel = str(meta.get("relativePath") or "").replace("\\", "/")
        if rel.endswith("/NOTICE") or rel.endswith("NOTICE"):
            return True
    return any(bool(row and row.get("copyrights")) for row in rows)


def _csv_has_header(path: Path) -> bool:
    text = path.read_text(encoding="utf-8")
    reader = csv.DictReader(StringIO(text))
    return reader.fieldnames is not None and any(reader.fieldnames)


def _find_csv(root: Path, stem: str) -> Path | None:
    matches = list(root.rglob(f"*{stem}*.csv"))
    return matches[0] if matches else None


def _parse_pairs_csv(path: Path) -> list[dict[str, str]]:
    from .paths import ensure_inside

    ensure_inside(path, ARTIFACT_ROOT)
    text = path.read_text(encoding="utf-8")
    reader = csv.DictReader(StringIO(text))
    if reader.fieldnames is None:
        return []
    rows = []
    for row in reader:
        rows.append({key: (value or "") for key, value in row.items()})
    return rows
