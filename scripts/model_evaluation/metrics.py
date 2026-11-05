"""End-to-end evaluation metrics (pure functions, no I/O).

Historical candidate-conditional metrics stay in ``scripts.pair_verification.evaluation_metrics``
and are unchanged. This layer scores what a pipeline emitted against judgments over
the full gallery:

* the recall denominator counts every RELEVANT file in the gallery, retrieved or not;
* UNKNOWN judgments are excluded from every quality denominator and counted;
* a query without a RELEVANT file but with reviewed negatives is a no-match query,
  scored by spurious-alert rate; a query with only UNKNOWN judgments is excluded;
* a pipeline may emit nothing (abstain); coverage is reported next to precision;
* per-query values are averaged within a seed, seeds equally within a language and
  languages equally (language-equal macro), exactly as the validation protocol did.
"""

import math
import random
from collections import defaultdict

from scripts.pair_verification.evaluation_metrics import LANGUAGES as _LANGUAGES

RELEVANT = "RELEVANT"
NOT_RELEVANT = "NOT_RELEVANT"
UNKNOWN = "UNKNOWN"
JUDGMENTS = frozenset((RELEVANT, NOT_RELEVANT, UNKNOWN))
LANGUAGES = _LANGUAGES
DIALECTS = ("ts", "tsx")
QUERY_KEYS = frozenset(("queryId", "language", "family", "seedGroup", "stratum", "dialect",
                        "judgments", "findings", "ranking"))
REQUIRED_QUERY_KEYS = QUERY_KEYS - {"dialect", "ranking"}
RANK_CUTOFFS = (5, 10, 20)
MRR_CUTOFF = 20
RANKING_METRICS = ("recallAt5", "recallAt10", "recallAt20", "mrrAt20", "unionRecall")
POSITIVE, NO_MATCH, UNKNOWN_ONLY = "POSITIVE", "NO_MATCH", "UNKNOWN_ONLY"
VALUE_METRICS = ("precision", "recall", "f1", "falseFindingsPerPositiveQuery")
VALIDATED, UNVALIDATED, FAILED = "VALIDATED", "UNVALIDATED", "FAILED"
_EPSILON = 1e-12
IMPLEMENTED_METRICS = frozenset(("precision", "recall", "f1", "falseFindingsPerPositiveQuery", "spuriousAlertRate",
                                 "abstentionCoverage", "multiMatchRecall"))
IMPLEMENTED_AGGREGATION = {"caseWithinSeed": "mean", "seedWithinLanguage": "mean", "languages": "equal-macro",
                           "micro": "reported"}


def _mean(values):
    if not values:
        raise ValueError("metric group is empty")
    return sum(values) / len(values)


def top_tied_findings(scores):
    """All files tied for the top positive score; nothing when no score is positive."""
    for value in scores.values():
        if not math.isfinite(float(value)):
            raise ValueError("score is not finite")
    top = max(scores.values(), default=0)
    return sorted(file_id for file_id, value in scores.items() if value == top) if top > 0 else []


def normalize_query(record):
    if not isinstance(record, dict) or not REQUIRED_QUERY_KEYS <= set(record) <= QUERY_KEYS:
        raise ValueError("query record keys differ from the contract")
    for key in ("queryId", "family", "seedGroup", "stratum"):
        if not isinstance(record[key], str) or not record[key]:
            raise ValueError("query field is empty: " + key)
    language = record["language"]
    if language not in LANGUAGES:
        raise ValueError("query language is unsupported")
    dialect = record.get("dialect")
    if language == "typescript":
        if dialect not in DIALECTS:
            raise ValueError("typescript queries need a dialect")
    elif dialect is not None:
        raise ValueError("only typescript queries carry a dialect")
    judgments = record["judgments"]
    if not isinstance(judgments, dict) or not judgments:
        raise ValueError("query needs judgments over the full gallery")
    if any(not isinstance(key, str) or not key or value not in JUDGMENTS
           for key, value in judgments.items()):
        raise ValueError("query judgments are invalid")
    findings = record["findings"]
    if not isinstance(findings, list) or len(set(findings)) != len(findings):
        raise ValueError("query findings must be a list without duplicates")
    if any(item not in judgments for item in findings):
        raise ValueError("query finding lies outside the judged gallery")
    ranking = record.get("ranking")
    if ranking is not None and (not isinstance(ranking, list) or len(set(ranking)) != len(ranking)
                                or any(item not in judgments for item in ranking)):
        raise ValueError("query ranking must list distinct judged gallery files")
    return {"queryId": record["queryId"], "language": language, "family": record["family"],
            "seedGroup": record["seedGroup"], "stratum": record["stratum"], "dialect": dialect,
            "judgments": dict(judgments), "findings": list(findings),
            "ranking": None if ranking is None else list(ranking)}


