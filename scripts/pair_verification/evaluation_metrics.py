"""Deterministic candidate-pair metrics for the pair verifier."""

import math
from collections import defaultdict

LANGUAGES = ("csharp", "java", "javascript", "python", "typescript")
STRATA = ("EXACT_COPY", "COMMENT_FORMAT", "AST_BOUND_RENAME")
HEADLINE_STRATA = frozenset(("COMMENT_FORMAT", "AST_BOUND_RENAME"))


def _mean(values):
    if not values:
        raise ValueError("metric group is empty")
    return sum(values) / len(values)


def _ap(rows):
    ordered = sorted(rows, key=lambda row: (-row["score"], row["fileId"]))
    relevant_total = sum(row["relevance"] == "POSITIVE" for row in ordered)
    if not relevant_total:
        return 0.0
    found, precision_sum = 0, 0.0
    for index, row in enumerate(ordered, 1):
        if row["relevance"] == "POSITIVE":
            found += 1
            precision_sum += found / index
    return precision_sum / relevant_total


def query_metrics(rows, *, threshold=None):
    included = [row for row in rows if row.get("relevance") != "UNKNOWN"]
    if not included:
        raise ValueError("candidate query has no known labels")
    if len({row["fileId"] for row in included}) != len(included):
        raise ValueError("candidate query has duplicate file ids")
    positives = sum(row["relevance"] == "POSITIVE" for row in included)
    if not positives:
        raise ValueError("candidate query has no reviewed relevant file")
    for row in included:
        if row.get("relevance") not in {"POSITIVE", "REVIEWED_DISTRACTOR"}:
            raise ValueError("candidate query has unsupported relevance")
        if not math.isfinite(float(row["score"])):
            raise ValueError("candidate score is not finite")
    if threshold is None:
        predicted = [False] * len(included)
    else:
        predicted = [float(row["score"]) >= threshold for row in included]
    tp = sum(hit and row["relevance"] == "POSITIVE" for hit, row in zip(predicted, included))
    fp = sum(hit and row["relevance"] != "POSITIVE" for hit, row in zip(predicted, included))
    fn = positives - tp
    precision = tp / (tp + fp) if tp + fp else 0.0
    recall = tp / positives
    f1 = 2 * precision * recall / (precision + recall) if precision + recall else 0.0
    return {"precision": precision, "recall": recall, "f1": f1,
            "auprc": _ap(included), "falseFindings": fp,
            "falseFindingsPerQuery": float(fp), "unknownExcluded": len(rows) - len(included),
            "candidateCount": len(included), "relevantCount": positives}


def _weighted(rows, *, metric, threshold=None):
    by_query = defaultdict(list)
    for row in rows:
        by_query[row["queryId"]].append(row)
    query_rows = []
    seen = set()
    for query_id, candidates in by_query.items():
        first = candidates[0]
        identity = (first["language"], first["stratum"], first["seedGroup"])
        if any((row["language"], row["stratum"], row["seedGroup"]) != identity for row in candidates):
            raise ValueError("candidate query metadata conflicts")
        if query_id in seen:
            raise ValueError("candidate query identity is duplicated")
        seen.add(query_id)
        query_rows.append({**first, "metrics": query_metrics(candidates, threshold=threshold)})
    per_seed = defaultdict(list)
    for row in query_rows:
        per_seed[(row["language"], row["seedGroup"])].append(row["metrics"][metric])
    seed_means = defaultdict(list)
    for (language, _), values in per_seed.items():
        seed_means[language].append(_mean(values))
    per_language = {language: _mean(seed_means[language]) for language in LANGUAGES
                    if seed_means[language]}
    if not per_language:
        raise ValueError("no candidate queries for metric")
    return {"perLanguage": per_language, "macro": _mean(list(per_language.values())),
            "queryCount": len(query_rows), "seedCountPerLanguage": {
                language: len(seed_means[language]) for language in per_language}}


