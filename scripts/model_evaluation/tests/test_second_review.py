import copy
import inspect
import json
import unittest
from pathlib import Path
from unittest.mock import patch

from scripts.code_analysis.contracts import canonical, digest
from scripts.model_evaluation import (candidates, guards, manifest, metrics, parity, pipelines, prepare,
                                      product_search, score, validation)
from scripts.model_evaluation.tests import test_bootstrap_and_gates as gate_tests
from scripts.model_evaluation.tests import test_guards
from scripts.model_evaluation.tests._fixtures import HEX
from scripts.model_evaluation.tests._pipeline import (G1, Workspace, corpus_document, fake_components,
                                                      fake_identity, fid, small_manifest_inputs)
from scripts.model_evaluation.tests.test_parity_resources_ranking import FakeClock


def read_json(path):
    return json.loads(Path(path).read_bytes())


class RegistryKeyTests(unittest.TestCase):
    """H1: the one-run rule is keyed by the frozen models, not by corpus or labels."""

    def setUp(self):
        self.ws = Workspace(self)
        self.prepared, self.cands = self.ws.full_run()
        self.ws.score(self.prepared, self.cands)

    def second_chain(self, corpus):
        self.ws.materialise({fid("file-qx"): G1})
        self.ws.freeze("freeze-02")
        prepared = self.ws.prepare("prepare-02", "freeze-02", corpus=corpus, name="corpus-2.json")
        cands = self.ws.candidates(run_id="cand-02", prepare_run_id="prepare-02")
        return prepared, cands

    def score_two(self, prepared, cands, **kwargs):
        return score.run(run_id="score-02", prepare_run_id="prepare-02", candidates_run_id="cand-02",
                         manifest_sha256=prepared["manifestSha256"], candidates_sha256=cands["candidatesSha256"],
                         run_root=self.ws.run_root, registry_root=self.ws.registry, capture=fake_identity, **kwargs)

    def test_relabelling_does_not_buy_a_fresh_first_run(self):
        prepared, cands = self.second_chain(corpus_document(swap=True))
        with self.assertRaises(guards.ScoringRunRefused):
            self.score_two(prepared, cands)
        done = self.score_two(prepared, cands, rerun_of="score-01", reason="IMPLEMENTATION_DEFECT")
        self.assertEqual(done["outcome"], "SUCCEEDED")

    def test_an_added_query_does_not_buy_a_fresh_first_run(self):
        corpus = corpus_document()
        corpus["files"].append({"fileId": fid("file-qx"), "sha256": digest(G1.encode()), "language": "python",
                                "family": "fx", "partition": "evaluation", "kind": "query",
                                "source": "synthetic-source-2"})
        template = next(q for q in corpus["queries"] if q["queryId"] == "qa")
        corpus["queries"].append({**copy.deepcopy(template), "queryId": "qx", "queryFileId": fid("file-qx"),
                                  "family": "fx", "seedGroup": "seed-fx"})
        prepared, cands = self.second_chain(corpus)
        with self.assertRaises(guards.ScoringRunRefused):
            self.score_two(prepared, cands)

    def test_attempts_record_corpus_and_label_hashes_and_the_listing_shows_a_label_change(self):
        prepared, cands = self.second_chain(corpus_document(swap=True))
        self.score_two(prepared, cands, rerun_of="score-01", reason="INFRASTRUCTURE_FAULT")
        first = read_json(guards.run_dir("score-01", self.ws.run_root) / score.RESULT_NAME)["registration"]
        second = read_json(guards.run_dir("score-02", self.ws.run_root) / score.RESULT_NAME)["registration"]
        self.assertEqual(first["corpusIdentitySha256"], second["corpusIdentitySha256"])
        self.assertNotEqual(first["labelSha256"], second["labelSha256"])
        self.assertEqual(first["modelKeySha256"], second["modelKeySha256"])
        results = {row: digest((guards.run_dir(row, self.ws.run_root) / score.RESULT_NAME).read_bytes())
                   for row in ("score-01", "score-02")}
        done = validation.run(run_id="evidence-02", freeze_run_id="freeze-02", prepare_run_id="prepare-02",
                              candidates_run_id="cand-02", score_run_id="score-02", run_root=self.ws.run_root,
                              registry_root=self.ws.registry, capture=fake_identity, attempt_results=results)
        package = read_json(Path(done["directory"]) / validation.PACKAGE_NAME)
        labels = [row["labelSha256"] for row in package["scoringAttempts"]]
        self.assertEqual(len(set(labels)), 2)

    def test_hashes_are_recomputed_from_the_prepared_files_and_queries(self):
        prepared = read_json(self.ws.prepared_dir() / prepare.PREPARED_NAME)
        registration = read_json(guards.run_dir("score-01", self.ws.run_root) / score.RESULT_NAME)["registration"]
        self.assertEqual(registration["corpusIdentitySha256"], prepare.corpus_identity_sha256(prepared))
        self.assertEqual(registration["labelSha256"], prepare.label_sha256(prepared))
        self.assertNotIn("contentSha256", prepared)
        relabelled = copy.deepcopy(prepared)
        entry = relabelled["queries"][0]["judgments"][next(iter(relabelled["queries"][0]["judgments"]))]
        entry["label"] = "NOT_RELEVANT" if entry["label"] == "UNKNOWN" else "UNKNOWN"
        self.assertEqual(prepare.corpus_identity_sha256(relabelled), prepare.corpus_identity_sha256(prepared))
        self.assertNotEqual(prepare.label_sha256(relabelled), prepare.label_sha256(prepared))
        extra = copy.deepcopy(prepared)
        extra["queries"].append({**copy.deepcopy(extra["queries"][0]), "queryId": "zz"})
        self.assertNotEqual(prepare.corpus_identity_sha256(extra), prepare.corpus_identity_sha256(prepared))

    def test_a_registration_that_differs_from_the_recomputed_hashes_is_refused_by_validation(self):
        record = next((self.ws.registry / guards.model_key(read_json(
            guards.run_dir("prepare-01", self.ws.run_root) / manifest.MANIFEST_NAME)["models"])).glob("attempt-*.json"))
        data = read_json(record)
        data["labelSha256"] = HEX
        record.write_bytes(canonical(data))
        with self.assertRaises(ValueError):
            validation.run(run_id="evidence-03", freeze_run_id="freeze-01", prepare_run_id="prepare-01",
                           candidates_run_id="cand-01", score_run_id="score-01", run_root=self.ws.run_root,
                           registry_root=self.ws.registry, capture=fake_identity)


