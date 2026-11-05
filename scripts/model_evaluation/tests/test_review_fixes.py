import copy
import json

import numpy as np
import re
import unittest
from pathlib import Path
from unittest.mock import patch

from scripts.code_analysis.contracts import canonical, digest
from scripts.model_evaluation import (candidates, cli, components, guards, manifest, metrics, models_check, prepare,
                                      product_search, resources, score, validation)
from scripts.model_evaluation.tests._fixtures import HEX, STATE, manifest_inputs, write_packages
from scripts.model_evaluation.tests._pipeline import (HistogramEncoder, TrigramVerifier, Workspace, corpus_document,
                                                      fake_components, fake_identity, fid, small_manifest_inputs)

PACKAGE_DIR = Path(manifest.__file__).resolve().parent


def read_json(path):
    return json.loads(Path(path).read_bytes())


class ManifestBindingTests(unittest.TestCase):
    """Item 1: the freeze binds what runs."""

    def setUp(self):
        self.ws = Workspace(self)
        self.ws.full_run()

    def tampered(self, change):
        components_ = fake_components()
        change(components_)
        return components_

    def test_a_loaded_identity_that_differs_from_the_manifest_is_refused_before_any_encoding(self):
        changes = {
            "e0 state": lambda c: c.e0.identity.update(stateSha256="00" * 32),
            "e3 state": lambda c: c.e3.identity.update(stateSha256="00" * 32),
            "device not in the pair": lambda c: [x.identity.update(device="tpu") for x in (c.e0, c.e3, c.verifier)],
            "device differs between models": lambda c: c.e0.identity.update(device="cuda"),
            "tokenizer files": lambda c: c.e0.identity.update(tokenizerHashes={"vocab.json": "00" * 32}),
            "chunking": lambda c: c.e3.identity.update(chunking="other-chunking"),
            "verifier threshold": lambda c: c.verifier.identity.update(threshold=0.5),
            "verifier state": lambda c: c.verifier.identity.update(stateSha256="11" * 32),
            "dolos shape": lambda c: setattr(c.token_scorer, "identity", {"dolosK": 5, "dolosWindow": 4}),
        }
        for number, (name, change) in enumerate(changes.items()):
            fake = self.tampered(change)
            touched = []
            fake.e0.windows = lambda raw, file, original=fake.e0.windows: touched.append(1) or original(raw, file)
            with self.subTest(name), self.assertRaises(guards.StageFailed):
                self.ws.candidates(run_id="cand-tamper-%d" % number, components=fake)
            self.assertEqual(touched, [], name)

    def tamper_candidates(self, change):
        path = guards.run_dir("cand-01", self.ws.run_root) / candidates.CANDIDATES_NAME
        document = read_json(path)
        change(document)
        raw = canonical(document)
        path.write_bytes(raw)
        return {"candidatesSha256": digest(raw)}

    def test_score_refuses_a_candidate_artifact_whose_recorded_identities_differ(self):
        changed = self.tamper_candidates(lambda d: d["modelIdentities"]["e0"].update(stateSha256="00" * 32))
        with self.assertRaises(ValueError):
            self.ws.score(self.ws_prepared(), changed)

    def ws_prepared(self):
        return {"manifestSha256": self.ws.bound_sha256}

    def test_validate_evidence_refuses_recorded_identities_that_differ(self):
        # score is made to accept the artifact so that only validate-evidence can catch the difference
        changed = self.tamper_candidates(lambda d: d["modelIdentities"]["verifier"].update(threshold=0.5))
        with patch.object(candidates, "check_identities", return_value="cpu"):
            self.ws.score(self.ws_prepared(), changed)
        with self.assertRaises(ValueError) as context:
            validation.run(run_id="evidence-01", freeze_run_id="freeze-01", prepare_run_id="prepare-01",
                           candidates_run_id="cand-01", score_run_id="score-01", run_root=self.ws.run_root,
                           registry_root=self.ws.registry, capture=fake_identity)
        self.assertIn("threshold", str(context.exception))

    def test_freeze_preflight_refuses_unverified_models_and_writes_nothing(self):
        inputs = small_manifest_inputs()
        paths = write_packages(self.ws.root, inputs)
        path = self.ws.write_json("preflight.json", inputs)
        threshold = inputs["operatingPoints"]["validationThresholdRuleResult"]["threshold"]
        states = dict(STATE)
        kwargs = dict(inputs_path=path, run_root=self.ws.run_root, inputs_root=self.ws.root,
                      evidence_paths=paths)
        with self.assertRaises(ValueError):
            manifest.freeze(run_id="pre-01", state_hashes={"e0": states["e0"]}, threshold=threshold, **kwargs)
        with self.assertRaises(ValueError):
            manifest.freeze(run_id="pre-01", state_hashes=states, threshold=None, **kwargs)
        with self.assertRaises(ValueError):
            manifest.freeze(run_id="pre-01", state_hashes={**states, "e3": "00" * 32}, threshold=threshold, **kwargs)
        self.assertFalse((guards.run_dir("pre-01", self.ws.run_root) / manifest.MANIFEST_NAME).exists())
        done = manifest.freeze(run_id="pre-01", state_hashes=states, threshold=threshold, **kwargs)
        self.assertEqual(done["outcome"], "SUCCEEDED")

    def test_the_service_budget_and_dolos_shape_are_pinned(self):
        contracts = manifest.service_contracts()
        built, _ = manifest.freeze_manifest(manifest_inputs())
        self.assertEqual((built["candidateBudget"]["tokenTop"], built["candidateBudget"]["semanticTop"],
                          built["candidateBudget"]["maxCandidates"]),
                         (contracts.TOKEN_TOP, contracts.SEMANTIC_TOP, contracts.UNION_BUDGET))
        for section, key, value in (("candidateBudget", "tokenTop", 19), ("candidateBudget", "semanticTop", 21),
                                    ("candidateBudget", "maxCandidates", 41), ("execution", "dolosK", 5),
                                    ("execution", "dolosWindow", 3), ("cpuGpuParity", "devices", ["cpu"])):
            data = manifest_inputs()
            data[section][key] = value
            with self.subTest(key=key), self.assertRaises(ValueError):
                manifest.freeze_manifest(data)

    def test_cuts_cannot_exceed_the_retained_channel_depth(self):
        data = manifest_inputs()
        data["operatingPoints"]["tokenRankCut"]["rank"] = 21
        with self.assertRaises(ValueError):
            manifest.freeze_manifest(data)