def normalize_queries(records):
    queries = [normalize_query(record) for record in records]
    if len({query["queryId"] for query in queries}) != len(queries):
        raise ValueError("query identities are duplicated")
    families = defaultdict(set)
    seed_families = defaultdict(set)
    for query in queries:
        families[query["family"]].add(query["language"])
        seed_families[(query["language"], query["seedGroup"])].add(query["family"])
    if any(len(languages) != 1 for languages in families.values()):
        raise ValueError("a family spans several languages")
    if any(len(owners) != 1 for owners in seed_families.values()):
        raise ValueError("a seed group spans several families")
    return queries


def query_outcome(query):
    judgments, findings = query["judgments"], set(query["findings"])
    relevant = {item for item, label in judgments.items() if label == RELEVANT}
    negative = {item for item, label in judgments.items() if label == NOT_RELEVANT}
    unknown = {item for item, label in judgments.items() if label == UNKNOWN}
    if relevant:
        kind = POSITIVE
    elif negative:
        kind = NO_MATCH
    else:
        kind = UNKNOWN_ONLY
    tp, fp = len(findings & relevant), len(findings & negative)
    precision = tp / (tp + fp) if tp + fp else 0.0
    recall = tp / len(relevant) if relevant else None
    f1 = 2 * precision * recall / (precision + recall) if recall is not None and precision + recall else 0.0
    return {"kind": kind, "relevant": len(relevant), "tp": tp, "fp": fp,
            "unknownFindings": len(findings & unknown), "unknownJudgments": len(unknown),
            "emitted": bool(findings), "fullyReviewed": not unknown,
            "precision": precision, "recall": recall, "f1": f1,
            "falseFindingsPerPositiveQuery": float(fp)}


def _outcomes(queries):
    return [{**query, "outcome": query_outcome(query)} for query in queries]


def _macro(rows, metric):
    """Mean within seed, equal seeds within language, equal languages; ``(macro, per_language)``."""
    by_seed = defaultdict(list)
    for row in rows:
        by_seed[(row["language"], row["seedGroup"])].append(row["outcome"][metric])
    seeds = defaultdict(list)
    for (language, _), values in by_seed.items():
        seeds[language].append(_mean(values))
    per_language = {language: _mean(seeds[language]) for language in LANGUAGES if seeds[language]}
    macro = _mean(list(per_language.values())) if per_language else None
    return macro, per_language, {language: len(seeds[language]) for language in per_language}


def _ratio(numerator, denominator):
    return numerator / denominator if denominator else None


