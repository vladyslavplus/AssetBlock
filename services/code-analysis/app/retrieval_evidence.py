"""Strict bounded retrieval evidence; never infers scores or decisions."""
import math
import re

from . import contracts

DETAIL_KEYS = {"queryFileId", "queryUnit", "channels", "tokenRank", "tokenScore",
               "semanticRank", "semanticScore", "material"}
QUERY_KEYS = {"queryFileId", "tokenMaxScore", "tokenCandidates", "tokenRetained", "tokenOverflow",
              "semanticCandidates", "semanticRetained", "semanticOverflow", "unionRetained",
              "reportOmitted", "reportOmittedToken", "reportOmittedSemantic"}


def _require(condition):
    if not condition:
        raise contracts.ContractError("TOOL_OUTPUT_INVALID", "retrieval evidence contract")


def _sha(value):
    return isinstance(value, str) and re.fullmatch("[0-9a-f]{64}", value) is not None


def validate_retrieval(body):
    summary = body.get("retrievalSummary")
    _require(isinstance(summary, dict) and set(summary) == {"tokenTop", "semanticTop", "unionBudget", "queries"})
    for field, expected in (("tokenTop", contracts.TOKEN_TOP), ("semanticTop", contracts.SEMANTIC_TOP),
                            ("unionBudget", contracts.UNION_BUDGET)):
        _require(type(summary[field]) is int and summary[field] == expected)
    queries = summary["queries"]
    _require(isinstance(queries, list) and len(queries) <= contracts.MAX_ENTRIES)
    if "analyzedFiles" in body.get("coverage", {}):
        _require(len(queries) == body["coverage"]["analyzedFiles"])
    by_query = {}
    for query in queries:
        _require(isinstance(query, dict) and set(query) == QUERY_KEYS and _sha(query["queryFileId"]))
        _require(query["queryFileId"] not in by_query)
        for key in QUERY_KEYS - {"queryFileId", "tokenMaxScore"}:
            _require(type(query[key]) is int and 0 <= query[key] <= 1000)
        for channel, top in (("token", contracts.TOKEN_TOP), ("semantic", contracts.SEMANTIC_TOP)):
            _require(query[channel + "Retained"] == min(query[channel + "Candidates"], top))
            _require(query[channel + "Overflow"] == query[channel + "Candidates"] - query[channel + "Retained"])
            _require(query["reportOmitted" + channel.title()] <= query[channel + "Retained"])
        _require(max(query["tokenRetained"], query["semanticRetained"]) <= query["unionRetained"]
                 <= min(contracts.UNION_BUDGET, query["tokenRetained"] + query["semanticRetained"]))
        _require(query["reportOmitted"] <= query["unionRetained"])
        _require(max(query["reportOmittedToken"], query["reportOmittedSemantic"]) <= query["reportOmitted"]
                 <= query["reportOmittedToken"] + query["reportOmittedSemantic"])
        maximum = query["tokenMaxScore"]
        _require(maximum is None if query["tokenRetained"] == 0 else
                 type(maximum) in (int, float) and math.isfinite(maximum) and 0 < maximum <= 1)
        by_query[query["queryFileId"]] = query
    groups = {key: [] for key in by_query}
    identities = set()
    for finding in body.get("findings", []):
        similarity = finding.get("type") in ("SIMILARITY_TOKEN", "SIMILARITY_SEMANTIC")
        detail = finding.get("retrieval")
        if not similarity:
            _require("retrieval" not in finding)
            continue
        _require(isinstance(detail, dict) and set(detail) == DETAIL_KEYS)
        query_id = detail["queryFileId"]
        _require(_sha(query_id) and query_id == finding.get("fileId") and query_id in groups)
        foreign_id = (finding.get("foreign") or {}).get("fileId")
        _require(_sha(foreign_id) and (query_id, foreign_id) not in identities)
        identities.add((query_id, foreign_id))
        _require(detail["queryUnit"] == "FILE" and detail["channels"] in ("TOKEN", "SEMANTIC", "TOKEN_AND_SEMANTIC"))
        for channel, top, member in (("token", contracts.TOKEN_TOP, "TOKEN"), ("semantic", contracts.SEMANTIC_TOP, "SEMANTIC")):
            rank, score = detail[channel + "Rank"], detail[channel + "Score"]
            present = member in detail["channels"]
            if present:
                _require(type(rank) is int and 1 <= rank <= top)
                _require(type(score) in (int, float) and math.isfinite(score) and 0 <= score <= 1)
                _require(channel != "token" or score > 0)
            else:
                _require(rank is None and score is None)
        maximum = by_query[query_id]["tokenMaxScore"]
        _require(detail["tokenScore"] is None or maximum is not None and detail["tokenScore"] <= maximum)
        if detail["tokenRank"] == 1:
            _require(detail["tokenScore"] == maximum)
        _require(type(detail["material"]) is bool and detail["material"] ==
                 (detail["tokenScore"] is not None and detail["tokenScore"] > 0 and detail["tokenScore"] == maximum))
        _require(finding["type"] == ("SIMILARITY_TOKEN" if detail["tokenRank"] is not None else "SIMILARITY_SEMANTIC"))
        groups[query_id].append((foreign_id, detail))
    for query_id, entries in groups.items():
        query = by_query[query_id]
        _require(len(entries) + query["reportOmitted"] == query["unionRetained"])
        for channel in ("token", "semantic"):
            ranked = sorted((d[channel + "Rank"], -d[channel + "Score"], fid) for fid, d in entries if d[channel + "Rank"] is not None)
            _require(len(ranked) + query["reportOmitted" + channel.title()] == query[channel + "Retained"])
            _require(len({r[0] for r in ranked}) == len(ranked))
            _require(all(r[0] <= query[channel + "Retained"] for r in ranked))
            # Semantic scores clamp negative cosine similarity to zero; equal displayed scores
            # need not imply equal underlying distances. Producer breaks distance ties by file ID.
            values = [(r[1], r[2]) for r in ranked] if channel == "token" else [r[1] for r in ranked]
            _require(values == sorted(values))
    if "findingOverflow" in body:
        _require(sum(q["reportOmitted"] for q in queries) <= body["findingOverflow"])
