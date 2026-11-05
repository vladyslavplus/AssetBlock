"""Third review: package-bound model facts, registry either-match, hash words, marker splits, parity, EXHAUSTIVE, process."""

import contextlib
import copy
import io
import json
import shutil
import unittest
from pathlib import Path
from unittest.mock import patch

from scripts.code_analysis.contracts import canonical, digest
from scripts.model_evaluation import (candidates, cli, guards, manifest, models_check, parity, score, validation)
from scripts.model_evaluation.tests import _fixtures
from scripts.model_evaluation.tests._fixtures import STATE, manifest_inputs, package_documents, write_packages
from scripts.model_evaluation.tests._pipeline import (Workspace, fake_components, fake_identity,
                                                      small_manifest_inputs)


def read_json(path):
    return json.loads(Path(path).read_bytes())


class RegistryEitherMatchTests(unittest.TestCase):
    """B1(1): the same packages with another checkpoint, or the same checkpoints in other packages, is a rerun."""

    def setUp(self):
        self.ws = Workspace(self)
        self.prepared, self.cands = self.ws.full_run()
        self.ws.score(self.prepared, self.cands)

    def second_score(self, prepared, cands, **kwargs):
        return score.run(run_id="score-02", prepare_run_id="prepare-02", candidates_run_id="cand-02",
                         manifest_sha256=prepared["manifestSha256"], candidates_sha256=cands["candidatesSha256"],
                         run_root=self.ws.run_root, registry_root=self.ws.registry, capture=fake_identity, **kwargs)

    def refreeze(self, inputs):
        self.ws.freeze("freeze-02", inputs=inputs)
        prepared = self.ws.prepare("prepare-02", "freeze-02")
        return prepared, self.ws.candidates(run_id="cand-02", prepare_run_id="prepare-02")

    def test_the_key_covers_all_package_hashes_and_all_state_hashes(self):
        models = copy.deepcopy(manifest_inputs()["models"])
        key = guards.model_key(models)
        for name in guards.MODEL_ORDER:
            for field in ("evidencePackageSha256", "stateSha256"):
                changed = copy.deepcopy(models)
                changed[name][field] = "99" * 32
                with self.subTest(name=name, field=field):
                    self.assertNotEqual(guards.model_key(changed), key)

    def test_package_triple_match_does_not_need_a_state_match(self):
        models = read_json(guards.run_dir("prepare-01", self.ws.run_root) / manifest.MANIFEST_NAME)["models"]
        triples = guards.model_triples(models)
        attempts = guards.matching_attempts(self.ws.registry, triples["packageTripleSha256"], "00" * 32)
        self.assertEqual([row["runId"] for row in attempts], ["score-01"])

    def test_state_triple_match_does_not_need_a_package_match(self):
        models = read_json(guards.run_dir("prepare-01", self.ws.run_root) / manifest.MANIFEST_NAME)["models"]
        triples = guards.model_triples(models)
        attempts = guards.matching_attempts(self.ws.registry, "00" * 32, triples["stateTripleSha256"])
        self.assertEqual([row["runId"] for row in attempts], ["score-01"])

    def test_the_same_packages_with_another_e3_state_is_a_rerun(self):
        with patch.dict(STATE, {"e3": "77" * 32}):
            prepared, cands = self.refreeze(small_manifest_inputs())
            old = read_json(guards.run_dir("prepare-01", self.ws.run_root) / manifest.MANIFEST_NAME)["models"]
            new = read_json(guards.run_dir("prepare-02", self.ws.run_root) / manifest.MANIFEST_NAME)["models"]
            self.assertEqual({n: old[n]["evidencePackageSha256"] for n in old},
                             {n: new[n]["evidencePackageSha256"] for n in new})
            self.assertNotEqual(guards.model_key(old), guards.model_key(new))
            with self.assertRaises(guards.ScoringRunRefused):
                self.second_score(prepared, cands)
            with self.assertRaises(guards.ScoringRunRefused):
                self.second_score(prepared, cands, rerun_of="score-01", reason="UNFAVORABLE_RESULT")
            done = self.second_score(prepared, cands, rerun_of="score-01", reason="IMPLEMENTATION_DEFECT")
        self.assertEqual(done["outcome"], "SUCCEEDED")
        self.assertEqual(read_json(guards.run_dir("score-02", self.ws.run_root) / score.RESULT_NAME)[
            "registration"]["sequence"], 2)

    def test_the_same_states_in_other_packages_is_a_rerun(self):
        with patch.object(_fixtures, "BASE_FILE_HASHES", {**_fixtures.BASE_FILE_HASHES, "extra.json": "ee" * 32}), \
                patch.dict(_fixtures.PACKAGE_BINDING["e0"]["trustedModelFileHashes"], {"extra.json": "ee" * 32}):
            changed = small_manifest_inputs()
            prepared, cands = self.refreeze(changed)
            old = read_json(guards.run_dir("prepare-01", self.ws.run_root) / manifest.MANIFEST_NAME)["models"]
            new = read_json(guards.run_dir("prepare-02", self.ws.run_root) / manifest.MANIFEST_NAME)["models"]
            self.assertNotEqual(old["e0"]["evidencePackageSha256"], new["e0"]["evidencePackageSha256"])
            self.assertEqual({n: old[n]["stateSha256"] for n in old}, {n: new[n]["stateSha256"] for n in new})
            with self.assertRaises(guards.ScoringRunRefused):
                self.second_score(prepared, cands)

    def test_unrelated_models_still_get_a_free_first_run(self):
        with patch.dict(STATE, {"e0": "11" * 32, "e3": "22" * 32, "verifier": "33" * 32}), \
                patch.object(_fixtures, "BASE_FILE_HASHES", {**_fixtures.BASE_FILE_HASHES, "other.json": "dd" * 32}), \
                patch.dict(_fixtures.PACKAGE_BINDING["e0"]["trustedModelFileHashes"], {"other.json": "dd" * 32}), \
                patch.object(_fixtures, "E3_MANIFEST_HASH", "d4" * 32), \
                patch.dict(_fixtures.PACKAGE_BINDING["e3"], {"checkpointManifestSha256": "d4" * 32}), \
                patch.object(_fixtures, "CHECKPOINT_HASHES", ["c%d" % n * 32 for n in (1, 2, 8, 4, 5)]), \
                patch.dict(_fixtures.PACKAGE_BINDING["verifier"], {"checkpointSha256": "c8" * 32}):
            prepared, cands = self.refreeze(small_manifest_inputs())
            done = self.second_score(prepared, cands)
        self.assertEqual(read_json(guards.run_dir("score-02", self.ws.run_root) / score.RESULT_NAME)[
            "registration"]["sequence"], 1)
        self.assertEqual(done["outcome"], "SUCCEEDED")