def choose_threshold(rows):
    by_query = defaultdict(list)
    events = defaultdict(lambda: defaultdict(lambda: [0, 0]))
    for row in rows:
        if row.get("relevance") == "UNKNOWN":
            continue
        by_query[row["queryId"]].append(row)
        event = events[float(row["score"])][row["queryId"]]
        event[0 if row["relevance"] == "POSITIVE" else 1] += 1
    if not events:
        raise ValueError("threshold search has no scored known candidates")
    languages = {items[0]["language"] for items in by_query.values()}
    seeds_per_language = defaultdict(set)
    query_count_per_seed = defaultdict(int)
    totals = {}
    for query_id, items in by_query.items():
        first = items[0]
        if any((row["language"], row["seedGroup"], row["stratum"])
               != (first["language"], first["seedGroup"], first["stratum"]) for row in items):
            raise ValueError("candidate query metadata conflicts")
        positives = sum(row["relevance"] == "POSITIVE" for row in items)
        if positives == 0:
            raise ValueError("candidate query has no reviewed relevant file")
        seeds_per_language[first["language"]].add(first["seedGroup"])
        query_count_per_seed[(first["language"], first["seedGroup"])] += 1
        totals[query_id] = {"tp": 0, "fp": 0, "positives": positives, "f1": 0.0}
    weights = {}
    for query_id, items in by_query.items():
        first = items[0]
        weight = 1.0 / (len(languages) * len(seeds_per_language[first["language"]])
                        * query_count_per_seed[(first["language"], first["seedGroup"])])
        weights[query_id] = weight

    def f1(state):
        tp, fp, positives = state["tp"], state["fp"], state["positives"]
        precision = tp / (tp + fp) if tp + fp else 0.0
        recall = tp / positives
        return 2 * precision * recall / (precision + recall) if precision + recall else 0.0

    macro = 0.0
    high = max(events) + 1e-12
    best_threshold, best_macro = high, macro
    for threshold in sorted(events, reverse=True):
        for query_id, (positive_count, negative_count) in events[threshold].items():
            state = totals[query_id]
            old = state["f1"]
            state["tp"] += positive_count
            state["fp"] += negative_count
            state["f1"] = f1(state)
            macro += weights[query_id] * (state["f1"] - old)
        if macro > best_macro:
            best_macro, best_threshold = macro, threshold
    low = min(events) - 1e-12
    if macro > best_macro:
        best_macro, best_threshold = macro, low
    return {"threshold": best_threshold, "macroF1": best_macro, "tieBreak": "higher threshold"}


def aggregate(rows, *, threshold):
    result = {}
    for metric in ("precision", "recall", "f1", "auprc", "falseFindingsPerQuery"):
        result[metric] = _weighted(rows, metric=metric, threshold=threshold)
    result["counts"] = {"queries": len({row["queryId"] for row in rows}),
                         "candidatePairs": sum(row.get("relevance") != "UNKNOWN" for row in rows),
                         "unknownExcluded": sum(row.get("relevance") == "UNKNOWN" for row in rows)}
    return result


def summary(rows, *, threshold):
    metrics = aggregate(rows, threshold=threshold)
    metrics["perLanguage"] = {
        language: aggregate([row for row in rows if row["language"] == language], threshold=threshold)
        for language in LANGUAGES if any(row["language"] == language for row in rows)}
    metrics["perStratum"] = {
        stratum: aggregate([row for row in rows if row["stratum"] == stratum], threshold=threshold)
        for stratum in STRATA if any(row["stratum"] == stratum for row in rows)}
    return metrics


def epoch_selection_key(epoch, rows):
    if type(epoch) is not int or epoch < 1:
        raise ValueError("epoch index is invalid")
    threshold = choose_threshold(rows)
    selected_summary = summary(rows, threshold=threshold["threshold"])
    return (selected_summary["auprc"]["macro"], selected_summary["f1"]["macro"],
            -epoch, epoch)


def select_epoch(epoch_rows):
    if set(epoch_rows) != {1, 2, 3, 4, 5}:
        raise ValueError("epoch selection requires all five epochs")
    return max(epoch_selection_key(epoch, rows) for epoch, rows in epoch_rows.items())[3]


