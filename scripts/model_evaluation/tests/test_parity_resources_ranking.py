import contextlib
import copy
import io
import json
import unittest
from pathlib import Path
from unittest.mock import patch

from scripts.code_analysis.contracts import canonical, digest
from scripts.model_evaluation import candidates, cli, guards, metrics, parity, pipelines, resources, validation
from scripts.model_evaluation.tests._fixtures import N, R, U, query
from scripts.model_evaluation.tests._pipeline import Workspace, fake_components, fake_identity, fid


class FakeClock:
    def __init__(self, step=1.0):
        self.now, self.step = 0.0, step

    def __call__(self):
        self.now += self.step
        return self.now


class StageTimerTests(unittest.TestCase):
    def test_warmup_is_excluded_and_percentiles_are_computed_per_stage(self):
        timer = resources.StageTimer(FakeClock(), warmup=2)
        for seconds in (9.0, 9.0, 1.0, 2.0, 3.0, 4.0, 5.0):
            timer.add("encoding", seconds)
        entry = timer.summary()["encoding"]
        self.assertEqual((entry["samples"], entry["excludedWarmup"], entry["max"], entry["p50"]), (7, 2, 5.0, 3.0))
        self.assertAlmostEqual(entry["p95"], 4.8)
        self.assertEqual(entry["total"], 15.0)

    def test_stages_stay_separate_and_a_short_run_is_marked_below_warmup(self):
        timer = resources.StageTimer(FakeClock(), warmup=3)
        timer.add("preprocessing", 1.0)
        with timer.measure("encoding"):
            pass
        summary = timer.summary()
        self.assertEqual(set(summary), {"preprocessing", "encoding"})
        self.assertTrue(summary["preprocessing"]["belowWarmup"])
        self.assertIsNone(summary["preprocessing"]["p50"])
        self.assertEqual(timer.run("encoding", lambda: 7), 7)
        self.assertEqual(timer.samples["encoding"], [1.0, 1.0])

    def test_invalid_use_is_rejected(self):
        with self.assertRaises(ValueError):
            resources.StageTimer(warmup=-1)
        with self.assertRaises(ValueError):
            resources.StageTimer().add("x", -0.1)

    def test_failed_measure_still_records_elapsed_time(self):
        timer = resources.StageTimer(FakeClock())
        with self.assertRaises(RuntimeError):
            timer.run("encoding", lambda: (_ for _ in ()).throw(RuntimeError("boom")))
        self.assertEqual(timer.samples["encoding"], [1.0])

    def test_memory_probe_reports_none_instead_of_inventing_values(self):
        memory = resources.peak_memory()
        self.assertEqual(set(memory), {"ramPeakBytes", "vramPeakBytes"})
        for value in memory.values():
            self.assertTrue(value is None or (type(value) is int and value >= 0))

    def test_resource_record_lists_unmeasured_components(self):
        timer = resources.StageTimer(warmup=1)
        for seconds in (1.0, 2.0):
            timer.add("endToEndQuery", seconds)
        record = resources.resource_record(timer, {"warmupQueries": 1, "timedQueries": 1,
                                                   "percentiles": [50, 95]},
                                           memory={"ramPeakBytes": 1, "vramPeakBytes": None})
        self.assertEqual(set(record["unmeasured"]), {"transfer", "queue"})
        self.assertEqual(record["memory"], {"ramPeakBytes": 1, "vramPeakBytes": None})


class CandidateResourceHookTests(unittest.TestCase):
    def test_candidate_artifact_records_per_stage_statistics_with_a_fake_clock(self):
        ws = Workspace(self)
        ws.full_run()
        ws.candidates(run_id="cand-clock", clock=FakeClock(0.5))
        document = json.loads((guards.run_dir("cand-clock", ws.run_root) / candidates.CANDIDATES_NAME).read_bytes())
        stages = document["resources"]["stages"]
        for name in ("basePreprocessing", "baseEncoding", "trainedEncoding", "tokenScoring", "windowAlignment",
                     "verifierInference", "endToEndQuery", "tokenRanking"):
            self.assertIn(name, stages, name)
        self.assertEqual(stages["basePreprocessing"]["samples"], 14)
        self.assertEqual(stages["basePreprocessing"]["excludedWarmup"], 1)
        self.assertEqual((stages["basePreprocessing"]["p50"], stages["basePreprocessing"]["max"]), (0.5, 0.5))
        self.assertEqual(stages["endToEndQuery"]["samples"], 6)
        self.assertEqual(document["resources"]["warmup"], 1)
        self.assertEqual(set(document["resources"]["unmeasured"]), {"transfer", "queue"})
        self.assertIn("ramPeakBytes", document["resources"]["memory"])