class PackageBindingTests(unittest.TestCase):
    """B1(2): selected checkpoint and threshold are read from the parsed packages at fixed key paths."""

    def setUp(self):
        self.ws = Workspace(self)
        self.ws.freeze()
        self.manifest = read_json(self.ws.prepared_dir("freeze-01") / manifest.MANIFEST_NAME)

    def packages(self, **changes):
        data = manifest_inputs()
        data["operatingPoints"]["validationThresholdRuleResult"]["threshold"] = (
            self.manifest["operatingPoints"]["validationThresholdRuleResult"]["threshold"])
        paths = write_packages(self.ws.root, data, **changes)
        document = copy.deepcopy(self.manifest)
        for name, entry in data["models"].items():
            document["models"][name]["evidencePackageSha256"] = entry["evidencePackageSha256"]
        return document, paths

    def check(self, document, paths, threshold=None):
        return models_check.check(document, paths, root=self.ws.root, state_hashes=dict(STATE),
                                  threshold=document["operatingPoints"]["validationThresholdRuleResult"]["threshold"]
                                  if threshold is None else threshold)

    def test_matching_packages_verify_every_model(self):
        document, paths = self.packages()
        self.assertTrue(self.check(document, paths)["allVerified"])

    def test_real_key_paths_are_the_documented_ones(self):
        values = {name: manifest.package_values(name, doc) for name, doc in package_documents().items()}
        self.assertEqual(values["e3"], {"checkpointManifestSha256": _fixtures.E3_MANIFEST_HASH})
        self.assertEqual(values["verifier"], {"selectedEpoch": 3, "checkpointSha256": _fixtures.CHECKPOINT_HASHES[2],
                                              "threshold": _fixtures.THRESHOLD})
        self.assertEqual(values["e0"]["trustedModelFileHashes"], _fixtures.BASE_FILE_HASHES)

    def test_a_different_checkpoint_in_the_package_is_refused(self):
        for name, change in (
                ("e3", {"identities": {"trustedModelFileHashes": _fixtures.BASE_FILE_HASHES,
                                       "checkpointManifestSha256": {"E3": "00" * 32}}}),
                ("verifier", {"selectedEpoch": 4}),
                ("verifier", {"identities": {"checkpointHashes": ["00" * 32] * 5}}),
                ("e0", {"identities": {"trustedModelFileHashes": {"vocab.json": "00" * 32},
                                       "checkpointManifestSha256": {"E3": _fixtures.E3_MANIFEST_HASH}}})):
            documents = package_documents()
            documents[name] = {**documents[name], **change}
            document, paths = self.write(documents)
            with self.subTest(name=name, change=list(change)), self.assertRaises(ValueError) as context:
                self.check(document, paths)
            self.assertIn("differs from its evidence package", str(context.exception))

    def write(self, documents):
        document = copy.deepcopy(self.manifest)
        paths = {}
        for name, value in documents.items():
            raw = canonical(value)
            path = self.ws.root / ("changed-%s.json" % name)
            path.write_bytes(raw)
            document["models"][name]["evidencePackageSha256"] = digest(raw)
            paths[name] = path
        return document, paths

    def test_a_threshold_in_the_package_that_differs_from_the_frozen_one_is_refused(self):
        document, paths = self.packages(verifier__metrics={"pipelines": {"E3": {"threshold": {"threshold": 0.77}}}})
        with self.assertRaises(ValueError) as context:
            self.check(document, paths)
        self.assertIn("threshold", str(context.exception))

    def test_a_package_without_the_fields_fails_closed(self):
        broken = package_documents()
        for name, drop in (("e3", ("identities", "checkpointManifestSha256")), ("e0", ("identities",)),
                           ("verifier", ("selectedEpoch",)), ("verifier", ("metrics",))):
            documents = copy.deepcopy(broken)
            node = documents[name]
            for step in drop[:-1]:
                node = node[step]
            del node[drop[-1]]
            document, paths = self.write(documents)
            with self.subTest(name=name, drop=drop), self.assertRaises(ValueError) as context:
                self.check(document, paths)
            self.assertIn("lacks", str(context.exception))

    def test_the_unbound_state_must_be_recorded_with_its_reason(self):
        for change in ({}, {"stateSha256": "because"}, {"stateSha256": manifest.UNBOUND_STATE_REASON, "other": "x"}):
            data = manifest_inputs()
            data["models"]["e3"]["unboundByPackage"] = change
            with self.subTest(change=change), self.assertRaises(ValueError):
                manifest.freeze_manifest(data)
        data = manifest_inputs()
        del data["models"]["e3"]["unboundByPackage"]
        with self.assertRaises(ValueError):
            manifest.freeze_manifest(data)

    def test_a_manifest_binding_that_differs_from_the_package_stops_the_freeze(self):
        data = small_manifest_inputs()
        data["models"]["verifier"]["packageBinding"]["selectedEpoch"] = 5
        paths = write_packages(self.ws.root, data)
        path = self.ws.write_json("bad-binding.json", data)
        with self.assertRaises(ValueError):
            manifest.freeze(run_id="freeze-bad", inputs_path=path, run_root=self.ws.run_root, inputs_root=self.ws.root,
                            evidence_paths=paths, state_hashes=dict(STATE),
                            threshold=data["operatingPoints"]["validationThresholdRuleResult"]["threshold"])
        self.assertFalse((self.ws.run_root / "freeze-bad").exists())


