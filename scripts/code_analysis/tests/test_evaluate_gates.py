import contextlib
import io
import json
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

from scripts.code_analysis.baselines import exact_copy_query_id
from scripts.code_analysis.cli import main
from scripts.code_analysis.contracts import canonical, digest
from scripts.code_analysis.corpus import ARTIFACT_ROOT
from scripts.code_analysis.evaluate_run import (
    combine_partition_evaluations, evaluate_fragment_diagnostics, evaluate_query_strata, evaluate_root,
)
from scripts.code_analysis.fragment_labels import pair, review as reviewed_pair


def review(pairs, rationale):
    return reviewed_pair(pairs, rationale, reviewer="Authored fixture reviewer", reviewed_on="2026-10-05")
from scripts.code_analysis.representations import vector_bytes
from scripts.code_analysis.reuse import check_live_index, live_index_evidence, publication_provenance_ref, reuse_frozen, verify_vector_artifacts
import test_intake as fixtures


def unit_vector(scale=1.0, axis=0):
    row = [0.0] * 768
    row[axis] = float(scale)
    norm = (sum(x * x for x in row)) ** 0.5
    return [x / norm for x in row]


class EvaluateCliTests(unittest.TestCase):
    def setUp(self):
        ARTIFACT_ROOT.mkdir(parents=True, exist_ok=True)
        self.temp = tempfile.TemporaryDirectory(dir=ARTIFACT_ROOT)
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.config_path = self.root / "sources.json"
        data = fixtures.config()
        source = data["sources"][0]
        source.update(partition="train", partitionReviewed=True)
        source["rights"] = {"status": "REVIEWED", "reviewer": "Authored unit-test reviewer", "reviewedOn": "2026-10-05",
                            "rationale": "Authored fixture permission", "terms": "MIT", "commit": source["commit"],
                            "fileHashes": {f["path"]: f["sha256"] for f in source["files"]},
                            "noticeHashes": {n["path"]: n["sha256"] for n in source["notices"]},
                            "grants": {"searchIndexAllowed": True, "trainingAllowed": True, "evaluationAllowed": True}}
        self.config_path.write_bytes(canonical(data))

    def invoke(self, stage, run="fixture-run"):
        with contextlib.redirect_stdout(io.StringIO()), patch("scripts.code_analysis.cli.ARTIFACT_ROOT", self.root):
            from scripts.code_analysis.corpus import intake
            with patch("scripts.code_analysis.cli.intake", side_effect=lambda c, r, offline=False:
                       intake(c, r, offline=offline, fetch=fixtures.IntakeTests.fetch)):
                return main([stage, "--run-id", run, "--config", str(self.config_path)])

    def test_evaluate_requires_vectors_and_token(self):
        for name in ("intake", "split", "extract"):
            self.assertEqual(self.invoke(name), 0)
        self.assertEqual(self.invoke("evaluate"), 1)

    def test_evaluate_writes_known_metrics(self):
        for name in ("intake", "split", "extract"):
            self.assertEqual(self.invoke(name), 0)
        run = self.root / "runs/fixture-run"
        extracted = json.loads((run / "extract.json").read_bytes())
        file_row = extracted["files"][0]
        fragment = extracted["fragments"][0]
        labels = {"seeds": [{"seedFileId": file_row["fileId"], "relevantFileIds": [file_row["fileId"]],
                             "distractorFileIds": []}]}
        (run / "materialize-labels.json").write_bytes(canonical(labels))
        query_id = exact_copy_query_id(file_row["fileId"])
        (run / "token-baseline.json").write_bytes(canonical({
            "status": "SUCCEEDED", "k": 23, "window": 17,
            "boundPairs": [{"leftId": query_id, "rightId": file_row["fileId"], "tokenScore": 0.9,
                            "leftSha256": file_row["sha256"], "rightSha256": file_row["sha256"]}],
        }))
        vec = unit_vector()
        raw = vector_bytes([vec])
        (run / "representations").mkdir()
        (run / "representations/frozen-vectors.fp32le").write_bytes(raw)
        chunk = {"fileId": file_row["fileId"], "fragmentId": fragment["fragmentId"], "chunkOrdinal": 0,
                 "partition": "train", "language": "python"}
        (run / "representations/chunks.jsonl").write_bytes(canonical(chunk)[:-1] + b"\n")
        (run / "representations/manifest.json").write_bytes(canonical({"chunks": 1, "chunkRowsSha256": digest((run / "representations/chunks.jsonl").read_bytes())}))
        import scripts.code_analysis.evaluate_run as evaluate_mod
        original = evaluate_mod.evaluate_root

        def dual(root, **kwargs):
            payload = original(root, **{**kwargs, "partition": "train"})
            return {**payload, "partition": kwargs.get("partition", "validation")}

        with patch("scripts.code_analysis.evaluate_run.evaluate_root", dual):
            self.assertEqual(self.invoke("evaluate"), 0)
        payload = json.loads((run / "evaluate.json").read_bytes())
        self.assertEqual(payload["languages"][0]["recall"], 1.0)
        self.assertIn("recall5", payload["languages"][0])
        self.assertIn("tokenRecall5", payload["union"][0])
        self.assertTrue(payload["identityBaselineOnly"])
        self.assertIn("train", payload["partitions"])
        self.assertIn("validation", payload["partitions"])
        first = payload["languages"][0]["recall"]
        (run / "evaluate.json").unlink()
        (run / "evaluate-stage.json").unlink()
        with patch("scripts.code_analysis.evaluate_run.evaluate_root", dual):
            self.assertEqual(self.invoke("evaluate"), 0)
        again = json.loads((run / "evaluate.json").read_bytes())
        self.assertEqual(again["languages"][0]["recall"], first)

    def test_evaluate_keeps_validation_failure(self):
        for name in ("intake", "split", "extract"):
            self.assertEqual(self.invoke(name), 0)
        run = self.root / "runs/fixture-run"
        extracted = json.loads((run / "extract.json").read_bytes())
        file_row = extracted["files"][0]
        fragment = extracted["fragments"][0]
        (run / "materialize-labels.json").write_bytes(canonical({
            "seeds": [{"seedFileId": file_row["fileId"], "relevantFileIds": [file_row["fileId"]],
                       "distractorFileIds": []}]}))
        query_id = exact_copy_query_id(file_row["fileId"])
        (run / "token-baseline.json").write_bytes(canonical({
            "status": "SUCCEEDED", "k": 23, "window": 17,
            "boundPairs": [{"leftId": query_id, "rightId": file_row["fileId"], "tokenScore": 0.9,
                            "leftSha256": file_row["sha256"], "rightSha256": file_row["sha256"]}],
        }))
        (run / "representations").mkdir()
        vec = unit_vector()
        (run / "representations/frozen-vectors.fp32le").write_bytes(vector_bytes([vec]))
        chunk = {"fileId": file_row["fileId"], "fragmentId": fragment["fragmentId"], "chunkOrdinal": 0,
                 "partition": "train", "language": "python"}
        (run / "representations/chunks.jsonl").write_bytes(canonical(chunk)[:-1] + b"\n")
        (run / "representations/manifest.json").write_bytes(canonical({"chunks": 1, "chunkRowsSha256": digest((run / "representations/chunks.jsonl").read_bytes())}))
        import scripts.code_analysis.evaluate_run as evaluate_mod
        original = evaluate_mod.evaluate_root

        def flaky(root, **kwargs):
            if kwargs.get("partition") == "validation":
                raise ValueError("missing validation gallery")
            return original(root, **kwargs)

        with patch("scripts.code_analysis.evaluate_run.evaluate_root", flaky):
            self.assertEqual(self.invoke("evaluate"), 1)
        record = json.loads((run / "evaluate-stage.json").read_bytes())
        self.assertEqual(record["ActualOutcome"], "FAILED")
        self.assertTrue(any("validation" in item for item in record.get("gaps") or []) or
                        "validation" in json.dumps(record))


