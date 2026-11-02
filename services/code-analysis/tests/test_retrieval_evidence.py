"""Moderator evidence stays complete, bounded and absent from seller projection."""
from copy import deepcopy
from types import SimpleNamespace

import pytest

from app import contracts, stages, mechanisms


def body():
    query = "a" * 64
    evidence = {"queryFileId": query, "queryUnit": "FILE", "channels": "TOKEN_AND_SEMANTIC",
                "tokenRank": 1, "tokenScore": 0.8, "semanticRank": 1, "semanticScore": 0.4,
                "material": True}
    return {"findings": [{"type": "SIMILARITY_TOKEN", "fileId": query,
                           "foreign": {"fileId": "b" * 64}, "retrieval": evidence}],
            "retrievalSummary": {"tokenTop": 20, "semanticTop": 20, "unionBudget": 40,
                 "queries": [{"queryFileId": query, "tokenMaxScore": 0.8, "tokenCandidates": 1, "tokenRetained": 1,
                     "tokenOverflow": 0, "semanticCandidates": 1, "semanticRetained": 1,
                     "semanticOverflow": 0, "unionRetained": 1, "reportOmitted": 0,
                     "reportOmittedToken": 0, "reportOmittedSemantic": 0}]}}


@pytest.mark.parametrize("field,value", [("tokenRank", 0), ("tokenRank", True),
    ("tokenRank", 21), ("tokenScore", float("nan")), ("tokenScore", float("inf")),
    ("semanticScore", -0.1), ("semanticScore", 1.1), ("channels", "SEMANTIC"),
    ("semanticRank", None), ("material", False), ("queryFileId", "bad")])
def test_invalid_candidate_fails_self_validation(field, value):
    report = body()
    report["findings"][0]["retrieval"][field] = value
    with pytest.raises(contracts.ContractError):
        stages._validate_retrieval(report)


def test_inconsistent_overflow_and_duplicate_ranks_fail():
    for mutate in (lambda r: r["retrievalSummary"]["queries"][0].update(tokenOverflow=1),
                   lambda r: r["findings"].append(deepcopy(r["findings"][0]))):
        report = body()
        mutate(report)
        with pytest.raises(contracts.ContractError):
            stages._validate_retrieval(report)


def test_absent_channel_null_and_semantic_zero_score():
    report = body()
    stages._validate_retrieval(report)
    row = report["findings"][0]["retrieval"]
    row.update(channels="TOKEN", semanticRank=None, semanticScore=None, tokenScore=0.8)
    report["retrievalSummary"]["queries"][0].update(semanticRetained=0, semanticCandidates=0)
    stages._validate_retrieval(report)
    row.update(channels="SEMANTIC", tokenRank=None, tokenScore=None,
               semanticRank=1, semanticScore=0.0, material=False)
    report["findings"][0]["type"] = "SIMILARITY_SEMANTIC"
    report["retrievalSummary"]["queries"][0].update(tokenRetained=0, tokenCandidates=0,
                                                        semanticRetained=1, semanticCandidates=1, tokenMaxScore=None)
    stages._validate_retrieval(report)


def test_union_preserves_both_channels_and_token_rank_one_material(monkeypatch):
    query, first, second = "a" * 64, "b" * 64, "c" * 64
    entry = lambda file_id: SimpleNamespace(file_id=file_id, origin="FOREIGN", sensitivity="PRIVATE", attribution={})
    pipe = stages.Pipeline(context=SimpleNamespace(),
        analyzed=[SimpleNamespace(file_id=query, canonical_path="src/main.py")],
        token_ranked={query: [{"fileId": first, "score": 0.8, "rank": 1}]},
        semantic_ranked={query: [{"fileId": second, "score": 0.6, "rank": 1},
                                 {"fileId": first, "score": 0.4, "rank": 2}]})
    monkeypatch.setattr(stages, "_require_snapshot", lambda _: SimpleNamespace(files=[entry(first), entry(second)]))
    monkeypatch.setattr(stages, "_own_excerpt", lambda *_: ("own", 1, 1, 0, 3))
    stages._stage_union(pipe)
    rows = {f["foreign"]["fileId"]: f["retrieval"] for f in pipe.findings}
    assert rows[first]["tokenScore"] == 0.8 and rows[first]["semanticScore"] == 0.4
    assert rows[first]["semanticRank"] == 2 and rows[first]["material"] is True
    assert rows[second]["tokenRank"] is None and rows[second]["tokenScore"] is None
    assert rows[second]["channels"] == "SEMANTIC" and rows[second]["material"] is False