class HexWordTests(unittest.TestCase):
    """N1: digests are not names; real text with hex letters is still read."""

    LEET_HASH = "5ea1" + "0123456789abcdef" * 3 + "0123456789ab"[:12]

    def test_the_fixture_is_a_64_character_hex_word_that_reads_as_a_marker_if_scanned(self):
        self.assertEqual(len(self.LEET_HASH), 64)
        self.assertTrue(guards.has_sealed_marker("5ea1x"))

    def test_hash_keys_values_and_arguments_are_not_read_as_names(self):
        guards.refuse_sealed(manifest={self.LEET_HASH: {"x": 1}, "y": [self.LEET_HASH]},
                             cli_args=["--manifest-sha256", self.LEET_HASH, "--candidates-sha256=" + self.LEET_HASH,
                                       "--attempt-result", "score-01=" + self.LEET_HASH, "--left-sha256", self.LEET_HASH],
                             ids=[self.LEET_HASH], paths=["runs/%s/file.json" % self.LEET_HASH])
        for length in (32, 48, 64):
            guards.refuse_sealed(manifest={("5ea1" + "ab" * 40)[:length]: 1})

    def test_text_that_merely_contains_hex_letters_is_still_read(self):
        for text in ("5ea1", "5ea1ab", "5ea1" + "ab" * 13, "seal" + "ab" * 30 + "zz"):
            with self.subTest(text=text), self.assertRaises(guards.SealedInputRefused):
                guards.refuse_sealed(manifest={text: 1})
            with self.subTest(text=text, kind="argument"), self.assertRaises(guards.SealedInputRefused):
                guards.refuse_sealed(cli_args=[text])
        long_text = "x5ea1" + "ab" * 30
        self.assertTrue(any(any(word.startswith("xse") for word in words) for words, _ in guards._variants(long_text)))
        with self.assertRaises(guards.SealedInputRefused):
            guards.refuse_sealed(cli_args=["--note", "5ea1", "--manifest-sha256", self.LEET_HASH])
        with self.assertRaises(guards.SealedInputRefused):
            guards.refuse_sealed(cli_args=["seal=" + self.LEET_HASH])


