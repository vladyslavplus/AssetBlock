"""Versioned evidence writers and required-artifact validation."""

from __future__ import annotations

import csv
import hashlib
import json
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

from . import PURPOSE, SCHEMA_VERSION
from .paths import ensure_inside, run_dir

REQUIRED_STAGE_ARTIFACTS = {
    "preflight": ["environment.json", "dependency-manifest.json", "input-manifest.json"],
    "extract": ["inventory.json", "fragments.jsonl", "coverage.json", "extraction-cases.json"],
    "tools": ["token-results.json", "license-results.json", "tool-compatibility.json"],
    "frozen": ["frozen-retrieval.json"],
    "train-retrieval": ["train-retrieval.json"],
    "reload-retrieval": ["reload-retrieval.json"],
    "train-pair": ["train-pair.json"],
    "reload-pair": ["reload-pair.json"],
    "summarize": ["summary.md", "compatibility.csv", "measurements.csv", "decisions.json"],
}


def utc_now() -> str:
    return datetime.now(timezone.utc).replace(microsecond=0).isoformat()


def write_json(path: Path, payload: dict[str, Any] | list[Any], roots: list[Path]) -> str:
    ensure_inside(path, *roots)
    path.parent.mkdir(parents=True, exist_ok=True)
    encoded = json.dumps(payload, indent=2, ensure_ascii=False) + "\n"
    path.write_text(encoded, encoding="utf-8")
    return hashlib.sha256(encoded.encode("utf-8")).hexdigest()


def append_jsonl(path: Path, rows: list[dict[str, Any]], roots: list[Path]) -> None:
    ensure_inside(path, *roots)
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("w", encoding="utf-8") as handle:
        for row in rows:
            handle.write(json.dumps(row, ensure_ascii=False) + "\n")


def write_text(path: Path, text: str, roots: list[Path]) -> str:
    ensure_inside(path, *roots)
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(text, encoding="utf-8")
    return hashlib.sha256(text.encode("utf-8")).hexdigest()


def write_csv(path: Path, rows: list[dict[str, Any]], fieldnames: list[str], roots: list[Path]) -> None:
    ensure_inside(path, *roots)
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("w", encoding="utf-8", newline="") as handle:
        writer = csv.DictWriter(handle, fieldnames=fieldnames)
        writer.writeheader()
        for row in rows:
            writer.writerow(row)


def stage_record(
    stage: str,
    outcome: str,
    execution_state: str,
    *,
    mandatory: bool,
    reason: str,
    fixture_scope: str,
    provenance: dict[str, Any],
    started: str,
    finished: str,
    duration_s: float,
    error: str | None,
    artifacts: list[str],
) -> dict[str, Any]:
    return {
        "schema": SCHEMA_VERSION,
        "stage": stage,
        "outcome": outcome,
        "executionState": execution_state,
        "reason": reason,
        "mandatoryForPilot": mandatory,
        "fixtureScope": fixture_scope,
        "toolOrModelProvenance": provenance,
        "startedAt": started,
        "finishedAt": finished,
        "durationSeconds": duration_s,
        "errorSummary": error,
        "artifactRefs": artifacts,
        "Purpose": PURPOSE,
        "CanAuthorizePublication": False,
        "SecurityEvidence": "NOT_RUN",
    }


def archive_attempt(root: Path, label: str, names: list[str]) -> Path:
    from .paths import ARTIFACT_ROOT

    stamp = utc_now().replace(":", "").replace("+00:00", "Z")
    dest = root / "attempts" / f"{stamp}-{label}"
    dest.mkdir(parents=True, exist_ok=True)
    copied = []
    for name in names:
        src = root / name
        if src.exists() and src.is_file():
            dest.joinpath(name).write_bytes(src.read_bytes())
            copied.append(name)
    write_json(
        dest / "attempt-meta.json",
        {
            "label": label,
            "archivedAt": utc_now(),
            "copied": copied,
            "runId": root.name,
        },
        [ARTIFACT_ROOT],
    )
    return dest