class FreezeBypassTests(unittest.TestCase):
    """Item 2: the one-run rule is keyed by the corpus, not by the manifest."""

    def setUp(self):
        self.ws = Workspace(self)
        self.prepared, self.cands = self.ws.full_run()
        self.ws.score(self.prepared, self.cands)

    def refreeze(self):
        changed = small_manifest_inputs()
        changed["operatingPoints"]["semanticRankCut"]["rank"] = 2
        self.ws.freeze("freeze-02", inputs=changed)
        prepared = self.ws.prepare("prepare-02", "freeze-02")
        cands = self.ws.candidates(run_id="cand-02", prepare_run_id="prepare-02")
        return prepared, cands

    def second_score(self, prepared, cands, **kwargs):
        return score.run(run_id="score-02", prepare_run_id="prepare-02", candidates_run_id="cand-02",
                         manifest_sha256=prepared["manifestSha256"], candidates_sha256=cands["candidatesSha256"],
                         run_root=self.ws.run_root, registry_root=self.ws.registry, capture=fake_identity, **kwargs)

    def test_a_second_manifest_over_the_same_corpus_needs_a_reason_and_a_reference(self):
        prepared, cands = self.refreeze()
        self.assertNotEqual(prepared["manifestSha256"], self.prepared["manifestSha256"])
        with self.assertRaises(guards.ScoringRunRefused):
            self.second_score(prepared, cands)
        with self.assertRaises(guards.ScoringRunRefused):
            self.second_score(prepared, cands, rerun_of="score-01", reason="UNFAVORABLE_RESULT")
        done = self.second_score(prepared, cands, rerun_of="score-01", reason="IMPLEMENTATION_DEFECT")
        self.assertEqual(done["outcome"], "SUCCEEDED")
        self.assertEqual(read_json(guards.run_dir("score-02", self.ws.run_root) / score.RESULT_NAME)[
            "registration"]["priorRunsRetained"], ["score-01"])

    def test_the_registry_key_is_stable_under_refreezing(self):
        prepared, cands = self.refreeze()
        first = read_json(guards.run_dir("prepare-01", self.ws.run_root) / prepare.PREPARED_NAME)
        second = read_json(guards.run_dir("prepare-02", self.ws.run_root) / prepare.PREPARED_NAME)
        self.assertNotEqual(first["freezeManifestSha256"], second["freezeManifestSha256"])
        self.assertEqual(prepare.corpus_identity_sha256(first), prepare.corpus_identity_sha256(second))
        self.assertEqual(prepare.label_sha256(first), prepare.label_sha256(second))

    def test_evidence_for_a_later_run_lists_every_attempt_result_hash(self):
        prepared, cands = self.refreeze()
        self.second_score(prepared, cands, rerun_of="score-01", reason="INFRASTRUCTURE_FAULT")
        first = digest((guards.run_dir("score-01", self.ws.run_root) / score.RESULT_NAME).read_bytes())
        second = digest((guards.run_dir("score-02", self.ws.run_root) / score.RESULT_NAME).read_bytes())

        def validate(**kwargs):
            return validation.run(run_id="evidence-%d" % len(kwargs.get("attempt_results") or ()),
                                  freeze_run_id="freeze-02", prepare_run_id="prepare-02",
                                  candidates_run_id="cand-02", score_run_id="score-02", run_root=self.ws.run_root,
                                  registry_root=self.ws.registry, capture=fake_identity, **kwargs)
        with self.assertRaises(ValueError):
            validate()
        with self.assertRaises(ValueError):
            validate(attempt_results={"score-02": second})
        with self.assertRaises(ValueError):
            validate(attempt_results={"score-01": "0" * 64, "score-02": second})
        done = validate(attempt_results={"score-01": first, "score-02": second})
        package = read_json(Path(done["directory"]) / validation.PACKAGE_NAME)
        self.assertEqual([(row["runId"], row["resultSha256"]) for row in package["scoringAttempts"]],
                         [("score-01", first), ("score-02", second)])


