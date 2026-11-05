import random
import unittest
from collections import Counter

from scripts.model_evaluation import manifest, metrics
from scripts.model_evaluation.tests._fixtures import N, R, minimum_counts, no_match_set, positive_set, query

GATES = manifest.CLAIM_GATES


def dataset(*groups):
    return metrics.normalize_queries([row for group in groups for row in group])


def family_dataset():
    rows = []
    for family in range(6):
        size = 1 + family % 3
        for index in range(size):
            hit = (family + index) % 2 == 0
            rows.append(query("q-%d-%d" % (family, index), family="fam%d" % family, seed="seed%d" % family,
                              judgments={"r": R, "n": N}, findings=["r"] if hit else ["n"]))
    return metrics.normalize_queries(rows)


class BootstrapTests(unittest.TestCase):
    def test_same_seed_gives_identical_interval_and_other_seed_differs(self):
        data = family_dataset()
        first = metrics.bootstrap_ci(data, resamples=200, seed=11)
        again = metrics.bootstrap_ci(data, resamples=200, seed=11)
        other = metrics.bootstrap_ci(data, resamples=200, seed=12)
        self.assertEqual(first, again)
        self.assertNotEqual(first["statistics"], other["statistics"])
        self.assertEqual(first["unit"], "family")
        self.assertEqual(first["families"], 6)

    def test_resampling_keeps_whole_families_never_single_rows(self):
        data = family_dataset()
        sizes = Counter(query["family"] for query in data)
        groups = metrics._family_groups(data)
        for seed in range(40):
            sample = metrics.resample_families(groups, random.Random(seed))
            drawn = Counter(query["family"].split("#")[0] for query in sample)
            for family, count in drawn.items():
                self.assertEqual(count % sizes[family], 0, (seed, family, count))
            self.assertEqual(len({query["queryId"] for query in sample}), len(sample))

    def test_interval_contains_point_estimate_for_stable_data(self):
        data = dataset(positive_set("python", 12, prefix="a"))
        result = metrics.bootstrap_ci(data, resamples=50, seed=3)
        interval = result["statistics"]["macroPrecision"]
        self.assertEqual((interval["lower"], interval["upper"]), (1.0, 1.0))
        self.assertEqual(result["point"]["macroPrecision"], 1.0)

    def test_paired_delta_matches_point_difference_and_is_deterministic(self):
        primary = dataset(positive_set("python", 8, prefix="a"))
        baseline = dataset(positive_set("python", 8, prefix="a", false_finding=True))
        result = metrics.paired_bootstrap_delta(primary, baseline, resamples=60, seed=5)
        self.assertAlmostEqual(result["point"]["macroPrecision"], 0.5)
        self.assertEqual(result, metrics.paired_bootstrap_delta(primary, baseline, resamples=60, seed=5))
        self.assertGreater(result["statistics"]["macroPrecision"]["lower"], 0)

    def test_paired_delta_refuses_different_queries_or_judgments(self):
        primary = dataset(positive_set("python", 3, prefix="a"))
        with self.assertRaises(ValueError):
            metrics.paired_bootstrap_delta(primary, primary[:2], resamples=5, seed=1)
        changed = [{**row, "judgments": {**row["judgments"], "n": R}} for row in primary]
        with self.assertRaises(ValueError):
            metrics.paired_bootstrap_delta(primary, changed, resamples=5, seed=1)

    def test_invalid_parameters_are_rejected(self):
        data = family_dataset()
        for kwargs in ({"resamples": 0, "seed": 1}, {"resamples": 5, "seed": "x"},
                       {"resamples": 5, "seed": 1, "confidence": 1.5}):
            with self.subTest(kwargs=kwargs), self.assertRaises(ValueError):
                metrics.bootstrap_ci(data, **kwargs)


SUPPORTED = metrics.LANGUAGES
BASELINES = tuple(manifest.REPLACEMENT_BASELINES)


