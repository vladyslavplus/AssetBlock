"""Versioned request/report contracts for the isolated code analysis service.

Stdlib only. The service never approves, publishes, or admits reports: it
produces a real analysis envelope that only product admission (outside this
service) may select. Unknown metadata fields are rejected fail-closed.
"""

from __future__ import annotations

import hashlib
import json
import re
from dataclasses import dataclass, field
from enum import Enum

SCHEMA = "code-analysis-v1"
SERVICE_VERSION = "1.0.0"

POLICY_ID = "code-analysis-policy-v1"

# Request transport budgets.
MAX_METADATA_BYTES = 64 * 1024
MAX_COMPRESSED_BYTES = 100 * 1024 * 1024
MAX_BODY_BYTES = MAX_COMPRESSED_BYTES + 2 * 1024 * 1024
UPLOAD_DEADLINE_SECONDS = 300.0
STREAM_CHUNK_BYTES = 1024 * 1024

# Analysis budgets shared by every stage.
MAX_ENTRIES = 2000
MAX_EXPANDED_BYTES = 200 * 1024 * 1024
MAX_SOURCE_BYTES = 2 * 1024 * 1024
MAX_UNITS = 10000
MAX_WINDOWS = 20000
MAX_RESPONSE_BYTES = 8 * 1024 * 1024
MAX_FINDINGS = 200
MAX_OMISSIONS = 1000

# Archive shape limits mirror the product archive inspector, tightened to the
# analysis caps above where the analysis contract is stricter.
MAX_PATH_LENGTH = 1024
MAX_PATH_DEPTH = 32
MAX_COMPRESSION_RATIO = 100.0

# Five language groups. TypeScript and TSX are pooled for retrieval with an
# explicit dialect recorded per file; parser and token coverage is tracked per
# dialect independently from retrieval quality.
LANGUAGE_GROUPS = {
    "javascript": ("javascript",),
    "typescript": ("typescript", "tsx"),
    "python": ("python",),
    "java": ("java",),
    "csharp": ("csharp",),
}
EXTENSION_DIALECTS = {
    ".js": "javascript",
    ".jsx": "javascript",
    ".ts": "typescript",
    ".tsx": "tsx",
    ".py": "python",
    ".java": "java",
    ".cs": "csharp",
}
DOLOS_LANGUAGES = {
    "javascript": "javascript",
    "typescript": "typescript",
    "tsx": "tsx",
    "python": "python",
    "java": "java",
    "csharp": "c-sharp",
}
TOKEN_K = 23
TOKEN_WINDOW = 17
TOKEN_TOP = 20
SEMANTIC_TOP = 20
UNION_BUDGET = 40


class STAGE(str, Enum):
    INVENTORY = "INVENTORY"
    EXTRACTION = "EXTRACTION"
    TOKEN = "TOKEN"
    SEMANTIC = "SEMANTIC"
    LICENSE = "LICENSE"
    UNION = "UNION"
    VERIFIER = "VERIFIER"
    REPORT = "REPORT"


class APPLICABILITY(str, Enum):
    MANDATORY = "MANDATORY"
    OPTIONAL = "OPTIONAL"


class STAGE_OUTCOME(str, Enum):
    SUCCEEDED = "SUCCEEDED"
    FAILED = "FAILED"


class RUN_OUTCOME(str, Enum):
    COMPLETED = "COMPLETED"
    FAILED = "FAILED"
    CANCELLED = "CANCELLED"


class COVERAGE(str, Enum):
    FULL = "FULL"
    PARTIAL = "PARTIAL"
    UNSUPPORTED = "UNSUPPORTED"


class VERIFIER_MODE(str, Enum):
    PENDING_RESEARCH_EVALUATION = "PENDING_RESEARCH_EVALUATION"


class SENSITIVITY(str, Enum):
    PUBLIC = "PUBLIC"
    PRIVATE = "PRIVATE"


class FINDING_TYPE(str, Enum):
    SIMILARITY_TOKEN = "SIMILARITY_TOKEN"
    SIMILARITY_SEMANTIC = "SIMILARITY_SEMANTIC"
    LICENSE_UNKNOWN = "LICENSE_UNKNOWN"
    LICENSE_CONTRADICTION = "LICENSE_CONTRADICTION"
    NOTICE_MISSING = "NOTICE_MISSING"


