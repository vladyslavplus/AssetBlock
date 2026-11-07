import unittest

from scripts.model_evaluation import metrics
from scripts.model_evaluation.tests._fixtures import N, R, U, query


def outcome(**kwargs):
    return metrics.query_outcome(metrics.normalize_query(query("q", **kwargs)))


class QueryOutcomeTests(unittest.TestCase):
    def test_all_negative_list_is_a_no_match_query_without_alarm_when_empty(self):
        quiet = outcome(judgments={"a": N, "b": N}, findings=[])
        self.assertEqual(quiet["kind"], metrics.NO_MATCH)
        self.assertEqual(quiet["fp"], 0)
        alarm = outcome(judgments={"a": N, "b": N}, findings=["a"])
        self.assertEqual(alarm["fp"], 1)

    def test_retrieval_miss_lowers_recall_through_gallery_denominator(self):
        miss = outcome(judgments={"r": R, "n": N}, findings=["n"])
        self.assertEqual((miss["tp"], miss["fp"], miss["recall"], miss["f1"]), (0, 1, 0.0, 0.0))
        partial = outcome(judgments={"r1": R, "r2": R, "n": N}, findings=["r1"])
        self.assertEqual(partial["recall"], 0.5)
        self.assertEqual(partial["precision"], 1.0)

    def test_empty_predictions_score_zero_and_do_not_divide_by_zero(self):
        empty = outcome(judgments={"r": R, "n": N}, findings=[])
        self.assertEqual((empty["precision"], empty["recall"], empty["f1"]), (0.0, 0.0, 0.0))
        self.assertFalse(empty["emitted"])

    def test_unknown_only_query_is_excluded_not_negative(self):
        result = outcome(judgments={"a": U, "b": U}, findings=["a"])
        self.assertEqual(result["kind"], metrics.UNKNOWN_ONLY)
        summary = metrics.summarize(metrics.normalize_queries(
            [query("q", judgments={"a": U, "b": U}, findings=["a"])]))
        self.assertEqual(summary["counts"]["unknownOnlyQueries"], 1)
        self.assertEqual(summary["counts"]["positiveQueries"], 0)
        self.assertIsNone(summary["macro"]["precision"])
        self.assertIsNone(summary["spuriousAlert"]["rate"])

    def test_unknown_findings_are_neither_true_nor_false(self):
        result = outcome(judgments={"r": R, "u": U}, findings=["r", "u"])
        self.assertEqual((result["precision"], result["fp"], result["unknownFindings"]), (1.0, 0, 1))

    def test_ties_all_emitted_and_scored_together(self):
        self.assertEqual(metrics.top_tied_findings({"a": 2.0, "b": 2.0, "c": 1.0}), ["a", "b"])
        self.assertEqual(metrics.top_tied_findings({"a": 0.0, "b": 0.0}), [])
        self.assertEqual(metrics.top_tied_findings({}), [])
        tied = outcome(judgments={"a": R, "b": N, "c": N}, findings=metrics.top_tied_findings(
            {"a": 2.0, "b": 2.0, "c": 1.0}))
        self.assertEqual(tied["precision"], 0.5)
        with self.assertRaises(ValueError):
            metrics.top_tied_findings({"a": float("nan")})

    def test_multi_positive_recall_and_f1(self):
        result = outcome(judgments={"a": R, "b": R, "c": R, "n": N}, findings=["a"])
        self.assertAlmostEqual(result["recall"], 1 / 3)
        self.assertAlmostEqual(result["f1"], 0.5)

    def test_invalid_records_are_rejected(self):
        bad = [query("q", judgments={}, findings=[]),
               query("q", judgments={"a": R}, findings=["zzz"]),
               query("q", judgments={"a": R}, findings=["a", "a"]),
               query("q", judgments={"a": "MAYBE"}, findings=[]),
               query("q", judgments={"a": R}, language="typescript"),
               query("q", judgments={"a": R}, language="python", dialect="tsx")]
        for record in bad:
            with self.subTest(record=record), self.assertRaises(ValueError):
                metrics.normalize_query(record)
        with self.assertRaises(ValueError):
            metrics.normalize_queries([query("q", judgments={"a": R}), query("q", judgments={"a": R})])
        with self.assertRaises(ValueError):
            metrics.normalize_queries([
                query("q1", judgments={"a": R}, family="f", language="python"),
                query("q2", judgments={"a": R}, family="f", language="java")])