class PoolingEnforcementTests(unittest.TestCase):
    """Item 6: pooled top-K files must be judged at scoring."""

    def run_with(self, change):
        ws = Workspace(self)
        corpus = corpus_document()
        change(corpus)
        prepared, cands = ws.full_run(corpus=corpus)
        return ws, prepared, cands

    def test_complete_pooling_scores(self):
        ws, prepared, cands = self.run_with(lambda corpus: None)
        self.assertEqual(ws.score(prepared, cands)["outcome"], "SUCCEEDED")

    def test_an_unjudged_pool_file_is_refused_with_the_list(self):
        def drop(corpus):
            for query in corpus["queries"]:
                if query["queryId"] == "qa":
                    query["judgments"].pop(fid("g2"))
        ws, prepared, cands = self.run_with(drop)
        with self.assertRaises(ValueError) as context:
            ws.score(prepared, cands)
        self.assertIn("qa:" + fid("g2"), str(context.exception))
        self.assertFalse(list(ws.registry.glob("*/attempt-*.json")))

    def test_provenance_naming_a_single_system_is_refused_under_the_union_rule(self):
        def narrow(corpus):
            for query in corpus["queries"]:
                if query["queryId"] == "qb":
                    query["judgments"][fid("g3")]["provenance"] = ["TOKEN"]
        ws, prepared, cands = self.run_with(narrow)
        with self.assertRaises(ValueError) as context:
            ws.score(prepared, cands)
        self.assertIn("qb:" + fid("g3"), str(context.exception))

    def test_the_candidate_artifact_records_the_pool_per_system_and_a_deterministic_sample(self):
        ws, prepared, cands = self.run_with(lambda corpus: None)
        document = read_json(guards.run_dir("cand-01", ws.run_root) / candidates.CANDIDATES_NAME)
        pool = {row["system"]: row["fileIds"] for row in document["queries"]["qa"]["pool"]}
        self.assertEqual(set(pool), {"TOKEN", "E0", "E3", "RANDOM_SAMPLE"})
        self.assertEqual(len(pool["RANDOM_SAMPLE"]), 6)
        pooling = {"randomSeed": 7, "randomRecallSampleSize": 3}
        gallery = [fid("g%d" % i) for i in range(1, 6)]
        self.assertEqual(candidates.pool_sample(pooling, "qa", gallery), candidates.pool_sample(pooling, "qa", gallery))
        self.assertNotEqual(candidates.pool_sample(pooling, "qa", gallery), candidates.pool_sample(pooling, "qb", gallery))