def test_token_ties_order_by_file_identity_independent_of_input_order():
    query = "a" * 64
    pairs = [{"leftId": query, "rightId": f"{i:064x}", "tokenScore": 0.5} for i in range(25)]
    forward = mechanisms.rank_token_files(pairs, query, [p["rightId"] for p in pairs], 20)
    backward = mechanisms.rank_token_files(list(reversed(pairs)), query, [p["rightId"] for p in pairs], 20)
    assert forward == backward
    assert [r["rank"] for r in forward] == list(range(1, 21))


def test_policy_digest_matches_product_and_material_rule():
    import re
    from pathlib import Path
    from app.policy import current_policy, EVALUATION_EVIDENCE_SHA256
    policy = current_policy()
    source = (Path(__file__).resolve().parents[3] / "asblock-backend/AssetBlock.Domain/Core/Constants/ReviewAnalysisPolicy.cs").read_text()
    assert re.search(r'DIGEST = "([0-9a-f]{64})"', source).group(1) == policy["policyDigest"]
    assert policy["retrieval"]["materialRule"] == "ALL_TOP_POSITIVE_TOKEN_SCORE_TIES_PER_QUERY_FILE"
    assert policy["retrieval"]["evidenceSha256"] == EVALUATION_EVIDENCE_SHA256
    assert int(re.search(r'MAX_QUERIES = (\d+)', source).group(1)) == policy["retrieval"]["maxQueries"]


def test_producer_summary_counts_channel_and_report_overflow(monkeypatch):
    query = "a" * 64
    entries = [SimpleNamespace(file_id=f"{i:064x}", origin="FOREIGN", sensitivity="PRIVATE",
                               attribution={}, language_group="python") for i in range(30)]
    pipe = stages.Pipeline(context=SimpleNamespace(snapshot=SimpleNamespace(files=entries)),
        analyzed=[SimpleNamespace(file_id=query, canonical_path="src/main.py", language_group="python")],
        token_candidate_counts={query: 25},
        token_ranked={query: [{"fileId": e.file_id, "score": 0.8, "rank": i + 1} for i, e in enumerate(entries[:20])]},
        semantic_ranked={query: [{"fileId": e.file_id, "score": 0.4, "rank": i + 1} for i, e in enumerate(entries[10:])]})
    monkeypatch.setattr(stages, "_own_excerpt", lambda *_: ("own", 1, 1, 0, 3))
    stages._stage_union(pipe)
    kept = pipe.findings[:5]
    summary = stages._retrieval_summary(pipe, kept)
    row = summary["queries"][0]
    assert row["tokenOverflow"] == 5 and row["semanticOverflow"] == 10
    assert row["reportOmitted"] == 25 and row["reportOmittedToken"] == 15 and row["reportOmittedSemantic"] == 20
    stages._validate_retrieval({"findings": kept, "findingOverflow": 25, "retrievalSummary": summary})


def test_semantic_distance_ties_use_file_identity(monkeypatch):
    from scripts.code_analysis import code_index
    query = "a" * 64
    entries = [SimpleNamespace(file_id="c" * 64, language_group="python"),
               SimpleNamespace(file_id="b" * 64, language_group="python")]
    monkeypatch.setattr(code_index, "exact_query", lambda *a, **k: [
        {"fileId": e.file_id, "distance": 0.25} for e in entries])
    snapshot = SimpleNamespace(files=entries, index_key="index", partition="validation")
    ranked = stages._exact_retrieval(None, snapshot, [{"queryFileId": query, "queryGroup": "python"}], [[0.0]])
    assert [r["fileId"] for r in ranked[query]] == ["b" * 64, "c" * 64]
    assert [r["rank"] for r in ranked[query]] == [1, 2]


@pytest.mark.parametrize("token_score", [0, 0.0])
def test_zero_score_is_not_token_membership(token_score):
    report = body()
    report["findings"][0]["retrieval"]["tokenScore"] = token_score
    with pytest.raises(contracts.ContractError):
        stages._validate_retrieval(report)