def all_languages(prefix="c", *, false_finding=(), alarms=(), dialects=("ts", "tsx")):
    """Ten positive and five no-match queries per language; TypeScript is split into the given dialects."""
    rows = []
    for language in SUPPORTED:
        for dialect in (dialects if language == "typescript" else (None,)):
            tag = prefix if dialect is None else "%s-%s" % (prefix, dialect)
            rows += positive_set(language, 10, prefix=tag, false_finding=language in false_finding, dialect=dialect)
            rows += no_match_set(language, 5, prefix=tag, alarms=1 if language in alarms else 0, dialect=dialect)
    return dataset(rows)


def validated_languages(prefix="c"):
    return dataset(positive_set("python", 10, prefix=prefix), no_match_set("python", 5, prefix=prefix))


def languages_with_no_match_counts(counts, prefix="c", *, false_finding=()):
    rows = []
    for language in SUPPORTED:
        dialect = "tsx" if language == "typescript" else None
        rows += positive_set(language, 10, prefix=prefix, false_finding=language in false_finding, dialect=dialect)
        rows += no_match_set(language, counts.get(language, 5), prefix=prefix, dialect=dialect)
    return dataset(rows)


def tsx_shortfall_population(prefix="c", *, false_finding=()):
    rows = []
    for language in SUPPORTED:
        if language == "typescript":
            rows += positive_set(language, 10, prefix=prefix + "-ts", false_finding=language in false_finding, dialect="ts")
            rows += no_match_set(language, 5, prefix=prefix + "-ts", dialect="ts")
            rows += positive_set(language, 10, prefix=prefix + "-tsx", false_finding=language in false_finding, dialect="tsx")
            rows += no_match_set(language, 0, prefix=prefix + "-tsx", dialect="tsx")
        else:
            rows += positive_set(language, 10, prefix=prefix, false_finding=language in false_finding)
            rows += no_match_set(language, 5, prefix=prefix)
    return dataset(rows)


RESAMPLES = {"resamples": 400, "seed": 1}