class FragmentRankTests(unittest.TestCase):
    def test_other_fragment_on_same_file_is_not_credit(self):
        query = "a" * 64
        other = "b" * 64
        file_id = "c" * 64
        vec_q, vec_o = unit_vector(axis=0), unit_vector(axis=1)
        rows = [
            {"fileId": file_id, "fragmentId": query, "chunkOrdinal": 0, "partition": "validation", "language": "java", "vector": vec_q},
            {"fileId": file_id, "fragmentId": other, "chunkOrdinal": 0, "partition": "validation", "language": "java", "vector": vec_o},
        ]
        extract = {"files": [{"fileId": file_id, "partition": "validation", "language": "java", "duplicateGroup": "d" * 64}],
                   "fragments": [{"fragmentId": query, "fileId": file_id, "sha256": query, "name": "parseBigDecimal"},
                                 {"fragmentId": other, "fileId": file_id, "sha256": other, "name": "intValue"}]}
        result = evaluate_fragment_diagnostics(extract, {"java": [query, other]}, rows, corpus_minima_met=True)
        cases = result["languages"]["java"]["cases"]
        self.assertEqual(cases[0]["galleryCount"], 1)
        self.assertEqual(cases[0]["recall5"], 1.0)

    def test_reviewed_similar_both_relevant_and_unknown_any_language(self):
        left, right, extra = "a" * 64, "b" * 64, "c" * 64
        file_id = "d" * 64
        rows = [
            {"fileId": file_id, "fragmentId": left, "chunkOrdinal": 0, "partition": "validation", "language": "typescript", "vector": unit_vector(1)},
            {"fileId": file_id, "fragmentId": right, "chunkOrdinal": 0, "partition": "validation", "language": "typescript", "vector": unit_vector(1)},
            {"fileId": file_id, "fragmentId": extra, "chunkOrdinal": 0, "partition": "validation", "language": "typescript", "vector": unit_vector(3)},
        ]
        extract = {"files": [{"fileId": file_id, "partition": "validation", "language": "typescript", "duplicateGroup": "e" * 64}],
                   "fragments": [{"fragmentId": left, "fileId": file_id, "sha256": left, "name": "one"},
                                 {"fragmentId": right, "fileId": file_id, "sha256": right, "name": "two"},
                                 {"fragmentId": extra, "fileId": file_id, "sha256": extra, "name": "three"}]}
        similar = review([pair(extract["fragments"][0], extract["fragments"][1], "SIMILAR")], "same switch table")
        unknown = review([pair(extract["fragments"][0], extract["fragments"][2], "UNKNOWN")], "undecided")
        result = evaluate_fragment_diagnostics(extract, {"typescript": [left, right, extra]}, rows,
                                               corpus_minima_met=True, fragment_reviews=[similar, unknown])
        case = result["languages"]["typescript"]["cases"][0]
        self.assertEqual(case["relevantCount"], 2)
        self.assertEqual(case["galleryCount"], 2)
        unlabeled = result["languages"]["typescript"]["cases"][2]
        self.assertEqual(unlabeled["galleryCount"], 1)
        self.assertEqual(unlabeled["status"], "PENDING_INCOMPLETE_RELATIONS")

    def test_query_strata_are_not_closed_by_identity_flag(self):
        file_id = "c" * 64
        extract = {"files": [{"fileId": file_id, "partition": "train", "language": "python",
                              "searchableExecutable": True, "generated": False, "duplicateGroup": "d" * 64}]}
        gallery = [{"fileId": file_id, "fragmentId": "e" * 64, "chunkOrdinal": 0, "partition": "train",
                    "language": "python", "vector": unit_vector()}]
        empty = evaluate_query_strata(extract, gallery, [], partition="train", corpus_minima_met=False)
        self.assertTrue(empty["identityBaselineDoesNotCloseStrata"])
        self.assertTrue(empty["identityBaselineOnly"])
        self.assertFalse(empty["queryEmbeddingsExecuted"])
        self.assertEqual(empty["strata"]["COMMENT_FORMAT"]["status"], "OMITTED_AWAITING_ACCEPTANCE")
        self.assertEqual(empty["strata"]["AST_BOUND_RENAME"]["queryVectors"], 0)
        self.assertNotEqual(empty["transformationStrataStatus"], "SCORED")
        queries = [{"stratum": "COMMENT_FORMAT", "partition": "train", "language": "python",
                    "vectors": [unit_vector()], "relevantFileIds": [file_id], "parentFileId": file_id,
                    "querySha256": "f" * 64, "queryId": "q" * 64}]
        scored = evaluate_query_strata(extract, gallery, queries, partition="train", corpus_minima_met=False)
        self.assertEqual(scored["strata"]["COMMENT_FORMAT"]["status"], "SCORED")
        self.assertEqual(scored["strata"]["COMMENT_FORMAT"]["queryCount"], 1)
        self.assertEqual(scored["strata"]["AST_BOUND_RENAME"]["status"], "OMITTED_AWAITING_ACCEPTANCE")
        self.assertEqual(scored["transformationStrataStatus"], "PARTIAL_OR_OMITTED")
        self.assertTrue(scored["identityBaselineOnly"])
        complete = [
            {"stratum": "EXACT_COPY", "partition": "train", "language": "python",
             "vectors": [unit_vector()], "relevantFileIds": [file_id], "parentFileId": file_id,
             "querySha256": "1" * 64, "queryId": "2" * 64},
            {"stratum": "COMMENT_FORMAT", "partition": "train", "language": "python",
             "vectors": [unit_vector()], "relevantFileIds": [file_id], "parentFileId": file_id,
             "querySha256": "3" * 64, "queryId": "4" * 64},
            {"stratum": "AST_BOUND_RENAME", "partition": "train", "language": "python",
             "vectors": [unit_vector()], "relevantFileIds": [file_id], "parentFileId": file_id,
             "querySha256": "5" * 64, "queryId": "6" * 64},
        ]
        full = evaluate_query_strata(extract, gallery, complete, partition="train", corpus_minima_met=False)
        self.assertFalse(full["identityBaselineOnly"])
        self.assertEqual(full["transformationStrataStatus"], "SCORED")
        self.assertEqual(full["scoredTransformationStrata"], ["EXACT_COPY", "COMMENT_FORMAT", "AST_BOUND_RENAME"])
        combined = combine_partition_evaluations({"train": full, "validation": empty})
        self.assertTrue(combined["identityBaselineOnly"])
        self.assertEqual(combined["transformationStrataStatus"], "PARTIAL_OR_OMITTED")
        self.assertTrue(any(row["partition"] == "validation" for row in combined["stratumOmissions"]))
        dual = combine_partition_evaluations({"train": full, "validation": full})
        self.assertFalse(dual["identityBaselineOnly"])
        self.assertEqual(dual["transformationStrataStatus"], "SCORED")
        self.assertFalse(dual["allLanguagePartitionStrataComplete"])
        self.assertTrue(any(row.get("status") == "LANGUAGE_COVERAGE_INCOMPLETE" and row["partition"] == "train"
                            and row["stratum"] == "AST_BOUND_RENAME" for row in dual["stratumOmissions"]))

    def test_query_strata_exclude_final_test_and_other_partition_rows(self):
        train_id, other_id, final_id = "c" * 64, "d" * 64, "e" * 64
        extract = {"files": [
            {"fileId": train_id, "partition": "train", "language": "python",
             "searchableExecutable": True, "generated": False, "duplicateGroup": "f" * 64},
            {"fileId": other_id, "partition": "validation", "language": "python",
             "searchableExecutable": True, "generated": False, "duplicateGroup": "1" * 64},
            {"fileId": final_id, "partition": "final-test", "language": "python",
             "searchableExecutable": True, "generated": False, "duplicateGroup": "2" * 64},
        ]}
        gallery = [
            {"fileId": train_id, "fragmentId": "a" * 64, "chunkOrdinal": 0, "partition": "train",
             "language": "python", "vector": unit_vector(axis=1)},
            {"fileId": other_id, "fragmentId": "b" * 64, "chunkOrdinal": 0, "partition": "validation",
             "language": "python", "vector": unit_vector(axis=0)},
            {"fileId": final_id, "fragmentId": "3" * 64, "chunkOrdinal": 0, "partition": "final-test",
             "language": "python", "vector": unit_vector(axis=0)},
        ]
        queries = [{"stratum": "COMMENT_FORMAT", "partition": "train", "language": "python",
                    "vectors": [unit_vector(axis=0)], "relevantFileIds": [train_id], "parentFileId": train_id,
                    "querySha256": "7" * 64, "queryId": "8" * 64}]
        scored = evaluate_query_strata(extract, gallery, queries, partition="train", corpus_minima_met=False)
        self.assertEqual(scored["strata"]["COMMENT_FORMAT"]["status"], "SCORED")
        self.assertEqual(scored["finalTestScored"], False)
        self.assertEqual(scored["strata"]["COMMENT_FORMAT"]["languages"][0]["recall"], 1.0)
        leaked = [{"stratum": "COMMENT_FORMAT", "partition": "final-test", "language": "python",
                   "vectors": [unit_vector()], "relevantFileIds": [final_id], "parentFileId": final_id,
                   "querySha256": "9" * 64, "queryId": "0" * 64}]
        with self.assertRaisesRegex(ValueError, "final-test scoring prohibited"):
            evaluate_query_strata(extract, gallery, leaked, partition="final-test", corpus_minima_met=False)

    def test_hash_drift_and_contradictory_labels_block(self):
        left, right = "a" * 64, "b" * 64
        file_id = "c" * 64
        rows = [
            {"fileId": file_id, "fragmentId": left, "chunkOrdinal": 0, "partition": "validation", "language": "csharp", "vector": unit_vector(1)},
            {"fileId": file_id, "fragmentId": right, "chunkOrdinal": 0, "partition": "validation", "language": "csharp", "vector": unit_vector(2)},
        ]
        extract = {"files": [{"fileId": file_id, "partition": "validation", "language": "csharp", "duplicateGroup": "d" * 64}],
                   "fragments": [{"fragmentId": left, "fileId": file_id, "sha256": left},
                                 {"fragmentId": right, "fileId": file_id, "sha256": right}]}
        stale = review([{"leftFragmentId": left, "leftSha256": "f" * 64, "rightFragmentId": right,
                         "rightSha256": right, "label": "SIMILAR"}], "stale")
        with self.assertRaisesRegex(ValueError, "hash drift"):
            evaluate_fragment_diagnostics(extract, {"csharp": [left, right]}, rows, corpus_minima_met=True,
                                          fragment_reviews=[stale])
        conflict = [review([pair(extract["fragments"][0], extract["fragments"][1], "SIMILAR")], "a"),
                    review([pair(extract["fragments"][0], extract["fragments"][1], "DISSIMILAR")], "b")]
        with self.assertRaisesRegex(ValueError, "contradictory"):
            evaluate_fragment_diagnostics(extract, {"csharp": [left, right]}, rows, corpus_minima_met=True,
                                          fragment_reviews=conflict)