class SplitMarkerTests(unittest.TestCase):
    """N2: remaining splits are refused, ordinary words still pass."""

    REFUSED = ("seAl", "hOldout", "fiNaltest", "se_al", "hel_dout", "myholdout", "evalfinaltest", "s09seal",
               "hoL_dout", "Se-Al", "he1d_out", "my_held_out", "evalFinalTest", "x_withheld_y", "w1thheld",
               "pre.sealed", "mySeAl", "sea_l", "S_eal", "final_t3st")
    ACCEPTED = ("closeAll", "parseAll", "release_all", "database_alias", "use all", "these alone", "falseAlarm",
                "Rosealba", "base-algorithms", "because all", "phase alignment", "increase all",
                "C:\\Users\\Rosealba\\x", "sea level", "se alias", "hold the door", "held back", "with out",
                "latest-run-contest", "tests/unit/a.py", "spuriousAlertRate")

    def test_every_split_is_refused_in_every_input_kind(self):
        for form in self.REFUSED:
            for kind, value in (("ids", [form]), ("paths", ["a/" + form + "/b"]), ("cli_args", ["--x=" + form]),
                                ("manifest", {"k": form}), ("roles", [form])):
                with self.subTest(form=form, kind=kind), self.assertRaises(guards.SealedInputRefused):
                    guards.refuse_sealed(**{kind: value})

    def test_the_words_the_guard_must_keep_accepting(self):
        for text in self.ACCEPTED:
            with self.subTest(text=text):
                guards.refuse_sealed(ids=[text], paths=[text], cli_args=[text], manifest={"k": text})

    def test_the_separator_delimited_word_is_tested_before_camel_and_digit_splitting(self):
        words = guards._variants("hOldout")
        self.assertTrue(any("holdout" in word for reading, _ in words for word in reading))
        self.assertFalse(any("holdout" in token for _, tokens in words for token in tokens))
        self.assertTrue(guards.has_sealed_marker("hOldout"))