class ClaimGateTests(unittest.TestCase):
    def evaluate(self, data, *, baselines=None, bootstrap=None, deltas=True):
        summary = metrics.summarize(data, bootstrap=bootstrap)
        summaries = None if baselines is None else {name: metrics.summarize(rows) for name, rows in baselines.items()}
        paired = None
        if baselines is not None and deltas is True:
            paired = {name: metrics.paired_bootstrap_delta(data, rows, **RESAMPLES) for name, rows in baselines.items()}
        elif deltas not in (True, None):
            paired = deltas
        return metrics.evaluate_claim_gates(summary, gates=GATES, minimum_counts=minimum_counts(),
                                            baselines=summaries, paired_deltas=paired, required_baselines=BASELINES)

    def test_every_supported_group_adequate_and_clean_validates_overall(self):
        result = self.evaluate(all_languages(), bootstrap={"resamples": 40, "seed": 1})
        for name in (*SUPPORTED, "typescript:tsx"):
            self.assertEqual(result["perGroup"][name]["status"], metrics.VALIDATED, name)
        self.assertEqual(result["overall"]["detector"]["status"], metrics.VALIDATED)
        self.assertEqual(result["overall"]["product"]["status"], metrics.VALIDATED)

    def test_frozen_stratum_without_queries_makes_the_overall_claim_unvalidated(self):
        minimum = minimum_counts()
        minimum["perStratum"]["near miss"] = 5
        summary = metrics.summarize(all_languages())
        result = metrics.evaluate_claim_gates(summary, gates=GATES, minimum_counts=minimum, baselines=None,
                                              paired_deltas=None, required_baselines=BASELINES)
        self.assertFalse(result["strata"]["near miss"]["adequatelySampled"])
        for claim in ("detector", "replacement", "product"):
            self.assertEqual(result["overall"][claim]["status"], metrics.UNVALIDATED, claim)
        self.assertIn("minimumCounts:stratum:near miss", result["overall"]["detector"]["unevaluatedGates"])

    def test_tsx_only_typescript_positives_leave_the_ts_dialect_unvalidated(self):
        result = self.evaluate(all_languages(dialects=("tsx",)))
        self.assertEqual(result["perGroup"]["typescript:tsx"]["status"], metrics.VALIDATED)
        self.assertEqual(result["perGroup"]["typescript:ts"]["status"], metrics.UNVALIDATED)
        self.assertEqual(result["overall"]["detector"]["status"], metrics.UNVALIDATED)
        self.assertIn("minimumCounts:typescript:ts", result["overall"]["detector"]["unevaluatedGates"])

    def test_one_adequate_language_alone_never_validates_overall(self):
        result = self.evaluate(validated_languages(), bootstrap={"resamples": 40, "seed": 1})
        self.assertEqual(result["perGroup"]["python"]["status"], metrics.VALIDATED)
        for claim in ("detector", "replacement", "product"):
            outcome = result["overall"][claim]
            self.assertEqual(outcome["status"], metrics.UNVALIDATED, claim)
            self.assertIn("minimumCounts:java", outcome["unevaluatedGates"])
            self.assertIn("minimumCounts:typescript:tsx", outcome["unevaluatedGates"])

    def test_a_missing_tsx_dialect_group_keeps_overall_unvalidated(self):
        rows = []
        for language in SUPPORTED:
            dialect = "ts" if language == "typescript" else None
            rows += positive_set(language, 10, prefix="c", dialect=dialect)
            rows += no_match_set(language, 5, prefix="c", dialect=dialect)
        result = self.evaluate(dataset(rows))
        self.assertEqual(result["overall"]["detector"]["status"], metrics.UNVALIDATED)
        self.assertEqual(result["overall"]["detector"]["unevaluatedGates"], ["minimumCounts:typescript:tsx"])

    def test_overall_and_bootstrap_use_the_same_population(self):
        data = all_languages(false_finding=("java",))
        summary = metrics.summarize(data, bootstrap={"resamples": 20, "seed": 3})
        overall = metrics._overall(summary)[0]
        self.assertAlmostEqual(overall["precision"], summary["confidenceIntervals"]["point"]["macroPrecision"])
        self.assertAlmostEqual(overall["f1"], summary["confidenceIntervals"]["point"]["macroF1"])

    def test_product_claim_without_intervals_is_unvalidated_not_validated(self):
        product = self.evaluate(all_languages())["overall"]["product"]
        self.assertEqual(product["status"], metrics.UNVALIDATED)
        self.assertIn("productPrecisionLowerBound", product["unevaluatedGates"])

    def test_low_counts_are_unvalidated_for_language_and_overall(self):
        result = self.evaluate(dataset(positive_set("python", 9, prefix="c"), no_match_set("python", 5, prefix="c")))
        self.assertEqual(result["perGroup"]["python"]["status"], metrics.UNVALIDATED)
        self.assertEqual(result["perGroup"]["python"]["unevaluatedGates"], ["minimumCounts"])
        self.assertEqual(result["overall"]["detector"]["status"], metrics.UNVALIDATED)
        self.assertEqual(result["adequateLanguages"], [])

    def test_precision_below_gate_fails_and_names_the_gates(self):
        result = self.evaluate(all_languages(false_finding=SUPPORTED))
        group = result["perGroup"]["python"]
        self.assertEqual(group["status"], metrics.FAILED)
        for gate in ("macroPrecisionMin", "macroF1Min", "falseFindingsPerPositiveQueryMax"):
            self.assertIn(gate, group["failedGates"])
            self.assertIn(gate, result["overall"]["detector"]["failedGates"])
        self.assertEqual(result["overall"]["detector"]["status"], metrics.FAILED)
        self.assertEqual(result["overall"]["product"]["status"], metrics.FAILED)

    def test_false_alarm_rate_over_gate_fails(self):
        result = self.evaluate(all_languages(alarms=SUPPORTED))
        self.assertIn("spuriousAlertRateMax", result["perGroup"]["python"]["failedGates"])
        self.assertIn("spuriousAlertRateMax", result["overall"]["detector"]["failedGates"])

    def test_missing_no_match_evidence_leaves_the_language_unvalidated(self):
        result = self.evaluate(dataset(positive_set("python", 10, prefix="c"), no_match_set("python", 3, prefix="c")))
        group = result["perGroup"]["python"]
        self.assertEqual(group["status"], metrics.UNVALIDATED)
        self.assertEqual(group["unevaluatedGates"], ["spuriousAlertRateMax"])

    def test_zero_no_match_evidence_cannot_validate_any_overall_claim(self):
        candidate = languages_with_no_match_counts({language: 0 for language in SUPPORTED})
        weak = languages_with_no_match_counts({language: 0 for language in SUPPORTED}, false_finding=SUPPORTED)
        delta = metrics.paired_bootstrap_delta(candidate, weak, **RESAMPLES)
        self.assertGreater(delta["statistics"]["macroF1"]["lower"], 0)
        result = self.evaluate(candidate, baselines={"PRODUCT": weak, "TOKEN_RANK_CUT": weak},
                               bootstrap={"resamples": 40, "seed": 1})
        self.assertEqual(result["overall"]["replacement"]["status"], metrics.UNVALIDATED)
        for claim in ("detector", "replacement", "product"):
            self.assertNotEqual(result["overall"][claim]["status"], metrics.VALIDATED, claim)

    def test_one_language_no_match_shortfall_is_named_despite_adequate_aggregate(self):
        counts = {language: 5 for language in SUPPORTED}
        counts["python"] = 0
        candidate = languages_with_no_match_counts(counts)
        weak = languages_with_no_match_counts(counts, false_finding=SUPPORTED)
        result = self.evaluate(candidate, baselines={"PRODUCT": weak, "TOKEN_RANK_CUT": weak},
                               bootstrap={"resamples": 40, "seed": 1})
        self.assertEqual(result["perGroup"]["python"]["status"], metrics.UNVALIDATED)
        for claim in ("detector", "replacement", "product"):
            outcome = result["overall"][claim]
            self.assertNotEqual(outcome["status"], metrics.VALIDATED, claim)
            self.assertIn("spuriousAlertRateMax:python", outcome["unevaluatedGates"], claim)

    def test_tsx_no_match_shortfall_is_named_despite_adequate_typescript_aggregate(self):
        candidate = tsx_shortfall_population()
        weak = tsx_shortfall_population(false_finding=SUPPORTED)
        result = self.evaluate(candidate, baselines={"PRODUCT": weak, "TOKEN_RANK_CUT": weak},
                               bootstrap={"resamples": 40, "seed": 1})
        self.assertEqual(result["perGroup"]["typescript:tsx"]["status"], metrics.UNVALIDATED)
        self.assertEqual(result["perGroup"]["typescript"]["status"], metrics.VALIDATED)
        for claim in ("detector", "replacement", "product"):
            outcome = result["overall"][claim]
            self.assertNotEqual(outcome["status"], metrics.VALIDATED, claim)
            self.assertIn("spuriousAlertRateMax:typescript:tsx", outcome["unevaluatedGates"], claim)

    def test_failed_gate_precedence_survives_no_match_unevaluated_groups(self):
        counts = {language: 5 for language in SUPPORTED}
        counts["python"] = 0
        candidate = languages_with_no_match_counts(counts, false_finding=("java",))
        weak = languages_with_no_match_counts(counts, false_finding=SUPPORTED)
        result = self.evaluate(candidate, baselines={"PRODUCT": weak, "TOKEN_RANK_CUT": weak},
                               bootstrap={"resamples": 40, "seed": 1})
        self.assertEqual(result["perGroup"]["python"]["status"], metrics.UNVALIDATED)
        for claim in ("detector", "replacement", "product"):
            self.assertEqual(result["overall"][claim]["status"], metrics.FAILED, claim)

    def test_a_failed_language_makes_the_overall_detector_fail(self):
        result = self.evaluate(all_languages(false_finding=("java",)))
        self.assertEqual(result["perGroup"]["python"]["status"], metrics.VALIDATED)
        self.assertEqual(result["perGroup"]["java"]["status"], metrics.FAILED)
        self.assertIn("languageGate:java", result["overall"]["detector"]["failedGates"])

    def test_a_failed_group_stays_failed_even_when_others_are_inadequate(self):
        data = dataset(positive_set("java", 10, prefix="c", false_finding=True), no_match_set("java", 5, prefix="c"))
        overall = self.evaluate(data)["overall"]["detector"]
        self.assertEqual(overall["status"], metrics.FAILED)
        self.assertIn("languageGate:java", overall["failedGates"])

    def test_replacement_is_gated_against_the_stronger_baseline_with_a_positive_paired_lower_bound(self):
        candidate = all_languages()
        weak = all_languages(false_finding=SUPPORTED)
        replacement = self.evaluate(candidate, baselines={"PRODUCT": weak, "TOKEN_RANK_CUT": weak})["overall"][
            "replacement"]
        self.assertEqual(replacement["status"], metrics.VALIDATED, replacement)
        # one baseline is as good as the candidate: the stronger baseline decides and the margin fails
        replacement = self.evaluate(candidate, baselines={"PRODUCT": weak, "TOKEN_RANK_CUT": all_languages()})[
            "overall"]["replacement"]
        self.assertEqual(replacement["comparedAgainst"], "TOKEN_RANK_CUT")
        self.assertEqual(replacement["status"], metrics.FAILED)
        self.assertIn("replacementF1Floor", replacement["failedGates"])

    def test_both_pre_registered_baselines_are_required(self):
        candidate = all_languages()
        for given in ({"PRODUCT": candidate}, {"TOKEN_RANK_CUT": candidate},
                      {"PRODUCT": candidate, "OTHER": candidate}):
            replacement = self.evaluate(candidate, baselines=given, deltas=None)["overall"]["replacement"]
            self.assertEqual(replacement["status"], metrics.UNVALIDATED, given)
            self.assertIn("baselineSummaries", replacement["unevaluatedGates"])
        self.assertEqual(self.evaluate(candidate)["overall"]["replacement"]["status"], metrics.UNVALIDATED)

    def test_a_non_positive_paired_lower_bound_is_inconclusive_not_validated(self):
        candidate = all_languages()
        weak = all_languages(false_finding=SUPPORTED)
        inconclusive = {name: {"statistics": {"macroF1": {"lower": 0.0}}} for name in BASELINES}
        replacement = self.evaluate(candidate, baselines={"PRODUCT": weak, "TOKEN_RANK_CUT": weak},
                                    deltas=inconclusive)["overall"]["replacement"]
        self.assertEqual(replacement["status"], metrics.UNVALIDATED)
        self.assertEqual(replacement["unevaluatedGates"], ["pairedDeltaLowerBound"])
        positive = {name: {"statistics": {"macroF1": {"lower": 0.01}}} for name in BASELINES}
        self.assertEqual(self.evaluate(candidate, baselines={"PRODUCT": weak, "TOKEN_RANK_CUT": weak},
                                       deltas=positive)["overall"]["replacement"]["status"], metrics.VALIDATED)

    def test_a_small_real_improvement_has_a_lower_bound_that_does_not_clear_zero(self):
        candidate = all_languages()
        flawed = {"c-python-00", "c-python-01", "c-java-00"}
        slightly_worse = [{**row, "findings": ["r", "n"]} if row["queryId"] in flawed else row for row in candidate]
        replacement = self.evaluate(candidate, baselines={"PRODUCT": slightly_worse,
                                                          "TOKEN_RANK_CUT": slightly_worse})["overall"]["replacement"]
        self.assertEqual(replacement["status"], metrics.UNVALIDATED)
        self.assertIn("pairedDeltaLowerBound", replacement["unevaluatedGates"])

    def test_detector_failures_fold_into_the_replacement_outcome(self):
        weak = all_languages(false_finding=SUPPORTED)
        alarming = self.evaluate(all_languages(alarms=SUPPORTED), baselines={"PRODUCT": weak, "TOKEN_RANK_CUT": weak})
        self.assertEqual(alarming["overall"]["replacement"]["status"], metrics.FAILED)
        self.assertIn("spuriousAlertRateMax", alarming["overall"]["replacement"]["failedGates"])
        noisy = self.evaluate(all_languages(false_finding=SUPPORTED), baselines={"PRODUCT": weak, "TOKEN_RANK_CUT": weak})
        self.assertIn("falseFindingsPerPositiveQueryMax", noisy["overall"]["replacement"]["failedGates"])

    def test_language_loss_gate_names_the_language(self):
        better = all_languages()
        replacement = self.evaluate(all_languages(false_finding=("java",)),
                                    baselines={"PRODUCT": better, "TOKEN_RANK_CUT": better})["overall"]["replacement"]
        self.assertIn("replacementMaxLanguageF1Loss:java", replacement["failedGates"])
        self.assertEqual(replacement["status"], metrics.FAILED)

    def test_tsx_dialect_gets_its_own_evaluation(self):
        tsx = (positive_set("typescript", 10, prefix="c", dialect="tsx")
               + no_match_set("typescript", 5, prefix="c", dialect="tsx"))
        result = self.evaluate(dataset(tsx))
        self.assertEqual(result["perGroup"]["typescript:tsx"]["status"], metrics.VALIDATED)

    def test_strata_adequacy_is_reported_with_denominators(self):
        result = self.evaluate(validated_languages())
        self.assertEqual(result["strata"]["exact copy"], {"queries": 10, "adequatelySampled": True})
        self.assertEqual(result["strata"]["hard negative"], {"queries": 5, "adequatelySampled": True})


