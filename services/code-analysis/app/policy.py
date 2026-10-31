"""Immutable analysis policy: stage matrix, capabilities, budgets, identities.

The policy disables pair verification based on frozen evaluation evidence.
The service ignores any client-supplied publication or
approval flag: admission lives outside this service.
"""

from __future__ import annotations

from copy import deepcopy

from . import contracts
from .contracts import VERIFIER_MODE, canonical, sha256_hex

VERIFIER_REASON = (
    "Pair verification is disabled by evaluation; retained comparison evidence "
    "requires an explicit human decision."
)
# Owned by the frozen pair-verification evidence package; never a mutable plan reference.
EVALUATION_EVIDENCE_SHA256 = "676ad4da5a40ea7a6f040b0c51cc060088689cfd26f23d4a3974b965f1063973"

_STAGE_MATRIX = (
    {"stage": "INVENTORY", "applicability": "MANDATORY"},
    {"stage": "EXTRACTION", "applicability": "MANDATORY"},
    {"stage": "TOKEN", "applicability": "MANDATORY"},
    {"stage": "SEMANTIC", "applicability": "MANDATORY"},
    {"stage": "LICENSE", "applicability": "MANDATORY"},
    {"stage": "UNION", "applicability": "MANDATORY"},
    {"stage": "VERIFIER", "applicability": "MANDATORY"},
    {"stage": "REPORT", "applicability": "MANDATORY"},
)

_CAPABILITIES = (
    {"group": "javascript", "dialects": ["javascript"],
     "parser": True, "token": True, "retrieval": "BASELINE_UNEVALUATED"},
    {"group": "typescript", "dialects": ["typescript", "tsx"],
     "parser": True, "token": True, "retrieval": "BASELINE_UNEVALUATED",
     "note": "TypeScript and TSX are pooled for retrieval with explicit dialect provenance."},
    {"group": "python", "dialects": ["python"],
     "parser": True, "token": True, "retrieval": "BASELINE_UNEVALUATED"},
    {"group": "java", "dialects": ["java"],
     "parser": True, "token": True, "retrieval": "BASELINE_UNEVALUATED"},
    {"group": "csharp", "dialects": ["csharp"],
     "parser": True, "token": True, "retrieval": "BASELINE_UNEVALUATED"},
)


def build_policy() -> dict:
    """Return the immutable policy document with its digest."""
    document = {
        "schema": contracts.SCHEMA,
        "policyVersion": contracts.POLICY_ID,
        "stageMatrix": list(_STAGE_MATRIX),
        "capabilities": list(_CAPABILITIES),
        "budgets": {
            "maxCompressedBytes": contracts.MAX_COMPRESSED_BYTES,
            "maxEntries": contracts.MAX_ENTRIES,
            "maxExpandedBytes": contracts.MAX_EXPANDED_BYTES,
            "maxSourceBytes": contracts.MAX_SOURCE_BYTES,
            "maxUnits": contracts.MAX_UNITS,
            "maxWindows": contracts.MAX_WINDOWS,
            "maxResponseBytes": contracts.MAX_RESPONSE_BYTES,
            "maxFindings": contracts.MAX_FINDINGS,
            "tokenK": contracts.TOKEN_K,
            "tokenWindow": contracts.TOKEN_WINDOW,
            "tokenTop": contracts.TOKEN_TOP,
            "semanticTop": contracts.SEMANTIC_TOP,
            "unionBudget": contracts.UNION_BUDGET,
        },
        "model": {
            "upstream": "microsoft/unixcoder-base-nine",
            "revision": "5f0dc256eb0904af496d427451da5d7f90261676",
            "dimension": 768,
            "framing": "encoder-only-256-overlap32-max16-v1",
            "pooling": "padding-masked mean then L2",
        },
        "verifier": {
            "mode": VERIFIER_MODE.DISABLED_BY_EVALUATION.value,
            "reason": VERIFIER_REASON,
            "evidenceSha256": EVALUATION_EVIDENCE_SHA256,
            "outputs": [],
        },
    }
    return {**document, "policyDigest": sha256_hex(canonical(document))}


_POLICY = build_policy()
POLICY_DIGEST = _POLICY["policyDigest"]


def current_policy() -> dict:
    return deepcopy(_POLICY)
