import copy
import json
import re
import unittest
from pathlib import Path

from scripts.demonstration import run_scenarios as runner
from scripts.demonstration.fixtures import FIXTURE_IDS

REPO_ROOT = runner.REPO_ROOT
FIXTURES_ROOT = runner.FIXTURES_ROOT


class ManifestTests(unittest.TestCase):
    def setUp(self):
        self.manifest = runner.load_manifest()

    def test_manifest_is_valid(self):
        self.assertEqual([], runner.validate_manifest(self.manifest))

    def test_scenarios_are_exactly_d1_to_d9(self):
        ids = [scenario["id"] for scenario in self.manifest["scenarios"]]
        self.assertEqual(list(runner.SCENARIO_IDS), ids)

    def test_every_scenario_has_lanes_and_gaps(self):
        for scenario in self.manifest["scenarios"]:
            with self.subTest(scenario=scenario["id"]):
                self.assertTrue(scenario["lanes"])
                self.assertTrue(scenario["gaps"])
                self.assertTrue(scenario["intended_live_steps"].strip())
                self.assertTrue(scenario["lanes_actually_do"].strip())

    def test_every_lane_declares_kind_minimum_and_timeout(self):
        for scenario in self.manifest["scenarios"]:
            for lane in scenario["lanes"]:
                with self.subTest(lane=lane["id"]):
                    self.assertIn(lane["evidence_kind"], runner.EVIDENCE_KINDS)
                    self.assertGreaterEqual(lane["expected_min_tests"], 1)
                    self.assertGreater(lane["timeout_seconds"], 0)

    def test_lane_tokens_resolve_in_referenced_test_files(self):
        for scenario in self.manifest["scenarios"]:
            for lane in scenario["lanes"]:
                texts = [(REPO_ROOT / path).read_text(encoding="utf-8") for path in lane["test_files"]]
                tokens = runner.lane_tokens(lane)
                with self.subTest(lane=lane["id"]):
                    self.assertTrue(tokens)
                    for token in tokens:
                        self.assertTrue(any(token in text for text in texts), token)

    def test_referenced_test_files_exist(self):
        for scenario in self.manifest["scenarios"]:
            for lane in scenario["lanes"]:
                for relative in lane["test_files"]:
                    with self.subTest(path=relative):
                        self.assertTrue((REPO_ROOT / relative).is_file())

    def test_optional_live_lane_is_excluded_by_default(self):
        scenario = next(item for item in self.manifest["scenarios"] if item["id"] == "D6")
        default_ids = [lane["id"] for lane in runner.select_lanes(scenario, [], False)]
        optional_ids = [lane["id"] for lane in runner.select_lanes(scenario, [], True)]
        self.assertNotIn("d6-live-pending-v2-optional", default_ids)
        self.assertIn("d6-live-pending-v2-optional", optional_ids)

    def test_validator_rejects_missing_scenario(self):
        broken = copy.deepcopy(self.manifest)
        broken["scenarios"] = [item for item in broken["scenarios"] if item["id"] != "D5"]
        self.assertTrue(runner.validate_manifest(broken))

    def test_validator_rejects_unknown_evidence_kind_and_empty_lanes(self):
        broken = copy.deepcopy(self.manifest)
        broken["scenarios"][0]["lanes"][0]["evidence_kind"] = "live-guess"
        broken["scenarios"][1]["lanes"] = []
        problems = runner.validate_manifest(broken)
        self.assertTrue(any("evidence_kind" in problem for problem in problems))
        self.assertTrue(any("lanes must be a non-empty list" in problem for problem in problems))

    def test_validator_rejects_token_missing_from_test_file(self):
        broken = copy.deepcopy(self.manifest)
        broken["scenarios"][0]["lanes"][0]["args"][-1] = "FullyQualifiedName~NoSuchTestAnywhere"
        problems = runner.validate_manifest(broken)
        self.assertTrue(any("token not found" in problem for problem in problems))

    def test_validator_rejects_unknown_fixture_and_duplicate_lane(self):
        broken = copy.deepcopy(self.manifest)
        broken["scenarios"][0]["live_run_fixtures"] = ["not-a-fixture"]
        broken["scenarios"][1]["lanes"][0]["id"] = broken["scenarios"][0]["lanes"][0]["id"]
        problems = runner.validate_manifest(broken)
        self.assertTrue(any("unknown fixture" in problem for problem in problems))
        self.assertTrue(any("duplicate lane id" in problem for problem in problems))

    def test_no_lane_consumes_the_live_run_fixtures(self):
        for scenario in self.manifest["scenarios"]:
            for lane in scenario["lanes"]:
                with self.subTest(lane=lane["id"]):
                    self.assertFalse(any("fixture" in argument.lower() for argument in lane["args"]))
                    self.assertFalse(any("fixture" in path.lower() for path in lane["test_files"]))

    def test_evidence_kinds_use_the_renamed_labels(self):
        kinds = {lane["evidence_kind"] for scenario in self.manifest["scenarios"] for lane in scenario["lanes"]}
        self.assertLessEqual(kinds, set(runner.EVIDENCE_KINDS))
        self.assertNotIn("real-server", kinds)
        self.assertEqual({"http-postgres-test-host", "postgres-store"}, set(runner.REAL_SERVER_KINDS))

    def test_secret_like_keys_are_flagged(self):
        self.assertTrue(runner.secret_key_hits({"apiToken": "x"}))
        self.assertFalse(runner.secret_key_hits({"expected_min_tests": 1}))

    def test_forbidden_text_is_flagged(self):
        self.assertTrue(runner.forbidden_hits("sample", "reference " + "S" + "10 entry"))
        self.assertTrue(runner.forbidden_hits("sample", "see " + "docs" + "/" + "ignored" + "/x"))
        self.assertFalse(runner.forbidden_hits("sample", "plain demonstration text"))

    def test_manifest_and_fixture_tree_have_no_forbidden_text(self):
        self.assertEqual([], runner.forbidden_hits("scenarios.json",
                                                   runner.MANIFEST_PATH.read_text(encoding="utf-8")))
        for path in FIXTURES_ROOT.rglob("*"):
            if path.is_file() and "__pycache__" not in path.parts and path.suffix != ".zip":
                with self.subTest(path=path.name):
                    self.assertEqual([], runner.forbidden_hits(path.name,
                                                               path.read_text(encoding="utf-8")))

    def test_fixture_ids_in_manifest_are_known(self):
        for scenario in self.manifest["scenarios"]:
            for fixture in scenario["live_run_fixtures"]:
                self.assertIn(fixture, FIXTURE_IDS)

    def test_manifest_is_plain_json_with_schema(self):
        document = json.loads(runner.MANIFEST_PATH.read_text(encoding="utf-8"))
        self.assertEqual("demonstration-scenarios-v1", document["schema"])
        self.assertTrue(re.fullmatch(r"D[1-9]", document["scenarios"][0]["id"]))


if __name__ == "__main__":
    unittest.main()