class DeviceParityTests(unittest.TestCase):
    """H2: CPU and CUDA artifacts of one manifest pass through parity.run."""

    def setUp(self):
        self.ws = Workspace(self)
        self.prepared, self.cands = self.ws.full_run()
        self.cuda = self.ws.candidates(run_id="cand-cuda", components=fake_components(device="cuda"))

    def run_parity(self, run_id, right, **kwargs):
        args = dict(run_id=run_id, prepare_run_id="prepare-01", manifest_sha256=self.ws.bound_sha256,
                    left_run_id="cand-01", left_sha256=self.cands["candidatesSha256"], right_run_id=right[0],
                    right_sha256=right[1], run_root=self.ws.run_root)
        args.update(kwargs)
        return parity.run(**args)

    def test_cpu_and_cuda_artifacts_compare_end_to_end(self):
        document = read_json(guards.run_dir("cand-cuda", self.ws.run_root) / candidates.CANDIDATES_NAME)
        self.assertEqual(document["device"], "cuda")
        self.assertEqual(read_json(guards.run_dir("cand-01", self.ws.run_root) / candidates.CANDIDATES_NAME)[
            "device"], "cpu")
        self.assertEqual(document["freezeManifestSha256"], self.ws.bound_sha256)
        result = self.run_parity("parity-01", ("cand-cuda", self.cuda["candidatesSha256"]))
        self.assertEqual((result["exactMatchRate"], result["disagreements"]), (1.0, 0))
        report = read_json(guards.run_dir("parity-01", self.ws.run_root) / parity.PARITY_NAME)
        self.assertEqual(report["protocol"]["devices"], {"left": "cpu", "right": "cuda"})

    def test_equal_devices_are_refused(self):
        again = self.ws.candidates(run_id="cand-cpu-2")
        with self.assertRaises(ValueError):
            self.run_parity("parity-02", ("cand-cpu-2", again["candidatesSha256"]))

    def test_a_device_outside_the_frozen_pair_is_refused_when_the_artifact_is_made(self):
        with self.assertRaises(guards.StageFailed):
            self.ws.candidates(run_id="cand-tpu", components=fake_components(device="tpu"))

    def test_artifacts_that_differ_beyond_the_device_are_refused(self):
        protocol = {"devices": ["cpu", "cuda"]}
        left = {"device": "cpu", "modelIdentities": {"e0": {"stateSha256": "a", "device": "cpu"}}}
        right = {"device": "cuda", "modelIdentities": {"e0": {"stateSha256": "b", "device": "cuda"}}}
        with self.assertRaises(ValueError):
            parity.check_devices(protocol, left, right)
        right["modelIdentities"]["e0"]["stateSha256"] = "a"
        parity.check_devices(protocol, left, right)

    def test_models_on_different_devices_in_one_run_are_refused(self):
        mixed = fake_components()
        mixed.verifier.identity["device"] = "cuda"
        with self.assertRaises(guards.StageFailed):
            self.ws.candidates(run_id="cand-mixed", components=mixed)


