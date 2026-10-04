"""Evaluate the isolated ScanCode prefix against root license policy.

pnpm deps:check is not evidence for this pip graph.
"""

from __future__ import annotations

import hashlib
import json
import re
import subprocess
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

from .paths import ARTIFACT_ROOT, CONFIG_ROOT, REPO_ROOT, env_dir

OBSERVE_IDENTITY = """
import json
import platform
import sys
print(json.dumps({
    "pythonExecutable": sys.executable,
    "pythonVersion": sys.version,
    "platform": platform.platform(),
}))
"""

WHEEL_RE = re.compile(
    r"^(?P<name>.+?) @ file://.+/(?P<file>[^/\s]+)\.whl#sha256=(?P<sha>[a-fA-F0-9]+)$"
)
EQ_RE = re.compile(r"^(?P<name>[^=\s]+)==(?P<version>\S+)$")

ALIASES = {
    "mit": "MIT",
    "mit license": "MIT",
    "mit-0": "MIT-0",
    "apache-2.0": "Apache-2.0",
    "apache-2": "Apache-2.0",
    "apache 2.0": "Apache-2.0",
    "bsd": "BSD-3-Clause",
    "bsd-2-clause": "BSD-2-Clause",
    "bsd-3-clause": "BSD-3-Clause",
    "bsd-simplified": "BSD-2-Clause",
    "bsd-new": "BSD-3-Clause",
    "bsd-original": "BSD-4-Clause",
    "0bsd": "0BSD",
    "isc": "ISC",
    "cc-by-4.0": "CC-BY-4.0",
    "mpl-2.0": "MPL-2.0",
    "psf-2.0": "PSF-2.0",
    "python": "Python-2.0",
    "python-2.0": "Python-2.0",
    "gpl-1.0-plus": "GPL-1.0-or-later",
    "gpl-1.0-or-later": "GPL-1.0-or-later",
    "gpl-3.0-or-later": "GPL-3.0-or-later",
    "lgpl-2.1": "LGPL-2.1-only",
    "lgpl-3.0-or-later": "LGPL-3.0-or-later",
    "artistic license": "Artistic-1.0",
    "public-domain": "LicenseRef-public-domain",
    "other-copyleft": "LicenseRef-other-copyleft",
    "other-permissive": "LicenseRef-other-permissive",
    "bsd-simplified-darwin": "LicenseRef-bsd-simplified-darwin",
}


def parse_pip_lock(text: str) -> list[dict[str, str]]:
    rows: list[dict[str, str]] = []
    for raw in text.splitlines():
        line = raw.strip()
        if not line or line.startswith("#"):
            continue
        match = WHEEL_RE.match(line)
        if match:
            filename = match.group("file") + ".whl"
            name, version = _name_version_from_wheel(filename)
            rows.append(
                {
                    "name": _canonical(match.group("name")),
                    "version": version or _canonical(match.group("name")),
                    "sha256": match.group("sha").lower(),
                    "filename": filename,
                    "lockName": match.group("name"),
                }
            )
            if name:
                rows[-1]["wheelName"] = name
            continue
        match = EQ_RE.match(line)
        if match:
            rows.append(
                {
                    "name": _canonical(match.group("name")),
                    "version": match.group("version"),
                    "sha256": "",
                    "filename": "",
                    "lockName": match.group("name"),
                }
            )
    return rows


def _canonical(name: str) -> str:
    return re.sub(r"[-_.]+", "-", name).lower()


def _name_version_from_wheel(filename: str) -> tuple[str, str]:
    stem = filename[:-4] if filename.endswith(".whl") else filename
    parts = stem.split("-")
    # Find first part that looks like a version (starts with a digit).
    for index, part in enumerate(parts):
        if part and part[0].isdigit():
            name = _canonical("-".join(parts[:index]))
            version = part
            return name, version
    return _canonical(stem), ""


def normalize_token(raw: str | None) -> str | None:
    if not raw:
        return None
    value = str(raw).strip()
    if not value:
        return None
    aliased = ALIASES.get(value.lower())
    return aliased or value


def tokenize_expression(expression: str) -> list[str]:
    source = re.sub(r"\s+", " ", expression.replace("/", " OR ")).strip()
    return re.findall(r"\(|\)|OR|AND|[^\s()]+", source, flags=re.IGNORECASE)


