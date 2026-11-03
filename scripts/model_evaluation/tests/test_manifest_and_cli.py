import contextlib
import copy
import io
import json
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

from scripts.code_analysis.contracts import canonical, digest
from scripts.model_evaluation import candidates, cli, components, guards, manifest, prepare, score
from scripts.model_evaluation.tests._fixtures import HEX, STATE, manifest_inputs, write_packages

# Hash of the canonical manifest built from the fixture inputs; any change to the contract moves it.
FIXTURE_MANIFEST_SHA256 = "9cc609ad5832ec943315e8b67a2be289446232938871af557fa89035b32b26f3"


class ManifestTests(unittest.TestCase):
    def test_hash_is_stable_and_independent_of_key_order(self):
        built, first = manifest.freeze_manifest(manifest_inputs())
        reordered = dict(reversed(list(manifest_inputs().items())))
        self.assertEqual(manifest.freeze_manifest(reordered)[1], first)
        self.assertEqual(first, digest(canonical(built)))
        self.assertEqual(manifest.manifest_sha256(built), first)
        self.assertEqual(first, FIXTURE_MANIFEST_SHA256)

    def test_any_value_change_changes_the_hash(self):
        _, base = manifest.freeze_manifest(manifest_inputs())
        changed = manifest_inputs()
        changed["pooling"]["randomSeed"] = 8
        self.assertNotEqual(manifest.freeze_manifest(changed)[1], base)

    def test_pre_registered_sections_are_added_and_gates_match_the_review(self):
        built, _ = manifest.freeze_manifest(manifest_inputs())
        self.assertEqual(built["claimGates"], manifest.CLAIM_GATES)
        self.assertEqual(built["rerunRule"]["allowedReasons"], list(guards.RERUN_REASONS))
        self.assertEqual(set(built), {"schema", *manifest.SECTIONS})

    def test_missing_and_unknown_sections_are_rejected(self):
        for section in manifest.INPUT_SECTIONS:
            incomplete = manifest_inputs()
            del incomplete[section]
            with self.subTest(missing=section), self.assertRaises(ValueError):
                manifest.freeze_manifest(incomplete)
        with self.assertRaises(ValueError):
            manifest.freeze_manifest({**manifest_inputs(), "extra": {}})
        built, _ = manifest.freeze_manifest(manifest_inputs())
        for path in (("bootstrap",), ("models", "e0"), ("pooling",), ("claimGates",), ("corpus",)):
            extended = copy.deepcopy(built)
            node = extended
            for key in path:
                node = node[key]
            node["unexpected"] = 1
            with self.subTest(extra_in=path), self.assertRaises(ValueError):
                manifest.validate_manifest(extended)
        top = copy.deepcopy(built)
        top["unexpected"] = {}
        with self.assertRaises(ValueError):
            manifest.validate_manifest(top)

    def test_pre_registered_values_cannot_be_loosened(self):
        built, _ = manifest.freeze_manifest(manifest_inputs())
        for path, value in ((("claimGates", "macroPrecisionMin"), 0.5),
                            (("claimGates", "spuriousAlertRateMax"), 0.5),
                            (("rerunRule", "rerunOnUnfavorableResult"), True),
                            (("bootstrap", "unit"), "query"),
                            (("pooling", "unjudgedLabel"), "NOT_RELEVANT")):
            loosened = copy.deepcopy(built)
            loosened[path[0]][path[1]] = value
            with self.subTest(path=path), self.assertRaises(ValueError):
                manifest.validate_manifest(loosened)

    def test_non_canonical_numbers_are_rejected(self):
        def with_value(section, key, value):
            data = manifest_inputs()
            data[section][key] = value
            return data
        broken = [with_value("candidateBudget", "maxCandidates", 40.0),
                  with_value("candidateBudget", "maxCandidates", True),
                  with_value("candidateBudget", "maxCandidates", -1),
                  with_value("pooling", "randomSeed", 1.5)]
        for sample in (float("nan"), float("inf"), 1.0):
            data = manifest_inputs()
            data["operatingPoints"]["validationThresholdRuleResult"]["threshold"] = sample
            broken.append(data)
        for data in broken:
            with self.assertRaises(ValueError):
                manifest.freeze_manifest(data)

    def test_corpus_bound_needs_an_identity_and_pending_cannot_have_one(self):
        bound = manifest_inputs()
        bound["corpus"] = {"status": manifest.CORPUS_BOUND, "identity": None}
        with self.assertRaises(ValueError):
            manifest.freeze_manifest(bound)
        bound["corpus"]["identity"] = {"corpusManifestSha256": HEX}
        self.assertEqual(manifest.freeze_manifest(bound)[0]["corpus"]["status"], manifest.CORPUS_BOUND)
        pending = manifest_inputs()
        pending["corpus"]["identity"] = {"corpusManifestSha256": HEX}
        with self.assertRaises(ValueError):
            manifest.freeze_manifest(pending)
        pending["corpus"] = {"status": "CLAIMED", "identity": None}
        with self.assertRaises(ValueError):
            manifest.freeze_manifest(pending)

    def test_withheld_markers_and_forbidden_evidence_keys_are_refused(self):
        marked = manifest_inputs()
        marked["excludedSources"] = ["checkpoint from final-test run"]
        with self.assertRaises(guards.SealedInputRefused):
            manifest.freeze_manifest(marked)
        secret = manifest_inputs()
        secret["models"]["e0"]["tokenizerHashes"]["password"] = HEX
        with self.assertRaises(ValueError):
            manifest.freeze_manifest(secret)

    def test_freeze_writes_once_and_hash_matches_file_bytes(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            data = manifest_inputs()
            paths = write_packages(root, data)
            source = root / "inputs.json"
            source.write_text(json.dumps(data), encoding="utf-8")
            checks = dict(evidence_paths=paths, state_hashes=dict(STATE),
                          threshold=data["operatingPoints"]["validationThresholdRuleResult"]["threshold"])
            args = dict(inputs_path=source, run_root=root / "runs", inputs_root=root, **checks)
            result = manifest.freeze(run_id="freeze-01", **args)
            written = (root / "runs" / "freeze-01" / manifest.MANIFEST_NAME).read_bytes()
            self.assertEqual(digest(written), result["manifestSha256"])
            guards.verify_manifest_bytes(written, result["manifestSha256"])
            with self.assertRaises(FileExistsError):
                manifest.freeze(run_id="freeze-01", **args)
            with self.assertRaises(guards.SealedInputRefused):
                manifest.freeze(run_id="freeze-final-test", **args)
            with self.assertRaises(ValueError):
                manifest.freeze(run_id="freeze-02", **{**args, "inputs_path": root.parent / "outside.json"})
            for name in ("e0", "e3", "verifier"):
                with self.subTest(missing=name), self.assertRaises(ValueError):
                    manifest.freeze(run_id="freeze-03", **{**args, "evidence_paths": {
                        k: v for k, v in checks["evidence_paths"].items() if k != name}})
            with self.assertRaises(ValueError):
                manifest.freeze(run_id="freeze-04", **{**args, "threshold": None})


FREEZE_CHECKS = ["--package", "e0=p0", "--package", "e3=p3", "--package", "verifier=pv",
                 "--state", "e0=" + HEX, "--state", "e3=" + HEX, "--state", "verifier=" + HEX, "--threshold", "0.9"]


class CliTests(unittest.TestCase):

    def test_freeze_has_no_optional_model_check(self):
        for drop in ("--package", "--state", "--threshold"):
            argv = ["freeze", "--run-id", "freeze-01", "--inputs", "in.json"]
            rest, skip = list(FREEZE_CHECKS), None
            while drop in rest:
                at = rest.index(drop)
                del rest[at:at + 2]
            with self.subTest(drop=drop), self.assertRaises(SystemExit):
                self.run_cli(argv + rest)
        with patch.object(manifest, "freeze", side_effect=ValueError("incomplete")) as freeze:
            code, _, err = self.run_cli(["freeze", "--run-id", "freeze-01", "--inputs", "in.json",
                                         "--package", "e0=p0", "--state", "e0=" + HEX, "--threshold", "0.9"])
        self.assertEqual(code, cli.EXIT_FAILED)

    def run_cli(self, argv):
        out, err = io.StringIO(), io.StringIO()
        with contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
            code = cli.main(argv)
        return code, out.getvalue(), err.getvalue()

    def test_withheld_arguments_are_refused_before_parsing_even_with_allow_flag(self):
        for argv in (["score", "--run-id", "r1", "--input", "final-test/m.json"],
                     ["score", "--allow-sealed", "--run-id", "r1", "--input", "sealed.json"],
                     ["freeze", "--run-id", "run-final-test", "--inputs", "x.json", *FREEZE_CHECKS]):
            with self.subTest(argv=argv):
                code, out, err = self.run_cli(argv)
                self.assertEqual(code, cli.EXIT_REFUSED)
                self.assertEqual(out, "")
                self.assertIn("REFUSED", err)

    def test_invalid_run_id_fails(self):
        code, out, err = self.run_cli(["freeze", "--run-id", "../x", "--inputs", "x.json", *FREEZE_CHECKS])
        self.assertEqual((code, out), (cli.EXIT_FAILED, ""))
        self.assertIn("FAILED", err)

    def test_freeze_is_wired_to_the_library(self):
        result = {"outcome": "SUCCEEDED", "runId": "freeze-01", "directory": Path("d"), "manifestSha256": HEX}
        with patch.object(manifest, "freeze", return_value=result) as freeze:
            code, out, _ = self.run_cli(["freeze", "--run-id", "freeze-01", "--inputs", "in.json", *FREEZE_CHECKS])
        self.assertEqual(code, 0)
        freeze.assert_called_once_with(
            run_id="freeze-01", inputs_path="in.json", threshold=0.9,
            evidence_paths={"e0": "p0", "e3": "p3", "verifier": "pv"},
            state_hashes={"e0": HEX, "e3": HEX, "verifier": HEX})
        self.assertEqual(json.loads(out)["manifestSha256"], HEX)

    def test_prepare_candidates_and_score_are_wired_to_their_stages(self):
        done = {"outcome": "SUCCEEDED", "directory": Path("d"), "manifestSha256": HEX,
                "preparedCorpusSha256": HEX, "previousManifestSha256": HEX, "candidatesSha256": HEX,
                "resultSha256": HEX}
        with patch.object(prepare, "run", return_value=done) as stage:
            code, out, _ = self.run_cli(["prepare", "--run-id", "p1", "--manifest-run-id", "f1", "--manifest-sha256", HEX,
                                         "--corpus", "c.json"])
        self.assertEqual(code, 0)
        stage.assert_called_once_with(run_id="p1", manifest_run_id="f1", manifest_sha256=HEX, corpus_path="c.json")
        with patch.object(components, "load_components", return_value="loaded"),                 patch.object(candidates, "run", return_value=done) as stage:
            code, out, _ = self.run_cli(["candidates", "--run-id", "c1", "--prepare-run-id", "p1",
                                         "--manifest-sha256", HEX, "--content-root", "root", "--components", "c.json"])
        self.assertEqual(code, 0)
        self.assertEqual(stage.call_args.kwargs["components"], "loaded")
        with patch.object(score, "run", return_value=done) as stage:
            code, out, _ = self.run_cli(["score", "--run-id", "s1", "--prepare-run-id", "p1",
                                         "--candidates-run-id", "c1", "--manifest-sha256", HEX,
                                         "--candidates-sha256", HEX, "--rerun-of", "s0",
                                         "--reason", "INFRASTRUCTURE_FAULT"])
        self.assertEqual(code, 0)
        self.assertEqual((stage.call_args.kwargs["rerun_of"], stage.call_args.kwargs["reason"]),
                         ("s0", "INFRASTRUCTURE_FAULT"))
        self.assertEqual(json.loads(out)["resultSha256"], HEX)

    def test_a_refused_rerun_reason_is_rejected_by_the_parser(self):
        with self.assertRaises(SystemExit):
            self.run_cli(["score", "--run-id", "s1", "--prepare-run-id", "p1", "--candidates-run-id", "c1",
                          "--manifest-sha256", HEX, "--candidates-sha256", HEX, "--reason", "UNFAVORABLE_RESULT"])

    def test_scoring_refusals_surface_as_failures(self):
        with patch.object(score, "run", side_effect=guards.ScoringRunRefused("again")):
            code, out, err = self.run_cli(["score", "--run-id", "s1", "--prepare-run-id", "p1",
                                           "--candidates-run-id", "c1", "--manifest-sha256", HEX,
                                           "--candidates-sha256", HEX])
        self.assertEqual((code, out), (cli.EXIT_FAILED, ""))
        self.assertIn("again", err)

    def test_library_failure_is_reported_as_failure(self):
        with patch.object(manifest, "freeze", side_effect=FileExistsError("exists")):
            code, out, err = self.run_cli(["freeze", "--run-id", "freeze-01", "--inputs", "in.json", *FREEZE_CHECKS])
        self.assertEqual((code, out), (cli.EXIT_FAILED, ""))
        self.assertIn("FAILED", err)


if __name__ == "__main__":
    unittest.main()