class RandomSampleTests(unittest.TestCase):
    """M1"""

    def test_a_random_sample_file_without_the_sample_provenance_is_refused(self):
        corpus = corpus_document()
        for query in corpus["queries"]:
            if query["queryId"] == "qa":
                query["judgments"][fid("g2")]["provenance"] = ["E0", "E3", "TOKEN"]
        ws = Workspace(self)
        prepared, cands = ws.full_run(corpus=corpus)
        with self.assertRaises(ValueError) as context:
            ws.score(prepared, cands)
        self.assertIn("qa:%s:RANDOM_SAMPLE" % fid("g2"), str(context.exception))

    def test_a_judged_unknown_sample_file_passes(self):
        ws = Workspace(self)
        prepared, cands = ws.full_run()
        self.assertEqual(ws.score(prepared, cands)["outcome"], "SUCCEEDED")


class FieldBindingTests(unittest.TestCase):
    """M2: changing one frozen field changes what a stage does, or the stage refuses."""

    REFUSALS = (ValueError, guards.SealedInputRefused, guards.ScoringRunRefused)

    def run_chain(self, mutate):
        ws = Workspace(self)
        inputs = small_manifest_inputs()
        mutate(inputs)
        from scripts.model_evaluation.tests import _pipeline
        ws.materialise({fid(n): t for n, t in [
            ("g1", _pipeline.G1), ("g1b", _pipeline.G1), ("g2", _pipeline.G2), ("g3", _pipeline.G3),
            ("g4", _pipeline.G4), ("g5", _pipeline.G5), ("j1", _pipeline.J1), ("j2", _pipeline.J2),
            ("file-qa", _pipeline.G1), ("file-qb", _pipeline.G3), ("file-qc", _pipeline.G4),
            ("file-qn1", _pipeline.NOVEL_1), ("file-qn2", _pipeline.NOVEL_2), ("file-qj", _pipeline.J1)]})
        ws.freeze(inputs=inputs)
        prepared = ws.prepare()
        cands = ws.candidates(clock=FakeClock(0.5))
        scored = ws.score(prepared, cands)
        document = read_json(guards.run_dir("cand-01", ws.run_root) / candidates.CANDIDATES_NAME)
        for key in ("freezeManifestSha256", "preparedCorpusSha256", "codeIdentity"):
            document.pop(key)
        result = read_json(guards.run_dir("score-01", ws.run_root) / score.RESULT_NAME)
        return json.dumps([document, result["pipelines"]], sort_keys=True)

    MUTATIONS = {
        "preprocessing.framing": lambda i: i["preprocessing"].update(framing="other"),
        "preprocessing.chunking": lambda i: i["preprocessing"].update(chunking="other"),
        "models.e0.stateSha256": lambda i: i["models"]["e0"].update(stateSha256="00" * 32),
        "models.e3.tokenizerHashes": lambda i: i["models"]["e3"].update(tokenizerHashes={"vocab.json": "00" * 32}),
        "models.verifier.stateSha256": lambda i: i["models"]["verifier"].update(stateSha256="00" * 32),
        "execution.dolosK": lambda i: i["execution"].update(dolosK=5),
        "execution.dolosWindow": lambda i: i["execution"].update(dolosWindow=3),
        "operatingPoints.tokenRankCut": lambda i: i["operatingPoints"]["tokenRankCut"].update(rank=2),
        "operatingPoints.semanticRankCut": lambda i: i["operatingPoints"]["semanticRankCut"].update(rank=2),
        "operatingPoints.threshold": lambda i: i["operatingPoints"]["validationThresholdRuleResult"].update(
            threshold=0.5),
        "candidateBudget.tokenTop": lambda i: i["candidateBudget"].update(tokenTop=19),
        "candidateBudget.maxCandidates": lambda i: i["candidateBudget"].update(maxCandidates=41),
        "pooling.depthK": lambda i: i["pooling"].update(depthK=3),
        "pooling.randomRecallSampleSize": lambda i: i["pooling"].update(randomRecallSampleSize=3),
        "bootstrap.seed": lambda i: i["bootstrap"].update(seed=2),
        "bootstrap.resamples": lambda i: i["bootstrap"].update(resamples=31),
        "minimumCounts.perLanguage": lambda i: i["minimumCounts"]["perLanguage"].update(positiveQueries=4),
        "resourceProtocol.warmupQueries": lambda i: i["resourceProtocol"].update(warmupQueries=0),
        "cpuGpuParity.devices": lambda i: i["cpuGpuParity"].update(devices=["cpu"]),
        "excludedSources": lambda i: i.update(excludedSources=["synthetic-source-1"]),
    }

    def test_each_frozen_field_is_consulted(self):
        baseline = self.run_chain(lambda inputs: None)
        for name, mutate in self.MUTATIONS.items():
            with self.subTest(field=name):
                try:
                    changed = self.run_chain(mutate)
                except (*self.REFUSALS, guards.StageFailed):
                    continue
                self.assertNotEqual(changed, baseline, name + " has no effect on any stage")

    def test_preprocessing_must_equal_the_enforced_representation(self):
        for key in ("framing", "chunking"):
            data = small_manifest_inputs()
            data["preprocessing"][key] = "other"
            with self.subTest(key=key), self.assertRaises(ValueError):
                manifest.freeze_manifest(data)
        actual = manifest.actual_representation()
        self.assertEqual(small_manifest_inputs()["preprocessing"],
                         {"framing": actual["framing"], "chunking": actual["chunking"]})

    def test_the_manifest_bootstrap_seed_reaches_the_result(self):
        ws = Workspace(self)
        prepared, cands = ws.full_run()
        ws.score(prepared, cands)
        result = read_json(guards.run_dir("score-01", ws.run_root) / score.RESULT_NAME)
        bound = read_json(guards.run_dir("prepare-01", ws.run_root) / manifest.MANIFEST_NAME)
        interval = result["pipelines"]["PRODUCT"]["summary"]["confidenceIntervals"]
        self.assertEqual((interval["seed"], interval["resamples"]),
                         (bound["bootstrap"]["seed"], bound["bootstrap"]["resamples"]))

    def test_pre_registered_sections_cannot_be_edited(self):
        built, _ = manifest.freeze_manifest(small_manifest_inputs())
        edits = {"claimGates.macroF1Min": lambda m: m["claimGates"].update(macroF1Min=0.1),
                 "replacementBaselines": lambda m: m.update(replacementBaselines=["PRODUCT"]),
                 "rerunRule.maxFreeScoringRuns": lambda m: m["rerunRule"].update(maxFreeScoringRuns=2),
                 "metrics.names": lambda m: m["metrics"].update(names=["precision"]),
                 "aggregation.micro": lambda m: m["aggregation"].update(micro="headline"),
                 "corpus.status": lambda m: m["corpus"].update(status="OTHER")}
        for name, edit in edits.items():
            changed = copy.deepcopy(built)
            edit(changed)
            with self.subTest(field=name), self.assertRaises(ValueError):
                manifest.validate_manifest(changed)

    def test_the_parity_sample_is_drawn_from_the_frozen_seed_and_size(self):
        protocol = {"sampleSize": 3, "seed": 11}
        ids = ["q%02d" % index for index in range(12)]
        sample = parity.parity_sample(protocol, ids)
        self.assertEqual(sample, parity.parity_sample(protocol, list(reversed(ids))))
        self.assertEqual(len(set(sample)), 3)
        self.assertNotEqual(sample, parity.parity_sample({**protocol, "seed": 12}, ids))
        with self.assertRaises(ValueError):
            parity.parity_sample({"sampleSize": 13, "seed": 11}, ids)