class ParityTests(unittest.TestCase):
    def setUp(self):
        self.ws = Workspace(self)
        self.prepared, self.cands = self.ws.full_run()
        path = guards.run_dir("cand-01", self.ws.run_root) / candidates.CANDIDATES_NAME
        self.left = json.loads(path.read_bytes())
        self.right = copy.deepcopy(self.left)
        self.sample = ["qa", "qb", "qc"]

    def compare(self, **kwargs):
        return parity.compare(self.left, self.right, query_ids=self.sample, **kwargs)

    def product(self, document, query_id="qa"):
        return document["queries"][query_id]["pipelines"][pipelines.PRODUCT]

    def write_candidate(self, run_id, document):
        directory = guards.run_dir(run_id, self.ws.run_root)
        directory.mkdir(parents=True)
        raw = canonical(document)
        (directory / candidates.CANDIDATES_NAME).write_bytes(raw)
        return digest(raw)

    def test_identical_artifacts_match_exactly(self):
        report = self.compare()
        self.assertEqual((report["exactMatchRate"], report["comparisons"], report["disagreements"]), (1.0, 3, []))

    def test_swapped_tied_candidates_are_a_tie_order_disagreement(self):
        rows = self.product(self.right)["candidates"]
        self.assertEqual(rows[0]["tokenScore"], rows[1]["tokenScore"])
        rows[0], rows[1] = rows[1], rows[0]
        report = self.compare()
        self.assertEqual([(d["queryId"], d["kind"]) for d in report["disagreements"]], [("qa", parity.TIE_ORDER)])
        self.assertAlmostEqual(report["exactMatchRate"], 2 / 3)

    def test_reordered_untied_candidates_are_a_rank_order_disagreement(self):
        rows = self.product(self.right, "qb")["candidates"]
        rows.append(rows.pop(0))
        kinds = [d["kind"] for d in self.compare()["disagreements"]]
        self.assertEqual(kinds, [parity.RANK_ORDER])

    def test_different_membership_counts_and_material_flags_are_all_reported(self):
        self.product(self.right, "qc")["candidates"].pop()
        self.product(self.right, "qb")["counts"]["tokenCandidates"] += 1
        self.product(self.right, "qa")["candidates"][0]["material"] = False
        kinds = sorted((d["queryId"], d["kind"]) for d in self.compare()["disagreements"])
        self.assertEqual(kinds, [("qa", parity.MATERIAL), ("qb", parity.COUNTS), ("qc", parity.MEMBERSHIP)])

    def test_top_k_limits_the_ordering_comparison(self):
        rows = self.product(self.right)["candidates"]
        rows[-1], rows[-2] = rows[-2], rows[-1]
        self.assertEqual(self.compare(top_k=1)["disagreements"], [])
        self.assertTrue(self.compare(top_k=len(rows))["disagreements"])

    def test_invalid_samples_and_mismatched_artifacts_fail_closed(self):
        for sample in ([], ["qa", "qa"]):
            with self.subTest(sample=sample), self.assertRaises(ValueError):
                parity.compare(self.left, self.right, query_ids=sample)
        with self.assertRaises(ValueError):
            parity.compare(self.left, self.right, query_ids=["missing"])
        with self.assertRaises(ValueError):
            parity.compare(self.left, self.right, query_ids=["qa"], top_k=0)
        self.right["freezeManifestSha256"] = "3" * 64
        with self.assertRaises(ValueError):
            self.compare()

    def test_stage_writes_a_write_once_report_and_checks_artifact_hashes(self):
        cuda = self.ws.candidates(run_id="cand-cuda", components=fake_components(device="cuda"))
        kwargs = dict(run_id="parity-01", prepare_run_id="prepare-01", manifest_sha256=self.ws.bound_sha256,
                      left_run_id="cand-01", left_sha256=self.cands["candidatesSha256"],
                      right_run_id="cand-cuda", right_sha256=cuda["candidatesSha256"],
                      run_root=self.ws.run_root)
        result = parity.run(**kwargs)
        self.assertEqual((result["exactMatchRate"], result["disagreements"]), (1.0, 0))
        with self.assertRaises(FileExistsError):
            parity.run(**kwargs)
        with self.assertRaises(ValueError):
            parity.run(**{**kwargs, "run_id": "parity-02", "right_sha256": "0" * 64})
        with self.assertRaises(ValueError):
            parity.run(**{**kwargs, "run_id": "parity-05", "manifest_sha256": self.ws.frozen_sha256})

    def test_stage_refuses_matching_wrong_frozen_model_identities_with_correct_hashes(self):
        cuda = self.ws.candidates(run_id="cand-cuda", components=fake_components(device="cuda"))
        right_path = guards.run_dir("cand-cuda", self.ws.run_root) / candidates.CANDIDATES_NAME
        original_right = json.loads(right_path.read_bytes())
        mutations = (
            ("state", lambda identity: identity.update(stateSha256="9f" * 32)),
            ("tokenizer", lambda identity: identity["tokenizerHashes"].update({"vocab.json": "9f" * 32})),
        )
        for suffix, mutate in mutations:
            left, right = copy.deepcopy(self.left), copy.deepcopy(original_right)
            mutate(left["modelIdentities"]["e0"])
            mutate(right["modelIdentities"]["e0"])
            left_sha = self.write_candidate("cand-p2-left-" + suffix, left)
            right_sha = self.write_candidate("cand-p2-right-" + suffix, right)
            run_id = "parity-p2-" + suffix
            with self.subTest(identity=suffix), self.assertRaises(ValueError):
                parity.run(run_id=run_id, prepare_run_id="prepare-01", manifest_sha256=self.ws.bound_sha256,
                           left_run_id="cand-p2-left-" + suffix, left_sha256=left_sha,
                           right_run_id="cand-p2-right-" + suffix, right_sha256=right_sha,
                           run_root=self.ws.run_root)
            self.assertFalse((guards.run_dir(run_id, self.ws.run_root) / parity.PARITY_NAME).exists())

    def test_stage_refuses_top_level_device_that_contradicts_component_devices(self):
        self.ws.candidates(run_id="cand-cuda", components=fake_components(device="cuda"))
        right_path = guards.run_dir("cand-cuda", self.ws.run_root) / candidates.CANDIDATES_NAME
        right = json.loads(right_path.read_bytes())
        for name in ("e0", "e3", "verifier"):
            right["modelIdentities"][name]["device"] = "cpu"
        right_sha = self.write_candidate("cand-p2-device", right)
        with self.assertRaises(ValueError):
            parity.run(run_id="parity-p2-device", prepare_run_id="prepare-01",
                       manifest_sha256=self.ws.bound_sha256, left_run_id="cand-01",
                       left_sha256=self.cands["candidatesSha256"], right_run_id="cand-p2-device",
                       right_sha256=right_sha, run_root=self.ws.run_root)
        self.assertFalse((guards.run_dir("parity-p2-device", self.ws.run_root) / parity.PARITY_NAME).exists())