def paired_deltas(primary, control, *, threshold_primary, threshold_control):
    def cases(rows, threshold):
        grouped = defaultdict(list)
        for row in rows:
            grouped[row["queryId"]].append(row)
        return {query: {**group[0], "metrics": query_metrics(group, threshold=threshold)}
                for query, group in grouped.items()}
    left, right = cases(primary, threshold_primary), cases(control, threshold_control)
    common = set(left) & set(right)
    result = {}
    for language in LANGUAGES:
        query_ids = [query for query in common if left[query]["language"] == language
                     and right[query]["language"] == language]
        if not query_ids:
            continue
        result[language] = {"denominatorQueries": len(query_ids), "metrics": {}}
        seed_groups = defaultdict(list)
        for query in query_ids:
            seed_groups[left[query]["seedGroup"]].append(query)
        def weighted(values):
            return _mean([_mean([values(query) for query in queries]) for queries in seed_groups.values()])
        for metric in ("precision", "recall", "f1", "falseFindingsPerQuery"):
            a = weighted(lambda query: left[query]["metrics"][metric])
            b = weighted(lambda query: right[query]["metrics"][metric])
            result[language]["metrics"][metric] = {"primary": a, "control": b, "delta": a - b}
    return result


def cross_fit(rows):
    seeds = sorted({row["seedGroup"] for row in rows})
    if len(seeds) < 2:
        raise ValueError("cross-fit requires at least two seed groups")
    folds = {seed: index % 2 for index, seed in enumerate(seeds)}
    scored = []
    summaries = []
    for fold in (0, 1):
        fit = [row for row in rows if folds[row["seedGroup"]] != fold]
        held = [row for row in rows if folds[row["seedGroup"]] == fold]
        threshold = choose_threshold(fit)
        for row in held:
            scored.append({**row, "crossFitThreshold": threshold["threshold"]})
        summaries.append({"fold": fold, "fitSeedCount": len({row["seedGroup"] for row in fit}),
                          "heldSeedCount": len({row["seedGroup"] for row in held}),
                          "threshold": threshold["threshold"],
                          "heldMetrics": summary(held, threshold=threshold["threshold"])})
    by_query = defaultdict(list)
    for row in scored:
        by_query[row["queryId"]].append(row)
    query_results = []
    for query_rows in by_query.values():
        threshold = query_rows[0]["crossFitThreshold"]
        query_results.append({**query_rows[0], "metrics": query_metrics(query_rows, threshold=threshold)})

    def cross_fit_table(selected):
        by_seed = defaultdict(list)
        for row in selected:
            by_seed[(row["language"], row["seedGroup"])].append(row["metrics"])
        by_language = defaultdict(list)
        for (language, _), values in by_seed.items():
            by_language[language].append({metric: _mean([value[metric] for value in values])
                                          for metric in ("precision", "recall", "f1", "auprc",
                                                         "falseFindingsPerQuery")})
        per_language = {language: {
            metric: _mean([seed[metric] for seed in seeds])
            for metric in ("precision", "recall", "f1", "auprc", "falseFindingsPerQuery")}
            for language, seeds in by_language.items()}
        macros = {metric: _mean([values[metric] for values in per_language.values()])
                  for metric in ("precision", "recall", "f1", "auprc", "falseFindingsPerQuery")}
        return {"macro": macros, "perLanguage": per_language,
                "queryCount": len(selected), "candidatePairs": sum(len(by_query[row["queryId"]])
                                                                       for row in selected)}

    strata = {stratum: cross_fit_table([row for row in query_results if row["stratum"] == stratum])
              for stratum in STRATA if any(row["stratum"] == stratum for row in query_results)}
    return {"splitRule": "sorted canonical seed file identity; alternating folds; every query for a seed stays together",
            "foldAssignment": folds, "folds": summaries,
            "heldOutQueryCount": len(by_query), "overallMetrics": cross_fit_table(query_results),
            "perStratum": strata, "thresholdsByFold": {
                str(row["fold"]): row["threshold"] for row in summaries}}



