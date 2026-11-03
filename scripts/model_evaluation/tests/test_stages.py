import copy
import json
import unittest

from scripts.code_analysis.contracts import canonical, digest
from scripts.model_evaluation import candidates, guards, manifest, pipelines, prepare, score
from scripts.model_evaluation.tests._pipeline import Workspace, corpus_document, fake_components, fid


def read_json(path):
    return json.loads(path.read_bytes())


class PrepareTests(unittest.TestCase):
    def setUp(self):
        self.ws = Workspace(self)
        self.ws.freeze()

    def mutated(self, change):
        document = corpus_document()
        change(document)
        return document

    def test_prepare_writes_prepared_corpus_and_a_new_bound_freeze_version(self):
        result = self.ws.prepare()
        directory = self.ws.prepared_dir()
        prepared = read_json(directory / prepare.PREPARED_NAME)
        bound = read_json(directory / manifest.MANIFEST_NAME)
        old = read_json(guards.run_dir("freeze-01", self.ws.run_root) / manifest.MANIFEST_NAME)
        self.assertEqual(digest((directory / prepare.PREPARED_NAME).read_bytes()),
                         bound["corpus"]["identity"]["corpusManifestSha256"])
        self.assertEqual(bound["corpus"]["identity"]["previousManifestSha256"], prepared["freezeManifestSha256"])
        self.assertEqual(digest(canonical(old)), prepared["freezeManifestSha256"])
        self.assertNotEqual(result["manifestSha256"], result["previousManifestSha256"])
        self.assertEqual(old["corpus"]["status"], manifest.CORPUS_PENDING)
        self.assertEqual(bound["corpus"]["status"], manifest.CORPUS_BOUND)
        self.assertEqual({k: v for k, v in bound.items() if k != "corpus"},
                         {k: v for k, v in old.items() if k != "corpus"})

    def test_unjudged_gallery_files_become_unknown_and_never_negative(self):
        corpus = corpus_document()
        for query in corpus["queries"]:
            query["judgments"].pop(fid("g5"), None)
        self.ws.prepare(corpus=corpus)
        prepared = read_json(self.ws.prepared_dir() / prepare.PREPARED_NAME)
        qa = next(q for q in prepared["queries"] if q["queryId"] == "qa")
        self.assertEqual(qa["judgments"][fid("g5")], {"label": "UNKNOWN", "provenance": []})
        self.assertEqual(qa["judgments"][fid("g2")]["label"], "NOT_RELEVANT")
        self.assertGreater(prepared["unjudgedFilledAsUnknown"], 0)

    def test_below_minimum_is_recorded_not_dropped(self):
        self.ws.prepare()
        prepared = read_json(self.ws.prepared_dir() / prepare.PREPARED_NAME)
        below = prepared["belowMinimum"]
        self.assertTrue(below["languages"]["java"]["positiveQueries"])
        self.assertFalse(below["languages"]["python"]["positiveQueries"])
        self.assertTrue(below["tsxDialect"]["positiveQueries"])
        self.assertIn("java", prepared["counts"]["perLanguage"])
        self.assertEqual(below["missingLanguages"], ["csharp", "javascript", "typescript"])

    def test_family_in_two_partition_roles_is_refused(self):
        def change(document):
            document["files"][0]["partition"] = "train"
            document["files"][1]["family"] = document["files"][0]["family"]
        with self.assertRaises(ValueError) as context:
            self.ws.prepare(corpus=self.mutated(change))
        self.assertIn("partition", str(context.exception))

    def test_invalid_corpora_are_refused(self):
        cases = {
            "query from a training partition": lambda d: d["files"].__setitem__(
                6, {**d["files"][8], "partition": "train", "family": "other"}) or d["queries"][0].update(
                queryFileId=d["files"][6]["fileId"], family="other"),
            "judgment outside the gallery": lambda d: d["queries"][0]["judgments"].__setitem__(
                fid("file-qb"), {"label": "RELEVANT", "provenance": ["TOKEN"]}),
            "judged without provenance": lambda d: d["queries"][0]["judgments"][fid("g2")].update(provenance=[]),
            "unknown label": lambda d: d["queries"][0]["judgments"][fid("g2")].update(label="MAYBE"),
            "duplicate query": lambda d: d["queries"].append(copy.deepcopy(d["queries"][0])),
            "missing family": lambda d: d["queries"][0].update(family=""),
            "extra key": lambda d: d["queries"][0].update(extra=1),
            "unsupported language": lambda d: d["files"][0].update(language="cobol"),
            "bad file id": lambda d: d["files"][0].update(fileId="short"),
        }
        for name, change in cases.items():
            with self.subTest(name), self.assertRaises(ValueError):
                self.ws.prepare(run_id="prepare-x", corpus=self.mutated(change))
            self.assertFalse((guards.run_dir("prepare-x", self.ws.run_root) / prepare.PREPARED_NAME).exists())

    def test_sealed_marker_anywhere_in_the_corpus_or_ids_is_refused(self):
        def change(document):
            document["queries"][0]["stratum"] = "final-test"
        with self.assertRaises(guards.SealedInputRefused):
            self.ws.prepare(corpus=self.mutated(change))
        with self.assertRaises(guards.SealedInputRefused):
            self.ws.prepare(run_id="prepare-sealed")
        with self.assertRaises(guards.SealedInputRefused):
            self.ws.prepare(manifest_run_id="freeze-final-test")

    def test_prepare_refuses_an_already_bound_manifest_and_a_second_write(self):
        self.ws.prepare()
        with self.assertRaises(FileExistsError):
            self.ws.prepare()
        with self.assertRaises(ValueError):
            self.ws.prepare(run_id="prepare-02", manifest_run_id="prepare-01")


