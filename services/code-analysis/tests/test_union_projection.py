"""Union keeps both channels; seller projection redacts foreign private data."""

from app import contracts, mechanisms
from app.stages import build_seller_safe


def test_union_retains_single_channel_candidates():
    token_only = ["a" * 64]
    semantic_only = ["b" * 64]
    assert mechanisms.union_candidates(token_only, [], 40) == token_only
    assert mechanisms.union_candidates([], semantic_only, 40) == semantic_only
    union = mechanisms.union_candidates(token_only, semantic_only, 40)
    assert union == token_only + semantic_only


def test_union_budget_and_order_are_deterministic():
    token = [f"{i:064x}" for i in range(30)]
    semantic = [f"{i:064x}" for i in range(20, 50)]
    union = mechanisms.union_candidates(token, semantic, 40)
    assert len(union) == 40
    assert union[:30] == token
    assert union[30:] == [f"{i:064x}" for i in range(30, 40)]


def test_seller_safe_redacts_private_foreign_evidence():
    sentinel_path = "PRIVATE-SENTINEL-PATH-abc123"
    sentinel_name = "PRIVATE-SENTINEL-NAME-abc123"
    report = {
        "reportId": "r" * 64, "runId": "11111111-2222-3333-4444-555555555555",
        "outcome": "COMPLETED",
        "coverage": {"level": "PARTIAL", "omissions": [{"reason": "UNSUPPORTED_EXTENSION"}]},
        "findingOverflow": 0,
        "findings": [
            {"findingId": "f" * 64, "type": "SIMILARITY_SEMANTIC",
             "path": "src/own.js", "fileId": "a" * 64,
             "startLine": 1, "endLine": 3, "ownExcerpt": "function own() {}",
             "foreign": {"fileId": "b" * 64, "sensitivity": "PRIVATE", "origin": "FOREIGN",
                         "attribution": {"path": sentinel_path, "title": sentinel_name}},
             "retrieval": {"tokenRank": 1, "tokenScore": 0.97, "semanticRank": 2, "semanticScore": 0.88, "channels": "TOKEN_AND_SEMANTIC", "material": True},
             "reason": "Similar code was found in another private asset.",
             "requestedAction": "Wait for review."},
            {"findingId": "e" * 64, "type": "SIMILARITY_TOKEN",
             "path": "src/own2.js", "fileId": "c" * 64,
             "startLine": 1, "endLine": 2, "ownExcerpt": "function two() {}",
             "foreign": {"fileId": "d" * 64, "sensitivity": "PUBLIC", "origin": "FOREIGN",
                         "attribution": {"label": "dayjs MIT"}},
             "retrieval": {"tokenRank": 1, "tokenScore": 0.88},
             "reason": "Overlaps public reference.",
             "requestedAction": "Review."},
        ],
    }
    safe = build_seller_safe(report)
    text = contracts.canonical(safe).decode()
    assert sentinel_path not in text
    assert sentinel_name not in text
    assert "0.97" not in text and "0.88" not in text
    for private_field in ("tokenRank", "tokenScore", "semanticRank", "semanticScore", "retrieval", "channels", "material"):
        assert private_field not in text
    assert "private asset" in text
    assert "dayjs MIT" in text
    assert "reviewEligible" not in safe
