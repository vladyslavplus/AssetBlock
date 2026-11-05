import json
import shutil
import unittest
from pathlib import Path

from scripts.code_analysis.contracts import canonical, digest
from scripts.model_evaluation import (candidates, guards, manifest, models_check, prepare, provenance, score,
                                      validation)
from scripts.model_evaluation.tests._fixtures import HEX, STATE
from scripts.model_evaluation.tests._pipeline import Workspace, fake_identity


def read_json(path):
    return json.loads(Path(path).read_bytes())


class ChainTests(unittest.TestCase):
    def setUp(self):
        self.ws = Workspace(self)
        self.prepared, self.cands = self.ws.full_run()
        self.ws.score(self.prepared, self.cands)

    def dir(self, run_id):
        return guards.run_dir(run_id, self.ws.run_root)

    def validate(self, run_id="evidence-01", **kwargs):
        kwargs.setdefault("capture", fake_identity)
        return validation.run(run_id=run_id, freeze_run_id="freeze-01", prepare_run_id="prepare-01",
                              candidates_run_id="cand-01", score_run_id="score-01", run_root=self.ws.run_root,
                              registry_root=self.ws.registry, **kwargs)

    def rewrite(self, directory, name, change):
        path = self.dir(directory) / name
        document = read_json(path)
        change(document)
        path.write_bytes(canonical(document))

    def test_a_consistent_chain_produces_a_hash_only_package(self):
        result = self.validate()
        package = read_json(self.dir("evidence-01") / validation.PACKAGE_NAME)
        self.assertEqual(digest((self.dir("evidence-01") / validation.PACKAGE_NAME).read_bytes()),
                         result["packageSha256"])
        by_role = {row["role"]: row for row in package["files"]}
        self.assertEqual(set(by_role), {"freezeManifest", "boundManifest", "preparedCorpus", "candidates", "result"})
        self.assertEqual(by_role["result"]["sha256"], package["hashes"]["resultSha256"])
        self.assertEqual(package["hashes"]["candidatesSha256"], self.cands["candidatesSha256"])
        self.assertEqual(package["models"], read_json(self.dir("prepare-01") / manifest.MANIFEST_NAME)["models"])
        self.assertEqual([step["stage"] for step in package["reconstruction"]],
                         ["freeze", "prepare", "candidates", "score"])
        self.assertTrue(package["codeIdentity"]["cleanAtScoring"])
        self.assertEqual(package["codeIdentity"]["score"]["commit"], "c" * 40)
        self.assertEqual(package["withheldTextReferences"], [])

    def test_three_statuses_stay_separate(self):
        statuses = self.validate()["statuses"]
        self.assertTrue(statuses["runCompleted"])
        self.assertEqual(statuses["reviewerAcceptance"], "NOT_RECORDED")
        outcome = statuses["claimGateOutcome"]
        # the two-language fixture never covers every supported group, so no overall claim can validate
        self.assertEqual(outcome["PRODUCT"]["detector"]["status"], "UNVALIDATED")
        self.assertEqual(outcome["PRODUCT"]["replacement"]["status"], "UNVALIDATED")
        self.assertEqual(outcome["FINE_TUNED_RETRIEVAL"]["detector"]["status"], "FAILED")
        # a failed claim gate neither fails the run nor changes reviewer acceptance
        self.assertTrue(statuses["runCompleted"])

    def test_reviewer_acceptance_cannot_be_asserted_by_the_result(self):
        self.rewrite("score-01", score.RESULT_NAME, lambda d: d["statuses"].update(reviewerAcceptance="ACCEPTED"))
        with self.assertRaises(ValueError) as context:
            self.validate()
        self.assertIn("NOT_RECORDED", str(context.exception))

    def test_limitations_are_generated_from_the_manifest_and_corpus(self):
        notes = read_json(self.dir(self.validate()["runId"]) / validation.PACKAGE_NAME)["limitations"]
        text = " ".join(notes)
        self.assertIn("java", text)
        self.assertIn("excluded-source list", text)
        self.assertIn("Reviewer acceptance is NOT_RECORDED", text)
        self.assertIn("typescript:tsx", text)

    def test_a_changed_document_in_any_link_is_refused(self):
        cases = {
            "freeze manifest": ("freeze-01", manifest.MANIFEST_NAME, lambda d: d["pooling"].update(randomSeed=8)),
            "bound manifest": ("prepare-01", manifest.MANIFEST_NAME, lambda d: d["pooling"].update(randomSeed=8)),
            "prepared corpus": ("prepare-01", prepare.PREPARED_NAME, lambda d: d["counts"].update(queries=99)),
            "candidates": ("cand-01", candidates.CANDIDATES_NAME,
                           lambda d: d["queries"]["qa"]["pipelines"]["PRODUCT"].update(findings=[])),
            "result": ("score-01", score.RESULT_NAME,
                       lambda d: d["pipelines"]["PRODUCT"]["summary"]["macro"].update(precision=0.5)),
        }
        for name, (directory, file, change) in cases.items():
            original = (self.dir(directory) / file).read_bytes()
            self.rewrite(directory, file, change)
            with self.subTest(name), self.assertRaises(ValueError):
                self.validate(run_id="evidence-" + name.replace(" ", "-"))
            (self.dir(directory) / file).write_bytes(original)
        self.assertEqual(self.validate(run_id="evidence-ok")["outcome"], "SUCCEEDED")

    def test_a_single_extra_byte_breaks_canonical_form(self):
        for directory, file in (("freeze-01", manifest.MANIFEST_NAME), ("prepare-01", prepare.PREPARED_NAME),
                                ("cand-01", candidates.CANDIDATES_NAME), ("score-01", score.RESULT_NAME)):
            path = self.dir(directory) / file
            original = path.read_bytes()
            path.write_bytes(original + b" ")
            with self.subTest(file=directory), self.assertRaises(ValueError):
                self.validate(run_id="evidence-byte")
            path.write_bytes(original)

    def test_missing_failed_and_extra_artifacts_are_refused(self):
        result = self.dir("score-01") / score.RESULT_NAME
        raw = result.read_bytes()
        result.unlink()
        with self.assertRaises(ValueError) as context:
            self.validate(run_id="evidence-missing")
        self.assertIn("missing", str(context.exception))
        result.write_bytes(raw)
        failed = self.dir("score-01") / "failed-run.json"
        failed.write_bytes(canonical({"outcome": "FAILED"}))
        with self.assertRaises(ValueError) as context:
            self.validate(run_id="evidence-failed")
        self.assertIn("failed run", str(context.exception))
        failed.unlink()
        extra = self.dir("cand-01") / "notes.txt"
        extra.write_bytes(b"x")
        with self.assertRaises(ValueError) as context:
            self.validate(run_id="evidence-extra")
        self.assertIn("unexpected files", str(context.exception))
        extra.unlink()
        empty = self.dir("cand-01") / candidates.CANDIDATES_NAME
        empty_raw = empty.read_bytes()
        empty.write_bytes(b"")
        with self.assertRaises(ValueError):
            self.validate(run_id="evidence-empty")
        empty.write_bytes(empty_raw)
        self.assertEqual(self.validate(run_id="evidence-restored")["outcome"], "SUCCEEDED")

    def test_registry_record_must_match_the_result(self):
        shutil.rmtree(self.ws.registry)
        with self.assertRaises(ValueError):
            self.validate()

    def test_the_package_is_write_once(self):
        self.validate()
        with self.assertRaises(FileExistsError):
            self.validate()

    def test_sealed_text_is_refused_unless_exactly_allowlisted(self):
        documents = {"candidates": {"queries": {"q": {"note": "from the final-test partition"}}}}
        with self.assertRaises(ValueError):
            validation.scan_sealed_text(documents)
        with self.assertRaises(ValueError):
            validation.scan_sealed_text({"candidates": {"sealedKey": 1}})
        allow = {("candidates", ("queries", "q", "note")): "from the final-test partition"}
        self.assertEqual(validation.scan_sealed_text(documents, allow),
                         [{"document": "candidates", "keyPath": ["queries", "q", "note"], "referenceOnly": True}])
        wrong_value = {("candidates", ("queries", "q", "note")): "something else"}
        with self.assertRaises(ValueError):
            validation.scan_sealed_text(documents, wrong_value)
        wrong_place = {("result", ("queries", "q", "note")): "from the final-test partition"}
        with self.assertRaises(ValueError):
            validation.scan_sealed_text(documents, wrong_place)
        validation.scan_sealed_text({"result": {"spuriousAlertRate": 0.1}})