class LazyIdentityTests(unittest.TestCase):
    """Item 7: identity is cheap and never loads a model; each model loads once and is released."""

    def lazy(self, name, counter):
        def loader():
            counter.append(name)
            return HistogramEncoder(name) if name != "verifier" else TrigramVerifier()
        identity = HistogramEncoder(name).identity if name != "verifier" else TrigramVerifier().identity
        return components.LazyComponent(identity, loader)

    def test_reading_identity_never_loads(self):
        loads = []
        lazy = self.lazy("e0", loads)
        for _ in range(5):
            self.assertEqual(lazy.identity["stateSha256"], STATE["e0"])
        self.assertEqual((loads, lazy.loads), ([], 0))
        lazy.embed([])
        lazy.embed([])
        self.assertEqual((loads, lazy.loads), (["e0"], 1))
        lazy.release()
        lazy.embed([])
        self.assertEqual(lazy.loads, 2)

    def test_a_loaded_model_that_differs_from_the_declared_identity_is_refused(self):
        lazy = components.LazyComponent(HistogramEncoder("e0").identity, lambda: HistogramEncoder("e3"))
        with self.assertRaises(ValueError):
            lazy.embed([])
        self.assertEqual(lazy.loads, 1)

    def test_the_candidate_stage_loads_each_model_once_and_releases_it(self):
        ws = Workspace(self)
        loads = []
        wrapped = components.Components(
            token_scorer=fake_components().token_scorer, e0=self.lazy("e0", loads), e3=self.lazy("e3", loads),
            verifier=self.lazy("verifier", loads))
        ws.full_run(components=wrapped)
        self.assertEqual(sorted(loads), ["e0", "e3", "verifier"])
        for lazy in (wrapped.e0, wrapped.e3, wrapped.verifier):
            self.assertEqual(lazy.loads, 1)
            self.assertIsNone(lazy._component)

    def test_the_declared_identity_is_required_by_the_loader(self):
        with self.assertRaises(ValueError):
            components.declared_identity({"e0": {"identity": {"recordedIdentity": "x"}}}, "e0")