class RankingMetricTests(unittest.TestCase):
    def ranking_query(self, name, order, judgments, **kwargs):
        return metrics.normalize_query(query(name, judgments=judgments, **kwargs) | {"ranking": order})

    def gallery(self, relevant_ranks, size=45, unknown=()):
        files = ["f%02d" % i for i in range(size)]
        labels = {f: N for f in files}
        for rank in relevant_ranks:
            labels[files[rank - 1]] = R
        for rank in unknown:
            labels[files[rank - 1]] = U
        return files, labels

    def outcome(self, relevant_ranks, **kwargs):
        files, labels = self.gallery(relevant_ranks, **kwargs)
        return metrics.query_ranking(self.ranking_query("q", files, labels))

    def test_recall_at_k_mrr_and_union_recall_from_the_ordered_list(self):
        result = self.outcome([7])
        self.assertEqual((result["recallAt5"], result["recallAt10"], result["recallAt20"]), (0.0, 1.0, 1.0))
        self.assertAlmostEqual(result["mrrAt20"], 1 / 7)
        self.assertEqual(result["unionRecall"], 1.0)

    def test_unknown_files_are_removed_before_ranking(self):
        result = self.outcome([6], unknown=[2])
        self.assertEqual(result["recallAt5"], 1.0)
        self.assertAlmostEqual(result["mrrAt20"], 1 / 5)

    def test_relevant_files_beyond_the_cutoffs_and_outside_the_list_lower_the_metrics(self):
        result = self.outcome([25, 30])
        self.assertEqual((result["recallAt20"], result["mrrAt20"], result["unionRecall"]), (0.0, 0.0, 1.0))
        files, labels = self.gallery([1, 2])
        partial = metrics.query_ranking(self.ranking_query("q", files[:1] + files[5:], labels))
        self.assertEqual(partial["unionRecall"], 0.5)

    def test_no_match_and_unknown_only_queries_are_excluded_and_missing_ranking_fails(self):
        files, labels = self.gallery([])
        self.assertIsNone(metrics.query_ranking(self.ranking_query("q", files, labels)))
        with self.assertRaises(ValueError):
            metrics.query_ranking(metrics.normalize_query(query("q", judgments={"a": R})))
        with self.assertRaises(ValueError):
            metrics.normalize_query(query("q", judgments={"a": R}) | {"ranking": ["zzz"]})
        with self.assertRaises(ValueError):
            metrics.normalize_query(query("q", judgments={"a": R}) | {"ranking": ["a", "a"]})

    def test_seed_and_language_aggregation_follow_the_validation_protocol(self):
        files, labels = self.gallery([1])
        rows = [self.ranking_query("a1", files, labels, language="python", seed="s1", family="f1"),
                self.ranking_query("a2", files, labels, language="python", seed="s1", family="f1"),
                self.ranking_query("b1", files[1:], labels, language="python", seed="s2", family="f2"),
                self.ranking_query("c1", files, labels, language="java", seed="s3", family="f3")]
        summary = metrics.ranking_summary(metrics.normalize_queries(rows))
        self.assertEqual(summary["perLanguage"]["python"]["macro"]["recallAt5"], 0.5)
        self.assertEqual(summary["overall"]["macro"]["recallAt5"], 0.75)
        self.assertEqual(summary["overall"]["positiveQueries"], 4)

    def test_bootstrap_intervals_are_deterministic_and_paired_deltas_use_families(self):
        files, labels = self.gallery([1])
        rows = []
        for index in range(6):
            order = files if index % 2 == 0 else files[1:]
            rows.append(self.ranking_query("q%d" % index, order, labels, seed="s%d" % index, family="f%d" % index))
        primary = metrics.normalize_queries(rows)
        baseline = metrics.normalize_queries([self.ranking_query(r["queryId"], files[1:], labels, seed=r["seedGroup"],
                                                                  family=r["family"]) for r in rows])
        shared = {"resamples": 40, "seed": 9, "confidence": 0.95}
        first = metrics.ranking_summary(primary, bootstrap=shared)
        self.assertEqual(first, metrics.ranking_summary(primary, bootstrap=shared))
        self.assertEqual(first["confidenceIntervals"]["unit"], "family")
        delta = metrics.paired_bootstrap_delta(primary, baseline, statistics=metrics.ranking_statistics, **shared)
        self.assertAlmostEqual(delta["point"]["recallAt5"], 0.5)
        self.assertEqual(delta["unit"], "family")