def _core(queries):
    rows = _outcomes(queries)
    positives = [row for row in rows if row["outcome"]["kind"] == POSITIVE]
    no_match = [row for row in rows if row["outcome"]["kind"] == NO_MATCH]
    reviewed = [row for row in no_match if row["outcome"]["fullyReviewed"]]
    scored = positives + no_match
    macro, seed_counts = {}, {}
    for metric in VALUE_METRICS:
        macro[metric], _, seed_counts = _macro(positives, metric)
    tp = sum(row["outcome"]["tp"] for row in positives)
    fp = sum(row["outcome"]["fp"] for row in positives)
    relevant = sum(row["outcome"]["relevant"] for row in positives)
    micro_precision = tp / (tp + fp) if tp + fp else (0.0 if positives else None)
    micro_recall = _ratio(tp, relevant)
    micro_f1 = None
    if micro_recall is not None:
        micro_f1 = (2 * micro_precision * micro_recall / (micro_precision + micro_recall)
                    if micro_precision + micro_recall else 0.0)
    multi = [row for row in positives if row["outcome"]["relevant"] >= 2]
    alarms = sum(row["outcome"]["fp"] > 0 for row in reviewed)
    answered = sum(row["outcome"]["emitted"] for row in scored)
    answered_positive = sum(row["outcome"]["emitted"] for row in positives)
    return {
        "counts": {"queries": len(rows), "positiveQueries": len(positives),
                   "noMatchQueries": len(no_match), "noMatchFullyReviewed": len(reviewed),
                   "unknownOnlyQueries": len(rows) - len(scored),
                   "families": len({row["family"] for row in rows}),
                   "positiveFamilies": len({row["family"] for row in positives}),
                   "seedsPerLanguage": seed_counts,
                   "unknownJudgments": sum(row["outcome"]["unknownJudgments"] for row in rows),
                   "unknownFindings": sum(row["outcome"]["unknownFindings"] for row in rows)},
        "macro": macro,
        "micro": {"precision": micro_precision, "recall": micro_recall, "f1": micro_f1,
                  "falseFindingsPerPositiveQuery": _ratio(fp, len(positives)),
                  "truePositives": tp, "falsePositives": fp, "relevantFiles": relevant},
        "spuriousAlert": {"rate": _ratio(alarms, len(reviewed)), "alarms": alarms, "queries": len(reviewed)},
        "abstention": {"coverage": _ratio(answered, len(scored)),
                       "coverageOnPositiveQueries": _ratio(answered_positive, len(positives)),
                       "answeredQueries": answered, "scoredQueries": len(scored)},
        "multiMatch": {"queries": len(multi), "recall": _macro(multi, "recall")[0]},
    }


def summarize(queries, *, bootstrap=None):
    """Full report: overall, per language, per stratum and per TypeScript dialect."""
    queries = list(queries)
    summary = _core(queries)
    summary["perLanguage"] = {language: _core([q for q in queries if q["language"] == language])
                              for language in LANGUAGES if any(q["language"] == language for q in queries)}
    summary["perStratum"] = {label: _core([q for q in queries if q["stratum"] == label])
                             for label in sorted({q["stratum"] for q in queries})}
    summary["perDialect"] = {dialect: _core([q for q in queries if q["dialect"] == dialect])
                             for dialect in DIALECTS if any(q["dialect"] == dialect for q in queries)}
    if bootstrap is not None:
        summary["confidenceIntervals"] = bootstrap_ci(queries, resamples=bootstrap["resamples"],
                                                      seed=bootstrap["seed"],
                                                      confidence=bootstrap.get("confidence", 0.95))
    return summary


def headline_statistics(queries):
    core = _core(list(queries))
    return {"macroPrecision": core["macro"]["precision"], "macroRecall": core["macro"]["recall"],
            "macroF1": core["macro"]["f1"],
            "macroFalseFindingsPerPositiveQuery": core["macro"]["falseFindingsPerPositiveQuery"],
            "microPrecision": core["micro"]["precision"], "microRecall": core["micro"]["recall"],
            "microF1": core["micro"]["f1"], "spuriousAlertRate": core["spuriousAlert"]["rate"],
            "multiMatchRecall": core["multiMatch"]["recall"],
            "abstentionCoverage": core["abstention"]["coverage"]}


# --- ranking metrics (independent of any emission rule) ------------------------------------

def query_ranking(query):
    """Recall@k, MRR@20 and union recall from the ordered candidates; UNKNOWN files are removed first."""
    judgments = query["judgments"]
    relevant = {item for item, label in judgments.items() if label == RELEVANT}
    if not relevant:
        return None
    if query["ranking"] is None:
        raise ValueError("query has no ranking")
    order = [item for item in query["ranking"] if judgments[item] != UNKNOWN]
    first = next((index for index, item in enumerate(order, 1) if item in relevant), None)
    result = {"recallAt%d" % k: len(set(order[:k]) & relevant) / len(relevant) for k in RANK_CUTOFFS}
    result["mrrAt20"] = 1 / first if first is not None and first <= MRR_CUTOFF else 0.0
    result["unionRecall"] = len(set(order) & relevant) / len(relevant)
    return result