class ProductSearchTests(unittest.TestCase):
    """Item 8: the product's per-fragment search and file aggregation."""

    def vectors(self, count, dimension=4, offset=0):
        rows = []
        for index in range(count):
            raw = [((index + offset + k * 3) % 7) + 1 for k in range(dimension)]
            norm = sum(v * v for v in raw) ** 0.5
            rows.append([v / norm for v in raw])
        return rows

    def brute(self, queries, gallery, limit=20):
        best = {}
        for file_id, stored in gallery.items():
            for vector in stored:
                for query in queries:
                    distance = float(1.0 - np.float32(np.dot(np.asarray(query, np.float32),
                                                                np.asarray(vector, np.float32))))
                    best[file_id] = min(best.get(file_id, 9.0), distance)
        ordered = sorted(best, key=lambda fid_: (best[fid_], fid_))[:limit]
        return [{"fileId": f, "score": max(0.0, 1.0 - best[f]), "rank": r} for r, f in enumerate(ordered, 1)]

    def same_ranking(self, ranked, expected):
        """Same files and scores (float32 rounding tolerance); order may differ only inside that tolerance."""
        self.assertEqual({r["fileId"] for r in ranked}, {r["fileId"] for r in expected})
        reference = {r["fileId"]: r["score"] for r in expected}
        for row in ranked:
            self.assertAlmostEqual(row["score"], reference[row["fileId"]], places=6)
        scores = [row["score"] for row in ranked]
        self.assertEqual(scores, sorted(scores, reverse=True))
        self.assertEqual([r["rank"] for r in ranked], list(range(1, len(ranked) + 1)))

    def test_matches_a_brute_force_minimum_distance_over_all_window_pairs(self):
        gallery = {fid("f%d" % i): self.vectors(1 + i % 3, offset=i * 5) for i in range(12)}
        queries = self.vectors(4, offset=2)
        ranked = product_search.semantic_rank(queries, gallery)
        expected = self.brute(queries, gallery)
        self.same_ranking(ranked, expected)

    def test_the_service_aggregation_function_is_the_one_used_with_batches_of_eighty(self):
        contracts, stages, _ = product_search._service()
        gallery = {fid("f%d" % i): self.vectors(2, offset=i) for i in range(6)}
        queries = self.vectors(85)
        with patch.object(stages, "_exact_retrieval", wraps=stages._exact_retrieval) as spy, \
                patch.object(product_search, "exact_query", wraps=product_search.exact_query) as search:
            ranked = product_search.semantic_rank(queries, gallery)
        self.assertEqual(spy.call_count, 1)
        self.assertEqual(search.call_count, 2)
        self.assertEqual([len(call.args[2]) for call in search.call_args_list], [80, 5])
        self.same_ranking(ranked, self.brute(queries, gallery))
        self.assertLessEqual(len(ranked), contracts.SEMANTIC_TOP)

    def test_ties_are_ordered_by_file_identity_and_depth_is_the_service_depth(self):
        same = self.vectors(1)
        gallery = {fid("f%d" % i): same for i in range(25)}
        ranked = product_search.semantic_rank(same, gallery)
        self.assertEqual(len(ranked), 20)
        self.assertEqual([r["fileId"] for r in ranked], sorted(gallery)[:20])
        self.assertEqual([r["rank"] for r in ranked], list(range(1, 21)))

    def test_fragments_are_extracted_units_and_non_executable_files_have_none(self):
        raw = b"def a(x):\n    return x + 1\n\n\ndef b(y):\n    return y * 2\n"
        row = {"fileId": digest(raw), "sha256": digest(raw), "language": "python"}
        self.assertEqual(len(product_search.fragments(raw, row)), 2)
        text = b"import os\nimport sys\n"
        self.assertEqual(product_search.fragments(text, {"fileId": digest(text), "sha256": digest(text),
                                                         "language": "python"}), [])

    def test_the_candidate_counts_never_exceed_the_service_depths(self):
        ws = Workspace(self)
        ws.full_run()
        document = read_json(guards.run_dir("cand-01", ws.run_root) / candidates.CANDIDATES_NAME)
        contracts = manifest.service_contracts()
        for entry in document["queries"].values():
            counts = entry["pipelines"]["PRODUCT"]["counts"]
            self.assertLessEqual(counts["tokenCandidates"], contracts.TOKEN_TOP)
            self.assertLessEqual(counts["semanticCandidates"], contracts.SEMANTIC_TOP)
            self.assertLessEqual(counts["union"], contracts.UNION_BUDGET)