class ParityFreezeTests(unittest.TestCase):
    """N3: topK, pipeline and the seeded sample are frozen; the artifacts are bound to the corpus."""

    def setUp(self):
        self.ws = Workspace(self)
        self.prepared, self.cands = self.ws.full_run()
        self.cuda = self.ws.candidates(run_id="cand-cuda", components=fake_components(device="cuda"))

    def args(self, **changes):
        base = dict(run_id="parity-01", prepare_run_id="prepare-01", manifest_sha256=self.ws.bound_sha256,
                    left_run_id="cand-01", left_sha256=self.cands["candidatesSha256"], right_run_id="cand-cuda",
                    right_sha256=self.cuda["candidatesSha256"], run_root=self.ws.run_root)
        base.update(changes)
        return base

    def test_the_run_takes_the_frozen_sample_depth_and_pipeline_only(self):
        result = parity.run(**self.args())
        report = read_json(Path(result["directory"]) / parity.PARITY_NAME)
        bound = read_json(self.ws.prepared_dir("prepare-01") / manifest.MANIFEST_NAME)["cpuGpuParity"]
        prepared = read_json(self.ws.prepared_dir("prepare-01") / "prepared-corpus.json")
        expected = parity.parity_sample(bound, [row["queryId"] for row in prepared["queries"]])
        self.assertEqual(report["protocol"]["queryIds"], expected)
        self.assertEqual((report["topK"], report["pipelines"], report["sampleSize"]),
                         (bound["topK"], [bound["pipeline"]], bound["sampleSize"]))
        for name in ("query_ids", "top_k", "pipeline_names"):
            with self.subTest(name=name), self.assertRaises(TypeError):
                parity.run(**self.args(run_id="parity-x"), **{name: [1]})

    def test_the_manifest_fixes_pipeline_depth_and_seed(self):
        for key, value in (("pipeline", "TOKEN_RANK_CUT"), ("topK", 0), ("seed", "7"), ("seed", True)):
            data = manifest_inputs()
            data["cpuGpuParity"][key] = value
            with self.subTest(key=key, value=value), self.assertRaises(ValueError):
                manifest.freeze_manifest(data)
        for key in ("pipeline", "topK", "seed"):
            data = manifest_inputs()
            del data["cpuGpuParity"][key]
            with self.subTest(missing=key), self.assertRaises(ValueError):
                manifest.freeze_manifest(data)

    def test_the_frozen_depth_reaches_the_report(self):
        changed = small_manifest_inputs()
        changed["cpuGpuParity"]["topK"] = 1
        ws = Workspace(self)
        ws.freeze(inputs=changed)
        self.assertEqual(read_json(ws.run_root / "freeze-01" / manifest.MANIFEST_NAME)["cpuGpuParity"]["topK"], 1)

    def test_an_artifact_for_another_corpus_or_schema_is_refused(self):
        for field, value in (("preparedCorpusSha256", "5a" * 32), ("schema", "model-evaluation-candidates-v0")):
            run_id = "cand-" + field[:3].lower()
            left_id, left_sha = "cand-01", self.cands["candidatesSha256"]
            if field == "preparedCorpusSha256":
                left = read_json(guards.run_dir(left_id, self.ws.run_root) / candidates.CANDIDATES_NAME)
                left[field] = value
                left_raw = canonical(left)
                left_id = "cand-left"
                left_dir = guards.run_dir(left_id, self.ws.run_root)
                left_dir.mkdir(parents=True)
                (left_dir / candidates.CANDIDATES_NAME).write_bytes(left_raw)
                left_sha = digest(left_raw)
            document = read_json(guards.run_dir("cand-cuda", self.ws.run_root) / candidates.CANDIDATES_NAME)
            document[field] = value
            raw = canonical(document)
            directory = guards.run_dir(run_id, self.ws.run_root)
            directory.mkdir(parents=True)
            (directory / candidates.CANDIDATES_NAME).write_bytes(raw)
            with self.subTest(field=field), self.assertRaises(ValueError):
                parity.run(**self.args(run_id="parity-" + field[:3].lower(), left_run_id=left_id,
                                       left_sha256=left_sha, right_run_id=run_id, right_sha256=digest(raw)))