def _ranking_core(queries):
    rows = [{**query, "outcome": outcome} for query in queries
            if (outcome := query_ranking(query)) is not None]
    macro, per_language = {}, {}
    for name in RANKING_METRICS:
        macro[name], per_language[name], _ = _macro(rows, name)
    return {"positiveQueries": len(rows), "macro": macro, "perLanguageMacro": per_language}


def ranking_statistics(queries):
    return _ranking_core(list(queries))["macro"]


def ranking_summary(queries, *, bootstrap=None):
    queries = list(queries)
    summary = {"overall": _ranking_core(queries),
               "perLanguage": {language: _ranking_core([q for q in queries if q["language"] == language])
                               for language in LANGUAGES if any(q["language"] == language for q in queries)}}
    if bootstrap is not None:
        summary["confidenceIntervals"] = bootstrap_ci(queries, statistics=ranking_statistics, **bootstrap)
    return summary


# --- family-clustered bootstrap -------------------------------------------------------------

def check_registration(metric_section, aggregation_section):
    """The frozen metric and aggregation sections must describe what this module computes."""
    if set(metric_section["judgments"]) != set(JUDGMENTS):
        raise ValueError("frozen judgment labels differ from the implemented labels")
    if not set(metric_section["names"]) <= IMPLEMENTED_METRICS:
        raise ValueError("a frozen metric is not implemented")
    if aggregation_section != IMPLEMENTED_AGGREGATION:
        raise ValueError("frozen aggregation differs from the implemented aggregation")


def percentile(sorted_values, quantile):
    position = quantile * (len(sorted_values) - 1)
    low = math.floor(position)
    high = math.ceil(position)
    return sorted_values[low] + (sorted_values[high] - sorted_values[low]) * (position - low)


class UndefinedResample(ValueError):
    """A resample that lost the positive queries of a language: counted as undefined, never averaged."""


def _positive_languages(rows):
    return {row["language"] for row in rows if query_outcome(row)["kind"] == POSITIVE}


def _family_groups(queries):
    """Families grouped by language and kind: ``{(language, POSITIVE | NO_MATCH): [family queries, ...]}``.

    A family is POSITIVE when any of its queries is a positive query and NO_MATCH otherwise, so a draw keeps
    every language's positive-family count.
    """
    families = defaultdict(lambda: defaultdict(list))
    for query in queries:
        families[query["language"]][query["family"]].append(query)
    groups = defaultdict(list)
    for language in sorted(families):
        for family in sorted(families[language]):
            members = families[language][family]
            kind = POSITIVE if any(query_outcome(item)["kind"] == POSITIVE for item in members) else NO_MATCH
            groups[(language, kind)].append(members)
    return dict(sorted(groups.items()))


def family_count(groups):
    return sum(len(families) for families in groups.values())


def draw_picks(groups, rng):
    """Whole families drawn with replacement within each language (stratified resampling)."""
    return {language: [rng.randrange(len(families)) for _ in families] for language, families in groups.items()}


def _clone(picked_groups):
    """Concatenate drawn families; every draw keeps its own seeds, queries and family label."""
    sample = []
    for draw, group in enumerate(picked_groups):
        for query in group:
            sample.append({**query, "queryId": "%s#%d" % (query["queryId"], draw),
                           "seedGroup": "%s#%d" % (query["seedGroup"], draw),
                           "family": "%s#%d" % (query["family"], draw)})
    return sample


def resample_families(groups, rng, picks=None):
    """Draw whole families with replacement, separately inside each (language, positive or no-match) group."""
    picks = draw_picks(groups, rng) if picks is None else picks
    return _clone([groups[key][index] for key in groups for index in picks[key]])


def _resampled(groups, rng, population, picks=None):
    sample = resample_families(groups, rng, picks)
    if _positive_languages(sample) != population:
        raise UndefinedResample("resample lost the positive queries of a language")
    return sample


