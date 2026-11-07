"""Candidate lists and emitted findings for the evaluated pipelines (pure functions).

Ranking, union and tie handling reuse ``scripts.code_analysis.baselines``, the same
functions the analysis service delegates to. The product's material rule is every
candidate whose positive token score equals the query-file maximum (all ties).
"""

import math

from scripts.code_analysis import baselines

PRODUCT = "PRODUCT"
TOKEN_RANK_CUT = "TOKEN_RANK_CUT"
FINE_TUNED_RETRIEVAL = "FINE_TUNED_RETRIEVAL"
FINE_TUNED_RETRIEVAL_PLUS_VERIFIER = "FINE_TUNED_RETRIEVAL_PLUS_VERIFIER"
PIPELINES = (PRODUCT, TOKEN_RANK_CUT, FINE_TUNED_RETRIEVAL, FINE_TUNED_RETRIEVAL_PLUS_VERIFIER)


def rank_token(pairs, query_id, gallery_ids, *, limit):
    """Token ranking; a zero score is the absence of an observation, a malformed one fails."""
    for pair in pairs:
        score = pair["tokenScore"]
        if type(score) not in (int, float) or not math.isfinite(score) or not 0 <= score <= 1:
            raise ValueError("token score is invalid")
    return baselines.rank_files([pair for pair in pairs if pair["tokenScore"] > 0], query_id,
                                gallery_ids, limit=limit)


def build_candidates(token_ranked, semantic_ranked, *, token_top, semantic_top, union_budget):
    """Union of the retained channels with per-candidate evidence and the material flag."""
    token = {row["fileId"]: row for row in token_ranked[:token_top]}
    semantic = {row["fileId"]: row for row in semantic_ranked[:semantic_top]}
    order = baselines.union_candidates([row["fileId"] for row in token_ranked[:token_top]],
                                       [row["fileId"] for row in semantic_ranked[:semantic_top]],
                                       budget=union_budget)
    maximum = token_ranked[0]["score"] if token_ranked else None
    entries = []
    for file_id in order:
        t, s = token.get(file_id), semantic.get(file_id)
        entries.append({"fileId": file_id,
                        "channels": "TOKEN_AND_SEMANTIC" if t and s else "TOKEN" if t else "SEMANTIC",
                        "tokenRank": t["rank"] if t else None, "tokenScore": t["score"] if t else None,
                        "semanticRank": s["rank"] if s else None, "semanticScore": s["score"] if s else None,
                        "material": t is not None and t["score"] > 0 and t["score"] == maximum})
    return {"candidates": entries,
            "counts": {"tokenCandidates": len(token_ranked), "tokenRetained": len(token),
                       "semanticCandidates": len(semantic_ranked), "semanticRetained": len(semantic),
                       "union": len(entries)}}


def product_findings(candidates):
    return [row["fileId"] for row in candidates if row["material"]]


def token_rank_cut_findings(token_ranked, rank):
    return [row["fileId"] for row in token_ranked if row["rank"] <= rank]


def fine_tuned_findings(candidates, semantic_rank_cut):
    return [row["fileId"] for row in candidates
            if row["material"] or (row["semanticRank"] is not None and row["semanticRank"] <= semantic_rank_cut)]


def verifier_findings(candidates, threshold):
    return [row["fileId"] for row in candidates
            if row.get("verifierScore") is not None and row["verifierScore"] >= threshold]
