"""Deterministic semantic ranking checks and exact candidate-set construction."""

from scripts.code_analysis.contracts import LANGUAGES
from scripts.code_analysis.evaluation import seed_macro
from scripts.code_analysis.baselines import union_candidates

METRICS = (("semantic@5", "recall"), ("semantic@10", "recall"),
           ("semantic@20", "recall"), ("semantic@20", "mrr"))
MAX_DIFFERENCE = 1e-9
DATABASE_DIFFERENCE = 1e-5


def require_same_orders(first, second):
    if not isinstance(first, dict) or not isinstance(second, dict) or set(first) != set(second):
        raise ValueError("ranking replay model set differs")
    for label in sorted(first):
        if set(first[label]) != set(second[label]):
            raise ValueError("ranking replay query membership differs: " + label)
        for query_id in sorted(first[label]):
            if first[label][query_id] != second[label][query_id]:
                raise ValueError("deterministic ranking replay differs: " + label)
    return True


def require_database_corroboration(records, *, models=("E0", "E1", "E2", "E3"), query_count=201):
    if not isinstance(records, dict) or set(records) != set(models):
        raise ValueError("database corroboration model set differs")
    for label in models:
        rows = records[label]
        if not isinstance(rows, list) or len(rows) != query_count:
            raise ValueError("database corroboration query denominator differs: " + label)
        query_ids = [row.get("queryId") for row in rows]
        if any(not isinstance(value, str) for value in query_ids) or len(set(query_ids)) != query_count:
            raise ValueError("database corroboration query identities are invalid: " + label)
        for row in rows:
            if row.get("exact") is not True or row.get("disagreements") != []:
                raise ValueError("database corroboration did not record exact agreement: " + label)
            difference = row.get("maxAbsScoreDifference")
            if type(difference) not in (int, float) or difference < 0 or difference > DATABASE_DIFFERENCE:
                raise ValueError("database score difference exceeds tolerance: " + label)
    return {label: {"queries": len(records[label]), "allExact": True,
                    "zeroDisagreements": True, "maximumDifference": max(
                        row["maxAbsScoreDifference"] for row in records[label])}
            for label in models}


def semantic_metrics(records, *, labels=("E0", "E3")):
    """Reproduce the evaluator's per-seed and per-language weighting for semantic rows."""
    out = {}
    for label in labels:
        model_rows = records[label]
        model_result = {}
        for language in LANGUAGES:
            rows = [row for row in model_rows if row["language"] == language]
            if not rows:
                raise ValueError("semantic metric language has no queries: " + language)
            cells = {}
            for metric_cell, metric_name in METRICS:
                metric_rows = [row for row in rows if row["cells"].get(metric_cell, {}).get("status") == "PRIMARY"]
                cases = [{"seedGroup": row["seedGroup"], "querySha256": row["querySha256"],
                          "metrics": {"recall": row["cells"][metric_cell]["recall"],
                                      "mrr": row["cells"][metric_cell]["mrr"]}}
                         for row in metric_rows]
                aggregated = seed_macro(cases)
                cells[metric_cell + "." + metric_name] = aggregated[metric_name]
            model_result[language] = {"queries": len(rows), "metrics": cells}
        if sum(row["queries"] for row in model_result.values()) != 201:
            raise ValueError("semantic evaluation denominator is not 201: " + label)
        out[label] = model_result
    return out


def require_metric_equality(actual, evaluation, *, tolerance=MAX_DIFFERENCE, labels=("E0", "E3")):
    if type(tolerance) not in (int, float) or tolerance < 0:
        raise ValueError("metric tolerance is invalid")
    for label in labels:
        ev_denominator = evaluation.get("denominators", {}).get(label, {}).get("queries")
        if ev_denominator != 201 or sum(row["queries"] for row in actual[label].values()) != ev_denominator:
            raise ValueError("evaluation metric denominator differs: " + label)
        for language in LANGUAGES:
            expected_language = evaluation.get("tables", {}).get(label, {}).get("languages", {})
            for metric_cell, metric_name in METRICS:
                expected = expected_language.get(metric_cell, {}).get(language)
                actual_value = actual[label][language]["metrics"][metric_cell + "." + metric_name]
                if not isinstance(expected, dict) or expected.get(metric_name) is None:
                    raise ValueError("evaluation metric is unavailable: " + label + "/" + language)
                if abs(actual_value - expected[metric_name]) > tolerance:
                    raise ValueError("recomputed evaluation metric differs: " + label + "/" + language)
    return True


def candidate_union(semantic_ids, token_ids, scored_gallery):
    gallery = set(scored_gallery)
    if len(gallery) <= 40:
        raise ValueError("strict gallery rule requires more than 40 scored files")
    if len(semantic_ids) != len(set(semantic_ids)) or not set(semantic_ids) <= gallery:
        raise ValueError("semantic ranking has duplicate or out-of-gallery files")
    if len(token_ids) != len(set(token_ids)) or not set(token_ids) <= gallery:
        raise ValueError("token ranking has duplicate or out-of-gallery files")
    combined = union_candidates(token_ids[:20], semantic_ids[:20], budget=40)
    if len(combined) > 40 or len(combined) != len(set(combined)):
        raise ValueError("candidate list budget or uniqueness mismatch")
    return combined