class ReuseArtifactTests(unittest.TestCase):
    def _write_repr(self, parent, raw, chunks, mapping=b"\x00\x00\x00\x00\x01\x00\x00\x00"):
        (parent / "representations").mkdir(exist_ok=True)
        (parent / "representations/frozen-vectors.fp32le").write_bytes(raw)
        (parent / "representations/chunks.jsonl").write_bytes(chunks)
        (parent / "representations/source-mapping.u32le").write_bytes(mapping)
        manifest = {"chunks": 1, "chunkRowsSha256": digest(chunks), "sourceMappingSha256": digest(mapping)}
        (parent / "representations/manifest.json").write_bytes(canonical(manifest))
        return {"fullVectorSha256": digest(raw), "representationManifestSha256": digest(canonical(manifest)),
                "modelKey": "a" * 64}

    def test_corrupt_vector_blob_fails_verification(self):
        ARTIFACT_ROOT.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(dir=ARTIFACT_ROOT) as tmp:
            parent = Path(tmp)
            raw = vector_bytes([unit_vector()])
            chunks = canonical({"fileId": "a" * 64})
            frozen = self._write_repr(parent, raw, chunks)
            self.assertEqual(verify_vector_artifacts(parent, frozen)["status"], "VERIFIED")
            (parent / "representations/frozen-vectors.fp32le").write_bytes(raw[:-4] + b"xxxx")
            self.assertEqual(verify_vector_artifacts(parent, frozen)["status"], "FAILED")

    def test_missing_or_changed_mapping_fails(self):
        ARTIFACT_ROOT.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(dir=ARTIFACT_ROOT) as tmp:
            parent = Path(tmp)
            raw = vector_bytes([unit_vector()])
            chunks = canonical({"fileId": "a" * 64})
            frozen = self._write_repr(parent, raw, chunks)
            (parent / "representations/source-mapping.u32le").unlink()
            self.assertEqual(verify_vector_artifacts(parent, frozen)["status"], "FAILED")
            mapping = b"\x02\x00\x00\x00\x03\x00\x00\x00"
            frozen = self._write_repr(parent, raw, chunks, mapping=mapping)
            (parent / "representations/source-mapping.u32le").write_bytes(b"\xff" * 8)
            self.assertEqual(verify_vector_artifacts(parent, frozen)["status"], "FAILED")

    def test_missing_db_is_unverified(self):
        result = check_live_index({"partitions": [{"partition": "train", "indexKey": "a" * 64, "rowOrdinals": [0]}]},
                                  connect=lambda: (_ for _ in ()).throw(OSError("down")))
        self.assertEqual(result["status"], "UNVERIFIED")
        self.assertNotEqual(result["status"], "REUSED")
        dumped = json.dumps(result)
        self.assertNotIn("ASSETBLOCK_CODE_INDEX_DSN", dumped)
        self.assertNotIn("postgres://", dumped)

    def test_producer_index_evidence_status_is_not_reused(self):
        live = {"status": "READY_VERIFIED", "partitions": {
            "train": {"indexKey": "a" * 64, "modelKey": "b" * 64, "state": "READY"}}}
        frozen = {"modelKey": "b" * 64, "partitions": [{"partition": "train", "indexKey": "a" * 64}]}
        reused = {"status": "REUSED", "parentRunId": "parent"}
        payload = live_index_evidence(live, frozen=frozen, reused=reused)
        self.assertEqual(payload["status"], "READY_VERIFIED")
        self.assertEqual(payload["reuseStatus"], "REUSED")
        dumped = json.dumps(payload)
        self.assertNotIn("postgres://", dumped)
        self.assertNotIn("ASSETBLOCK_CODE_INDEX_DSN", dumped)

    def test_embedding_or_span_drift_fails_live_check(self):
        ARTIFACT_ROOT.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(dir=ARTIFACT_ROOT) as tmp:
            parent = Path(tmp)
            vec = unit_vector()
            raw = vector_bytes([vec])
            fragment_id, file_id, snap = "e" * 64, "c" * 64, "a" * 64
            chunk = {"fileId": file_id, "fragmentId": fragment_id, "chunkOrdinal": 0, "tokenStart": 0, "tokenEnd": 4,
                     "sourceStart": 10, "sourceEnd": 30, "representationSha256": "1" * 64, "partition": "train",
                     "language": "python"}
            chunks = canonical(chunk)[:-1] + b"\n"
            frozen = self._write_repr(parent, raw, chunks)
            frozen["partitions"] = [{"partition": "train", "indexKey": "b" * 64, "rowOrdinals": [0],
                                     "gallerySha256": "g" * 64}]
            extract = {
                "extractionSha256": "2" * 64,
                "fragments": [{"fragmentId": fragment_id, "fileId": file_id, "sourceSha256": "d" * 64,
                               "sha256": "f" * 64, "startByte": 10, "endByte": 30, "language": "python",
                               "dialect": "python"}],
                "files": [{"fileId": file_id, "sha256": "d" * 64, "partition": "train"}],
            }
            provenance = publication_provenance_ref(parent, extract, extract["fragments"][0])
            self.assertTrue(provenance.startswith("run:" + str(parent)))
            self.assertIn("; extraction:" + "2" * 64, provenance)
            self.assertIn("; source:" + "d" * 64, provenance)
            embedding = [(fragment_id, 0, 0, 4, 10, 30, "1" * 64, json.dumps(vec))]
            fragment_row = (file_id, "d" * 64, "f" * 64, "python", "python", 10, 30, provenance)
            index_row = ("READY", "train", 1, "a" * 64, digest(raw), snap)

            def make_conn(emb=None, frag=None, idx=None):
                return FakeIndexConn(idx or index_row, 1, emb if emb is not None else embedding, frag or fragment_row)

            with patch("scripts.code_analysis.reuse.preflight"):
                ok = check_live_index(frozen, connect=make_conn, parent=parent, extract=extract)
            self.assertEqual(ok["status"], "READY_VERIFIED", ok)
            drifted = [(fragment_id, 0, 0, 4, 10, 30, "1" * 64, json.dumps(unit_vector(axis=1)))]
            with patch("scripts.code_analysis.reuse.preflight"):
                bad_vec = check_live_index(frozen, connect=lambda: make_conn(emb=drifted), parent=parent, extract=extract)
            self.assertEqual(bad_vec["status"], "FAILED")
            bad_frag = (file_id, "d" * 64, "f" * 64, "python", "python", 10, 31, provenance)
            with patch("scripts.code_analysis.reuse.preflight"):
                span = check_live_index(frozen, connect=lambda: make_conn(frag=bad_frag), parent=parent, extract=extract)
            self.assertEqual(span["status"], "FAILED")
            self.assertIn("endByte", span["reason"])
            self.assertIn(fragment_id, span["reason"])
            hash_frag = (file_id, "3" * 64, "f" * 64, "python", "python", 10, 30, provenance)
            with patch("scripts.code_analysis.reuse.preflight"):
                source = check_live_index(frozen, connect=lambda: make_conn(frag=hash_frag), parent=parent, extract=extract)
            self.assertEqual(source["status"], "FAILED")
            self.assertIn("sourceSha256", source["reason"])
            stale_prov = (file_id, "d" * 64, "f" * 64, "python", "python", 10, 30, fragment_id)
            with patch("scripts.code_analysis.reuse.preflight"):
                prov = check_live_index(frozen, connect=lambda: make_conn(frag=stale_prov), parent=parent, extract=extract)
            self.assertEqual(prov["status"], "FAILED")
            self.assertIn("provenanceRef", prov["reason"])
            self.assertIn(fragment_id, prov["reason"])
            meta = [(fragment_id, 0, 0, 4, 10, 30, "2" * 64, json.dumps(vec))]
            with patch("scripts.code_analysis.reuse.preflight"):
                digest_mismatch = check_live_index(frozen, connect=lambda: make_conn(emb=meta), parent=parent, extract=extract)
            self.assertEqual(digest_mismatch["status"], "FAILED")
            self.assertIn("representationSha256", digest_mismatch["reason"])

    def test_restore_payload_uses_verified_vectors_not_rebuild(self):
        ARTIFACT_ROOT.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(dir=ARTIFACT_ROOT) as tmp:
            parent = Path(tmp)
            vec = unit_vector()
            raw = vector_bytes([vec])
            fragment_id, file_id = "e" * 64, "c" * 64
            chunk = {"fileId": file_id, "fragmentId": fragment_id, "chunkOrdinal": 0, "tokenStart": 0, "tokenEnd": 4,
                     "sourceStart": 10, "sourceEnd": 30, "representationSha256": "1" * 64, "partition": "train",
                     "language": "python"}
            chunks = canonical(chunk)[:-1] + b"\n"
            frozen = self._write_repr(parent, raw, chunks)
            snap = "a" * 64
            rights = "b" * 64
            (parent / "intake.json").write_bytes(canonical({"snapshotId": snap, "rightsSha256": rights}))
            (parent / "split.json").write_bytes(canonical({"snapshotId": snap}))
            frozen.update({"modelKey": "c" * 64, "partitions": [
                {"partition": "train", "indexKey": "d" * 64, "rowOrdinals": [0], "gallerySha256": "e" * 64}]})
            extract = {
                "extractionSha256": "2" * 64,
                "fragments": [{"fragmentId": fragment_id, "fileId": file_id, "sourceSha256": "d" * 64,
                               "sha256": "f" * 64, "startByte": 10, "endByte": 30, "language": "python",
                               "dialect": "python", "canonicalIndexed": True,
                               "searchable": True, "executable": True}],
                "files": [{"fileId": file_id, "sha256": "d" * 64, "partition": "train", "bytes": 100,
                           "extraction": "VALID_EXTRACTED", "assignmentReviewed": True,
                           "permissions": {"searchIndexAllowed": True}}],
            }
            from scripts.code_analysis.reuse import restore_partition_payload
            payload = restore_partition_payload(parent, frozen, extract, "train")
            self.assertEqual(payload["expectedRows"], 1)
            self.assertEqual(payload["identity"]["index_key"], "d" * 64)
            self.assertEqual(payload["vectorSha256"], digest(raw))
            self.assertEqual(payload["fragments"][0]["provenanceRef"],
                             publication_provenance_ref(parent, extract, extract["fragments"][0]))


class FakeIndexConn:
    def __init__(self, index_row, count, embeddings, fragment_row):
        self.index_row = index_row
        self.count = count
        self.embeddings = embeddings
        self.fragment_row = fragment_row

    def cursor(self):
        return FakeIndexCursor(self)

    def close(self):
        return None


class FakeIndexCursor:
    def __init__(self, owner):
        self.owner = owner
        self.sql = ""

    def __enter__(self):
        return self

    def __exit__(self, *exc):
        return False

    def execute(self, sql, params=None):
        self.sql = sql

    def fetchone(self):
        if "code_indexes" in self.sql:
            return self.owner.index_row
        if "count(*)" in self.sql:
            return (self.owner.count,)
        if "code_fragments" in self.sql:
            return self.owner.fragment_row
        return None

    def fetchall(self):
        if "code_embeddings" in self.sql:
            return self.owner.embeddings
        return []