class AggregationTests(unittest.TestCase):
    def test_no_match_false_alarm_rate_uses_fully_reviewed_queries_only(self):
        rows = [query("n%d" % i, judgments={"a": N, "b": N}, findings=["a"] if i == 0 else [])
                for i in range(4)]
        rows.append(query("partial", judgments={"a": N, "u": U}, findings=["a"]))
        summary = metrics.summarize(metrics.normalize_queries(rows))
        self.assertEqual(summary["spuriousAlert"], {"rate": 0.25, "alarms": 1, "queries": 4})
        self.assertEqual(summary["counts"]["noMatchQueries"], 5)

    def test_abstention_coverage_counts_scored_queries_that_emitted(self):
        rows = [query("p1", judgments={"r": R}, findings=["r"]),
                query("p2", judgments={"r": R}, findings=[]),
                query("n1", judgments={"a": N}, findings=["a"]),
                query("n2", judgments={"a": N}, findings=["a"]),
                query("unk", judgments={"a": U}, findings=["a"])]
        abstention = metrics.summarize(metrics.normalize_queries(rows))["abstention"]
        self.assertEqual(abstention["coverage"], 0.75)
        self.assertEqual(abstention["coverageOnPositiveQueries"], 0.5)
        self.assertEqual(abstention["scoredQueries"], 4)

    def test_seed_macro_ignores_variant_count_per_seed(self):
        def rows(extra):
            data = [query("s1-a", judgments={"r": R}, findings=["r"], seed="s1", family="f1")]
            data += [query("s2-%d" % i, judgments={"r": R}, findings=[], seed="s2", family="f2")
                     for i in range(1 + extra)]
            return metrics.normalize_queries(data)
        base = metrics.summarize(rows(0))["macro"]["f1"]
        self.assertEqual(base, 0.5)
        self.assertEqual(metrics.summarize(rows(5))["macro"]["f1"], base)

    def test_language_equal_macro_differs_from_micro(self):
        rows = [query("py", language="python", judgments={"r": R}, findings=["r"])]
        rows += [query("j%d" % i, language="java", judgments={"r": R}, findings=[]) for i in range(9)]
        summary = metrics.summarize(metrics.normalize_queries(rows))
        self.assertEqual(summary["macro"]["recall"], 0.5)
        self.assertEqual(summary["micro"]["recall"], 0.1)
        self.assertEqual(summary["perLanguage"]["java"]["macro"]["recall"], 0.0)

    def test_strata_report_their_own_denominators(self):
        rows = [query("a", judgments={"r": R}, findings=["r"], stratum="exact copy"),
                query("b", judgments={"r": R}, findings=[], stratum="hard negative"),
                query("c", judgments={"x": N}, findings=[], stratum="hard negative")]
        summary = metrics.summarize(metrics.normalize_queries(rows))
        self.assertEqual(summary["perStratum"]["exact copy"]["counts"]["positiveQueries"], 1)
        self.assertEqual(summary["perStratum"]["hard negative"]["counts"]["positiveQueries"], 1)
        self.assertEqual(summary["perStratum"]["hard negative"]["counts"]["noMatchQueries"], 1)
        self.assertEqual(summary["perStratum"]["exact copy"]["macro"]["recall"], 1.0)
        self.assertEqual(summary["perStratum"]["hard negative"]["macro"]["recall"], 0.0)

    def test_typescript_dialects_are_reported_inside_the_pooled_language(self):
        rows = [query("a", language="typescript", dialect="ts", judgments={"r": R}, findings=["r"]),
                query("b", language="typescript", dialect="tsx", judgments={"r": R}, findings=[])]
        summary = metrics.summarize(metrics.normalize_queries(rows))
        self.assertEqual(summary["perLanguage"]["typescript"]["macro"]["recall"], 0.5)
        self.assertEqual(summary["perDialect"]["ts"]["macro"]["recall"], 1.0)
        self.assertEqual(summary["perDialect"]["tsx"]["macro"]["recall"], 0.0)

    def test_multi_match_recall_uses_only_multi_relevant_queries(self):
        rows = [query("m", judgments={"a": R, "b": R}, findings=["a"]),
                query("s", judgments={"a": R}, findings=["a"])]
        summary = metrics.summarize(metrics.normalize_queries(rows))
        self.assertEqual(summary["multiMatch"], {"queries": 1, "recall": 0.5})

    def test_false_findings_per_positive_query(self):
        rows = [query("a", judgments={"r": R, "n1": N, "n2": N}, findings=["r", "n1", "n2"]),
                query("b", judgments={"r": R, "n1": N}, findings=["r"])]
        summary = metrics.summarize(metrics.normalize_queries(rows))
        self.assertEqual(summary["macro"]["falseFindingsPerPositiveQuery"], 1.0)
        self.assertEqual(summary["micro"]["falseFindingsPerPositiveQuery"], 1.0)


if __name__ == "__main__":
    unittest.main()
