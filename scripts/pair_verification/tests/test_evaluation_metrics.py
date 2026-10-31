import unittest
import random

from scripts.pair_verification import evaluation_metrics as metrics


def row(query, file_id, relevance, score, *, language="python", seed="a", stratum="COMMENT_FORMAT"):
    return {"queryId": query, "fileId": file_id, "relevance": relevance, "score": score,
            "language": language, "seedGroup": seed, "stratum": stratum}


class EvaluationMetricTests(unittest.TestCase):
    def test_exact_candidate_scores_and_unknown_exclusion(self):
        rows = [row("q", "p", "POSITIVE", 0.9),
                row("q", "n", "REVIEWED_DISTRACTOR", 0.1),
                row("q", "u", "UNKNOWN", 1.0)]
        result = metrics.query_metrics(rows, threshold=0.5)
        self.assertEqual(result["precision"], 1.0)
        self.assertEqual(result["recall"], 1.0)
        self.assertEqual(result["f1"], 1.0)
        self.assertEqual(result["auprc"], 1.0)
        self.assertEqual(result["unknownExcluded"], 1)

    def test_threshold_tie_chooses_higher_value(self):
        rows = [row("q1", "p1", "POSITIVE", 0.9, seed="s1"),
                row("q1", "n1", "REVIEWED_DISTRACTOR", 0.8, seed="s1"),
                row("q2", "p2", "POSITIVE", 0.8, seed="s2"),
                *[row("q2", "n" + str(index), "REVIEWED_DISTRACTOR", 0.8, seed="s2")
                  for index in range(4)]]
        selected = metrics.choose_threshold(rows)
        self.assertEqual(selected["macroF1"], 0.5)
        self.assertEqual(selected["threshold"], 0.9)

    def test_epoch_selection_uses_shared_f1_source_on_exact_auprc_tie(self):
        tied_rows = [row("q", "p", "POSITIVE", 0.9),
                     row("q", "n", "REVIEWED_DISTRACTOR", 0.1)]
        selected = metrics.select_epoch({epoch: list(tied_rows) for epoch in range(1, 6)})
        self.assertEqual(selected, 1)
        self.assertEqual(metrics.epoch_selection_key(1, tied_rows)[:2],
                         metrics.epoch_selection_key(2, tied_rows)[:2])

    def test_sweep_matches_direct_threshold_search(self):
        rng = random.Random(311)
        rows = []
        for index in range(12):
            language = ("python", "java")[index % 2]
            seed = "seed-" + str(index // 2)
            query = "query-" + str(index)
            positive_score = rng.choice((0.2, 0.5, 0.8))
            negative_score = rng.choice((0.2, 0.5, 0.8))
            rows.extend([row(query, "p-" + query, "POSITIVE", positive_score,
                             language=language, seed=seed),
                         row(query, "n-" + query, "REVIEWED_DISTRACTOR", negative_score,
                             language=language, seed=seed)])
        scores = {float(item["score"]) for item in rows}
        candidates = sorted(scores | {min(scores) - 1e-12, max(scores) + 1e-12}, reverse=True)
        reference = max(((metrics._weighted(rows, metric="f1", threshold=value)["macro"], value)
                         for value in candidates), key=lambda item: (item[0], item[1]))
        actual = metrics.choose_threshold(rows)
        self.assertEqual(actual["threshold"], reference[1])
        self.assertAlmostEqual(actual["macroF1"], reference[0], places=14)

    def test_cross_fit_is_deterministic_by_sorted_seed(self):
        rows = []
        for seed in ("d", "b", "c", "a"):
            rows.extend([row("q-" + seed, "p-" + seed, "POSITIVE", 0.8, seed=seed),
                         row("q-" + seed, "n-" + seed, "REVIEWED_DISTRACTOR", 0.2, seed=seed)])
        first = metrics.cross_fit(rows)
        second = metrics.cross_fit(rows)
        self.assertEqual(first, second)
        self.assertEqual(first["foldAssignment"], {"a": 0, "b": 1, "c": 0, "d": 1})
        self.assertEqual(first["heldOutQueryCount"], 4)

    def test_per_stratum_metrics_keep_denominators_separate(self):
        rows = [row("q1", "p1", "POSITIVE", 0.8, seed="s1", stratum="EXACT_COPY"),
                row("q1", "n1", "REVIEWED_DISTRACTOR", 0.2, seed="s1", stratum="EXACT_COPY"),
                row("q2", "p2", "POSITIVE", 0.8, seed="s2", stratum="AST_BOUND_RENAME"),
                row("q2", "n2", "REVIEWED_DISTRACTOR", 0.2, seed="s2", stratum="AST_BOUND_RENAME")]
        split = metrics.summary(rows, threshold=0.5)["perStratum"]
        self.assertEqual(set(split), {"EXACT_COPY", "AST_BOUND_RENAME"})
        self.assertEqual(split["EXACT_COPY"]["counts"]["queries"], 1)
        self.assertEqual(split["AST_BOUND_RENAME"]["counts"]["candidatePairs"], 2)

    def test_paired_deltas_use_shared_query_denominator(self):
        primary = [row("q1", "p1", "POSITIVE", 0.9, seed="s1"),
                   row("q1", "n1", "REVIEWED_DISTRACTOR", 0.1, seed="s1"),
                   row("q2", "p2", "POSITIVE", 0.1, seed="s2"),
                   row("q2", "n2", "REVIEWED_DISTRACTOR", 0.9, seed="s2")]
        control = [{**item, "score": 0.5} for item in primary]
        result = metrics.paired_deltas(primary, control, threshold_primary=0.5, threshold_control=0.5)
        self.assertEqual(result["python"]["denominatorQueries"], 2)
        self.assertEqual(result["python"]["metrics"]["f1"]["primary"], 0.5)

    def test_duplicate_candidates_and_unknown_only_queries_refuse(self):
        with self.assertRaisesRegex(ValueError, "duplicate"):
            metrics.query_metrics([row("q", "p", "POSITIVE", 0.8),
                                   row("q", "p", "POSITIVE", 0.8)])
        with self.assertRaisesRegex(ValueError, "no known labels"):
            metrics.query_metrics([row("q", "u", "UNKNOWN", 0.8)])


if __name__ == "__main__":
    unittest.main()
