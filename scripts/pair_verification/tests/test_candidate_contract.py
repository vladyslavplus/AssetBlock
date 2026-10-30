import unittest

from scripts.code_analysis.contracts import LANGUAGES
from scripts.code_analysis.evaluation import cpu_rank
from scripts.pair_verification import candidate_contract
from scripts.pair_verification import validation_candidates


class CandidateContractTests(unittest.TestCase):
 def test_tied_cosines_follow_file_id_order(self):
    rows = [{"fileId": "b", "vector": [1.0, 0.0]}, {"fileId": "a", "vector": [1.0, 0.0]}]
    self.assertEqual([row["fileId"] for row in cpu_rank([[1.0, 0.0]], rows, key="fileId", limit=2)], ["a", "b"])


 def test_candidate_replay_matching_and_single_position_mismatch(self):
    left = {"E0": {"q": ["a", "b"]}, "E3": {"q": ["c", "d"]}}
    self.assertTrue(candidate_contract.require_same_orders(left, left))
    right = {"E0": {"q": ["a", "b"]}, "E3": {"q": ["c", "x"]}}
    with self.assertRaisesRegex(ValueError, "replay differs"):
        candidate_contract.require_same_orders(left, right)


 def test_candidate_list_keeps_strict_gallery_rule(self):
    with self.assertRaisesRegex(ValueError, "strict gallery"):
        candidate_contract.candidate_union(["a"], [], [str(i) for i in range(40)])

 def test_candidate_union_matches_token_then_semantic_top_twenty(self):
    gallery = [f"{i:064x}" for i in range(41)]
    token = [f"{i:064x}" for i in range(20)]
    semantic = [f"{i:064x}" for i in range(10, 30)]
    expected = [f"{i:064x}" for i in range(30)]
    self.assertEqual(candidate_contract.candidate_union(semantic, token, gallery), expected)


 def test_unknown_validation_file_cannot_enter_candidate_labels(self):
    with self.assertRaisesRegex(ValueError, "unknown or unscored"):
        validation_candidates.candidate_relevance(["unknown"], [], [], ["unknown"])
    self.assertEqual(validation_candidates.candidate_relevance(["positive", "distractor"], ["positive"],
                                                               ["positive", "distractor"], ["unknown"]),
                     {"positive": "POSITIVE", "distractor": "REVIEWED_DISTRACTOR"})

 def test_unknown_exclusion_counts_remain_per_model(self):
    counts = {("E0", "python", "EXACT_COPY"): 7,
              ("E3", "python", "EXACT_COPY"): 7}
    self.assertEqual(validation_candidates.unknown_exclusions_per_model(counts), {"E0": 7, "E3": 7})


 def test_database_corroboration_and_wrong_denominator(self):
    rows = {label: [{"queryId": f"q-{i}", "exact": True, "disagreements": [],
                     "maxAbsScoreDifference": 0.000001} for i in range(201)]
            for label in ("E0", "E1", "E2", "E3")}
    self.assertEqual(candidate_contract.require_database_corroboration(rows)["E0"]["queries"], 201)
    with self.assertRaisesRegex(ValueError, "denominator"):
        candidate_contract.require_database_corroboration({**rows, "E0": rows["E0"][:-1]})


 def test_wrong_aggregate_metric_denominator_fails(self):
    actual = {label: {language: {"queries": 200, "metrics": {}}
                      for language in LANGUAGES} for label in ("E0", "E3")}
    with self.assertRaisesRegex(ValueError, "denominator"):
        candidate_contract.require_metric_equality(actual, {"denominators": {"E0": {"queries": 201},
                                                                              "E3": {"queries": 201}}})


 def test_positive_metric_equality_accepts_and_rejects_drift(self):
    cells = {"semantic@5.recall": 0.5, "semantic@10.recall": 0.75,
             "semantic@20.recall": 0.9, "semantic@20.mrr": 0.8}
    actual = {label: {language: {"queries": 40, "metrics": dict(cells)} for language in LANGUAGES}
              for label in ("E0", "E3")}
    actual["E0"]["typescript"]["queries"] = 41
    actual["E3"]["typescript"]["queries"] = 41
    tables = {label: {"languages": {
        "semantic@5": {language: {"recall": value} for language, value in ((lang, cells["semantic@5.recall"]) for lang in LANGUAGES)},
        "semantic@10": {language: {"recall": cells["semantic@10.recall"]} for language in LANGUAGES},
        "semantic@20": {language: {"recall": cells["semantic@20.recall"],
                                   "mrr": cells["semantic@20.mrr"]} for language in LANGUAGES}}}
        for label in ("E0", "E3")}
    evaluation = {"denominators": {label: {"queries": 201} for label in ("E0", "E3")}, "tables": tables}
    self.assertTrue(candidate_contract.require_metric_equality(actual, evaluation))
    actual["E0"]["csharp"]["metrics"]["semantic@5.recall"] += 0.01
    with self.assertRaisesRegex(ValueError, "metric differs"):
        candidate_contract.require_metric_equality(actual, evaluation)