class MarkerTokenTests(unittest.TestCase):
    """M4: token matching."""

    ACCEPTED = ("closeAll", "parseAll", "release_all", "base-algorithms", "database_alias", "use all",
                "these alone", "because all", "falseAlarm", "C:\\Users\\Rosealba\\x", "phase alignment",
                "increase all", "seed1", "latest-contest")
    REFUSED = ("$eal", "5eal", "se4l", "fina1te5t", "ꜱeal", "seaʟ", "seɑl", "Ꮪeal", "һeldout", "holԁout",
               "hօldout", "սeal", "sealed", "sealing-run", "Final-Test", "s e a l")

    def test_ordinary_names_pass(self):
        for name in self.ACCEPTED:
            for kind in ("ids", "paths", "cli_args"):
                with self.subTest(name=name, kind=kind):
                    guards.refuse_sealed(**{kind: [name]})
            guards.refuse_sealed(manifest={"k": name})

    def test_new_and_prior_evasions_are_refused(self):
        for name in (*self.REFUSED, *test_guards.EVASIONS, *test_guards.SEALED_FORMS):
            for kind in ("ids", "paths", "cli_args"):
                with self.subTest(name=name, kind=kind), self.assertRaises(guards.SealedInputRefused):
                    guards.refuse_sealed(**{kind: [name]})

    def test_generated_fields_are_not_scanned(self):
        digest_like = "5ea1" + "0" * 60
        guards.refuse_sealed(manifest={"hash": digest_like, "codeIdentity": {
            "sourceClosure": [{"path": "scripts/sealed_helper.py", "sha256": digest_like}],
            "environment": {"platform": "sealed-box"}}})
        with self.assertRaises(guards.SealedInputRefused):
            guards.refuse_sealed(manifest={"note": "sealed", "codeIdentity": {}})

    def test_evidence_scan_skips_generated_fields_only(self):
        generated = {"codeIdentity": {"sourceClosure": [{"path": "scripts/sealed_helper.py"}]}, "hash": "5ea1" + "0" * 60}
        self.assertEqual(validation.scan_sealed_text({"result": generated}), [])
        with self.assertRaises(ValueError):
            validation.scan_sealed_text({"result": {"notes": ["sealed"], **generated}})

    def test_authored_inputs_are_scanned_before_any_encoding(self):
        ws = Workspace(self)
        ws.full_run()
        fake = fake_components()
        fake.e0.identity["note"] = "held-out"
        touched = []
        fake.e0.windows = lambda raw, file: touched.append(1)
        with self.assertRaises(guards.SealedInputRefused):
            ws.candidates(run_id="cand-marked", components=fake)
        self.assertEqual(touched, [])


