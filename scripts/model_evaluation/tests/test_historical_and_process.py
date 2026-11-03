import ast
import unittest
from pathlib import Path

from scripts.model_evaluation import metrics
from scripts.pair_verification import evaluation_metrics

PACKAGE = Path(__file__).resolve().parents[1]


def candidate_rows():
    """Candidate-conditional fixture: every list holds a relevant file, scores decide the findings."""
    spec = {("python", "s1", "q1"): [("r", 0.9, "POSITIVE"), ("d1", 0.8, "REVIEWED_DISTRACTOR"),
                                     ("d2", 0.1, "REVIEWED_DISTRACTOR")],
            ("python", "s1", "q2"): [("r", 0.2, "POSITIVE"), ("d1", 0.7, "REVIEWED_DISTRACTOR")],
            ("python", "s2", "q3"): [("r1", 0.9, "POSITIVE"), ("r2", 0.6, "POSITIVE"),
                                     ("d1", 0.4, "REVIEWED_DISTRACTOR")],
            ("java", "s3", "q4"): [("r", 0.55, "POSITIVE"), ("u", 0.9, "UNKNOWN")]}
    rows = []
    for (language, seed, query_id), files in spec.items():
        for file_id, score, relevance in files:
            rows.append({"queryId": query_id, "language": language, "stratum": "COMMENT_FORMAT",
                         "seedGroup": seed, "fileId": file_id, "score": score, "relevance": relevance})
    return rows


def end_to_end_queries(rows, threshold):
    labels = {"POSITIVE": "RELEVANT", "REVIEWED_DISTRACTOR": "NOT_RELEVANT", "UNKNOWN": "UNKNOWN"}
    grouped = {}
    for row in rows:
        grouped.setdefault(row["queryId"], []).append(row)
    records = []
    for query_id, items in grouped.items():
        first = items[0]
        records.append({"queryId": query_id, "language": first["language"], "family": first["seedGroup"],
                        "seedGroup": first["seedGroup"], "stratum": first["stratum"],
                        "judgments": {row["fileId"]: labels[row["relevance"]] for row in items},
                        "findings": [row["fileId"] for row in items if row["score"] >= threshold]})
    return metrics.normalize_queries(records)


class HistoricalCompatibilityTests(unittest.TestCase):
    def test_end_to_end_matches_the_conditional_protocol_when_the_gallery_is_the_candidate_list(self):
        rows = candidate_rows()
        historical = evaluation_metrics.aggregate(rows, threshold=0.5)
        summary = metrics.summarize(end_to_end_queries(rows, 0.5))
        for name, key in (("precision", "precision"), ("recall", "recall"), ("f1", "f1"),
                          ("falseFindingsPerPositiveQuery", "falseFindingsPerQuery")):
            self.assertAlmostEqual(summary["macro"][name], historical[key]["macro"], msg=name)
        self.assertEqual(summary["counts"]["unknownJudgments"], historical["counts"]["unknownExcluded"])

    def test_historical_results_are_unchanged(self):
        result = evaluation_metrics.aggregate(candidate_rows(), threshold=0.5)
        self.assertAlmostEqual(result["precision"]["macro"], 0.8125)
        self.assertAlmostEqual(result["recall"]["macro"], 0.875)
        self.assertAlmostEqual(result["f1"]["macro"], 0.8333333333333333)
        self.assertEqual(result["counts"], {"queries": 4, "candidatePairs": 9, "unknownExcluded": 1})
        self.assertEqual(evaluation_metrics.choose_threshold(candidate_rows())["threshold"], 0.2)
        with self.assertRaises(ValueError):
            evaluation_metrics.query_metrics([{"fileId": "d", "score": 0.1, "relevance": "REVIEWED_DISTRACTOR"}])


class HiddenProcessTests(unittest.TestCase):
    def test_package_starts_no_processes_directly(self):
        """Process launches must go through the bounded runner, which hides the window by default."""
        forbidden = {"subprocess", "multiprocessing"}
        for path in sorted(PACKAGE.glob("*.py")):
            tree = ast.parse(path.read_text(encoding="utf-8"))
            for node in ast.walk(tree):
                if isinstance(node, ast.Import):
                    names = {alias.name.split(".")[0] for alias in node.names}
                elif isinstance(node, ast.ImportFrom):
                    names = {(node.module or "").split(".")[0]}
                else:
                    names = set()
                self.assertFalse(names & forbidden, "%s imports a process module" % path.name)
                if isinstance(node, ast.Attribute) and isinstance(node.value, ast.Name) and node.value.id == "os":
                    self.assertNotIn(node.attr, {"system", "popen", "startfile", "spawnl"}, path.name)

    def test_bounded_runner_hides_the_window_by_default(self):
        import inspect
        from scripts.feasibility_pilot import subprocess_util
        self.assertIsNone(inspect.signature(subprocess_util.run_bounded).parameters["creationflags"].default)


if __name__ == "__main__":
    unittest.main()