def _interval(values, confidence):
    defined = sorted(value for value in values if value is not None)
    if not defined:
        return {"lower": None, "upper": None, "resamples": 0, "undefinedResamples": len(values)}
    tail = (1 - confidence) / 2
    return {"lower": percentile(defined, tail), "upper": percentile(defined, 1 - tail),
            "resamples": len(defined), "undefinedResamples": len(values) - len(defined)}


def _check_bootstrap(resamples, seed, confidence):
    if type(resamples) is not int or resamples < 1 or type(seed) is not int:
        raise ValueError("bootstrap needs a positive resample count and an integer seed")
    if not 0 < confidence < 1:
        raise ValueError("bootstrap confidence is invalid")


def bootstrap_ci(queries, *, resamples, seed, confidence=0.95, statistics=headline_statistics):
    _check_bootstrap(resamples, seed, confidence)
    groups = _family_groups(list(queries))
    if not groups:
        raise ValueError("bootstrap needs queries")
    rng = random.Random(seed)
    population = _positive_languages(queries)
    point = statistics(list(queries))
    draws = []
    for _ in range(resamples):
        try:
            draws.append(statistics(_resampled(groups, rng, population)))
        except UndefinedResample:
            draws.append({name: None for name in point})
    return {"unit": "family", "resamples": resamples, "seed": seed, "confidence": confidence,
            "families": family_count(groups),
            "point": point,
            "statistics": {name: _interval([draw[name] for draw in draws], confidence) for name in point}}


def paired_bootstrap_delta(primary, baseline, *, resamples, seed, confidence=0.95,
                           statistics=headline_statistics):
    """Primary minus baseline on the same queries, resampling whole families for both."""
    _check_bootstrap(resamples, seed, confidence)
    left = {query["queryId"]: query for query in primary}
    right = {query["queryId"]: query for query in baseline}
    if set(left) != set(right) or len(left) != len(list(primary)):
        raise ValueError("paired pipelines must score the same queries")
    for query_id, query in left.items():
        other = right[query_id]
        if any(query[key] != other[key] for key in ("language", "family", "seedGroup", "stratum",
                                                     "dialect", "judgments")):
            raise ValueError("paired pipelines disagree on query metadata or judgments")
    left_groups = _family_groups(list(primary))
    right_groups = {key: [[right[query["queryId"]] for query in group] for group in families]
                    for key, families in left_groups.items()}
    population = _positive_languages(primary)
    rng = random.Random(seed)
    point_a, point_b = statistics(list(primary)), statistics(list(baseline))
    deltas = []
    for _ in range(resamples):
        picks = draw_picks(left_groups, rng)
        try:
            a = statistics(_resampled(left_groups, rng, population, picks))
            b = statistics(_resampled(right_groups, rng, population, picks))
        except UndefinedResample:
            deltas.append({name: None for name in point_a})
            continue
        deltas.append({name: None if a[name] is None or b[name] is None else a[name] - b[name]
                       for name in a})
    return {"unit": "family", "resamples": resamples, "seed": seed, "confidence": confidence,
            "families": family_count(left_groups),
            "point": {name: None if point_a[name] is None or point_b[name] is None
                      else point_a[name] - point_b[name] for name in point_a},
            "statistics": {name: _interval([draw[name] for draw in deltas], confidence)
                           for name in point_a}}


# --- claim gates -----------------------------------------------------------------------------

def _ge(value, bound):
    return value >= bound - _EPSILON


def _le(value, bound):
    return value <= bound + _EPSILON


def _status(failed, unevaluated):
    if failed:
        return FAILED
    return UNVALIDATED if unevaluated else VALIDATED


def _adequate(core, minimum):
    counts = core["counts"]
    return (counts["positiveQueries"] >= minimum["positiveQueries"]
            and counts["positiveFamilies"] >= minimum["families"])


