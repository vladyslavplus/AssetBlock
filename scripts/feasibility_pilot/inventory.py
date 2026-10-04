"""Fixture inventory, hashes, and training allowlist."""

from __future__ import annotations

import hashlib
import json
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Iterable

from .paths import FIXTURE_ROOT, PathEscapeError, ensure_inside

MAX_CODE_FILES = 64
MAX_FILE_BYTES = 64 * 1024
MAX_CODE_TOTAL_BYTES = 2 * 1024 * 1024
MAX_LICENSE_FILES = 64
MAX_LICENSE_TOTAL_BYTES = 2 * 1024 * 1024


@dataclass(frozen=True)
class FixtureFile:
    fixture_id: str
    relative_path: str
    language: str
    dialect: str
    sha256: str
    origin: str
    rights: str
    source_family: str
    variant: str
    expected_outcome: str
    expected_fragment_ids: list[str]
    expected_diagnostics: list[dict[str, Any]]
    expected_omissions: list[dict[str, Any]]
    required_case: bool
    diagnostic_only: bool
    training_allowed: bool
    vendor_or_generated: bool
    newline: str
    notes: str
    path: Path
    size_bytes: int


@dataclass(frozen=True)
class PairCase:
    pair_id: str
    language: str
    left_id: str
    right_id: str
    label: str
    diagnostic_only: bool
    training_allowed: bool
    source_family: str
    notes: str