ERROR_CODES = frozenset({
    "AUTH_MISSING",
    "AUTH_INVALID",
    "BODY_TOO_LARGE",
    "MALFORMED_MULTIPART",
    "MALFORMED_METADATA",
    "UNKNOWN_METADATA_FIELD",
    "RUN_ID_INVALID",
    "DIGEST_MISMATCH",
    "POLICY_MISMATCH",
    "RESERVATION_INVALID",
    "SECURITY_EVIDENCE_INVALID",
    "DECLARATION_INVALID",
    "SERVICE_BUSY",
    "STALE_RUN_CONFLICT",
    "ARCHIVE_EMPTY",
    "ARCHIVE_CORRUPT",
    "ARCHIVE_UNSUPPORTED_FORMAT",
    "ARCHIVE_ABSOLUTE_PATH",
    "ARCHIVE_PATH_TRAVERSAL",
    "ARCHIVE_PATH_TOO_LONG",
    "ARCHIVE_PATH_TOO_DEEP",
    "ARCHIVE_EMPTY_PATH",
    "ARCHIVE_DUPLICATE_ENTRY",
    "ARCHIVE_SYMLINK_NOT_ALLOWED",
    "ARCHIVE_SPECIAL_ENTRY_NOT_ALLOWED",
    "ARCHIVE_ENCRYPTED",
    "ARCHIVE_TOO_MANY_ENTRIES",
    "ARCHIVE_ENTRY_TOO_LARGE",
    "ARCHIVE_TOTAL_SIZE_EXCEEDED",
    "ARCHIVE_COMPRESSION_RATIO_EXCEEDED",
    "EXTRACTION_FAILED",
    "TOOL_MISSING",
    "TOOL_TIMEOUT",
    "TOOL_FAILED",
    "TOOL_OUTPUT_INVALID",
    "MODEL_UNAVAILABLE",
    "TOKENIZER_UNAVAILABLE",
    "INDEX_UNAVAILABLE",
    "INDEX_NOT_READY",
    "REFERENCE_MANIFEST_INVALID",
    "REFERENCE_GRANT_MISSING",
    "SCAN_ERROR",
    "DEADLINE_EXCEEDED",
    "CANCELLED",
    "CLEANUP_FAILED",
    "REPORT_TOO_LARGE",
    "INTERNAL_ERROR",
})

OMISSION_REASONS = frozenset({
    "UNSUPPORTED_EXTENSION",
    "UNSUPPORTED_DIALECT",
    "VENDOR_EXCLUDED",
    "GENERATED_EXCLUDED",
    "PARSE_INVALID",
    "FILE_TOO_LARGE",
    "NESTED_ARCHIVE",
    "UNIT_BUDGET_EXCEEDED",
    "WINDOW_BUDGET_EXCEEDED",
    "WINDOW_TRUNCATED",
    "EMPTY_SHORT_CODE",
    "BINARY_CONTENT",
    "DECODE_ERROR",
})

_SHA_RE = re.compile(r"[0-9a-f]{64}")
_UUID_RE = re.compile(
    r"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}")
_OPAQUE_ID_RE = re.compile(r"[A-Za-z0-9][A-Za-z0-9_.\-]{0,127}")


def canonical(value) -> bytes:
    return (json.dumps(value, sort_keys=True, ensure_ascii=False,
                       separators=(",", ":"), allow_nan=False) + "\n").encode("utf-8")