def _group_result(core, gates, minimum):
    if not _adequate(core, minimum):
        return {"status": UNVALIDATED, "adequatelySampled": False, "failedGates": [],
                "unevaluatedGates": ["minimumCounts"]}
    failed, unevaluated = [], []
    macro = core["macro"]
    if not _ge(macro["precision"], gates["macroPrecisionMin"]):
        failed.append("macroPrecisionMin")
    if not _ge(macro["f1"], gates["macroF1Min"]):
        failed.append("macroF1Min")
    if not _le(macro["falseFindingsPerPositiveQuery"], gates["falseFindingsPerPositiveQueryMax"]):
        failed.append("falseFindingsPerPositiveQueryMax")
    alarm = core["spuriousAlert"]
    if alarm["queries"] < minimum["noMatchQueries"]:
        unevaluated.append("spuriousAlertRateMax")
    elif not _le(alarm["rate"], gates["spuriousAlertRateMax"]):
        failed.append("spuriousAlertRateMax")
    return {"status": _status(failed, unevaluated), "adequatelySampled": True,
            "failedGates": failed, "unevaluatedGates": unevaluated}


def _overall(summary):
    """Overall values over every supported language: the same population as the bootstrap point estimate."""
    macros = {metric: _mean([summary["perLanguage"][language]["macro"][metric] for language in LANGUAGES])
              for metric in VALUE_METRICS}
    alarms = [summary["perLanguage"][language]["spuriousAlert"] for language in LANGUAGES]
    return macros, sum(row["alarms"] for row in alarms), sum(row["queries"] for row in alarms)


def _detector_gates(macros, alarms, reviewed, gates, minimum, group_results):
    failed, unevaluated = [], []
    if not _ge(macros["precision"], gates["macroPrecisionMin"]):
        failed.append("macroPrecisionMin")
    if not _ge(macros["f1"], gates["macroF1Min"]):
        failed.append("macroF1Min")
    if not _le(macros["falseFindingsPerPositiveQuery"], gates["falseFindingsPerPositiveQueryMax"]):
        failed.append("falseFindingsPerPositiveQueryMax")
    if reviewed < minimum["noMatchQueries"]:
        unevaluated.append("spuriousAlertRateMax")
    elif not _le(alarms / reviewed, gates["spuriousAlertRateMax"]):
        failed.append("spuriousAlertRateMax")
    for name, result in group_results.items():
        if result["status"] == FAILED:
            failed.append("languageGate:" + name)
        unevaluated.extend(gate + ":" + name for gate in result["unevaluatedGates"])
    return failed, list(dict.fromkeys(unevaluated))


def stronger_baseline(baselines):
    """The baseline with the highest overall macro F1 (first by name on a tie)."""
    scores = {name: _mean([core["macro"]["f1"] for core in summary["perLanguage"].values()])
              for name, summary in sorted(baselines.items())}
    return max(scores, key=lambda name: scores[name]), scores


def _replacement(summary, macros, failed, unevaluated, baselines, paired_deltas, required, gates):
    """Replacement outcome: detector evidence and failures carry forward; stronger baseline decides."""
    r_failed, r_unevaluated = list(failed), list(unevaluated)
    if baselines is None or set(baselines) != set(required) or any(
            language not in baseline["perLanguage"] for baseline in baselines.values() for language in LANGUAGES):
        r_unevaluated.append("baselineSummaries")
        return {"status": _status(r_failed, r_unevaluated), "failedGates": r_failed,
                "unevaluatedGates": r_unevaluated, "comparedAgainst": None}
    strongest, baseline_scores = stronger_baseline(baselines)
    reference = baselines[strongest]["perLanguage"]
    if not _ge(macros["f1"], max(gates["replacementF1Floor"], baseline_scores[strongest]
                                  + gates["replacementF1Margin"])):
        r_failed.append("replacementF1Floor")
    if not _ge(macros["precision"], gates["replacementPrecisionMin"]):
        r_failed.append("replacementPrecisionMin")
    if not _ge(macros["recall"], gates["replacementRecallMin"]):
        r_failed.append("replacementRecallMin")
    for language in LANGUAGES:
        loss = reference[language]["macro"]["f1"] - summary["perLanguage"][language]["macro"]["f1"]
        if not _le(loss, gates["replacementMaxLanguageF1Loss"]):
            r_failed.append("replacementMaxLanguageF1Loss:" + language)
    delta = None if paired_deltas is None else paired_deltas.get(strongest)
    interval = None if delta is None else delta["statistics"]["macroF1"]
    lower = None if interval is None else interval["lower"]
    if lower is None or interval.get("undefinedResamples", 0) or lower <= gates["replacementDeltaLowerBoundAbove"]:
        r_unevaluated.append("pairedDeltaLowerBound")
    return {"status": _status(r_failed, r_unevaluated), "failedGates": r_failed,
            "unevaluatedGates": r_unevaluated, "comparedAgainst": strongest,
            "baselineMacroF1": baseline_scores}