def parse_license_expression(raw: str | None) -> dict[str, Any] | None:
    if not raw or not str(raw).strip():
        return None
    text = str(raw).strip()
    if not re.search(r"[\s()/]|OR|AND", text, flags=re.IGNORECASE):
        token = normalize_token(text)
        return {"type": "license", "value": token} if token else None
    tokens = tokenize_expression(text)
    index = 0

    def peek() -> str | None:
        return tokens[index] if index < len(tokens) else None

    def consume(expected: str | None = None) -> str:
        nonlocal index
        token = tokens[index]
        index += 1
        if expected and token.upper() != expected.upper():
            raise ValueError(f"expected {expected}")
        return token

    def parse_primary() -> dict[str, Any]:
        token = peek()
        if token is None:
            raise ValueError("end of expression")
        if token == "(":
            consume("(")
            node = parse_or()
            consume(")")
            return node
        consume()
        normalized = normalize_token(token)
        if not normalized:
            raise ValueError(f"unknown token {token}")
        return {"type": "license", "value": normalized}

    def parse_and() -> dict[str, Any]:
        left = parse_primary()
        while peek() and peek().upper() == "AND":
            consume("AND")
            left = {"type": "and", "left": left, "right": parse_primary()}
        return left

    def parse_or() -> dict[str, Any]:
        left = parse_and()
        while peek() and peek().upper() == "OR":
            consume("OR")
            left = {"type": "or", "left": left, "right": parse_and()}
        return left

    try:
        ast = parse_or()
    except ValueError:
        token = normalize_token(text)
        return {"type": "license", "value": token} if token else None
    if index != len(tokens):
        token = normalize_token(text)
        return {"type": "license", "value": token} if token else None
    return ast


def is_license_allowed(raw: str | None, allowed: set[str], extra: list[str] | None = None) -> bool:
    ast = parse_license_expression(raw)
    if ast is None:
        return False
    leaves = set(allowed)
    leaves.update(extra or [])

    def eval_ast(node: dict[str, Any]) -> bool:
        kind = node["type"]
        if kind == "license":
            return node["value"] in leaves
        if kind == "or":
            return eval_ast(node["left"]) or eval_ast(node["right"])
        if kind == "and":
            return eval_ast(node["left"]) and eval_ast(node["right"])
        return False

    return eval_ast(ast)


def find_exception(exceptions: list[dict[str, Any]], name: str, version: str) -> dict[str, Any] | None:
    canonical = _canonical(name)
    for entry in exceptions:
        if entry.get("ecosystem") != "pypi":
            continue
        if _canonical(str(entry.get("name") or "")) != canonical:
            continue
        versions = entry.get("versions") or []
        if version in versions:
            return entry
    return None


def exception_is_reviewed(entry: dict[str, Any] | None) -> bool:
    if not entry:
        return False
    reviewed = entry.get("reviewedOn")
    return isinstance(reviewed, str) and bool(re.fullmatch(r"\d{4}-\d{2}-\d{2}", reviewed))


def evaluate_packages(
    packages: list[dict[str, Any]],
    *,
    allowed_licenses: list[str],
    exceptions: list[dict[str, Any]],
) -> dict[str, Any]:
    allowed = set(allowed_licenses)
    covered: list[dict[str, Any]] = []
    gaps: list[dict[str, Any]] = []
    for pkg in packages:
        name = pkg["name"]
        version = pkg["version"]
        license_expr = pkg.get("license")
        exception = find_exception(exceptions, name, version)
        extra: list[str] = []
        if exception_is_reviewed(exception) and exception.get("license"):
            ast = parse_license_expression(exception["license"])
            extra = _collect_ids(ast)
        ok = is_license_allowed(license_expr, allowed, extra)
        row = {
            "name": name,
            "version": version,
            "license": license_expr,
            "lockSha256": pkg.get("lockSha256") or pkg.get("sha256"),
            "installedFileHashMeasured": bool(pkg.get("installedFileHashMeasured")),
            "exceptionName": (exception or {}).get("name"),
            "exceptionVersionMatched": bool(exception) and version in (exception.get("versions") or []),
            "exceptionReviewed": exception_is_reviewed(exception),
        }
        if not license_expr:
            gaps.append(row | {"reason": "missing or unknown license metadata"})
        elif ok:
            covered.append(row)
        else:
            if exception and not exception_is_reviewed(exception):
                reason = "matching exception exists but is not reviewed"
            elif exception and version not in (exception.get("versions") or []):
                reason = "exception version does not match installed version"
            elif not exception:
                reason = "license not on default allowlist and no reviewed exact-version pypi exception"
            else:
                reason = "reviewed exception does not cover the distributed license expression"
            gaps.append(row | {"reason": reason})
    return {
        "approved": len(gaps) == 0 and len(packages) > 0,
        "packageCount": len(packages),
        "coveredCount": len(covered),
        "gaps": gaps,
        "covered": covered,
        "pnpmDepsCheckIsNotEvidence": True,
    }