class CorpusProtectionTests(unittest.TestCase):
    """Item 10: structural protection through excluded sources and the closed role allowlist."""

    def prepare_with(self, change):
        ws = Workspace(self)
        corpus = corpus_document()
        change(corpus)
        ws.materialise({})
        ws.freeze()
        return ws, corpus

    def attempt(self, change):
        ws, corpus = self.prepare_with(change)
        return ws.prepare(corpus=corpus)

    def test_a_declared_excluded_source_is_refused_for_gallery_and_query_files(self):
        for index in (0, -1):
            def relabel(corpus, index=index):
                corpus["files"][index]["source"] = "synthetic-excluded-a"
            with self.subTest(index=index), self.assertRaises(guards.SealedInputRefused):
                self.attempt(relabel)

    def test_evaluation_members_relabelled_to_an_excluded_source_are_refused(self):
        def relabel(corpus):
            for row in corpus["files"]:
                if row["kind"] == "query":
                    row["source"] = "synthetic-excluded-b"
        with self.assertRaises(guards.SealedInputRefused):
            self.attempt(relabel)

    def test_disguised_excluded_sources_are_refused(self):
        for disguise in ("Synthetic-Excluded-A", "synthetic_excluded_a", "synthetic​-excluded-a",
                         "synthetic-excluded-а"):
            def relabel(corpus, disguise=disguise):
                corpus["files"][0]["source"] = disguise
            with self.subTest(disguise=disguise), self.assertRaises(guards.SealedInputRefused):
                self.attempt(relabel)

    def test_an_undeclared_or_empty_source_is_refused(self):
        def drop(corpus):
            del corpus["files"][0]["source"]
        with self.assertRaises(ValueError):
            self.attempt(drop)

        def empty(corpus):
            corpus["files"][0]["source"] = ""
        with self.assertRaises(ValueError):
            self.attempt(empty)

    def test_declared_sources_outside_the_list_pass(self):
        result = self.attempt(lambda corpus: None)
        self.assertEqual(result["outcome"], "SUCCEEDED")

    def test_query_roles_outside_the_closed_allowlist_are_refused(self):
        for role in ("train", "validation", "heldout", "test"):
            def relabel(corpus, role=role):
                for row in corpus["files"]:
                    if row["kind"] == "query":
                        row["partition"] = role
            with self.subTest(role=role), self.assertRaises((ValueError, guards.SealedInputRefused)):
                self.attempt(relabel)

    def test_a_marker_in_the_corpus_is_refused_in_any_spelling(self):
        for spelling in ("held-out", "with-held", "s_e_a_l", "f1nal-test"):
            def mark(corpus, spelling=spelling):
                corpus["files"][0]["family"] = spelling
            with self.subTest(spelling=spelling), self.assertRaises(guards.SealedInputRefused):
                self.attempt(mark)

    def test_the_excluded_source_list_is_required_and_unique(self):
        for value in ([], "synthetic", ["a", "a"], [""], None):
            data = manifest_inputs()
            data["excludedSources"] = value
            with self.subTest(value=value), self.assertRaises(ValueError):
                manifest.freeze_manifest(data)