def sha256_bytes(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def sha256_file(path: Path) -> str:
    return sha256_bytes(path.read_bytes())


def load_manifest(path: Path | None = None) -> dict[str, Any]:
    manifest_path = path or (FIXTURE_ROOT / "manifest.json")
    ensure_inside(manifest_path, FIXTURE_ROOT)
    return json.loads(manifest_path.read_text(encoding="utf-8"))


def load_pairs(path: Path | None = None) -> list[PairCase]:
    pairs_path = path or (FIXTURE_ROOT / "pairs.json")
    ensure_inside(pairs_path, FIXTURE_ROOT)
    raw = json.loads(pairs_path.read_text(encoding="utf-8"))
    return [
        PairCase(
            pair_id=item["pairId"],
            language=item["language"],
            left_id=item["leftId"],
            right_id=item["rightId"],
            label=item["label"],
            diagnostic_only=bool(item["diagnosticOnly"]),
            training_allowed=bool(item["trainingAllowed"]),
            source_family=item["sourceFamily"],
            notes=item.get("notes", ""),
        )
        for item in raw["pairs"]
    ]


def load_code_fixtures(manifest: dict[str, Any] | None = None) -> list[FixtureFile]:
    data = manifest or load_manifest()
    files = []
    total = 0
    for item in data["code"]:
        rel = item["relativePath"].replace("\\", "/")
        path = ensure_inside(FIXTURE_ROOT / rel, FIXTURE_ROOT)
        raw = path.read_bytes()
        digest = sha256_bytes(raw)
        if digest != item["sha256"]:
            raise PathEscapeError(f"hash mismatch for {rel}")
        if len(raw) > MAX_FILE_BYTES:
            raise PathEscapeError(f"{rel} exceeds 64 KiB")
        total += len(raw)
        files.append(
            FixtureFile(
                fixture_id=item["id"],
                relative_path=rel,
                language=item["language"],
                dialect=item["dialect"],
                sha256=digest,
                origin=item["origin"],
                rights=item["rights"],
                source_family=item["sourceFamily"],
                variant=item["variant"],
                expected_outcome=item["expectedOutcome"],
                expected_fragment_ids=list(item.get("expectedFragmentIds", [])),
                expected_diagnostics=list(item.get("expectedDiagnostics", [])),
                expected_omissions=list(item.get("expectedOmissions", [])),
                required_case=bool(item["requiredCase"]),
                diagnostic_only=bool(item["diagnosticOnly"]),
                training_allowed=bool(item["trainingAllowed"]),
                vendor_or_generated=bool(item.get("vendorOrGenerated", False)),
                newline=item.get("newline", "lf"),
                notes=item.get("notes", ""),
                path=path,
                size_bytes=len(raw),
            )
        )
    if len(files) > MAX_CODE_FILES:
        raise PathEscapeError("code fixture count exceeds cap")
    if total > MAX_CODE_TOTAL_BYTES:
        raise PathEscapeError("code fixture bytes exceed 2 MiB")
    return files


def load_license_fixtures(manifest: dict[str, Any] | None = None) -> list[dict[str, Any]]:
    data = manifest or load_manifest()
    total = 0
    count = 0
    packages = []
    for package in data["licenses"]:
        files = []
        for item in package["files"]:
            rel = item["relativePath"].replace("\\", "/")
            path = ensure_inside(FIXTURE_ROOT / rel, FIXTURE_ROOT)
            raw = path.read_bytes()
            if sha256_bytes(raw) != item["sha256"]:
                raise PathEscapeError(f"hash mismatch for {rel}")
            total += len(raw)
            count += 1
            files.append({**item, "path": str(path), "sizeBytes": len(raw)})
        packages.append({**package, "resolvedFiles": files})
    if count > MAX_LICENSE_FILES or total > MAX_LICENSE_TOTAL_BYTES:
        raise PathEscapeError("license fixture cap exceeded")
    return packages


def successful_extracted_ids(cases: Iterable[dict[str, Any]]) -> set[str]:
    ok: set[str] = set()
    for item in cases:
        if item.get("ActualOutcome") != "VALID_EXTRACTED":
            continue
        if item.get("ExpectationMatched") is not True:
            continue
        if item.get("operationalFailure"):
            continue
        fixture_id = item.get("fixtureId")
        if fixture_id:
            ok.add(str(fixture_id))
    return ok


def extraction_binding_error(root: Path, files: Iterable[FixtureFile]) -> str | None:
    cases_path = root / "extraction-cases.json"
    inventory_path = root / "inventory.json"
    if not cases_path.exists() or not inventory_path.exists():
        return "extraction report is missing"
    try:
        cases = json.loads(cases_path.read_text(encoding="utf-8"))
        inventory = json.loads(inventory_path.read_text(encoding="utf-8"))
    except json.JSONDecodeError:
        return "extraction report is malformed"
    if not isinstance(cases, list) or not isinstance(inventory, list):
        return "extraction report is malformed"
    current = {item.fixture_id: item.sha256 for item in files}
    recorded = {item.get("id"): item.get("sha256") for item in inventory if isinstance(item, dict)}
    if recorded != current:
        return "extraction report is stale versus current fixture hashes"
    case_ids = {item.get("fixtureId") for item in cases if isinstance(item, dict)}
    if set(current) - case_ids:
        return "extraction report is missing fixtures from the current inventory"
    for item in cases:
        if not isinstance(item, dict):
            return "extraction report is malformed"
        fid = item.get("fixtureId")
        recorded_hash = item.get("sourceSha256")
        if recorded_hash and fid in current and recorded_hash != current[fid]:
            return "extraction report is stale versus current fixture hashes"
    return None


def build_retrieval_triplets(
    pairs: Iterable[PairCase],
    texts: dict[str, str],
    allowed: set[str],
    files: Iterable[FixtureFile] | None = None,
) -> list[dict[str, Any]]:
    available = {fid for fid in allowed if fid in texts}
    hashes = {item.fixture_id: item.sha256 for item in files or []}
    reviewed: list[PairCase] = [
        pair for pair in pairs if pair.training_allowed and not pair.diagnostic_only and pair.label in {"positive", "negative"}
    ]
    triplets: list[dict[str, Any]] = []
    languages: list[str] = []
    for pair in reviewed:
        if pair.language not in languages:
            languages.append(pair.language)
    for language in languages:
        lang_pairs = [pair for pair in reviewed if pair.language == language]
        positives = [
            pair
            for pair in lang_pairs
            if pair.label == "positive" and pair.left_id in available and pair.right_id in available and pair.left_id != pair.right_id
        ]
        chosen: dict[str, Any] | None = None
        for pos in positives:
            query_id = pos.left_id
            negatives = [
                pair
                for pair in lang_pairs
                if pair.label == "negative"
                and pair.left_id == query_id
                and pair.right_id in available
                and pair.right_id not in {query_id, pos.right_id}
            ]
            unique_rights: list[PairCase] = []
            seen: set[str] = set()
            for pair in negatives:
                if pair.right_id in seen:
                    continue
                seen.add(pair.right_id)
                unique_rights.append(pair)
                if len(unique_rights) == 2:
                    break
            if len(unique_rights) < 2:
                continue
            n1, n2 = unique_rights[0], unique_rights[1]
            chosen = {
                "language": language,
                "query": texts[query_id],
                "positive": texts[pos.right_id],
                "negativeA": texts[n1.right_id],
                "negativeB": texts[n2.right_id],
                "queryId": query_id,
                "positiveId": pos.right_id,
                "negativeAId": n1.right_id,
                "negativeBId": n2.right_id,
                "positivePairId": pos.pair_id,
                "negativeAPairId": n1.pair_id,
                "negativeBPairId": n2.pair_id,
                "querySha256": hashes.get(query_id),
                "positiveSha256": hashes.get(pos.right_id),
                "negativeASha256": hashes.get(n1.right_id),
                "negativeBSha256": hashes.get(n2.right_id),
                "negativeALabel": n1.notes or "negative",
                "negativeBLabel": n2.notes or "negative",
            }
            break
        if chosen:
            triplets.append(chosen)
    return triplets


def training_allowlist(
    files: Iterable[FixtureFile],
    pairs: Iterable[PairCase],
    extracted_ok: set[str] | None = None,
) -> set[str]:
    allowed_files = {item.fixture_id for item in files if item.training_allowed and not item.diagnostic_only}
    if extracted_ok is not None:
        allowed_files &= extracted_ok
    allowed: set[str] = set()
    for pair in pairs:
        if not pair.training_allowed or pair.diagnostic_only:
            continue
        if pair.left_id in allowed_files and pair.right_id in allowed_files:
            allowed.add(pair.left_id)
            allowed.add(pair.right_id)
    return allowed


def byte_to_line_col(text: str, byte_offset: int) -> tuple[int, int]:
    encoded = text.encode("utf-8")
    if byte_offset < 0 or byte_offset > len(encoded):
        raise ValueError("byte offset out of range")
    prefix = encoded[:byte_offset].decode("utf-8")
    lines = prefix.splitlines(keepends=True)
    if not lines:
        return 1, 1
    if prefix.endswith(("\n", "\r")):
        return len(lines) + 1, 1
    line_text = lines[-1]
    return len(lines), len(line_text) + 1