def _collect_ids(ast: dict[str, Any] | None) -> list[str]:
    if not ast:
        return []
    if ast["type"] == "license":
        return [ast["value"]]
    return _collect_ids(ast.get("left")) + _collect_ids(ast.get("right"))


def load_root_policy() -> dict[str, Any]:
    return json.loads((REPO_ROOT / "dependency-policy.json").read_text(encoding="utf-8"))


def load_root_exceptions() -> list[dict[str, Any]]:
    payload = json.loads((REPO_ROOT / "dependency-exceptions.json").read_text(encoding="utf-8"))
    return payload.get("exceptions") or []


def load_license_inventory(path: Path | None = None) -> dict[str, dict[str, Any]]:
    inventory_path = path or (CONFIG_ROOT / "scancode-license-inventory.json")
    payload = json.loads(inventory_path.read_text(encoding="utf-8"))
    by_key: dict[str, dict[str, Any]] = {}
    for item in payload.get("packages") or []:
        key = f"{_canonical(item['name'])}=={item['version']}"
        by_key[key] = item
    return by_key


def _utc_now() -> str:
    return datetime.now(timezone.utc).replace(microsecond=0).isoformat()


def _file_sha256(path: Path) -> str | None:
    if not path.exists() or not path.is_file():
        return None
    return hashlib.sha256(path.read_bytes()).hexdigest()


def scanner_interpreter(prefix: Path | None = None) -> Path | None:
    root = prefix if prefix is not None else env_dir("scancode")
    for relative in ("Scripts/python.exe", "bin/python"):
        candidate = root / relative
        if candidate.exists():
            return candidate
    return None


def observe_installed_packages(python: Path) -> dict[str, Any]:
    identity = subprocess.run(
        [str(python), "-c", OBSERVE_IDENTITY],
        capture_output=True,
        text=True,
        check=False,
        shell=False,
        timeout=30,
    )
    freeze = subprocess.run(
        [str(python), "-m", "pip", "freeze"],
        capture_output=True,
        text=True,
        check=False,
        shell=False,
        timeout=60,
    )
    if identity.returncode != 0:
        raise RuntimeError((identity.stderr or identity.stdout or "interpreter identity failed")[-2000:])
    if freeze.returncode != 0:
        raise RuntimeError((freeze.stderr or freeze.stdout or "pip freeze failed")[-2000:])
    payload = json.loads(identity.stdout)
    payload["packages"] = parse_pip_lock(freeze.stdout)
    payload["observedAt"] = _utc_now()
    payload["observationMethod"] = "scanner-venv-python pip freeze (same encoding as the reviewed lock; freeze hashes are not measured installed-file hashes)"
    return payload


def compare_graphs(locked: list[dict[str, Any]], installed: list[dict[str, Any]]) -> list[dict[str, Any]]:
    locked_map: dict[str, str] = {}
    for item in locked:
        locked_map[_canonical(item["name"])] = item["version"]
    installed_map: dict[str, str] = {}
    duplicates: list[dict[str, Any]] = []
    for item in installed:
        key = _canonical(item["name"])
        version = str(item["version"])
        if key in installed_map and installed_map[key] != version:
            duplicates.append(
                {
                    "name": key,
                    "installedVersion": version,
                    "otherInstalledVersion": installed_map[key],
                    "reason": "installed graph reports conflicting versions for one package",
                }
            )
        installed_map[key] = version
    gaps: list[dict[str, Any]] = list(duplicates)
    names = sorted(set(locked_map) | set(installed_map))
    for name in names:
        locked_version = locked_map.get(name)
        installed_version = installed_map.get(name)
        if locked_version is None:
            gaps.append(
                {
                    "name": name,
                    "installedVersion": installed_version,
                    "reason": "installed package is not in the reviewed lock",
                }
            )
        elif installed_version is None:
            gaps.append(
                {
                    "name": name,
                    "lockVersion": locked_version,
                    "reason": "reviewed lock package is missing from the installed scanner graph",
                }
            )
        elif locked_version != installed_version:
            gaps.append(
                {
                    "name": name,
                    "lockVersion": locked_version,
                    "installedVersion": installed_version,
                    "reason": "installed version does not match the reviewed lock",
                }
            )
    return gaps