def evaluate_claim_gates(summary, *, gates, minimum_counts, baselines=None, paired_deltas=None,
                         required_baselines=()):
    """Per language and overall VALIDATED / UNVALIDATED / FAILED; a failure is never softened.

    An overall claim needs every supported language group, both TypeScript dialects (tsx and ts) and every
    frozen stratum adequately sampled.
    """
    per_group = {language: _group_result(core, gates, minimum_counts["perLanguage"])
                 for language, core in summary["perLanguage"].items()}
    dialect_minimum = {"tsx": minimum_counts["tsxDialect"], "ts": minimum_counts["perLanguage"]}
    for dialect in DIALECTS:
        name = "typescript:" + dialect
        if dialect in summary["perDialect"]:
            per_group[name] = _group_result(summary["perDialect"][dialect], gates, dialect_minimum[dialect])
        else:
            per_group[name] = {"status": UNVALIDATED, "adequatelySampled": False, "failedGates": [],
                               "unevaluatedGates": ["minimumCounts"]}
    adequate = [language for language in summary["perLanguage"]
                if per_group[language]["adequatelySampled"]]
    strata = {label: {"queries": core["counts"]["positiveQueries"] + core["counts"]["noMatchQueries"],
                      "adequatelySampled": core["counts"]["positiveQueries"] + core["counts"]["noMatchQueries"]
                      >= minimum_counts["perStratum"].get(label, math.inf)}
              for label, core in summary["perStratum"].items()}
    strata.update({label: {"queries": 0, "adequatelySampled": False}
                   for label in minimum_counts["perStratum"] if label not in strata})
    required_groups = (*LANGUAGES, "typescript:tsx", "typescript:ts")
    inadequate = [name for name in required_groups if not per_group.get(name, {}).get("adequatelySampled")]
    inadequate += ["stratum:" + label for label, row in strata.items() if not row["adequatelySampled"]]
    group_failures = ["languageGate:" + name for name, result in per_group.items() if result["status"] == FAILED]
    if inadequate:
        unevaluated = ["minimumCounts:" + name for name in inadequate]
        unevaluated.extend(gate + ":" + name for name, result in per_group.items()
                           for gate in result["unevaluatedGates"])
        base = {"status": _status(group_failures, True), "failedGates": group_failures,
                "unevaluatedGates": list(dict.fromkeys(unevaluated))}
        return {"perGroup": per_group, "strata": strata, "adequateLanguages": adequate,
                "overall": {"detector": dict(base), "replacement": dict(base), "product": dict(base)}}
    macros, alarms, reviewed = _overall(summary)
    failed, unevaluated = _detector_gates(macros, alarms, reviewed, gates, minimum_counts["perLanguage"], per_group)
    detector = {"status": _status(failed, unevaluated), "failedGates": failed, "unevaluatedGates": unevaluated}
    replacement = _replacement(summary, macros, failed, unevaluated, baselines, paired_deltas,
                               required_baselines, gates)

    p_failed, p_unevaluated = list(failed), list(unevaluated)
    intervals = (summary.get("confidenceIntervals") or {}).get("statistics")
    for name, label in (("macroPrecision", "productPrecisionLowerBound"), ("macroF1", "productF1LowerBound")):
        lower = None if intervals is None else intervals[name]["lower"]
        if lower is None or intervals[name]["undefinedResamples"]:
            p_unevaluated.append(label)
        elif not _ge(lower, gates["productLowerBoundMin"]):
            p_failed.append(label)
    product = {"status": _status(p_failed, p_unevaluated), "failedGates": p_failed,
               "unevaluatedGates": p_unevaluated}
    return {"perGroup": per_group, "strata": strata, "adequateLanguages": adequate,
            "overall": {"detector": detector, "replacement": replacement, "product": product}}