class SamplingStructureTests(unittest.TestCase):
    def test_a_seed_group_must_belong_to_one_family(self):
        rows = [query("a", family="f1", seed="s1", judgments={"r": R}),
                query("b", family="f2", seed="s1", judgments={"r": R})]
        with self.assertRaises(ValueError):
            metrics.normalize_queries(rows)
        metrics.normalize_queries([query("a", family="f1", seed="s1", judgments={"r": R}),
                                   query("b", family="f1", seed="s1", judgments={"r": R})])

    def test_the_same_seed_name_in_two_languages_is_two_seeds(self):
        rows = [query("a", family="f1", seed="s1", judgments={"r": R}),
                query("b", language="java", family="f2", seed="s1", judgments={"r": R})]
        self.assertEqual(len(metrics.normalize_queries(rows)), 2)

    def test_every_resample_keeps_each_language_positive_family_count(self):
        data = all_languages()
        groups = metrics._family_groups(data)

        def positive_family_counts(rows):
            found = {}
            for row in rows:
                if metrics.query_outcome(row)["kind"] == metrics.POSITIVE:
                    found[row["family"]] = row["language"]
            return Counter(found.values())

        expected = positive_family_counts(data)
        for seed in range(60):
            sample = metrics.resample_families(groups, random.Random(seed))
            self.assertEqual(positive_family_counts(sample), expected, seed)

    def test_resampling_draws_families_inside_every_language(self):
        data = all_languages()
        groups = metrics._family_groups(data)
        per_language = Counter(row["language"] for row in data)
        for seed in range(25):
            sample = metrics.resample_families(groups, random.Random(seed))
            drawn = Counter(row["language"] for row in sample)
            for language in SUPPORTED:
                self.assertEqual(drawn[language], per_language[language], (seed, language))


if __name__ == "__main__":
    unittest.main()