class PipelineRuleTests(unittest.TestCase):
    def ranked(self, scores):
        order = sorted(scores, key=lambda f: (-scores[f], f))
        return [{"fileId": f, "score": scores[f], "rank": i} for i, f in enumerate(order, 1)]

    def shape(self):
        return dict(token_top=20, semantic_top=20, union_budget=40)

    def test_all_top_positive_ties_are_material(self):
        a, b, c = (fid(x) for x in "abc")
        token = self.ranked({a: 0.8, b: 0.8, c: 0.3})
        built = pipelines.build_candidates(token, [], **self.shape())
        self.assertEqual(pipelines.product_findings(built["candidates"]), sorted([a, b]))
        self.assertEqual(pipelines.token_rank_cut_findings(token, 1), [min(a, b)])

    def test_no_positive_token_score_means_nothing_is_material(self):
        a = fid("a")
        built = pipelines.build_candidates([], self.ranked({a: 0.4}), **self.shape())
        self.assertEqual(pipelines.product_findings(built["candidates"]), [])
        self.assertEqual(pipelines.fine_tuned_findings(built["candidates"], 1), [a])
        with self.assertRaises(ValueError):
            pipelines.rank_token([{"leftId": "x", "rightId": "y", "tokenScore": 2}], "x", ["y"], limit=5)
        self.assertEqual(pipelines.rank_token([{"leftId": fid("q"), "rightId": fid("y"), "tokenScore": 0}],
                                              fid("q"), [fid("y")], limit=5), [])

    def test_union_order_and_budget_follow_the_shared_union(self):
        token = self.ranked({fid("t%d" % i): 1 - i / 100 for i in range(30)})
        semantic = self.ranked({fid("s%d" % i): 1 - i / 100 for i in range(30)})
        built = pipelines.build_candidates(token, semantic, **self.shape())
        ids = [row["fileId"] for row in built["candidates"]]
        self.assertEqual(len(ids), 40)
        self.assertEqual(ids[:20], [row["fileId"] for row in token[:20]])
        self.assertEqual(built["counts"], {"tokenCandidates": 30, "tokenRetained": 20, "semanticCandidates": 30,
                                           "semanticRetained": 20, "union": 40})

    def test_product_union_projection_matches_the_analysis_service(self):
        # an import failure is a test failure: the golden comparison never skips
        contracts = manifest.service_contracts()
        from app import mechanisms, policy
        pairs = [{"leftId": fid("q"), "rightId": fid(n), "tokenScore": s}
                 for n, s in (("a", 0.8), ("b", 0.8), ("c", 0.3), ("d", 0.0))]
        gallery = [fid(n) for n in "abcd"]
        service_token = mechanisms.rank_token_files(pairs, fid("q"), gallery, contracts.TOKEN_TOP)
        mine = pipelines.rank_token(pairs, fid("q"), gallery, limit=contracts.TOKEN_TOP)
        self.assertEqual(mine, service_token)
        semantic = [fid("c"), fid("e")]
        union = mechanisms.union_candidates([r["fileId"] for r in service_token], semantic, contracts.UNION_BUDGET)
        built = pipelines.build_candidates(mine, [{"fileId": f, "score": 0.5, "rank": i}
                                                  for i, f in enumerate(semantic, 1)],
                                           token_top=contracts.TOKEN_TOP, semantic_top=contracts.SEMANTIC_TOP,
                                           union_budget=contracts.UNION_BUDGET)
        self.assertEqual([row["fileId"] for row in built["candidates"]], union)
        self.assertEqual(policy.build_policy()["retrieval"]["materialRule"],
                         "ALL_TOP_POSITIVE_TOKEN_SCORE_TIES_PER_QUERY_FILE")
        self.assertEqual(sorted(pipelines.product_findings(built["candidates"])), sorted([fid("a"), fid("b")]))