class ScoredRankingTests(unittest.TestCase):
    def test_result_artifact_carries_ranking_metrics_and_the_emission_notes(self):
        ws = Workspace(self)
        prepared, cands = ws.full_run()
        ws.score(prepared, cands)
        result = ws.result()
        product = result["pipelines"][pipelines.PRODUCT]["retrievalRanking"]["perLanguage"]["python"]["macro"]
        self.assertEqual((product["recallAt5"], product["mrrAt20"], product["unionRecall"]), (1.0, 1.0, 1.0))
        tuned = result["pipelines"][pipelines.FINE_TUNED_RETRIEVAL]
        self.assertIn("recallAt10", tuned["retrievalRankingPairedDeltaVsProduct"]["point"])
        self.assertEqual(tuned["retrievalRanking"]["confidenceIntervals"]["unit"], "family")
        self.assertIsNone(result["pipelines"][pipelines.PRODUCT]["retrievalRankingPairedDeltaVsProduct"])
        self.assertTrue(any("rank cut has no abstention" in note for note in result["notes"]))
        self.assertEqual(result["statuses"]["reviewerAcceptance"], "NOT_RECORDED")


class WindowPairShapeTests(unittest.TestCase):
    def test_window_pair_helper_output_shape_is_pinned(self):
        from scripts.pair_verification.validation_candidates import _window_pairs
        rows = [{"fileId": "f", "vector": [1.0, 0.0], "tokenStart": 0, "tokenEnd": 20, "sourceTokenSpans": None},
                {"fileId": "f", "vector": [0.0, 1.0], "tokenStart": 20, "tokenEnd": 30, "sourceTokenSpans": None}]
        pairs = _window_pairs([[1.0, 0.0]], [{"tokenStart": 0, "tokenEnd": 10}], rows, "f", limit=3)
        self.assertEqual(len(pairs), 2)
        self.assertEqual(set(pairs[0]), {
            "cosine", "queryWindow", "fileWindow", "queryWindowCount", "fileWindowCount", "queryTokenStart",
            "queryTokenEnd", "queryContentTokens", "fileContentTokens", "querySourceSpan", "fileTokenStart",
            "fileTokenEnd", "fileSourceSpan", "layoutTruncated"})
        self.assertEqual((pairs[0]["cosine"], pairs[0]["queryWindow"], pairs[0]["fileWindow"]), (1.0, 0, 0))
        self.assertEqual((pairs[1]["cosine"], pairs[1]["fileWindow"]), (0.0, 1))
        self.assertEqual((pairs[0]["queryContentTokens"], pairs[0]["fileContentTokens"],
                          pairs[0]["layoutTruncated"]), (10, 20, False))
        self.assertIsNone(pairs[0]["querySourceSpan"])


