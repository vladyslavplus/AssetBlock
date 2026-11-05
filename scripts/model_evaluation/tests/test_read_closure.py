import contextlib
import json
import tempfile
import unittest
from pathlib import Path
from unittest import mock

from scripts.model_evaluation import guards, validation
from scripts.model_evaluation.tests._pipeline import Workspace, fake_identity

SPIED = ("is_file", "is_dir", "stat", "exists", "open", "read_bytes", "read_text")


def _recording(name, original, touched):
    def wrapper(path, *args, **kwargs):
        touched.append((name, str(path)))
        return original(path, *args, **kwargs)
    return wrapper


def run_recording_path_access(call):
    """Run ``call`` and return (its result, every Path access as (method, path text))."""
    touched = []
    with contextlib.ExitStack() as stack:
        for name in SPIED:
            stack.enter_context(mock.patch.object(Path, name, _recording(name, getattr(Path, name), touched)))
        result = call()
    return result, touched


def refusal_with_recorded_access(call):
    """Run ``call``, which must refuse with SealedInputRefused; return (refused, every Path access)."""
    touched = []
    with contextlib.ExitStack() as stack:
        for name in SPIED:
            stack.enter_context(mock.patch.object(Path, name, _recording(name, getattr(Path, name), touched)))
        try:
            call()
        except guards.SealedInputRefused:
            return True, touched
    return False, touched


class ReadClosureTests(unittest.TestCase):
    """Restricted-named entries are skipped by name, before any stat or open."""

    def setUp(self):
        temp = tempfile.TemporaryDirectory()
        self.addCleanup(temp.cleanup)
        self.root = Path(temp.name)
        self.run_root = self.root / "runs"
        self.registry = self.root / "registry"
        self.run_root.mkdir()
        self.registry.mkdir()
        self.decoy = "decoy-" + guards.SEAL + "-run"
        for base in (self.run_root, self.registry):
            decoy = base / self.decoy
            decoy.mkdir()
            (decoy / "score-result.json").write_text("{}", encoding="utf-8")
            (decoy / "attempt-0001.json").write_text("{}", encoding="utf-8")
        ordinary = self.run_root / "score-01"
        ordinary.mkdir()
        (ordinary / "score-result.json").write_text("{}", encoding="utf-8")

    def test_restricted_named_run_entry_is_never_stat_or_opened(self):
        (names, skipped), touched = run_recording_path_access(
            lambda: validation.unregistered_results(self.run_root, registered=set()))
        self.assertEqual(names, ["score-01"])
        self.assertEqual(skipped, 1)
        self.assertFalse([item for item in touched if self.decoy in item[1]], touched)
        self.assertTrue([item for item in touched if "score-01" in item[1]])

    def test_restricted_named_registry_entry_refuses_the_registry_without_stat_or_open(self):
        refused, touched = refusal_with_recorded_access(lambda: guards.registered_run_ids(self.registry))
        self.assertTrue(refused)
        self.assertFalse([item for item in touched if self.decoy in item[1]], touched)

    def test_ordinary_names_separates_kept_and_skipped_entries(self):
        kept, skipped = guards.ordinary_names(self.run_root)
        self.assertEqual(kept, ["score-01"])
        self.assertEqual(skipped, 1)
        self.assertEqual(guards.ordinary_names(self.root / "missing"), ([], 0))


class RegistryAndListingTests(unittest.TestCase):
    """matching_attempts skips restricted-named key directories; attempt_listing refuses a restricted attempt."""

    def setUp(self):
        temp = tempfile.TemporaryDirectory()
        self.addCleanup(temp.cleanup)
        self.registry = Path(temp.name) / "registry"
        self.decoy = "decoy-" + guards.SEAL + "-run"
        matched = {"runId": "score-01", "sequence": 1, "packageTripleSha256": "p" * 64, "stateTripleSha256": "s" * 64}
        other = {"runId": "score-02", "sequence": 2, "packageTripleSha256": "q" * 64, "stateTripleSha256": "t" * 64}
        for key, attempt in (("key-a", matched), ("key-b", other), (self.decoy, matched)):
            directory = self.registry / key
            directory.mkdir(parents=True)
            (directory / "attempt-0001.json").write_text(json.dumps(attempt), encoding="utf-8")

    def test_matching_attempts_refuses_a_restricted_named_registry_entry_without_opening_it(self):
        refused, touched = refusal_with_recorded_access(
            lambda: guards.matching_attempts(self.registry, "p" * 64, "x" * 64))
        self.assertTrue(refused)
        self.assertFalse([item for item in touched if self.decoy in item[1]], touched)

    def test_matching_attempts_returns_ordinary_matches_from_a_clean_registry(self):
        clean = self.registry.parent / "clean-registry"
        matched = {"runId": "score-01", "sequence": 1, "packageTripleSha256": "p" * 64, "stateTripleSha256": "s" * 64}
        other = {"runId": "score-02", "sequence": 2, "packageTripleSha256": "q" * 64, "stateTripleSha256": "t" * 64}
        for key, attempt in (("key-a", matched), ("key-b", other)):
            (clean / key).mkdir(parents=True)
            (clean / key / "attempt-0001.json").write_text(json.dumps(attempt), encoding="utf-8")
        found, _ = run_recording_path_access(lambda: guards.matching_attempts(clean, "p" * 64, "x" * 64))
        self.assertEqual([attempt["runId"] for attempt in found], ["score-01"])

    def test_attempt_listing_refuses_a_restricted_registered_attempt(self):
        restricted = {"runId": self.decoy, "sequence": 1, "manifestSha256": "0" * 64, "reason": "first",
                      "rerunOf": None, "labelSha256": "0" * 64, "corpusIdentitySha256": "0" * 64}
        with mock.patch.object(validation, "_attempt_records", return_value=[restricted]):
            with self.assertRaisesRegex(ValueError, "restricted-marked"):
                validation.attempt_listing(self.registry, self.registry, {}, {}, None, None)


class DecoyEndToEndTests(unittest.TestCase):
    """validation.run over a complete synthetic chain with one restricted-named run-root entry."""

    def setUp(self):
        self.ws = Workspace(self)
        self.prepared, self.cands = self.ws.full_run()
        self.ws.score(self.prepared, self.cands)
        self.decoy = "decoy-" + guards.SEAL + "-run"
        decoy = self.ws.run_root / self.decoy
        decoy.mkdir()
        (decoy / "score-result.json").write_text("{}", encoding="utf-8")

    def test_validation_run_skips_the_decoy_by_name_and_records_only_the_count(self):
        result, touched = run_recording_path_access(lambda: validation.run(
            run_id="evidence-01", freeze_run_id="freeze-01", prepare_run_id="prepare-01",
            candidates_run_id="cand-01", score_run_id="score-01", run_root=self.ws.run_root,
            registry_root=self.ws.registry, capture=fake_identity))
        self.assertEqual(result["skippedRestrictedNamedRunEntries"], 1)
        package_path = guards.run_dir("evidence-01", self.ws.run_root) / validation.PACKAGE_NAME
        package = json.loads(package_path.read_bytes())
        self.assertEqual(package["skippedRestrictedNamedRunEntries"], 1)
        self.assertTrue(any("restricted-marked names" in note for note in package["limitations"]))
        self.assertNotIn(self.decoy, package_path.read_text(encoding="utf-8"))
        self.assertFalse([item for item in touched if self.decoy in item[1]], touched)


if __name__ == "__main__":
    unittest.main()