class DirtyScoringTests(unittest.TestCase):
    def test_dirty_scoring_is_recorded_and_require_clean_refuses_it(self):
        ws = Workspace(self)
        prepared, cands = ws.full_run()
        ws.score(prepared, cands, capture=lambda: fake_identity(dirty=True))
        kwargs = dict(freeze_run_id="freeze-01", prepare_run_id="prepare-01", candidates_run_id="cand-01",
                      score_run_id="score-01", run_root=ws.run_root, registry_root=ws.registry,
                      capture=fake_identity)
        with self.assertRaises(ValueError) as context:
            validation.run(run_id="evidence-strict", require_clean=True, **kwargs)
        self.assertIn("dirty", str(context.exception))
        result = validation.run(run_id="evidence-lenient", **kwargs)
        package = read_json(guards.run_dir(result["runId"], ws.run_root) / validation.PACKAGE_NAME)
        self.assertFalse(package["codeIdentity"]["cleanAtScoring"])
        self.assertTrue(package["codeIdentity"]["score"]["dirty"])


class ProvenanceTests(unittest.TestCase):
    def test_source_closure_hashes_loaded_repository_modules(self):
        closure = provenance.source_closure()
        paths = {row["path"] for row in closure}
        self.assertIn("scripts/model_evaluation/provenance.py", paths)
        self.assertTrue(all(len(row["sha256"]) == 64 for row in closure))
        self.assertEqual([row["path"] for row in closure], sorted(paths))
        validation.scan_sealed_text({"closure": closure})

    def test_environment_identity_lists_installed_packages_without_installing(self):
        environment = provenance.environment_identity()
        self.assertGreater(environment["packageCount"], 0)
        self.assertEqual(len(environment["packageListSha256"]), 64)

    def test_capture_uses_git_through_the_injected_runner(self):
        calls = []

        def git(arguments, repo_root):
            calls.append(arguments)
            return "d" * 40 + "\n" if arguments[0] == "rev-parse" else " M scripts/x.py\n?? scripts/y.py\n"

        identity = provenance.capture(git=git)
        self.assertEqual((identity["commit"], identity["dirty"], identity["dirtyPathCount"]), ("d" * 40, True, 2))
        self.assertEqual([call[0] for call in calls], ["rev-parse", "status"])

    def test_capture_against_the_real_repository(self):
        try:
            identity = provenance.capture()
        except OSError as error:
            self.skipTest("git is not usable here: %s" % error)
        self.assertRegex(identity["commit"], "^[0-9a-f]{40}$")
        self.assertIsInstance(identity["dirty"], bool)