def file_hash(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def hash_tree(root: Path) -> str:
    digest = hashlib.sha256()
    for path in sorted(root.rglob("*")):
        if path.is_file():
            digest.update(path.relative_to(root).as_posix().encode("utf-8"))
            digest.update(path.read_bytes())
    return digest.hexdigest()


def _stage_completed(record: dict[str, Any]) -> bool:
    return record.get("outcome") == "SUCCEEDED" and record.get("executionState") == "COMPLETED"


def validate_run(run_id: str) -> dict[str, Any]:
    return validate_evidence_tree(run_dir(run_id), run_id=run_id)


def evaluate_dependency_gate(root: Path, by_stage: dict[str, Any]) -> dict[str, Any]:
    from .dependency_approval import evaluate_installed_scanner
    from .paths import ARTIFACT_ROOT

    license_path = root / "license-results.json"
    scancode_ran = False
    if license_path.exists():
        payload = json.loads(license_path.read_text(encoding="utf-8"))
        scancode_ran = payload.get("executionState") == "COMPLETED" and payload.get("outcome") == "SUCCEEDED"
    tools = by_stage.get("tools") or {}
    tools_completed = tools.get("outcome") == "SUCCEEDED" and tools.get("executionState") == "COMPLETED"
    required = scancode_ran or tools_completed or (ARTIFACT_ROOT / "env" / "scancode-lock.txt").exists()
    if not required:
        return {"required": False, "approved": True, "gaps": [], "pnpmDepsCheckIsNotEvidence": True}
    env = evaluate_installed_scanner()
    attempt_compliant = False
    historical = False
    if license_path.exists():
        payload = json.loads(license_path.read_text(encoding="utf-8"))
        historical = bool(payload.get("historicalTechnicalEvidence"))
        attempt_compliant = (
            payload.get("policyCompliant") is True
            and payload.get("executionState") == "COMPLETED"
            and payload.get("outcome") == "SUCCEEDED"
        )
    env["required"] = True
    env["environmentReady"] = bool(env.get("approved"))
    env["scannerAttemptPolicyCompliant"] = attempt_compliant
    env["historicalScannerOutput"] = scancode_ran
    env["historicalTechnicalEvidence"] = historical
    env["approved"] = bool(env.get("environmentReady") and attempt_compliant)
    env["policyCompliantCompletion"] = env["approved"]
    if env["environmentReady"] and not attempt_compliant:
        env["gaps"] = list(env.get("gaps") or []) + [
            {
                "reason": "scanner attempt is not marked policy-compliant; historical output cannot complete the pilot",
            }
        ]
    return env


def validate_evidence_tree(root: Path, *, run_id: str) -> dict[str, Any]:
    missing: list[str] = []
    present: list[str] = []
    evidence_gaps: list[dict[str, Any]] = []
    stages_path = root / "stages.json"
    try:
        stages = json.loads(stages_path.read_text(encoding="utf-8")) if stages_path.exists() else []
    except json.JSONDecodeError:
        stages = []
        evidence_gaps.append({"stage": "stages.json", "reason": "malformed stages.json"})
    by_stage = {item["stage"]: item for item in stages if isinstance(item, dict) and item.get("stage")}
    for stage, names in REQUIRED_STAGE_ARTIFACTS.items():
        record = by_stage.get(stage)
        if record is None:
            evidence_gaps.append({"stage": stage, "reason": "mandatory stage record is missing"})
        for name in names:
            path = root / name
            if path.exists():
                present.append(name)
            else:
                missing.append(name)
                evidence_gaps.append({"stage": stage, "reason": f"required artifact missing: {name}"})
        if record is not None:
            evidence_gaps.extend(_inspect_stage(root, stage, record))
    mandatory_gaps = [
        item
        for item in stages
        if isinstance(item, dict)
        and item.get("mandatoryForPilot")
        and (
            item.get("executionState") == "SKIPPED_DEPENDENCY"
            or item.get("outcome") in {"FAILED", "NOT_SUPPORTED"}
        )
    ]
    for gap in evidence_gaps:
        mandatory_gaps.append(
            {
                "stage": gap.get("stage"),
                "outcome": "FAILED",
                "executionState": "SKIPPED_DEPENDENCY" if "missing" in str(gap.get("reason")) else "COMPLETED",
                "reason": gap.get("reason"),
                "mandatoryForPilot": True,
            }
        )
    status = "READY_FOR_REVIEW"
    if mandatory_gaps or missing or evidence_gaps:
        status = "BLOCKED"
    approval = evaluate_dependency_gate(root, by_stage)
    if approval.get("required") and not approval.get("approved"):
        status = "BLOCKED"
        env_ready = bool(approval.get("environmentReady"))
        attempt_ok = bool(approval.get("scannerAttemptPolicyCompliant"))
        if not env_ready:
            reason = "current scanner environment graph is unapproved, unknown, or does not match the reviewed lock"
        elif not attempt_ok:
            reason = "current environment is ready but the scanner attempt is not policy-compliant historical output cannot complete the pilot"
        else:
            reason = "installed scanner graph has unapproved or unknown license terms"
        mandatory_gaps.append(
            {
                "stage": "dependency-approval",
                "outcome": "FAILED",
                "executionState": "SKIPPED_DEPENDENCY",
                "reason": reason,
                "mandatoryForPilot": True,
                "gaps": approval.get("gaps") or [],
                "environmentReady": env_ready,
                "scannerAttemptPolicyCompliant": attempt_ok,
                "pnpmDepsCheckIsNotEvidence": True,
            }
        )
    return {
        "schema": SCHEMA_VERSION,
        "runId": run_id,
        "status": status,
        "presentArtifacts": present,
        "missingOrIncomplete": missing,
        "mandatoryGaps": mandatory_gaps,
        "evidenceGaps": evidence_gaps,
        "dependencyApproval": approval,
        "Purpose": PURPOSE,
        "CanAuthorizePublication": False,
        "SecurityEvidence": "NOT_RUN",
    }


def _read_json(path: Path) -> tuple[Any | None, str | None]:
    if not path.exists():
        return None, "missing"
    try:
        return json.loads(path.read_text(encoding="utf-8")), None
    except json.JSONDecodeError:
        return None, "malformed"


def _inspect_stage(root: Path, stage: str, record: dict[str, Any]) -> list[dict[str, Any]]:
    from .inventory import load_code_fixtures, load_license_fixtures

    gaps: list[dict[str, Any]] = []
    if not _stage_completed(record):
        gaps.append({"stage": stage, "reason": f"mandatory stage {stage} is not SUCCEEDED/COMPLETED"})
    if stage == "extract":
        payload, err = _read_json(root / "extraction-cases.json")
        if err:
            gaps.append({"stage": stage, "reason": f"extraction-cases.json is {err}"})
        elif not isinstance(payload, list):
            gaps.append({"stage": stage, "reason": "extraction-cases.json is not a case list"})
        else:
            expected = [item.fixture_id for item in load_code_fixtures() if item.required_case]
            by_id = {item.get("fixtureId"): item for item in payload if isinstance(item, dict)}
            missing = [fid for fid in expected if fid not in by_id]
            if not payload:
                gaps.append({"stage": stage, "reason": "extraction-cases.json is empty"})
            if missing:
                gaps.append({"stage": stage, "reason": f"extraction-cases.json is missing required fixtures: {', '.join(missing)}"})
            required_rows = [by_id[fid] for fid in expected if fid in by_id]
            if any(item.get("ExpectationMatched") is not True for item in required_rows):
                gaps.append({"stage": stage, "reason": "required extraction case results are not matched"})
            if record.get("outcome") == "SUCCEEDED" and any(item.get("ExpectationMatched") is not True for item in required_rows):
                gaps.append({"stage": stage, "reason": "extract stage SUCCEEDED contradicts required-case failures"})
    elif stage == "tools":
        tokens, err = _read_json(root / "token-results.json")
        if err:
            gaps.append({"stage": stage, "reason": f"token-results.json is {err}"})
        elif not isinstance(tokens, list):
            gaps.append({"stage": stage, "reason": "token-results.json is not a language-batch list"})
        else:
            expected_langs: list[str] = []
            for item in load_code_fixtures():
                if item.expected_outcome != "VALID_EXTRACTED" or item.diagnostic_only:
                    continue
                key = item.dialect if item.dialect == "tsx" else item.language
                if key not in expected_langs:
                    expected_langs.append(key)
            by_lang = {item.get("language"): item for item in tokens if isinstance(item, dict) and not item.get("diagnostic")}
            if not tokens:
                gaps.append({"stage": stage, "reason": "token-results.json is empty"})
            missing_langs = [lang for lang in expected_langs if lang not in by_lang]
            if missing_langs:
                gaps.append({"stage": stage, "reason": f"token-results.json is missing required language batches: {', '.join(missing_langs)}"})
            for item in tokens:
                if not isinstance(item, dict):
                    gaps.append({"stage": stage, "reason": "token-results.json contains a non-object batch"})
                    continue
                if item.get("outcome") != "SUCCEEDED":
                    continue
                if item.get("executionState") != "COMPLETED":
                    gaps.append({"stage": stage, "reason": "Dolos batch SUCCEEDED without COMPLETED executionState"})
                raw = item.get("rawCsv")
                if not raw or not Path(raw).exists():
                    gaps.append({"stage": stage, "reason": "Dolos CSV is missing for a SUCCEEDED batch"})
                elif item.get("csvSha256") and file_hash(Path(raw)) != item["csvSha256"]:
                    gaps.append({"stage": stage, "reason": "Dolos CSV hash does not match the recorded binding"})
                if not item.get("inputBinding"):
                    gaps.append({"stage": stage, "reason": "Dolos batch is missing input/config binding"})
        licenses, err = _read_json(root / "license-results.json")
        if err:
            gaps.append({"stage": stage, "reason": f"license-results.json is {err}"})
        elif not isinstance(licenses, dict):
            gaps.append({"stage": stage, "reason": "license-results.json is not an object payload"})
        else:
            if licenses.get("outcome") != "SUCCEEDED" or licenses.get("executionState") != "COMPLETED":
                gaps.append({"stage": stage, "reason": "FAILED ScanCode payload" if licenses.get("outcome") == "FAILED" else "ScanCode payload is missing SUCCEEDED/COMPLETED"})
            evaluation = licenses.get("signalEvaluation")
            if not isinstance(evaluation, dict) or evaluation.get("ok") is not True:
                gaps.append({"stage": stage, "reason": "ScanCode SUCCEEDED without required fixture signal evaluation"})
            expected_pkgs = [pkg["id"] for pkg in load_license_fixtures()]
            seen = {item.get("id") for item in (evaluation or {}).get("evaluations") or [] if isinstance(item, dict)}
            missing_pkgs = [pkg_id for pkg_id in expected_pkgs if pkg_id not in seen]
            if missing_pkgs:
                gaps.append({"stage": stage, "reason": f"ScanCode evaluation is missing required license fixtures: {', '.join(missing_pkgs)}"})
            if licenses.get("filesWithScanErrors"):
                gaps.append({"stage": stage, "reason": "ScanCode report contains per-file errors"})
    elif stage == "frozen":
        payload, err = _read_json(root / "frozen-retrieval.json")
        if err:
            gaps.append({"stage": stage, "reason": f"frozen-retrieval.json is {err}"})
        elif not isinstance(payload, dict):
            gaps.append({"stage": stage, "reason": "frozen-retrieval.json is not an object payload"})
        elif not payload.get("finite") or not payload.get("frozenInvariant"):
            gaps.append({"stage": stage, "reason": "frozen SUCCEEDED contradicts payload"})
    elif stage in {"train-retrieval", "train-pair"}:
        name = "train-retrieval.json" if stage == "train-retrieval" else "train-pair.json"
        payload, err = _read_json(root / name)
        if err:
            gaps.append({"stage": stage, "reason": f"{name} is {err}"})
        elif not isinstance(payload, dict) or not payload:
            gaps.append({"stage": stage, "reason": f"{name} is not a complete object payload"})
        else:
            if "outcome" not in payload:
                gaps.append({"stage": stage, "reason": f"{name} is missing outcome"})
            if payload.get("outcome") != "SUCCEEDED":
                gaps.append({"stage": stage, "reason": f"FAILED training payload in {name}" if payload.get("outcome") == "FAILED" else f"{name} is missing SUCCEEDED outcome"})
            gpu = payload.get("gpu") or {}
            if int(payload.get("updates") or 0) < 5:
                gaps.append({"stage": stage, "reason": "SUCCEEDED training did not record five optimizer updates"})
            stepped = [
                item
                for item in (payload.get("successfulAttempts") or [])
                if isinstance(item, dict) and item.get("optimizerStepped") is True and item.get("skipped") is not True
            ]
            if len(stepped) < 5:
                gaps.append({"stage": stage, "reason": "training evidence is missing five successful optimizer steps"})
            if payload.get("encoderChanged") is not True:
                gaps.append({"stage": stage, "reason": "training evidence encoderChanged is not true"})
            if payload.get("frozenInvariant") is not True:
                gaps.append({"stage": stage, "reason": "training evidence frozenInvariant is not true"})
            if stage == "train-pair" and payload.get("headChanged") is not True:
                gaps.append({"stage": stage, "reason": "pair training evidence headChanged is not true"})
            expected_arch = "retrieval-encoder" if stage == "train-retrieval" else "joint-input-pair-verifier"
            if payload.get("architecture") != expected_arch:
                gaps.append({"stage": stage, "reason": f"{name} architecture does not match {expected_arch}"})
            training_gpu = gpu.get("training") if isinstance(gpu, dict) else None
            if not isinstance(training_gpu, dict) or not training_gpu.get("isTrainingPeak"):
                gaps.append({"stage": stage, "reason": "training peak measurement is missing or is still a probe"})
            ckpt = payload.get("checkpointDir")
            ckpt_path = Path(ckpt) if ckpt else None
            if not ckpt_path or not ckpt_path.exists():
                gaps.append({"stage": stage, "reason": "training checkpoint directory is missing"})
            else:
                actual_hash = hash_tree(ckpt_path)
                recorded = payload.get("checkpointHash")
                if not recorded:
                    gaps.append({"stage": stage, "reason": "training evidence is missing checkpointHash"})
                elif recorded != actual_hash:
                    gaps.append({"stage": stage, "reason": "training checkpoint hash does not match the stored tree"})
    elif stage in {"reload-retrieval", "reload-pair"}:
        name = "reload-retrieval.json" if stage == "reload-retrieval" else "reload-pair.json"
        payload, err = _read_json(root / name)
        if err:
            gaps.append({"stage": stage, "reason": f"{name} is {err}"})
        elif not isinstance(payload, dict):
            gaps.append({"stage": stage, "reason": f"{name} is not an object payload"})
        else:
            if payload.get("fallbackUsed"):
                gaps.append({"stage": stage, "reason": "reload used a dummy fallback input"})
            if not (payload.get("matched") and payload.get("finite")):
                gaps.append({"stage": stage, "reason": "reload SUCCEEDED contradicts payload"})
            if payload.get("reason") in {"missing query/reference", "dimension mismatch"}:
                gaps.append({"stage": stage, "reason": f"reload {payload.get('reason')}"})
    elif stage == "summarize":
        payload, err = _read_json(root / "decisions.json")
        if err:
            gaps.append({"stage": stage, "reason": f"decisions.json is {err}"})
        elif not isinstance(payload, dict):
            gaps.append({"stage": stage, "reason": "decisions.json is not an object payload"})
    return gaps