def test_all_positive_top_score_ties_are_material():
    report = body()
    first = report["findings"][0]
    second = deepcopy(first)
    second["foreign"]["fileId"] = "c" * 64
    second["retrieval"].update(tokenRank=2, semanticRank=2, material=True)
    report["findings"].append(second)
    report["retrievalSummary"]["queries"][0].update(tokenCandidates=2, tokenRetained=2,
        semanticCandidates=2, semanticRetained=2, unionRetained=2, tokenMaxScore=0.8)
    stages._validate_retrieval(report)
    second["retrieval"]["material"] = False
    with pytest.raises(contracts.ContractError):
        stages._validate_retrieval(report)


@pytest.mark.parametrize("omitted,token,semantic", [(0, 1, 0), (3, 1, 1)])
def test_impossible_omitted_union_arithmetic_is_rejected(omitted, token, semantic):
    report = body()
    report["retrievalSummary"]["queries"][0].update(reportOmitted=omitted,
        reportOmittedToken=token, reportOmittedSemantic=semantic)
    with pytest.raises(contracts.ContractError):
        stages._validate_retrieval(report)


def test_all_zero_token_pairs_have_no_membership_or_material(monkeypatch):
    query, foreign = "a" * 64, "b" * 64
    ranked = mechanisms.rank_token_files([{"leftId": query, "rightId": foreign, "tokenScore": 0}], query, [foreign], 20)
    assert ranked == []
    entry = SimpleNamespace(file_id=foreign, origin="FOREIGN", sensitivity="PRIVATE", attribution={}, language_group="python")
    pipe = stages.Pipeline(context=SimpleNamespace(snapshot=SimpleNamespace(files=[entry])),
        analyzed=[SimpleNamespace(file_id=query, canonical_path="own.py", language_group="python")],
        token_ranked={query: ranked}, token_candidate_counts={query: 0},
        semantic_ranked={query: [{"fileId": foreign, "rank": 1, "score": 0.4}]})
    monkeypatch.setattr(stages, "_own_excerpt", lambda *_: ("own", 1, 1, 0, 3))
    stages._stage_union(pipe)
    detail = pipe.findings[0]["retrieval"]
    assert detail["channels"] == "SEMANTIC" and detail["tokenRank"] is None and detail["tokenScore"] is None
    assert detail["material"] is False
    summary = stages._retrieval_summary(pipe, pipe.findings)
    assert summary["queries"][0]["tokenCandidates"] == summary["queries"][0]["tokenOverflow"] == 0
    assert summary["queries"][0]["tokenMaxScore"] is None
    stages._validate_retrieval({"findings": pipe.findings, "retrievalSummary": summary})


def test_zero_pairs_do_not_consume_positive_top_or_overflow_counts():
    query = "a" * 64
    pairs = [{"leftId": query, "rightId": f"{i:064x}", "tokenScore": 0 if i < 25 else 0.5} for i in range(50)]
    ranked = mechanisms.rank_token_files(pairs, query, [p["rightId"] for p in pairs], 50)
    assert len(ranked) == 25 and len(ranked) - contracts.TOKEN_TOP == 5
    assert all(row["score"] > 0 for row in ranked)
    assert [row["rank"] for row in ranked] == list(range(1, 26))


def test_top_material_score_survives_report_omission_of_rank_one():
    report = body()
    report["findings"][0]["retrieval"]["tokenRank"] = 2
    report["retrievalSummary"]["queries"][0].update(tokenCandidates=2, tokenRetained=2,
        unionRetained=2, reportOmitted=1, reportOmittedToken=1)
    report["findingOverflow"] = 1
    stages._validate_retrieval(report)
    report["findings"][0]["retrieval"].update(tokenScore=0.7, material=False)
    stages._validate_retrieval(report)


def test_producer_marks_every_top_positive_tie_and_no_lower_score():
    query = "a" * 64
    ids = ["b" * 64, "c" * 64, "d" * 64]
    pipe = stages.Pipeline(context=None, token_ranked={query: [
        {"fileId": ids[0], "rank": 1, "score": 0.8},
        {"fileId": ids[1], "rank": 2, "score": 0.8},
        {"fileId": ids[2], "rank": 3, "score": 0.7}]})
    assert [stages._candidate_evidence(pipe, query, file_id)["material"] for file_id in ids] == [True, True, False]


@pytest.mark.parametrize("score", [-0.1, float("nan"), float("inf"), 1.1, True])
def test_invalid_token_pair_score_fails_before_zero_filter(score):
    with pytest.raises(contracts.ContractError):
        mechanisms.rank_token_files([{"leftId": "a" * 64, "rightId": "b" * 64, "tokenScore": score}],
                                    "a" * 64, ["b" * 64], 20)