def evaluate_installed_scanner(
    *,
    lock_path: Path | None = None,
    inventory_path: Path | None = None,
    policy: dict[str, Any] | None = None,
    exceptions: list[dict[str, Any]] | None = None,
    interpreter_path: Path | None = None,
    installed_packages: list[dict[str, Any]] | None = None,
    prefix: Path | None = None,
) -> dict[str, Any]:
    resolved_lock = lock_path or (ARTIFACT_ROOT / "env" / "scancode-lock.txt")
    resolved_inventory = inventory_path or (CONFIG_ROOT / "scancode-license-inventory.json")
    policy_path = REPO_ROOT / "dependency-policy.json"
    exceptions_path = REPO_ROOT / "dependency-exceptions.json"
    evidence_hashes = {
        "lockSha256": _file_sha256(resolved_lock),
        "inventorySha256": _file_sha256(resolved_inventory),
        "policySha256": _file_sha256(policy_path),
        "exceptionsSha256": _file_sha256(exceptions_path),
        "lockSha256IsNotInstalledFileHash": True,
    }
    base = {
        "pnpmDepsCheckIsNotEvidence": True,
        "evidenceHashes": evidence_hashes,
        "lockPath": str(resolved_lock),
        "inventoryPath": str(resolved_inventory),
    }

    python = interpreter_path
    if python is None and installed_packages is None:
        python = scanner_interpreter(prefix)
    if installed_packages is None and (python is None or not Path(python).exists()):
        return base | {
            "approved": False,
            "environmentReady": False,
            "packageCount": 0,
            "coveredCount": 0,
            "gaps": [{"reason": "scanner interpreter is missing"}],
            "environment": {"pythonExecutable": str(python) if python else None, "observedAt": _utc_now()},
        }

    observation: dict[str, Any]
    if installed_packages is not None:
        observation = {
            "pythonExecutable": str(python) if python else "injected-test-graph",
            "pythonVersion": None,
            "platform": None,
            "packages": installed_packages,
            "observedAt": _utc_now(),
            "observationMethod": "injected-test-graph",
        }
    else:
        try:
            observation = observe_installed_packages(Path(python))
        except (OSError, RuntimeError, json.JSONDecodeError, subprocess.TimeoutExpired) as exc:
            return base | {
                "approved": False,
                "environmentReady": False,
                "packageCount": 0,
                "coveredCount": 0,
                "gaps": [{"reason": f"failed to observe installed scanner graph: {exc}"}],
                "environment": {"pythonExecutable": str(python), "observedAt": _utc_now()},
            }

    if not resolved_lock.exists():
        return base | {
            "approved": False,
            "environmentReady": False,
            "packageCount": 0,
            "coveredCount": 0,
            "gaps": [{"reason": "scanner lock file is missing", "path": str(resolved_lock)}],
            "environment": observation,
        }
    try:
        inventory = load_license_inventory(resolved_inventory)
    except FileNotFoundError:
        return base | {
            "approved": False,
            "environmentReady": False,
            "packageCount": 0,
            "coveredCount": 0,
            "gaps": [{"reason": "scanner license inventory is missing"}],
            "environment": observation,
        }

    locked = parse_pip_lock(resolved_lock.read_text(encoding="utf-8-sig"))
    graph_gaps = compare_graphs(locked, observation.get("packages") or [])
    packages: list[dict[str, Any]] = []
    inventory_gaps: list[dict[str, Any]] = []
    for item in locked:
        key = f"{item['name']}=={item['version']}"
        inv = inventory.get(key)
        if inv is None:
            inventory_gaps.append(
                {
                    "name": item["name"],
                    "version": item["version"],
                    "lockSha256": item.get("sha256"),
                    "reason": "reviewed lock package has no license inventory row",
                }
            )
            continue
        if inv.get("sha256") and item.get("sha256") and inv["sha256"].lower() != item["sha256"].lower():
            inventory_gaps.append(
                {
                    "name": item["name"],
                    "version": item["version"],
                    "lockSha256": item.get("sha256"),
                    "inventorySha256": inv["sha256"],
                    "reason": "lock hash does not match inventory hash",
                }
            )
            continue
        packages.append(
            {
                "name": item["name"],
                "version": item["version"],
                "lockSha256": item.get("sha256") or inv.get("sha256"),
                "installedFileHashMeasured": False,
                "license": inv.get("license"),
            }
        )
    result = evaluate_packages(
        packages,
        allowed_licenses=(policy or load_root_policy()).get("allowedLicenses") or [],
        exceptions=exceptions if exceptions is not None else load_root_exceptions(),
    )
    result["gaps"] = graph_gaps + inventory_gaps + result["gaps"]
    result["approved"] = len(result["gaps"]) == 0 and len(packages) > 0
    result["environmentReady"] = result["approved"]
    result["lockPath"] = str(resolved_lock)
    result["inventoryPath"] = str(resolved_inventory)
    result["pnpmDepsCheckIsNotEvidence"] = True
    result["evidenceHashes"] = evidence_hashes
    result["environment"] = {
        "pythonExecutable": observation.get("pythonExecutable"),
        "pythonVersion": observation.get("pythonVersion"),
        "platform": observation.get("platform"),
        "observedAt": observation.get("observedAt"),
        "observationMethod": observation.get("observationMethod"),
        "installedCount": len(observation.get("packages") or []),
        "lockCount": len(locked),
    }
    return result