class TimingAndExitTests(unittest.TestCase):
    """Item 11: complete end-to-end timing, enforced protocol, exit codes, external hashes."""

    class Clock:
        def __init__(self):
            self.now = 0.0

        def __call__(self):
            return self.now

    def timed_components(self, clock):
        class Scorer:
            identity = fake_components().token_scorer.identity
            inner = fake_components().token_scorer

            def pair_scores(self, language, files):
                clock.now += 10.0
                return self.inner.pair_scores(language, files)

        class Encoder(HistogramEncoder):
            def embed(self, chunks):
                clock.now += 1.0
                return super().embed(chunks)

        made = fake_components()
        return components.Components(token_scorer=Scorer(), e0=Encoder("e0"), e3=Encoder("e3", shift=1),
                                     verifier=made.verifier)

    def test_end_to_end_time_includes_query_encoding_and_token_scoring(self):
        ws = Workspace(self)
        ws.full_run()
        clock = self.Clock()
        ws.candidates(run_id="cand-clock", components=self.timed_components(clock), clock=clock)
        stages = read_json(guards.run_dir("cand-clock", ws.run_root) / candidates.CANDIDATES_NAME)[
            "resources"]["stages"]["endToEndQuery"]
        # java: 10s token batch for its single query + one base and one trained query encoding
        self.assertEqual(stages["max"], 12.0)
        # python: ten seconds shared by five queries + the same two encodings
        self.assertEqual((stages["samples"], stages["excludedWarmup"]), (6, 1))
        self.assertEqual(stages["p50"], 4.0)

    def test_warmup_is_counted_in_queries(self):
        timer = resources.StageTimer(warmup=2)
        for seconds in (50.0, 60.0, 1.0, 2.0, 3.0):
            timer.add("endToEndQuery", seconds)
        record = resources.resource_record(timer, {"warmupQueries": 2, "timedQueries": 3, "percentiles": [50, 95]},
                                           memory={"ramPeakBytes": None, "vramPeakBytes": None})
        self.assertEqual(record["stages"]["endToEndQuery"]["max"], 3.0)

    def test_too_few_timed_queries_refuse_the_measurement(self):
        timer = resources.StageTimer(warmup=1)
        for seconds in (1.0, 2.0, 3.0):
            timer.add("endToEndQuery", seconds)
        protocol = {"warmupQueries": 1, "timedQueries": 3, "percentiles": [50, 95]}
        with self.assertRaises(ValueError):
            resources.resource_record(timer, protocol, memory={})
        with self.assertRaises(ValueError):
            resources.resource_record(resources.StageTimer(), protocol, memory={})

    def test_the_candidate_stage_enforces_the_frozen_timed_query_count(self):
        ws = Workspace(self)
        inputs = small_manifest_inputs()
        inputs["resourceProtocol"]["timedQueries"] = 6
        with self.assertRaises(guards.StageFailed):
            self.full_with_inputs(ws, inputs)
        failure = read_json(guards.run_dir("cand-01", ws.run_root) / "failed-run.json")
        self.assertIn("timed queries", failure["error"])

    def full_with_inputs(self, ws, inputs):
        from scripts.model_evaluation.tests import _pipeline
        names = [("g1", _pipeline.G1), ("g1b", _pipeline.G1), ("g2", _pipeline.G2), ("g3", _pipeline.G3),
                 ("g4", _pipeline.G4), ("g5", _pipeline.G5), ("j1", _pipeline.J1), ("j2", _pipeline.J2),
                 ("file-qa", _pipeline.G1), ("file-qb", _pipeline.G3), ("file-qc", _pipeline.G4),
                 ("file-qn1", _pipeline.NOVEL_1), ("file-qn2", _pipeline.NOVEL_2), ("file-qj", _pipeline.J1)]
        ws.materialise({fid(name): text for name, text in names})
        ws.freeze(inputs=inputs)
        ws.prepare()
        ws.candidates()

    def test_a_refusal_raised_inside_scoring_is_not_wrapped_into_a_failure(self):
        ws = Workspace(self)
        prepared, cands = ws.full_run()
        with patch.object(score, "_compute", side_effect=guards.SealedInputRefused("withheld")):
            with self.assertRaises(guards.SealedInputRefused):
                ws.score(prepared, cands)
        self.assertFalse((guards.run_dir("score-01", ws.run_root) / "failed-run.json").exists())

    def test_the_cli_exits_with_the_refusal_code_for_a_refusal_inside_scoring(self):
        with patch.object(score, "run", side_effect=guards.SealedInputRefused("withheld")):
            code = cli.main(["score", "--run-id", "s1", "--prepare-run-id", "p1", "--candidates-run-id", "c1",
                             "--manifest-sha256", HEX, "--candidates-sha256", HEX])
        self.assertEqual(code, cli.EXIT_REFUSED)

    def test_the_components_description_is_scanned_for_withheld_markers(self):
        ws = Workspace(self)
        description = {"device": "cpu", "dolos": {"workRoot": "work", "k": 23, "window": 17},
                       "e0": {"modelDir": "m", "note": "from held-out run"}}
        path = ws.write_json("components.json", description)
        with self.assertRaises(guards.SealedInputRefused):
            components.load_components(path)

    def test_the_manifest_must_hash_to_an_externally_supplied_value(self):
        ws = Workspace(self)
        ws.full_run()
        directory = guards.run_dir("prepare-01", ws.run_root)
        value, actual = prepare.read_manifest(directory, ws.bound_sha256)
        self.assertEqual(actual, ws.bound_sha256)
        with self.assertRaises(ValueError):
            prepare.read_manifest(directory, HEX)
        # a self-consistent rewrite of the manifest is caught because the hash comes from outside
        rewritten = copy.deepcopy(value)
        rewritten["pooling"]["randomRecallSampleSize"] += 1
        (directory / manifest.MANIFEST_NAME).write_bytes(canonical(rewritten))
        with self.assertRaises(ValueError):
            prepare.read_manifest(directory, ws.bound_sha256)
        with self.assertRaises(TypeError):
            prepare.read_manifest(directory)

    def test_the_stages_refuse_a_manifest_that_differs_from_the_supplied_hash(self):
        ws = Workspace(self)
        ws.freeze()
        with self.assertRaises(ValueError):
            ws.prepare(manifest_sha256=HEX)

    def test_replaced_metrics_or_aggregation_sections_are_refused(self):
        built, _ = manifest.freeze_manifest(manifest_inputs())
        metrics.check_registration(built["metrics"], built["aggregation"])
        for section, change in (("metrics", {"names": ["precision", "invented"], "judgments": built["metrics"]["judgments"]}),
                                ("aggregation", {"caseWithinSeed": "median"})):
            with self.subTest(section=section), self.assertRaises(ValueError):
                metrics.check_registration(change if section == "metrics" else built["metrics"],
                                           change if section == "aggregation" else built["aggregation"])