class ExhaustivePoolingTests(unittest.TestCase):
    """N4: an exhaustive pooling rule draws no random recall sample."""

    def test_exhaustive_requires_a_zero_sample_size(self):
        data = manifest_inputs()
        data["pooling"]["rule"] = "EXHAUSTIVE"
        with self.assertRaises(ValueError):
            manifest.freeze_manifest(data)
        data["pooling"]["randomRecallSampleSize"] = 0
        built, _ = manifest.freeze_manifest(data)
        self.assertEqual(built["pooling"]["randomRecallSampleSize"], 0)

    def test_the_union_rule_keeps_its_sample(self):
        data = manifest_inputs()
        self.assertEqual(data["pooling"]["rule"], "UNION_TOKEN_E0_E3_PLUS_RANDOM_SAMPLE")
        manifest.freeze_manifest(data)

    def test_an_exhaustive_run_scores_without_a_sample(self):
        changed = small_manifest_inputs()
        changed["pooling"].update(rule="EXHAUSTIVE", randomRecallSampleSize=0)
        ws = Workspace(self)
        from scripts.model_evaluation.tests import _pipeline
        document = _pipeline.corpus_document()
        for query in document["queries"]:
            for entry in query["judgments"].values():
                entry["provenance"] = sorted(set(entry["provenance"]) | {"EXHAUSTIVE"})
        with patch.object(_pipeline, "small_manifest_inputs", return_value=changed):
            try:
                prepared, cands = ws.full_run(corpus=document)
            except ValueError as error:
                self.fail("exhaustive run refused: %s" % error)
        self.assertEqual(ws.score(prepared, cands)["outcome"], "SUCCEEDED")


class AttemptRecordProcessTests(unittest.TestCase):
    """N5: the attempt hash is printed, and unregistered score results stop the evidence."""

    def run_cli(self, argv):
        out, err = io.StringIO(), io.StringIO()
        with contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
            code = cli.main(argv)
        return code, out.getvalue(), err.getvalue()

    def test_score_prints_the_attempt_hash_to_record(self):
        done = {"outcome": "SUCCEEDED", "directory": Path("d"), "resultSha256": "a" * 64}
        with patch.object(score, "run", return_value=done):
            code, out, err = self.run_cli(["score", "--run-id", "s1", "--prepare-run-id", "p1",
                                           "--candidates-run-id", "c1", "--manifest-sha256", "b" * 64,
                                           "--candidates-sha256", "c" * 64])
        self.assertEqual(code, 0)
        line = "attempt hash to record (validate-evidence --attempt-result): s1=" + "a" * 64
        self.assertEqual(err.strip(), line)
        self.assertEqual(json.loads(out)["attemptHashToRecord"], "s1=" + "a" * 64)

    def test_an_unregistered_score_result_stops_validate_evidence(self):
        ws = Workspace(self)
        prepared, cands = ws.full_run()
        ws.score(prepared, cands)
        stray = guards.run_dir("score-stray", ws.run_root)
        shutil.copytree(guards.run_dir("score-01", ws.run_root), stray)
        arguments = dict(run_id="evidence-01", freeze_run_id="freeze-01", prepare_run_id="prepare-01",
                         candidates_run_id="cand-01", score_run_id="score-01", run_root=ws.run_root,
                         registry_root=ws.registry, capture=fake_identity)
        with self.assertRaises(ValueError) as context:
            validation.run(**arguments)
        self.assertIn("score-stray", str(context.exception))
        shutil.rmtree(stray)
        self.assertEqual(validation.run(**arguments)["outcome"], "SUCCEEDED")


if __name__ == "__main__":
    unittest.main()