class EndToEndTests(unittest.TestCase):
    def setUp(self):
        self.ws = Workspace(self)
        self.prepared, self.cands = self.ws.full_run()

    def test_candidate_artifact_records_channels_findings_counts_and_timings(self):
        document = read_json(guards.run_dir("cand-01", self.ws.run_root) / candidates.CANDIDATES_NAME)
        self.assertEqual(digest((guards.run_dir("cand-01", self.ws.run_root) / candidates.CANDIDATES_NAME)
                                .read_bytes()), self.cands["candidatesSha256"])
        entry = document["queries"]["qa"]["pipelines"]
        self.assertEqual(set(entry), set(pipelines.PIPELINES))
        self.assertEqual(sorted(entry[pipelines.PRODUCT]["findings"]), sorted([fid("g1"), fid("g1b")]))
        self.assertEqual(len(entry[pipelines.TOKEN_RANK_CUT]["findings"]), 1)
        product = entry[pipelines.PRODUCT]["candidates"][0]
        self.assertEqual(product["channels"], "TOKEN_AND_SEMANTIC")
        self.assertTrue(product["material"])
        verified = entry[pipelines.FINE_TUNED_RETRIEVAL_PLUS_VERIFIER]
        self.assertTrue(all("verifierScore" in row and row["windowPairs"] for row in verified["candidates"]))
        self.assertIn("verifier", verified["timingsSeconds"])
        self.assertIn("tokenRanking", document["queries"]["qa"]["stageSeconds"])
        self.assertIn("baseEncoding", document["resources"]["stages"])
        self.assertEqual(document["queries"]["qa"]["windows"]["queryWindows"], 1)
        self.assertEqual(document["freezeManifestSha256"], self.prepared["manifestSha256"])

    def test_scores_match_hand_computed_expectations(self):
        score_run = self.ws.score(self.prepared, self.cands)
        self.assertEqual(score_run["outcome"], "SUCCEEDED")
        result = self.ws.result()
        python = lambda name: result["pipelines"][name]["summary"]["perLanguage"]["python"]
        product = python(pipelines.PRODUCT)
        self.assertEqual((product["macro"]["precision"], product["macro"]["recall"], product["macro"]["f1"]),
                         (1.0, 1.0, 1.0))
        self.assertEqual(product["spuriousAlert"], {"rate": 0.0, "alarms": 0, "queries": 1})
        self.assertEqual(product["abstention"]["coverage"], 0.6)
        self.assertEqual(product["multiMatch"], {"queries": 1, "recall": 1.0})
        self.assertEqual(product["counts"]["noMatchQueries"], 2)
        self.assertEqual(product["counts"]["noMatchFullyReviewed"], 1)
        self.assertEqual(product["counts"]["unknownJudgments"], 3)
        cut = python(pipelines.TOKEN_RANK_CUT)["macro"]
        self.assertAlmostEqual(cut["recall"], 2.5 / 3)
        self.assertEqual(cut["precision"], 1.0)
        self.assertAlmostEqual(cut["f1"], (2 / 3 + 2) / 3)
        tuned = python(pipelines.FINE_TUNED_RETRIEVAL)
        self.assertEqual(tuned["spuriousAlert"]["rate"], 1.0)
        verified = python(pipelines.FINE_TUNED_RETRIEVAL_PLUS_VERIFIER)
        self.assertEqual(verified["macro"]["f1"], 1.0)
        self.assertEqual(verified["spuriousAlert"]["rate"], 0.0)
        delta = result["pipelines"][pipelines.TOKEN_RANK_CUT]["pairedDeltaVsProduct"]
        self.assertAlmostEqual(delta["point"]["macroRecall"], (2.5 / 3 + 1) / 2 - 1)
        self.assertIsNone(result["pipelines"][pipelines.PRODUCT]["pairedDeltaVsProduct"])
        self.assertEqual(result["pipelines"][pipelines.PRODUCT]["summary"]["confidenceIntervals"]["unit"], "family")

    def test_claim_gates_cover_validated_unvalidated_and_failed(self):
        self.ws.score(self.prepared, self.cands)
        gates = lambda name: self.ws.result()["pipelines"][name]["claimGates"]
        product = gates(pipelines.PRODUCT)
        self.assertEqual(product["perGroup"]["python"]["status"], "VALIDATED")
        self.assertEqual(product["perGroup"]["java"]["status"], "UNVALIDATED")
        self.assertEqual(product["adequateLanguages"], ["python"])
        self.assertEqual(product["perGroup"]["typescript:tsx"]["status"], "UNVALIDATED")
        tuned = gates(pipelines.FINE_TUNED_RETRIEVAL)
        self.assertEqual(tuned["perGroup"]["python"]["status"], "FAILED")
        self.assertIn("spuriousAlertRateMax", tuned["perGroup"]["python"]["failedGates"])
        self.assertEqual(self.ws.result()["statuses"]["reviewerAcceptance"], "NOT_RECORDED")

    def test_precision_below_the_gate_is_reported_as_failed(self):
        other = Workspace(self)
        prepared, cands = other.full_run(corpus=corpus_document(swap=True))
        other.score(prepared, cands)
        product = other.result()["pipelines"][pipelines.PRODUCT]
        self.assertEqual(product["summary"]["perLanguage"]["python"]["macro"]["precision"], 0.0)
        self.assertEqual(product["claimGates"]["perGroup"]["python"]["status"], "FAILED")
        self.assertIn("macroPrecisionMin", product["claimGates"]["perGroup"]["python"]["failedGates"])
        self.assertEqual(product["claimGates"]["overall"]["detector"]["status"], "FAILED")

    def test_second_score_for_the_same_manifest_is_refused_and_a_rerun_needs_a_reason(self):
        self.ws.score(self.prepared, self.cands)
        first = (guards.run_dir("score-01", self.ws.run_root) / score.RESULT_NAME).read_bytes()
        with self.assertRaises(guards.ScoringRunRefused):
            self.ws.score(self.prepared, self.cands, run_id="score-02")
        with self.assertRaises(guards.ScoringRunRefused):
            self.ws.score(self.prepared, self.cands, run_id="score-02", rerun_of="score-01",
                          reason="UNFAVORABLE_RESULT")
        self.assertFalse((guards.run_dir("score-02", self.ws.run_root) / score.RESULT_NAME).exists())
        rerun = self.ws.score(self.prepared, self.cands, run_id="score-02", rerun_of="score-01",
                              reason="IMPLEMENTATION_DEFECT")
        self.assertEqual(rerun["outcome"], "SUCCEEDED")
        self.assertEqual((guards.run_dir("score-01", self.ws.run_root) / score.RESULT_NAME).read_bytes(), first)
        recorded = self.ws.result("score-02")["registration"]
        self.assertEqual((recorded["rerunOf"], recorded["reason"], recorded["priorRunsRetained"]),
                         ("score-01", "IMPLEMENTATION_DEFECT", ["score-01"]))

    def test_same_run_id_collides(self):
        self.ws.score(self.prepared, self.cands)
        with self.assertRaises(guards.ScoringRunRefused):
            self.ws.score(self.prepared, self.cands)

    def test_manifest_drift_is_refused_at_score(self):
        path = self.ws.prepared_dir() / manifest.MANIFEST_NAME
        drifted = read_json(path)
        drifted["pooling"]["depthK"] += 1
        path.write_bytes(canonical(drifted))
        with self.assertRaises(ValueError):
            self.ws.score(self.prepared, self.cands)
        path.write_bytes(json.dumps(drifted, indent=1).encode())
        with self.assertRaises(ValueError):
            self.ws.score(self.prepared, self.cands)

    def test_candidate_artifact_hash_mismatch_is_refused(self):
        with self.assertRaises(ValueError):
            self.ws.score(self.prepared, {**self.cands, "candidatesSha256": "0" * 64})
        path = guards.run_dir("cand-01", self.ws.run_root) / candidates.CANDIDATES_NAME
        path.write_bytes(path.read_bytes() + b" ")
        with self.assertRaises(ValueError):
            self.ws.score(self.prepared, self.cands)
        self.assertFalse(list(self.ws.registry.glob("*/*")) if self.ws.registry.exists() else [])

    def test_candidates_must_embed_the_manifest_and_corpus_hashes_they_were_generated_under(self):
        path = guards.run_dir("cand-01", self.ws.run_root) / candidates.CANDIDATES_NAME
        original = path.read_bytes()
        for key in ("freezeManifestSha256", "preparedCorpusSha256"):
            document = json.loads(original)
            document[key] = "2" * 64
            raw = canonical(document)
            path.write_bytes(raw)
            with self.subTest(key=key), self.assertRaises(ValueError) as context:
                self.ws.score(self.prepared, {"candidatesSha256": digest(raw)})
            self.assertIn("not bound", str(context.exception))
        path.write_bytes(original)
        self.assertEqual(self.ws.score(self.prepared, self.cands)["outcome"], "SUCCEEDED")
        result = self.ws.result()
        self.assertEqual((result["freezeManifestSha256"], result["preparedCorpusSha256"], result["candidatesSha256"]),
                         (self.prepared["manifestSha256"], read_json(path)["preparedCorpusSha256"],
                          self.cands["candidatesSha256"]))
        self.assertNotIn("generatedAtNs", read_json(path))

    def test_candidates_bound_to_another_manifest_are_refused(self):
        document = read_json(guards.run_dir("cand-01", self.ws.run_root) / candidates.CANDIDATES_NAME)
        document["freezeManifestSha256"] = "1" * 64
        raw = canonical(document)
        path = guards.run_dir("cand-01", self.ws.run_root) / candidates.CANDIDATES_NAME
        path.write_bytes(raw)
        with self.assertRaises(ValueError):
            self.ws.score(self.prepared, {"candidatesSha256": digest(raw)})

    def test_scoring_an_unbound_corpus_is_refused(self):
        pending = Workspace(self)
        pending.freeze()
        with self.assertRaises(ValueError):
            candidates.verify_bound_inputs(guards.run_dir("freeze-01", pending.run_root), pending.frozen_sha256)

    def test_sealed_marker_in_stage_arguments_is_refused(self):
        with self.assertRaises(guards.SealedInputRefused):
            self.ws.score(self.prepared, self.cands, run_id="score-final-test")
        with self.assertRaises(guards.SealedInputRefused):
            candidates.run(run_id="cand-02", prepare_run_id="sealed-prepare", manifest_sha256=self.ws.bound_sha256,
                           components=None, store=None,
                           run_root=self.ws.run_root)

    def test_a_failure_leaves_failed_run_evidence_and_no_result(self):
        class Broken:
            identity = fake_components().verifier.identity

            def probabilities(self, pair_ids):
                raise RuntimeError("verifier exploded")

        components = fake_components()
        components.verifier = Broken()
        with self.assertRaises(guards.StageFailed):
            self.ws.candidates(run_id="cand-02", components=components)
        directory = guards.run_dir("cand-02", self.ws.run_root)
        self.assertFalse((directory / candidates.CANDIDATES_NAME).exists())
        failure = read_json(directory / "failed-run.json")
        self.assertEqual((failure["outcome"], failure["stage"], failure["errorType"]),
                         ("FAILED", "candidates", "RuntimeError"))

    def test_models_run_one_at_a_time_and_are_released(self):
        components = fake_components()
        self.ws.candidates(run_id="cand-03", components=components)
        self.assertEqual((components.e0.released, components.e3.released, components.verifier.released),
                         (1, 1, 1))

    def test_candidate_lists_are_deterministic_apart_from_timings(self):
        again = self.ws.candidates(run_id="cand-04")
        def stable(run_id):
            document = read_json(guards.run_dir(run_id, self.ws.run_root) / candidates.CANDIDATES_NAME)
            for query in document["queries"].values():
                query.pop("stageSeconds")
                for record in query["pipelines"].values():
                    record.pop("timingsSeconds")
            return {key: value for key, value in document.items() if key != "resources"}
        self.assertEqual(stable("cand-01"), stable("cand-04"))
        self.assertEqual(again["outcome"], "SUCCEEDED")


if __name__ == "__main__":
    unittest.main()
