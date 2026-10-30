import json
import tempfile
import unittest
from pathlib import Path
from unittest import mock

try:
    import torch
    from transformers import RobertaTokenizerFast
    from scripts.code_analysis import baselines
    from scripts.code_analysis.contracts import LANGUAGES, digest
    from scripts.code_analysis.evaluation import cpu_rank
    from scripts.code_analysis.frozen_encoder import encode_chunks
    from scripts.code_analysis.representations import mapped_chunks
    from scripts.code_retrieval import index_export, index_verify, retrieval_eval, retrieval_models, validation_plan
    from scripts.code_retrieval.tests import _tiny_encoder as tiny
    TORCH_IMPORT_ERROR = None
except ImportError as exc:  # torch-less interpreters skip the evaluation checks
    TORCH_IMPORT_ERROR = exc

DIM = 768


def unit(index):
    vector = [0.0] * DIM
    vector[index % DIM] = 1.0
    return vector


def file_id(name):
    return digest(name.encode("utf-8"))


def identity(kind="frozen", **overrides):
    base = {"kind": kind, "label": "E0" if kind == "frozen" else "E1", "upstreamRevision": "rev",
            "checkpointSha256": "b" * 64, "fullStateSha256": "a" * 64,
            "tokenizerHashes": {"vocab.json": "c" * 64}, "chunking": "encoder-only-256-overlap32-max16-v1",
            "pooling": retrieval_models.POOLING, "encodeFunction": retrieval_models.ENCODE_FUNCTION,
            "dimension": DIM}
    base.update(overrides)
    return base