class ModelsCheckTests(unittest.TestCase):
    def setUp(self):
        self.ws = Workspace(self)
        self.freeze = self.ws.freeze()
        self.manifest = read_json(self.ws.prepared_dir("freeze-01") / manifest.MANIFEST_NAME)
        self.package = self.ws.root / "package-retrieval.json"
        self.digest = digest(self.package.read_bytes())

    def manifest_with(self, digest_value):
        document = json.loads(json.dumps(self.manifest))
        document["models"]["e0"]["evidencePackageSha256"] = digest_value
        return document

    def test_unverified_models_are_reported_not_passed(self):
        report = models_check.check(self.manifest)
        self.assertFalse(report["allVerified"])
        self.assertEqual({row["status"] for row in report["models"].values()}, {models_check.NOT_VERIFIED})

    def test_a_matching_package_is_verified_and_a_mismatch_refused(self):
        report = models_check.check(self.manifest_with(self.digest), {"e0": self.package}, root=self.ws.root,
                                    state_hashes={"e0": STATE["e0"]})
        self.assertEqual(report["models"]["e0"]["status"], models_check.VERIFIED)
        self.assertEqual(report["models"]["e3"]["status"], models_check.NOT_VERIFIED)
        self.assertFalse(report["allVerified"])
        without_state = models_check.check(self.manifest_with(self.digest), {"e0": self.package}, root=self.ws.root)
        self.assertEqual(without_state["models"]["e0"]["status"], models_check.NOT_VERIFIED)
        with self.assertRaises(ValueError):
            models_check.check(self.manifest_with(HEX), {"e0": self.package}, root=self.ws.root)

    def test_checkpoint_state_and_threshold_must_equal_the_frozen_values(self):
        manifest = self.manifest_with(self.digest)
        with self.assertRaises(ValueError):
            models_check.check(manifest, {"e0": self.package}, root=self.ws.root, state_hashes={"e0": "00" * 32})
        threshold = manifest["operatingPoints"]["validationThresholdRuleResult"]["threshold"]
        with self.assertRaises(ValueError):
            models_check.check(manifest, threshold=threshold + 0.5)
        verified = models_check.check(manifest, threshold=threshold)
        self.assertEqual(verified["threshold"], models_check.VERIFIED)
        self.assertFalse(verified["allVerified"])
        everything = models_check.check(manifest, {"e0": self.package}, root=self.ws.root, threshold=threshold,
                                        state_hashes={"e0": STATE["e0"], "e3": STATE["e3"], "verifier": STATE["verifier"]})
        self.assertEqual(everything["models"]["e0"]["status"], models_check.VERIFIED)

    def test_withheld_names_are_refused_before_opening_and_unknown_models_rejected(self):
        with self.assertRaises(guards.SealedInputRefused):
            models_check.check(self.manifest, {"e0": self.ws.root / "final-test" / "package.json"},
                               root=self.ws.root)
        with self.assertRaises(ValueError):
            models_check.check(self.manifest, {"other": self.package}, root=self.ws.root)
        with self.assertRaises(ValueError):
            models_check.check(self.manifest_with(self.digest), {"e0": self.ws.root.parent / "x.json"},
                               root=self.ws.root)

    def test_malformed_identities_are_refused(self):
        broken = json.loads(json.dumps(self.manifest))
        broken["models"]["e3"]["recordedIdentity"] = " "
        with self.assertRaises(ValueError):
            models_check.check(broken)
        del broken["models"]["e3"]
        with self.assertRaises(ValueError):
            models_check.check(broken)


if __name__ == "__main__":
    unittest.main()
