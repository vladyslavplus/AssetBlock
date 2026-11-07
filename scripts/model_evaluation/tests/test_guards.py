import json
import tempfile
import unittest
from pathlib import Path

from scripts.code_analysis.contracts import canonical, digest
from scripts.model_evaluation import guards
from scripts.model_evaluation.tests._fixtures import HEX

CORPUS = "12" * 32
KEY = "56" * 32
LABELS = "78" * 32

SEALED_FORMS = ("final-test", "FINAL_TEST", "finalTest", "Final Test", "final.test", "sealed", "seal",
                "SEALED-membership", "ｓｅａｌ", "ﬁnal-test")
EVASIONS = ("heldout", "held-out", "Held_Out", "holdout", "hold-out", "withheld", "with-held", "s_e_a_l",
            "s-e-a-l", "final1test", "f1nal-test", "f1nal_t3st", "s3al", "se\u200bal", "fin\u200dal-test",
            "ѕeal", "sеal", "ѕеаl", "fіnal-test", "hеld-out", "ωithheld", "seaＬ", "S.E.A.L", "h0ldout",
            "he1d0ut", "s\u0301eal", "final\u00adtest")


class SealedRefusalTests(unittest.TestCase):
    def test_every_marker_form_is_refused_in_every_input_kind(self):
        for form in SEALED_FORMS:
            inputs = {"paths": ["artifacts/x/%s/members.json" % form], "ids": ["seed-%s-01" % form],
                      "roles": [form], "cli_args": ["--partition=" + form, "--input=D:\\x\\%s\\a.json" % form],
                      "manifest": {"a": [{"nested": form}]}}
            for kind, value in inputs.items():
                with self.subTest(form=form, kind=kind), self.assertRaises(guards.SealedInputRefused):
                    guards.refuse_sealed(**{kind: value})
            with self.subTest(form=form, kind="manifest-key"), self.assertRaises(guards.SealedInputRefused):
                guards.refuse_sealed(manifest={form: 1})

    def test_evasive_spellings_are_refused(self):
        for form in EVASIONS:
            for kind, value in (("ids", [form]), ("paths", ["a/" + form + "/b"]), ("cli_args", ["--x=" + form]),
                                ("manifest", {"k": form})):
                with self.subTest(form=form, kind=kind), self.assertRaises(guards.SealedInputRefused):
                    guards.refuse_sealed(**{kind: value})

    def test_role_aliases_are_refused_only_as_a_whole_role(self):
        for role in ("test", "testing", "Test", "T-E-S-T", "t3st", "Testing"):
            with self.subTest(role=role), self.assertRaises(guards.SealedInputRefused):
                guards.refuse_sealed(roles=[role])
        guards.refuse_sealed(ids=["latest-run-contest"], paths=["tests/unit/a.py"])

    def test_the_corpus_role_allowlist_is_closed(self):
        self.assertEqual(guards.require_corpus_role("evaluation"), "evaluation")
        for role in ("train", "validation", "dev", "", "Evaluation", "evaluation "):
            with self.subTest(role=role), self.assertRaises(ValueError):
                guards.require_corpus_role(role)
        with self.assertRaises(guards.SealedInputRefused):
            guards.require_corpus_role("heldout")

    def test_there_is_no_flag_or_receipt_that_admits_a_marker(self):
        for kwargs in ({"allow_sealed": True}, {"receipt": {}}, {"frozen_receipt_sha256": HEX}):
            with self.subTest(kwargs=kwargs), self.assertRaises(TypeError):
                guards.refuse_sealed(paths=["final-test/members.json"], **kwargs)
        for name in ("validate_receipt", "receipt_sha256", "require_scoring_receipt"):
            self.assertFalse(hasattr(guards, name), name)
        with self.assertRaises(guards.SealedInputRefused):
            guards.refuse_sealed(cli_args=["--allow-sealed", "--input", "sealed.json"])

    def test_ordinary_inputs_and_manifest_words_are_accepted(self):
        guards.refuse_sealed(paths=["artifacts/validation/queries.json"], ids=["seed-01"],
                             roles=["evaluation"], cli_args=["score", "--run-id", "run-1"],
                             manifest={"corpus": {"status": "PENDING_OWNER_DECISION"},
                                       "claimGates": {"spuriousAlertRateMax": 0.05},
                                       "hash": HEX, "rule": "false findings per positive query"})