def build_preflight_dependency_manifest(
    requested: dict[str, Any],
    *,
    approval: dict[str, Any],
    proposed_exception_path: Path | None = None,
) -> dict[str, Any]:
    """Record scanner graph status from the approval gate, not from an executable path."""
    proposed = proposed_exception_path or (CONFIG_ROOT / "proposed-dependency-exceptions.json")
    proposed_status = "NOT_YET_OBSERVED"
    if proposed.exists():
        try:
            payload = json.loads(proposed.read_text(encoding="utf-8"))
        except json.JSONDecodeError:
            payload = {}
        proposed_status = str(payload.get("status") or "NOT_YET_OBSERVED")
    gaps = [item for item in (approval.get("gaps") or []) if isinstance(item, dict)]
    reasons = " ".join(str(item.get("reason") or "") for item in gaps)
    observed = bool(approval.get("environment", {}).get("observationMethod")) or int(approval.get("packageCount") or 0) > 0
    missing = "scanner interpreter is missing" in reasons or "failed to observe installed scanner graph" in reasons
    graph_approved = bool(approval.get("approved") and approval.get("environmentReady"))
    if missing:
        scancode_status = "NOT_YET_OBSERVED"
        scancode_reason = reasons.strip() or "scanner graph has not been observed"
    elif graph_approved:
        scancode_status = "APPROVED_INSTALLED"
        scancode_reason = (
            "Installed scanner graph matches the reviewed lock/inventory and recorded exceptions. "
            "Executable presence is not policy compliance."
        )
    else:
        scancode_status = "BLOCKED"
        scancode_reason = reasons.strip() or "installed scanner graph is unapproved or unknown"
    return {
        "requested": requested,
        "scancodeStatus": scancode_status,
        "scancodeReason": scancode_reason,
        "environmentGraphApproved": graph_approved,
        "environmentReady": bool(approval.get("environmentReady")),
        "packageCount": approval.get("packageCount"),
        "coveredCount": approval.get("coveredCount"),
        "gaps": gaps,
        "observed": observed and not missing,
        "policyCompliant": None,
        "policyComplianceSource": "not-inferred-from-executable",
        "executablePresenceIsNotPolicyCompliance": True,
        "pnpmDepsCheckIsNotEvidence": True,
        "proposedException": str(proposed),
        "proposedExceptionStatus": proposed_status,
        "evidenceHashes": approval.get("evidenceHashes"),
        "lockPath": approval.get("lockPath"),
        "inventoryPath": approval.get("inventoryPath"),
    }