class SearchMirrorTests(unittest.TestCase):
    """L2"""

    def index(self, files):
        return product_search.InMemoryIndex(files)

    def unit(self, *values):
        norm = sum(v * v for v in values) ** 0.5
        return [v / norm for v in values]

    def call(self, connection, queries, ids, **kwargs):
        return product_search.exact_query(connection, "0" * 64, queries, ids, "python", partition="validation",
                                          **kwargs)

    def test_non_unit_vectors_are_refused(self):
        connection = self.index({"a": [self.unit(1, 2)]})
        with self.assertRaises(ValueError):
            self.call(connection, [[1.0, 2.0]], ["a"])
        bad = self.index({"a": [[2.0, 0.0]]})
        with self.assertRaises(ValueError):
            self.call(bad, [self.unit(1, 2)], ["a"])
        self.assertEqual(self.call(connection, [self.unit(1, 2)], ["a"])[0]["fileId"], "a")

    def test_the_sql_refusals_are_mirrored(self):
        vectors = {"f%d" % i: [self.unit(1, i + 1)] for i in range(1001)}
        connection = self.index(vectors)
        query = [self.unit(1, 1)]
        with self.assertRaises(ValueError):
            self.call(connection, query, sorted(vectors))
        with self.assertRaises(ValueError):
            self.call(connection, query, ["f1", "f1"])
        with self.assertRaises(ValueError):
            self.call(connection, query, ["f1", "missing"])
        with self.assertRaises(ValueError):
            self.call(connection, query, ["f1"], limit=41)
        with self.assertRaises(ValueError):
            self.call(connection, [query[0]] * 81, ["f1"])
        self.assertEqual(len(self.call(connection, query, ["f1", "f2"])), 2)

    def test_distance_is_computed_in_float32(self):
        connection = self.index({"a": [self.unit(1, 2, 3)]})
        row = self.call(connection, [self.unit(1, 2, 3)], ["a"])[0]
        self.assertAlmostEqual(row["distance"], 0.0, places=6)