class RunAndOutputTests(unittest.TestCase):
    def test_run_ids_are_validated(self):
        self.assertEqual(guards.validate_run_id("eval-20261103-01"), "eval-20261103-01")
        for bad in ("", "../x", "a/b", "-start", "x" * 81, None, 5):
            with self.subTest(bad=bad), self.assertRaises(ValueError):
                guards.validate_run_id(bad)
        with self.assertRaises(guards.SealedInputRefused):
            guards.validate_run_id("eval-final-test-01")
        with self.assertRaises(guards.SealedInputRefused):
            guards.validate_run_id("eval-heldout-01")

    def test_write_once_refuses_a_collision_and_keeps_the_first_bytes(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            target = root / "run-1" / "evidence.json"
            guards.write_output(target, b"first", root)
            with self.assertRaises(FileExistsError):
                guards.write_output(target, b"second", root)
            self.assertEqual(target.read_bytes(), b"first")

    def test_output_cannot_leave_its_root(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp) / "root"
            root.mkdir()
            with self.assertRaises(ValueError):
                guards.write_output(root / ".." / "escape.json", b"x", root)


class ManifestGateTests(unittest.TestCase):
    def test_canonical_bytes_with_the_frozen_hash_pass(self):
        raw = canonical({"a": 1, "b": [2]})
        self.assertEqual(guards.verify_manifest_bytes(raw, digest(raw)), {"a": 1, "b": [2]})

    def test_drift_and_noncanonical_bytes_are_refused(self):
        raw = canonical({"a": 1})
        frozen = digest(raw)
        with self.assertRaises(ValueError):
            guards.verify_manifest_bytes(canonical({"a": 2}), frozen)
        pretty = json.dumps({"a": 1}, indent=2).encode()
        with self.assertRaises(ValueError):
            guards.verify_manifest_bytes(pretty, digest(pretty))
        with self.assertRaises(ValueError):
            guards.verify_manifest_bytes(raw, "not-a-hash")


class SingleRunRuleTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)

    def register(self, run_id, manifest=HEX, key=KEY, corpus=CORPUS, labels=LABELS, **kwargs):
        return guards.register_scoring_run(manifest_sha256=manifest, key_sha256=key, package_triple_sha256=key,
                                           state_triple_sha256=key[::-1], corpus_identity_sha256=corpus,
                                           label_sha256=labels, run_id=run_id, root=self.root, **kwargs)

    def test_first_run_is_free_and_second_needs_reason_and_reference(self):
        first = self.register("score-01")
        self.assertEqual((first["sequence"], first["rerunOf"]), (1, None))
        with self.assertRaises(guards.ScoringRunRefused):
            self.register("score-02")
        for reason in ("UNFAVORABLE_RESULT", "IMPLEMENTATION", None):
            with self.subTest(reason=reason), self.assertRaises(guards.ScoringRunRefused):
                self.register("score-02", rerun_of="score-01", reason=reason)
        with self.assertRaises(guards.ScoringRunRefused):
            self.register("score-02", rerun_of="score-99", reason="IMPLEMENTATION_DEFECT")
        second = self.register("score-02", rerun_of="score-01", reason="INFRASTRUCTURE_FAULT")
        self.assertEqual((second["sequence"], second["rerunOf"], second["priorRunsRetained"]),
                         (2, "score-01", ["score-01"]))

    def test_changed_labels_or_corpus_on_the_same_models_still_need_a_reason(self):
        first = self.register("score-01")
        self.assertEqual(first["labelSha256"], LABELS)
        with self.assertRaises(guards.ScoringRunRefused):
            self.register("score-02", labels="9a" * 32)
        with self.assertRaises(guards.ScoringRunRefused):
            self.register("score-03", corpus="bc" * 32)
        again = self.register("score-02", labels="9a" * 32, rerun_of="score-01", reason="IMPLEMENTATION_DEFECT")
        self.assertEqual(again["labelSha256"], "9a" * 32)
        self.assertEqual(self.register("score-04", key="de" * 32)["sequence"], 1)

    def test_a_second_manifest_over_the_same_corpus_is_a_rerun(self):
        self.register("score-01", manifest=HEX)
        other_manifest = "cd" * 32
        with self.assertRaises(guards.ScoringRunRefused):
            self.register("score-02", manifest=other_manifest)
        with self.assertRaises(guards.ScoringRunRefused):
            self.register("score-02", manifest=other_manifest, rerun_of="score-01", reason="UNFAVORABLE_RESULT")
        again = self.register("score-02", manifest=other_manifest, rerun_of="score-01",
                              reason="IMPLEMENTATION_DEFECT")
        self.assertEqual((again["sequence"], again["priorRunsRetained"]), (2, ["score-01"]))
        self.assertEqual(self.register("score-03", manifest=other_manifest, key="34" * 32)["sequence"], 1)

    def test_same_run_id_and_first_run_rerun_claims_are_refused(self):
        self.register("score-01")
        with self.assertRaises(guards.ScoringRunRefused):
            self.register("score-01", rerun_of="score-01", reason="IMPLEMENTATION_DEFECT")
        with self.assertRaises(guards.ScoringRunRefused):
            self.register("score-09", key="56" * 31 + "00", reason="IMPLEMENTATION_DEFECT")

    def test_attempt_records_are_write_once(self):
        self.register("score-01")
        record = next((self.root / KEY).glob("attempt-*.json"))
        with self.assertRaises(FileExistsError):
            guards.write_output(record, b"overwrite", self.root)


if __name__ == "__main__":
    unittest.main()