def sha256_hex(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def is_sha256(value) -> bool:
    return isinstance(value, str) and bool(_SHA_RE.fullmatch(value))


def is_uuid(value) -> bool:
    return isinstance(value, str) and bool(_UUID_RE.fullmatch(value))


def is_opaque_id(value) -> bool:
    return isinstance(value, str) and bool(_OPAQUE_ID_RE.fullmatch(value))


class ContractError(ValueError):
    def __init__(self, code: str, detail: str = ""):
        super().__init__(detail or code)
        if code not in ERROR_CODES:
            raise ValueError(f"unknown error code {code}")
        self.code = code


@dataclass(frozen=True)
class CapacityReservation:
    service_key: str
    slot_number: int
    generation: int
    lease_token: str
    hard_deadline_utc: str


@dataclass(frozen=True)
class SecurityEvidenceBinding:
    content_sha256: str
    archive_evidence_id: str
    malware_evidence_id: str
    security_generation: int


@dataclass(frozen=True)
class DeclaredLicense:
    scope_path: str
    spdx: str


@dataclass(frozen=True)
class AnalysisMetadata:
    run_id: str
    asset_id: str
    version_id: str
    content_sha256: str
    input_digest: str
    policy_version: str
    declaration_digest: str
    material_digest: str
    reservation: CapacityReservation
    security_evidence: SecurityEvidenceBinding
    declared_licenses: tuple = ()


def _require_mapping(value, what: str) -> dict:
    if not isinstance(value, dict):
        raise ContractError("MALFORMED_METADATA", f"{what} must be an object")
    return value


def validate_metadata(raw: dict) -> AnalysisMetadata:
    """Strictly validate streamed request metadata; rejects unknown fields."""
    data = _require_mapping(raw, "metadata")
    allowed = {"runId", "assetId", "versionId", "contentSha256", "inputDigest",
               "policyVersion", "declarationDigest", "materialDigest",
               "reservation", "securityEvidence", "declaredLicenses"}
    unknown = set(data) - allowed
    if unknown:
        raise ContractError("UNKNOWN_METADATA_FIELD", sorted(unknown)[0])
    for name in ("runId", "assetId", "versionId", "contentSha256",
                 "inputDigest", "policyVersion", "declarationDigest",
                 "materialDigest"):
        if name not in data:
            raise ContractError("MALFORMED_METADATA", f"missing {name}")
    if not is_uuid(data["runId"]):
        raise ContractError("RUN_ID_INVALID")
    for name in ("assetId", "versionId"):
        if not is_opaque_id(data[name]):
            raise ContractError("MALFORMED_METADATA", f"invalid {name}")
    for name in ("contentSha256", "inputDigest", "declarationDigest", "materialDigest"):
        if not is_sha256(data[name]):
            raise ContractError("DIGEST_MISMATCH", f"invalid {name}")
    if data["policyVersion"] != POLICY_ID:
        raise ContractError("POLICY_MISMATCH")
    reservation = _validate_reservation(data["reservation"])
    evidence = _validate_security_evidence(data["securityEvidence"])
    declared = _validate_declared_licenses(data.get("declaredLicenses", []))
    return AnalysisMetadata(
        run_id=data["runId"],
        asset_id=data["assetId"],
        version_id=data["versionId"],
        content_sha256=data["contentSha256"],
        input_digest=data["inputDigest"],
        policy_version=data["policyVersion"],
        declaration_digest=data["declarationDigest"],
        material_digest=data["materialDigest"],
        reservation=reservation,
        security_evidence=evidence,
        declared_licenses=declared,
    )


def _validate_reservation(raw) -> CapacityReservation:
    data = _require_mapping(raw, "reservation")
    if set(data) != {"serviceKey", "slotNumber", "generation", "leaseToken", "hardDeadlineUtc"}:
        raise ContractError("RESERVATION_INVALID", "reservation shape mismatch")
    if not is_opaque_id(data["serviceKey"]):
        raise ContractError("RESERVATION_INVALID", "serviceKey")
    if (type(data["slotNumber"]) is not int or data["slotNumber"] < 0
            or type(data["generation"]) is not int or data["generation"] < 1):
        raise ContractError("RESERVATION_INVALID", "slot/generation")
    if not is_sha256(data["leaseToken"]):
        raise ContractError("RESERVATION_INVALID", "leaseToken")
    if not isinstance(data["hardDeadlineUtc"], str) or len(data["hardDeadlineUtc"]) > 64:
        raise ContractError("RESERVATION_INVALID", "hardDeadlineUtc")
    parse_reservation_deadline(data["hardDeadlineUtc"])
    return CapacityReservation(
        service_key=data["serviceKey"],
        slot_number=data["slotNumber"],
        generation=data["generation"],
        lease_token=data["leaseToken"],
        hard_deadline_utc=data["hardDeadlineUtc"],
    )


def _validate_security_evidence(raw) -> SecurityEvidenceBinding:
    data = _require_mapping(raw, "securityEvidence")
    if set(data) != {"contentSha256", "archiveEvidenceId", "malwareEvidenceId",
                     "securityGeneration"}:
        raise ContractError("SECURITY_EVIDENCE_INVALID", "securityEvidence shape mismatch")
    if not is_sha256(data["contentSha256"]):
        raise ContractError("SECURITY_EVIDENCE_INVALID", "contentSha256")
    for name in ("archiveEvidenceId", "malwareEvidenceId"):
        if not is_opaque_id(data[name]):
            raise ContractError("SECURITY_EVIDENCE_INVALID", name)
    if type(data["securityGeneration"]) is not int or data["securityGeneration"] < 1:
        raise ContractError("SECURITY_EVIDENCE_INVALID", "securityGeneration")
    return SecurityEvidenceBinding(
        content_sha256=data["contentSha256"],
        archive_evidence_id=data["archiveEvidenceId"],
        malware_evidence_id=data["malwareEvidenceId"],
        security_generation=data["securityGeneration"],
    )


def _validate_declared_licenses(raw) -> tuple:
    if not isinstance(raw, list) or len(raw) > 64:
        raise ContractError("DECLARATION_INVALID", "declaredLicenses")
    result = []
    for item in raw:
        entry = _require_mapping(item, "declaredLicenses entry")
        if set(entry) != {"scopePath", "spdx"}:
            raise ContractError("DECLARATION_INVALID", "declared license shape")
        if not isinstance(entry["scopePath"], str) or len(entry["scopePath"]) > 512:
            raise ContractError("DECLARATION_INVALID", "scopePath")
        if not isinstance(entry["spdx"], str) or not 1 <= len(entry["spdx"]) <= 128:
            raise ContractError("DECLARATION_INVALID", "spdx")
        result.append(DeclaredLicense(scope_path=entry["scopePath"], spdx=entry["spdx"]))
    return tuple(result)


def parse_reservation_deadline(value: str) -> float:
    """Parse an explicit UTC deadline; naive or non-UTC values are rejected."""
    from datetime import datetime, timezone
    text = value.strip()
    if text.endswith("Z"):
        text = text[:-1] + "+00:00"
    try:
        moment = datetime.fromisoformat(text)
    except ValueError:
        raise ContractError("RESERVATION_INVALID", "hardDeadlineUtc")
    if moment.tzinfo is None or moment.utcoffset() is None:
        raise ContractError("RESERVATION_INVALID", "hardDeadlineUtc")
    return moment.astimezone(timezone.utc).timestamp()


def compute_analysis_digest(metadata: AnalysisMetadata, policy_digest: str,
                            reference: dict | None) -> str:
    """Immutable computational identity: execution reservation excluded.

    The digest binds the exact declaration projection actually used, the
    precise security evidence bindings, and the pinned reference/model
    identity. A fresh lease for the same run keeps the same digest.
    """
    if reference is not None:
        reference_part = {"snapshotId": reference.get("snapshotId"),
                          "modelKey": reference.get("modelKey"),
                          "indexKey": reference.get("indexKey")}
    else:
        reference_part = None
    return sha256_hex(canonical({
        "schema": SCHEMA,
        "runId": metadata.run_id,
        "assetId": metadata.asset_id,
        "versionId": metadata.version_id,
        "contentSha256": metadata.content_sha256,
        "declarationDigest": metadata.declaration_digest,
        "declaredLicenses": [{"scopePath": item.scope_path, "spdx": item.spdx}
                             for item in metadata.declared_licenses],
        "materialDigest": metadata.material_digest,
        "policyDigest": policy_digest,
        "securityEvidence": {
            "contentSha256": metadata.security_evidence.content_sha256,
            "archiveEvidenceId": metadata.security_evidence.archive_evidence_id,
            "malwareEvidenceId": metadata.security_evidence.malware_evidence_id,
            "securityGeneration": metadata.security_evidence.security_generation,
        },
        "reference": reference_part,
    }))


@dataclass
class StageRecord:
    stage: str
    applicability: str
    outcome: str
    error_code: str | None = None
    retryable: bool = False
    duration_seconds: float = 0.0
    input_sha256: str | None = None
    output_sha256: str | None = None
    tool_identity: dict = field(default_factory=dict)
    counts: dict = field(default_factory=dict)

    def as_dict(self) -> dict:
        return {
            "stage": self.stage,
            "applicability": self.applicability,
            "outcome": self.outcome,
            "errorCode": self.error_code,
            "retryable": self.retryable,
            "durationSeconds": round(self.duration_seconds, 3),
            "inputSha256": self.input_sha256,
            "outputSha256": self.output_sha256,
            "toolIdentity": self.tool_identity,
            "counts": self.counts,
        }