@unittest.skipIf(TORCH_IMPORT_ERROR is not None, "torch/transformers not importable")
class RetrievalEvalTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name) / "retrieval"
        self.root.mkdir()
        self.corpus = {"inputsManifestSha256": "d" * 64, "prepareRunId": "prepare-fixture",
                       "validationReportSha256": "e" * 64, "protocolSha256": "f" * 64, "configSha256": "1" * 64}
        self.galleries = {language: [{"fileId": file_id(language + "-a"), "sha256": digest(language.encode())},
                                      {"fileId": file_id(language + "-b"), "sha256": digest((language + "b").encode())}]
                          for language in LANGUAGES}
        self.plan = {"galleries": self.galleries, "queries": {}, "reportSha256": self.corpus["validationReportSha256"]}

    def encoded(self):
        result = {}
        for language, gallery in self.galleries.items():
            chunk_rows = [{"fileId": row["fileId"], "fragmentId": row["fileId"], "chunkOrdinal": 0, "tokenStart": 0,
                           "tokenEnd": 4, "totalInputTokens": 4, "representationSha256": "0" * 64,
                           "inputIds": [0, 1, 2, 3]} for row in gallery]
            file_rows = [{"fileId": row["fileId"], "chunkCount": 1, "originalTokenCount": 4,
                          "retainedUniqueTokenCount": 4, "omittedTokenCount": 0, "partialMLCoverage": False}
                         for row in gallery]
            vectors = [unit(index) for index in range(len(gallery))]
            result[language] = (chunk_rows, file_rows, vectors, 0.01)
        return result

    def write_index(self, run_name, label="E0", model_identity=None, encoded=None):
        run = self.root / "runs" / run_name
        model_identity = model_identity or identity()
        model_key = index_verify.model_key(model_identity)
        entry = {"modelKey": model_key, "kind": model_identity["kind"], "fields": model_identity}
        with mock.patch.object(index_export, "RETRIEVAL_ROOT", self.root):
            return index_export.write_index(run, label, entry, self.galleries, encoded or self.encoded(), self.corpus)

    def export_for(self, run_name, label="E0"):
        """Export evidence that describes the index exactly as it is on disk now."""
        manifest = json.loads((self.root / "runs" / run_name / "index" / label / "index-manifest.json")
                              .read_text(encoding="utf-8"))
        return {"schema": index_export.EXPORT_SCHEMA, "runId": run_name, "outcome": "SUCCEEDED", "identity": {},
                "models": {label: {"modelKey": manifest["modelKey"], "languages": manifest["languages"]}}}

    def load(self, run_name, label, model_key, export_evidence=None, corpus=None):
        export_evidence = export_evidence or self.export_for(run_name, label)
        with mock.patch.object(index_verify, "run_dir", lambda run_id: self.root / "runs" / run_id):
            return retrieval_eval.load_index(run_name, label, self.plan, corpus or self.corpus, model_key,
                                             export_evidence)

    # -- model and index identities --------------------------------------
    def test_frozen_and_trained_keys_differ_and_track_every_identity_field(self):
        frozen = index_verify.model_key(identity("frozen"))
        trained = index_verify.model_key(identity("trained"))
        self.assertNotEqual(frozen, trained)
        for field, value in (("fullStateSha256", "9" * 64), ("tokenizerHashes", {"vocab.json": "9" * 64}),
                             ("chunking", "other"), ("pooling", "other"), ("upstreamRevision", "other"),
                             ("checkpointSha256", "9" * 64), ("label", "E2")):
            with self.subTest(field=field):
                self.assertNotEqual(index_verify.model_key(identity("frozen", **{field: value})), frozen)

    def test_incomplete_model_identity_is_refused(self):
        broken = identity("frozen")
        del broken["fullStateSha256"]
        with self.assertRaisesRegex(ValueError, "incomplete model identity"):
            index_verify.model_key(broken)

    def test_index_key_changes_with_model_gallery_and_corpus(self):
        base = index_verify.index_key("a" * 64, self.galleries["python"], self.corpus)
        self.assertNotEqual(base, index_verify.index_key("b" * 64, self.galleries["python"], self.corpus))
        self.assertNotEqual(base, index_verify.index_key("a" * 64, self.galleries["python"][:1], self.corpus))
        self.assertNotEqual(base, index_verify.index_key("a" * 64, self.galleries["python"],
                                                         {**self.corpus, "protocolSha256": "0" * 64}))

    def test_index_write_is_immutable(self):
        self.write_index("export-fixture")
        with self.assertRaises(FileExistsError):
            self.write_index("export-fixture")

    def test_matching_index_loads_and_verifies_every_language(self):
        manifest = self.write_index("export-ok")
        model_key = manifest["modelKey"]
        _, indexed = self.load("export-ok", "E0", model_key)
        self.assertEqual(set(indexed), set(LANGUAGES))
        self.assertEqual(indexed["python"][0]["vector"], unit(0))

    def test_mixed_model_identity_is_refused(self):
        self.write_index("export-mixed")
        with self.assertRaisesRegex(ValueError, "mixed identity"):
            self.load("export-mixed", "E0", "0" * 64)

    def test_tampered_vectors_are_refused(self):
        manifest = self.write_index("export-tamper")
        path = self.root / "runs" / "export-tamper" / "index" / "E0" / "python.fp32le"
        raw = bytearray(path.read_bytes())
        raw[0] ^= 0x01
        path.write_bytes(bytes(raw))
        with self.assertRaisesRegex(ValueError, "vector bytes drift"):
            self.load("export-tamper", "E0", manifest["modelKey"])

    def test_incomplete_index_is_refused(self):
        manifest = self.write_index("export-missing")
        (self.root / "runs" / "export-missing" / "index" / "E0" / "java.chunks.json").unlink()
        with self.assertRaises(OSError):
            self.load("export-missing", "E0", manifest["modelKey"])

    def test_train_or_final_test_file_in_a_gallery_is_refused(self):
        for partition in ("train", "final-test"):
            with self.subTest(partition=partition):
                with self.assertRaisesRegex(ValueError, "escaped the validation partition"):
                    validation_plan.require_validation_row({"partition": partition, "language": "python",
                                                            "fileId": "x"}, "python")
        validation_plan.require_validation_row({"partition": "validation", "language": "python", "fileId": "x"},
                                               "python")

    # -- ranking, aggregation and scoring rules -------------------------------
    def query(self, scored, relevant, language="python", sha="s" * 64, seed="g" * 64, stratum="EXACT_COPY"):
        return {"queryId": file_id("q" + sha), "language": language, "scoredGallery": scored,
                "relevantFileIds": relevant, "querySha256": sha, "seedGroup": seed, "stratum": stratum,
                "excludedUnknownGalleryFiles": []}

    def test_file_level_maximum_is_taken_before_the_top_k(self):
        a, b = file_id("A"), file_id("B")
        indexed = {"python": [{"fileId": a, "vector": unit(0)}, {"fileId": a, "vector": unit(1)},
                              {"fileId": b, "vector": unit(0)}]}
        query = self.query([a, b], [a])
        rankings = retrieval_eval.rank_query(query, [unit(0)], indexed, {}, "BLOCKED")
        # Each file scores its best chunk (1.0 for both), so the file identity breaks the tie.
        self.assertEqual(rankings["semantic"], sorted([a, b]))
        self.assertEqual(len(rankings["semantic"]), len(set(rankings["semantic"])))

    def test_ties_break_by_file_identity_ascending(self):
        low, high = file_id("low"), file_id("high")
        low, high = sorted([low, high])
        rows = [{"fileId": high, "vector": unit(0)}, {"fileId": low, "vector": unit(0)}]
        ranking = cpu_rank([unit(0)], rows, key="fileId", limit=2)
        self.assertEqual([item["fileId"] for item in ranking], [low, high])

    def test_token_and_semantic_branches_are_independent_in_the_union(self):
        semantic = [file_id("s%d" % index) for index in range(20)]
        token = [file_id("t%d" % index) for index in range(20)]
        union = baselines.union_candidates(token, semantic, budget=40)
        self.assertTrue(set(semantic) <= set(union))
        self.assertEqual(len(union), 40)
        only_low_token = baselines.union_candidates([], semantic, budget=40)
        self.assertEqual(only_low_token, semantic)

    def scored_gallery(self, count=41):
        return [file_id("gallery-%d" % index) for index in range(count)]

    def test_unknown_gallery_file_is_neither_ranked_nor_a_false_positive(self):
        scored = self.scored_gallery()
        unknown = file_id("unknown-file")
        # Every scored file is relevant, so any top-20 entry outside the gallery would be the only false positive.
        query = self.query(scored, scored)
        query["excludedUnknownGalleryFiles"] = [unknown]
        # The UNKNOWN file matches the query exactly; it must still stay out of every ranking and count.
        rows = [{"fileId": unknown, "vector": unit(0)}] + [{"fileId": file_id_value, "vector": unit(index)}
                                                           for index, file_id_value in enumerate(scored)]
        indexed = {"python": rows}
        rankings = retrieval_eval.rank_query(query, [unit(0)], indexed, {}, "BLOCKED")
        self.assertNotIn(unknown, rankings["semantic"])
        record = retrieval_eval.score_query(query, rankings, "BLOCKED")
        self.assertEqual(record["falsePositivesTop20"]["semantic"], 0)
        self.assertAlmostEqual(record["cells"]["semantic@20"]["recall"], 20 / len(scored))
        self.assertEqual(record["unknownExcludedCount"], 1)

    def test_rank_query_with_token_branch_stays_inside_the_scored_gallery(self):
        scored = self.scored_gallery()
        outside = file_id("outside-the-gallery")
        query = self.query(scored, [scored[0]])
        indexed = {"python": [{"fileId": outside, "vector": unit(0)}, {"fileId": scored[0], "vector": unit(0)}]
                   + [{"fileId": file_id_value, "vector": unit(index + 1)}
                      for index, file_id_value in enumerate(scored[1:])]}
        # A low token score for the best semantic file must not remove it from the union.
        pairs = [{"leftId": query["queryId"], "rightId": outside, "tokenScore": 0.99},
                 {"leftId": query["queryId"], "rightId": scored[0], "tokenScore": 0.01}]
        pairs += [{"leftId": query["queryId"], "rightId": file_id_value, "tokenScore": 0.9}
                  for file_id_value in scored[1:21]]
        rankings = retrieval_eval.rank_query(query, [unit(0)], indexed, {"python": pairs}, "SUCCEEDED")
        self.assertEqual(rankings["semantic"][0], scored[0])
        self.assertNotIn(outside, rankings["semantic"] + rankings["token"] + rankings["union"] + rankings["rrf"])
        # Equal token scores order by file identity, so the top 20 are compared as a set.
        self.assertEqual(set(rankings["token"][:20]), set(scored[1:21]))
        self.assertEqual(rankings["token"][20], scored[0])
        self.assertEqual(set(rankings["union"][:20]), set(scored[1:21]))
        self.assertIn(scored[0], rankings["union"])

    def test_score_query_refuses_a_gallery_not_larger_than_the_largest_cutoff(self):
        scored = [file_id("small-%d" % index) for index in range(retrieval_eval.CUTOFFS[-1])]
        with self.assertRaisesRegex(ValueError, "exceed the largest cutoff"):
            retrieval_eval.score_query(self.query(scored, [scored[0]]), {"semantic": scored}, "BLOCKED")

    def test_non_primary_cells_are_skipped_by_aggregation_and_paired_deltas(self):
        seed_a, seed_b = digest(b"seed-a"), digest(b"seed-b")

        def row(seed, status, recall):
            cell = {"recall": recall, "mrr": recall, "status": status}
            return {"seedGroup": seed, "querySha256": digest(seed.encode()), "language": "python",
                    "stratum": "EXACT_COPY", "cells": {"semantic@20": cell}}
        base = [row(seed_a, "PRIMARY", 1.0), row(seed_b, "DIAGNOSTIC_INSUFFICIENT_GALLERY", 0.0)]
        self.assertEqual([case["metrics"]["recall"] for case in retrieval_eval.cell_cases(base, "semantic@20")], [1.0])
        table = retrieval_eval.aggregate(base, ["semantic@20"])
        self.assertEqual(table["languages"]["semantic@20"]["python"]["uniqueQueryCases"], 1)
        model = [row(seed_a, "PRIMARY", 0.0), row(seed_b, "DIAGNOSTIC_INSUFFICIENT_CORPUS", 1.0)]
        result = retrieval_eval.paired_deltas({"E0": base, "E1": model, "E2": base, "E3": base},
                                              [("semantic@20", "recall")])
        self.assertEqual(result["E1"]["semantic@20.recall"]["seeds"], 1)
        self.assertAlmostEqual(result["E1"]["semantic@20.recall"]["meanDifference"], -1.0)

    def test_variants_are_deduplicated_by_query_sha_within_a_seed(self):
        seed_one, seed_two = digest(b"seed-one"), digest(b"seed-two")

        def record(seed, sha, recall):
            return {"seedGroup": seed, "querySha256": sha, "language": "python", "stratum": "EXACT_COPY",
                    "cells": {"semantic@20": {"recall": recall, "mrr": recall, "status": "PRIMARY"}}}
        records = [record(seed_one, "1" * 64, 1.0), record(seed_one, "1" * 64, 1.0),
                   record(seed_one, "2" * 64, 0.0), record(seed_two, "3" * 64, 1.0)]
        table = retrieval_eval.aggregate(records, ["semantic@20"])
        self.assertEqual(table["languages"]["semantic@20"]["python"]["uniqueQueryCases"], 3)
        self.assertAlmostEqual(table["overall"]["semantic@20"]["recall"], 0.75)

    def test_selection_prefers_mrr_then_recall_then_the_earlier_epoch(self):
        self.assertEqual(retrieval_eval.choose_epoch({1: {"mrr": 0.5, "recall": 0.5},
                                                      2: {"mrr": 0.5, "recall": 0.5},
                                                      3: {"mrr": 0.4, "recall": 0.9}}), 1)
        self.assertEqual(retrieval_eval.choose_epoch({1: {"mrr": 0.5, "recall": 0.4},
                                                      2: {"mrr": 0.5, "recall": 0.6}}), 2)

    def test_selection_record_is_chosen_by_rule_and_never_approved(self):
        scores = {"E1": {"mrr": 0.5, "recall": 0.4}, "E2": {"mrr": 0.6, "recall": 0.7},
                  "E3": {"mrr": 0.45, "recall": 0.5}}
        record = retrieval_eval.selection_record("run-fixture", scores, {"mrr": 0.55, "recall": 0.5},
                                                 {"E1": "1" * 64, "E2": "2" * 64, "E3": "3" * 64}, "E2")
        self.assertEqual(record["status"], "CHOSEN_BY_RULE")
        self.assertIs(record["approvedForUse"], False)
        self.assertNotIn("selectedForUse", record)
        self.assertEqual(record["scope"], retrieval_eval.SELECTION_SCOPE)
        self.assertEqual(record["comparedAgainstFrozen"]["model"], "E2")
        self.assertAlmostEqual(record["comparedAgainstFrozen"]["mrrDifference"], 0.05)
        self.assertAlmostEqual(record["comparedAgainstFrozen"]["recallDifference"], 0.2)

    def test_paired_deltas_are_reported_per_language_and_overall(self):
        def row(language, seed, recall):
            return {"seedGroup": seed, "querySha256": digest((seed + language).encode()), "language": language,
                    "cells": {"semantic@20": {"recall": recall, "mrr": recall, "status": "PRIMARY"}}}
        seed_a, seed_b, seed_c = digest(b"a"), digest(b"b"), digest(b"c")
        base = [row("python", seed_a, 0.5), row("python", seed_b, 0.0), row("python", seed_c, 1.0),
                row("java", seed_a, 1.0), row("java", seed_b, 0.25)]
        model = [row("python", seed_a, 0.5), row("python", seed_b, 1.0), row("python", seed_c, 0.5),
                 row("java", seed_a, 1.0), row("java", seed_b, 0.25)]
        records = {"E0": base, "E1": model, "E2": base, "E3": base}
        result = retrieval_eval.paired_deltas(records, [("semantic@20", "recall")])
        overall = result["E1"]["semantic@20.recall"]
        self.assertEqual((overall["seeds"], overall["improved"], overall["worse"], overall["equal"]), (5, 1, 1, 3))
        self.assertAlmostEqual(overall["meanDifference"], 0.1)
        python = overall["perLanguage"]["python"]
        self.assertEqual((python["seeds"], python["improved"], python["worse"], python["equal"]), (3, 1, 1, 1))
        self.assertAlmostEqual(python["meanDifference"], 1.0 / 6.0)
        java = overall["perLanguage"]["java"]
        self.assertEqual((java["seeds"], java["improved"], java["worse"], java["equal"]), (2, 0, 0, 2))
        self.assertEqual(java["meanDifference"], 0.0)
        empty = overall["perLanguage"]["javascript"]
        self.assertEqual((empty["seeds"], empty["meanDifference"]), (0, None))

    def test_saturation_counts_rank_one_and_full_recall_per_language(self):
        def row(language, rank, recall):
            return {"language": language, "semanticFirstRelevantRank": rank,
                    "cells": {"semantic@20": {"recall": recall, "mrr": 1.0}}}
        records = {"E1": [row("python", 1, 1.0), row("python", 2, 1.0), row("python", None, 0.5),
                          row("javascript", 1, 1.0)]}
        result = retrieval_eval.saturation(records)["E1"]
        self.assertEqual(result["perLanguage"]["python"]["firstRelevantAtRankOne"], 1)
        self.assertAlmostEqual(result["perLanguage"]["python"]["firstRelevantAtRankOneShare"], 1.0 / 3.0)
        self.assertEqual(result["perLanguage"]["python"]["recallAt20One"], 2)
        self.assertEqual(result["overall"]["queries"], 4)
        self.assertEqual(result["overall"]["firstRelevantAtRankOne"], 2)
        self.assertAlmostEqual(result["overall"]["firstRelevantAtRankOneShare"], 0.5)
        self.assertEqual(result["overall"]["recallAt20One"], 3)
        self.assertEqual(result["perLanguage"]["java"]["queries"], 0)
        self.assertIsNone(result["perLanguage"]["java"]["recallAt20OneShare"])

    def test_selection_treats_differences_within_tolerance_as_ties(self):
        tiny = retrieval_eval.TIE_TOLERANCE / 10
        self.assertEqual(retrieval_eval.choose_epoch({1: {"mrr": 0.5, "recall": 0.5},
                                                      2: {"mrr": 0.5 + tiny, "recall": 0.5 - tiny}}), 1)
        self.assertEqual(retrieval_eval.choose_epoch({1: {"mrr": 0.5, "recall": 0.5},
                                                      2: {"mrr": 0.5 - tiny, "recall": 0.5 + tiny}}), 1)
        self.assertEqual(retrieval_eval.choose_epoch({1: {"mrr": 0.5, "recall": 0.5},
                                                      2: {"mrr": 0.5 + tiny, "recall": 0.6}}), 2)
        self.assertEqual(retrieval_eval.choose_epoch({1: {"mrr": 0.5, "recall": 0.5},
                                                      2: {"mrr": 0.5 + 1e-6, "recall": 0.4}}), 2)

    def test_index_drift_is_refused_for_corpus_gallery_chunks_and_row_count(self):
        manifest = self.write_index("drift-base")
        key = manifest["modelKey"]
        with self.assertRaisesRegex(ValueError, "corpus identity differs"):
            self.load("drift-base", "E0", key, corpus={**self.corpus, "protocolSha256": "0" * 64})
        gallery_plan = {**self.plan, "galleries": {**self.galleries, "python": self.galleries["python"][:1]}}
        with mock.patch.object(index_verify, "run_dir", lambda run_id: self.root / "runs" / run_id), \
                self.assertRaisesRegex(ValueError, "differs from the admitted gallery"):
            retrieval_eval.load_index("drift-base", "E0", gallery_plan, self.corpus, key,
                                      self.export_for("drift-base"))

        self.write_index("drift-chunks")
        chunks = self.root / "runs" / "drift-chunks" / "index" / "E0" / "python.chunks.json"
        chunks.write_bytes(chunks.read_bytes() + b" ")
        with self.assertRaisesRegex(ValueError, "chunk manifest drift"):
            self.load("drift-chunks", "E0", key)

        self.write_index("drift-rows")
        manifest_path = self.root / "runs" / "drift-rows" / "index" / "E0" / "index-manifest.json"
        tampered = json.loads(manifest_path.read_text(encoding="utf-8"))
        tampered["languages"]["python"]["rows"] += 1
        manifest_path.write_bytes(json.dumps(tampered, sort_keys=True).encode("utf-8"))
        with self.assertRaisesRegex(ValueError, "row count drift"):
            self.load("drift-rows", "E0", key)

    def test_index_changed_with_its_vectors_but_not_matching_the_export_evidence_is_refused(self):
        original = self.write_index("export-original")
        evidence = self.export_for("export-original")
        shifted = {language: (chunks, file_rows, [unit(index + 1) for index in range(len(vectors))], seconds)
                   for language, (chunks, file_rows, vectors, seconds) in self.encoded().items()}
        changed = self.write_index("export-changed", encoded=shifted)
        with self.assertRaisesRegex(ValueError, "differs from the export evidence"):
            self.load("export-changed", "E0", changed["modelKey"], export_evidence=evidence)
        self.load("export-original", "E0", original["modelKey"], export_evidence=evidence)

    def test_export_evidence_must_describe_this_index_training_run_and_corpus(self):
        def evidence(**changes):
            base = {"schema": index_export.EXPORT_SCHEMA, "runId": "index-run", "outcome": "SUCCEEDED",
                    "identity": {**self.corpus, "trainRunId": "train-run", "trainingEvidenceSha256": "9" * 64}}
            base.update(changes)
            return base
        expectations = {"evidenceSha256": "9" * 64}
        retrieval_eval.require_export_evidence(evidence(), index_run_id="index-run", train_run_id="train-run",
                                               expectations=expectations, corpus=self.corpus)
        refusals = [
            ("index run id", evidence(runId="other"), "index-run", "train-run", expectations, self.corpus),
            ("export outcome", evidence(outcome="FAILED_ERROR"), "index-run", "train-run", expectations, self.corpus),
            ("train run id", evidence(), "index-run", "other-train", expectations, self.corpus),
            ("training evidence digest", evidence(), "index-run", "train-run", {"evidenceSha256": "8" * 64},
             self.corpus),
            ("corpus identity", evidence(), "index-run", "train-run", expectations,
             {**self.corpus, "configSha256": "0" * 64}),
        ]
        for name, export, index_run_id, train_run_id, expected, corpus in refusals:
            with self.subTest(name=name), self.assertRaises(ValueError):
                retrieval_eval.require_export_evidence(export, index_run_id=index_run_id, train_run_id=train_run_id,
                                                       expectations=expected, corpus=corpus)

    def test_validation_plan_refuses_held_out_wrong_language_and_receipt_mismatches(self):
        relevant = file_id("a")
        seed = {"queryFragmentId": "fragment-1", "querySha256": "1" * 64, "relevantFileIds": [relevant]}
        receipt = {"queryFragmentId": "fragment-1", "querySha256": "1" * 64,
                   "scoredGallery": [file_id("b"), relevant], "excludedUnknownGalleryFiles": []}
        self.assertEqual(validation_plan.require_receipt_for_seed(seed, receipt), sorted([file_id("b"), relevant]))
        with self.assertRaisesRegex(ValueError, "does not follow"):
            validation_plan.require_receipt_for_seed(seed, {**receipt, "querySha256": "2" * 64})
        with self.assertRaisesRegex(ValueError, "outside the scored gallery"):
            validation_plan.require_receipt_for_seed(seed, {**receipt, "scoredGallery": [file_id("b")]})

        held_families, held_normalized = {"family-held"}, {("python", "n" * 64)}

        def row(**changes):
            base = {"fileId": file_id("gallery"), "partition": "validation", "language": "python",
                    "familyId": "family-open", "normalizedSha256": "m" * 64}
            base.update(changes)
            return base
        validation_plan.require_gallery_file(row(), "python", held_families, held_normalized)
        gallery_refusals = [
            ("held-out family", row(familyId="family-held"), "held-out family or content"),
            ("held-out content", row(normalizedSha256="n" * 64), "held-out family or content"),
            ("train partition", row(partition="train"), "escaped the validation partition"),
            ("final-test partition", row(partition="final-test"), "escaped the validation partition"),
            ("wrong language file", row(language="java"), "escaped the validation partition"),
        ]
        for name, candidate, message in gallery_refusals:
            with self.subTest(name=name), self.assertRaisesRegex(ValueError, message):
                validation_plan.require_gallery_file(candidate, "python", held_families, held_normalized)
        with self.assertRaisesRegex(ValueError, "escaped the validation partition"):
            validation_plan.require_validation_row(row(), "java")

    def test_failed_evaluation_writes_failed_evidence_and_no_success_marker(self):
        target = self.root / "runs" / "eval-failed"
        target.mkdir(parents=True)
        loaded = {"manifestSha256": "a" * 64, "sourcesRoot": self.root}
        with mock.patch.object(retrieval_eval, "RETRIEVAL_ROOT", self.root), \
                mock.patch.object(retrieval_eval, "token_branch", side_effect=RuntimeError("dolos unavailable")):
            result = retrieval_eval._evaluate(
                run_id="eval-failed", target=target, loaded=loaded, plan=self.plan, prepare_binding={"runId": "prep"},
                expectations={"evidenceSha256": "d" * 64, "epochs": {}}, train_run_id="train",
                index_run_id="index", reference_raw=b"{}", reference_probe={}, live_probe={}, comparison={},
                export_evidence={"outcome": "SUCCEEDED"}, corpus=self.corpus, device=torch.device("cpu"))
        self.assertEqual(result["outcome"], "FAILED_ERROR")
        evidence = json.loads((target / retrieval_eval.EVALUATION_EVIDENCE).read_text(encoding="utf-8"))
        self.assertEqual(evidence["outcome"], "FAILED_ERROR")
        self.assertIn("dolos unavailable", evidence["reason"])
        self.assertNotEqual(evidence["outcome"], "SUCCEEDED")
        self.assertFalse((target / retrieval_eval.SELECTION_FILE).exists())

    # -- the shared encode path and failure evidence --------------------------
    def test_shared_encode_path_equals_encode_chunks_on_the_frozen_model(self):
        support = self.root / "upstream"
        upstream = tiny.write_upstream(support)
        tokenizer = RobertaTokenizerFast.from_pretrained(str(support), local_files_only=True)
        model = tiny.build_model(support, seed=0)
        for param in model.parameters():
            param.requires_grad_(False)
        raw = b"def f(value):\n    return value + 1\n" * 3
        chunks = mapped_chunks(raw, {"fragmentId": "f", "startByte": 0, "endByte": len(raw),
                                     "sha256": digest(raw)}, tokenizer)["chunks"]
        entry = {"model": model, "tokenizer": tokenizer}
        ours = retrieval_models.encode(model, tokenizer, chunks[:4], torch.device("cpu"))
        theirs, _ = encode_chunks(model, tokenizer, [{"inputIds": c["inputIds"], "framingTokens": 4}
                                                     for c in chunks[:4]], batch_size=1)
        difference = max(abs(a - b) for left, right in zip(ours, theirs) for a, b in zip(left, right))
        self.assertLessEqual(difference, 1e-6)
        self.assertEqual(index_export.like_for_like(entry, chunks[:4], torch.device("cpu"))["result"], "MATCH")
        self.assertEqual(upstream["modelRevision"], tiny.MODEL_REVISION)

    def test_failed_export_writes_failed_evidence_and_no_success_marker(self):
        target = self.root / "runs" / "export-failed"
        target.mkdir(parents=True)
        loaded = {"manifestSha256": "a" * 64, "sourcesRoot": self.root}
        with mock.patch.object(index_export, "RETRIEVAL_ROOT", self.root), \
                mock.patch.object(index_export.retrieval_models, "frozen_model", side_effect=RuntimeError("no encoder")):
            result = index_export._export(
                run_id="export-failed", target=target, loaded=loaded, config_loaded={"configSha256": "b" * 64},
                plan=self.plan, prepare_binding={"runId": "prep"},
                expectations={"evidenceSha256": "d" * 64, "epochs": {}}, train_run_id="train",
                reference_raw=b"{}", reference_probe={}, live_probe={}, comparison={}, device=torch.device("cpu"))
        self.assertEqual(result["outcome"], "FAILED_ERROR")
        evidence = json.loads((target / index_export.EXPORT_EVIDENCE).read_text(encoding="utf-8"))
        self.assertEqual(evidence["outcome"], "FAILED_ERROR")
        self.assertIn("no encoder", evidence["reason"])
        self.assertNotIn("SUCCEEDED", json.dumps(evidence["models"]))


if __name__ == "__main__":
    unittest.main()