class CliCompletionTests(unittest.TestCase):
    def run_cli(self, argv):
        out, err = io.StringIO(), io.StringIO()
        with contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
            try:
                code = cli.main(argv)
            except SystemExit as exit_:
                code = exit_.code
        return code, out.getvalue(), err.getvalue()

    def test_help_lists_every_stage(self):
        code, out, _ = self.run_cli(["--help"])
        self.assertEqual(code, 0)
        for name in ("freeze", "prepare", "candidates", "score", "validate-evidence", "parity", "models-check"):
            self.assertIn(name, out)

    def test_no_stage_is_unimplemented(self):
        self.assertFalse(hasattr(cli, "StageNotImplemented"))
        source = Path(cli.__file__).read_text(encoding="utf-8")
        self.assertNotIn("NOT_IMPLEMENTED", source)

    def test_validate_evidence_and_parity_are_wired(self):
        done = {"outcome": "SUCCEEDED", "directory": Path("d"), "packageSha256": "a" * 64,
                "statuses": {"runCompleted": True}, "reportSha256": "b" * 64, "exactMatchRate": 1.0,
                "disagreements": 0}
        with patch.object(validation, "run", return_value=done) as stage:
            code, out, _ = self.run_cli(["validate-evidence", "--run-id", "e1", "--freeze-run-id", "f1",
                                         "--prepare-run-id", "p1", "--candidates-run-id", "c1",
                                         "--score-run-id", "s1", "--require-clean"])
        self.assertEqual(code, 0)
        self.assertTrue(stage.call_args.kwargs["require_clean"])
        self.assertEqual(json.loads(out)["packageSha256"], "a" * 64)
        with patch.object(parity, "run", return_value=done) as stage:
            code, out, _ = self.run_cli(["parity", "--run-id", "y1", "--prepare-run-id", "p1",
                                         "--manifest-sha256", "c" * 64, "--left-run-id", "c1", "--left-sha256", "a" * 64,
                                         "--right-run-id", "c2", "--right-sha256", "b" * 64])
        self.assertEqual(code, 0)
        self.assertNotIn("query_ids", stage.call_args.kwargs)

    def test_failures_and_unverified_models_exit_nonzero(self):
        with patch.object(validation, "run", side_effect=ValueError("broken link")):
            code, out, err = self.run_cli(["validate-evidence", "--run-id", "e1", "--freeze-run-id", "f1",
                                           "--prepare-run-id", "p1", "--candidates-run-id", "c1",
                                           "--score-run-id", "s1"])
        self.assertEqual((code, out), (cli.EXIT_FAILED, ""))
        self.assertIn("broken link", err)
        code, _, _ = self.run_cli(["parity", "--run-id", "y1", "--prepare-run-id", "p1",
                                   "--manifest-sha256", "c" * 64, "--left-run-id", "c1", "--left-sha256", "a" * 64,
                                   "--right-run-id", "c2", "--right-sha256", "b" * 64, "--query-ids", "final-test-q"])
        self.assertEqual(code, cli.EXIT_REFUSED)


if __name__ == "__main__":
    unittest.main()