class TokenCountTests(unittest.TestCase):
    """L3"""

    def test_token_ranking_is_not_capped_before_counting(self):
        ws = Workspace(self)
        limits = []
        original = pipelines.rank_token

        def spy(pairs, query_id, gallery_ids, *, limit):
            limits.append((limit, len(gallery_ids)))
            return original(pairs, query_id, gallery_ids, limit=limit)
        with patch.object(pipelines, "rank_token", spy):
            ws.full_run()
        self.assertTrue(limits)
        for limit, gallery in limits:
            self.assertEqual(limit, gallery)

    def test_product_counts_report_all_positive_token_candidates(self):
        ws = Workspace(self)
        ws.full_run()
        document = read_json(guards.run_dir("cand-01", ws.run_root) / candidates.CANDIDATES_NAME)
        counts = document["queries"]["qa"]["pipelines"]["PRODUCT"]["counts"]
        self.assertGreaterEqual(counts["tokenCandidates"], counts["tokenRetained"])
        windows = document["queries"]["qa"]["windows"]
        self.assertEqual((windows["queryOmittedFragments"], windows["galleryOmittedFragments"]), (0, 0))


class GoldenNeverSkipsTests(unittest.TestCase):
    """L5"""

    NAME = ("scripts.model_evaluation.tests.test_stages.PipelineRuleTests."
            "test_product_union_projection_matches_the_analysis_service")

    def test_the_golden_case_runs_and_carries_no_skip_or_expected_failure_marker(self):
        suite = unittest.TestLoader().loadTestsFromName(self.NAME)
        cases = list(suite)
        self.assertEqual(len(cases), 1)
        case = cases[0]
        method = getattr(case, case._testMethodName)
        for owner in (method, type(case)):
            for marker in ("__unittest_skip__", "__unittest_expecting_failure__"):
                self.assertFalse(getattr(owner, marker, False), marker)
        source = inspect.getsource(method) + inspect.getsource(type(case))
        for word in ("skipTest", "SkipTest", "skipUnless", "skipIf", "expectedFailure", "unittest.skip"):
            self.assertNotIn(word, source)
        result = unittest.TestResult()
        suite.run(result)
        self.assertEqual(result.testsRun, 1)
        self.assertEqual((result.skipped, result.expectedFailures, result.errors, result.failures), ([], [], [], []))


class UndefinedResampleTests(unittest.TestCase):
    """L6"""

    def test_undefined_resamples_leave_lower_bound_gates_unvalidated(self):
        data = gate_tests.all_languages()
        summary = metrics.summarize(data, bootstrap={"resamples": 20, "seed": 1})
        gates, counts = manifest.CLAIM_GATES, gate_tests.minimum_counts()
        clean = metrics.evaluate_claim_gates(summary, gates=gates, minimum_counts=counts)
        self.assertEqual(clean["overall"]["product"]["status"], metrics.VALIDATED)
        broken = copy.deepcopy(summary)
        broken["confidenceIntervals"]["statistics"]["macroPrecision"]["undefinedResamples"] = 1
        product = metrics.evaluate_claim_gates(broken, gates=gates, minimum_counts=counts)["overall"]["product"]
        self.assertEqual(product["status"], metrics.UNVALIDATED)
        self.assertIn("productPrecisionLowerBound", product["unevaluatedGates"])

    def test_undefined_resamples_in_a_paired_delta_leave_replacement_unvalidated(self):
        data = gate_tests.all_languages()
        weak = gate_tests.all_languages(false_finding=gate_tests.SUPPORTED)
        summary = metrics.summarize(data)
        baselines = {name: metrics.summarize(weak) for name in gate_tests.BASELINES}
        for undefined, expected in ((0, metrics.VALIDATED), (2, metrics.UNVALIDATED)):
            deltas = {name: {"statistics": {"macroF1": {"lower": 0.1, "undefinedResamples": undefined}}}
                      for name in gate_tests.BASELINES}
            outcome = metrics.evaluate_claim_gates(
                summary, gates=manifest.CLAIM_GATES, minimum_counts=gate_tests.minimum_counts(), baselines=baselines,
                paired_deltas=deltas, required_baselines=gate_tests.BASELINES)["overall"]["replacement"]
            self.assertEqual(outcome["status"], expected, undefined)


if __name__ == "__main__":
    unittest.main()