class RemovedMachineryTests(unittest.TestCase):
    """Item 9: sealed roles are refused unconditionally and no owner-receipt machinery remains."""

    def test_no_receipt_machinery_remains_in_the_package(self):
        pattern = re.compile(r"receipt", re.IGNORECASE)
        offenders = []
        for path in sorted(PACKAGE_DIR.rglob("*.py")):
            if path.name in ("test_review_fixes.py", "test_guards.py"):
                continue
            for number, line in enumerate(path.read_text(encoding="utf-8").splitlines(), 1):
                if pattern.search(line):
                    offenders.append("%s:%d" % (path.name, number))
        self.assertEqual(offenders, [])
        self.assertNotIn("receipt", (PACKAGE_DIR / "README.md").read_text(encoding="utf-8").lower())

    def test_the_golden_comparison_with_the_service_never_skips(self):
        text = (PACKAGE_DIR / "tests" / "test_stages.py").read_text(encoding="utf-8")
        self.assertIn("test_product_union_projection_matches_the_analysis_service", text)
        self.assertNotIn("skipTest", text)
        self.assertNotIn("skipIf", text)

    def test_the_manifest_no_longer_has_a_receipt_or_overflow_field(self):
        built, _ = manifest.freeze_manifest(manifest_inputs())
        self.assertEqual(set(built["corpus"]), {"status", "identity"})
        self.assertNotIn("overflowRule", built["candidateBudget"])


if __name__ == "__main__":
    unittest.main()
