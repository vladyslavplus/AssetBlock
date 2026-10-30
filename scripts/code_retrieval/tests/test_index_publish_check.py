import json
import os
import tempfile
import unittest
from pathlib import Path
from unittest import mock

try:
    from scripts.code_analysis.contracts import LANGUAGES, digest
    from scripts.code_analysis.representations import vector_bytes
    from scripts.code_retrieval import admission, index_publish_check, index_verify, prepare
    TEST_IMPORT_ERROR = None
except ImportError as exc:  # keeps the module import failure visible in the report
    TEST_IMPORT_ERROR = exc

DIM = 768


def unit(index):
    vector = [0.0] * DIM
    vector[index % DIM] = 1.0
    return vector


def h(name):
    return digest(name.encode("utf-8"))


def gallery_row(name, **changes):
    row = {"fileId": h("file-" + name), "sha256": h("sha-" + name), "bytes": 100, "partition": "validation",
           "language": "python", "extraction": "VALID_EXTRACTED", "assignmentReviewed": True,
           "permissions": {"searchIndexAllowed": True}, "searchableExecutable": True, "generated": False,
           "vendor": False}
    row.update(changes)
    return row


def indexed_for(rows, vectors):
    """Verified chunk rows as load_index returns them: manifest fields plus the vector, one chunk per file."""
    return [{"fileId": row["fileId"], "fragmentId": row["fileId"], "chunkOrdinal": 0, "tokenStart": 0,
             "tokenEnd": 4, "representationSha256": h("representation-" + row["fileId"]), "vector": vector}
            for row, vector in zip(rows, vectors)]


def inventory_fixture():
    return {"indexes": [[h("index-a"), h("snapshot"), h("model"), h("gallery"), h("representation"), "validation",
                         5, h("vector"), "READY"]],
            "embeddings": [[h("index-a"), 5, h("embedding-digest")]],
            "snapshots": [[h("snapshot"), h("manifest"), h("rights"), h("split"), "READY"]],
            "fragments": [[h("snapshot"), 3, h("fragment-digest")]]}


SNAPSHOT = {"snapshot_id": h("snapshot"), "manifest_sha256": h("manifest"), "rights_sha256": h("rights"),
            "split_sha256": h("split")}
INFO = {"indexKey": h("index-key"), "galleryKey": h("gallery-key")}


def query_manifest_fixture():
    corpus = {"configSha256": h("config")}
    prepare_binding = {"runId": "prepare-fixture"}
    digests = {"trainingEvidenceSha256": h("train"), "exportEvidenceSha256": h("export"),
               "evaluationEvidenceSha256": h("evaluation")}
    manifest = {"runId": "run-fixture", "prepare": prepare_binding, "corpus": corpus, "queries": [{}, {}], **digests}
    kwargs = {"run_id": "run-fixture", "prepare_binding": prepare_binding, "corpus": corpus,
              "evidence_digests": digests, "query_counts": {label: 2 for label in index_verify.MODEL_LABELS}}
    return manifest, kwargs


def query_manifest_files(target, raw, *, windows=1, vectors=None):
    """Write the same query-vector bytes for every label and return a manifest that binds them."""
    directory = target / index_verify.QUERY_DIR
    directory.mkdir(parents=True, exist_ok=True)
    manifest = {"schema": index_verify.QUERY_SCHEMA, "CanAuthorizePublication": False,
                "queries": [{"windows": windows}], "labels": {}}
    for label in index_verify.MODEL_LABELS:
        (directory / (label + ".fp32le")).write_bytes(raw)
        manifest["labels"][label] = {"bytes": len(raw), "sha256": digest(raw),
                                     "vectors": len(raw) // (DIM * 4) if vectors is None else vectors}
    return manifest


@unittest.skipIf(TEST_IMPORT_ERROR is not None, "publish-check imports unavailable")
class PublishCheckTests(unittest.TestCase):
    # -- agreement between the database and the CPU ranking -------------------------------
    def test_exact_order_and_matching_distances_agree(self):
        cpu = {h("a"): 0.9, h("b"): 0.5, h("c"): 0.1}
        db = [{"fileId": h("a"), "distance": 1 - 0.9}, {"fileId": h("b"), "distance": 1 - 0.5},
              {"fileId": h("c"), "distance": 1 - 0.1}]
        outcome = index_publish_check.compare_query(db, cpu, limit=3)
        self.assertTrue(outcome["exact"])
        self.assertEqual(outcome["nearTies"], [])
        self.assertEqual(outcome["disagreements"], [])

    def test_order_swap_within_tolerance_is_reported_as_a_near_tie_with_both_orders(self):
        cpu = {h("a"): 0.9, h("b"): 0.9 + 1e-7, h("c"): 0.1}
        db = [{"fileId": h("a"), "distance": 1 - 0.9}, {"fileId": h("b"), "distance": 1 - 0.9 - 1e-7},
              {"fileId": h("c"), "distance": 1 - 0.1}]
        outcome = index_publish_check.compare_query(db, cpu, limit=3)
        self.assertFalse(outcome["exact"])
        self.assertEqual(outcome["disagreements"], [])
        # A two-file swap is reported at each position it touches, with both orders.
        self.assertEqual(len(outcome["nearTies"]), 2)
        tie = outcome["nearTies"][0]
        self.assertEqual((tie["position"], tie["database"], tie["cpu"]), (1, h("a"), h("b")))
        self.assertLessEqual(abs(tie["databaseScore"] - tie["cpuScore"]), index_publish_check.TIE_TOLERANCE)

    def test_order_swap_beyond_tolerance_is_a_real_disagreement(self):
        cpu = {h("a"): 0.9, h("b"): 0.5}
        db = [{"fileId": h("b"), "distance": 1 - 0.5}, {"fileId": h("a"), "distance": 1 - 0.9}]
        outcome = index_publish_check.compare_query(db, cpu, limit=2)
        self.assertEqual([item["kind"] for item in outcome["disagreements"]], ["ORDER", "ORDER"])
        self.assertEqual(outcome["nearTies"], [])

    def test_distance_beyond_tolerance_is_a_disagreement(self):
        cpu = {h("a"): 0.9, h("b"): 0.5}
        db = [{"fileId": h("a"), "distance": 0.2}, {"fileId": h("b"), "distance": 1 - 0.5}]
        outcome = index_publish_check.compare_query(db, cpu, limit=2)
        self.assertEqual([item["kind"] for item in outcome["disagreements"]], ["DISTANCE"])
        self.assertGreater(outcome["maxAbsScoreDifference"], index_publish_check.DISTANCE_TOLERANCE)

    def test_result_length_mismatch_is_a_disagreement(self):
        cpu = {h("a"): 0.9, h("b"): 0.5, h("c"): 0.1}
        db = [{"fileId": h("a"), "distance": 1 - 0.9}, {"fileId": h("b"), "distance": 1 - 0.5}]
        outcome = index_publish_check.compare_query(db, cpu, limit=3)
        self.assertFalse(outcome["exact"])
        self.assertEqual(outcome["disagreements"], [{"kind": "LENGTH", "database": 2, "cpu": 3}])

    def test_database_file_outside_the_gallery_is_a_disagreement(self):
        cpu = {h("a"): 0.9, h("b"): 0.5}
        db = [{"fileId": h("a"), "distance": 1 - 0.9}, {"fileId": h("outside"), "distance": 0.6}]
        outcome = index_publish_check.compare_query(db, cpu, limit=2)
        kinds = [(item["kind"], item["fileId"]) for item in outcome["disagreements"] if "fileId" in item]
        self.assertIn(("FILE_NOT_IN_GALLERY", h("outside")), kinds)

    def test_equal_scores_break_by_file_identity_ascending_on_both_sides(self):
        low, high = sorted([h("tie-one"), h("tie-two")])
        cpu = {low: 0.5, high: 0.5}
        db = [{"fileId": low, "distance": 0.5}, {"fileId": high, "distance": 0.5}]
        outcome = index_publish_check.compare_query(db, cpu, limit=2)
        self.assertTrue(outcome["exact"])
        self.assertEqual(outcome["disagreements"], [])

    # -- payloads: derived flags, refusals and an accepted tiny index --------------------------
    def test_tiny_gallery_publishes_through_the_writer_payload_check(self):
        rows = [gallery_row("one"), gallery_row("two")]
        gallery = [{"fileId": row["fileId"], "row": row} for row in rows]
        vectors = [unit(0), unit(1)]
        payload = index_publish_check.language_payload(
            SNAPSHOT, "python", h("index-manifest"), h("model"), {**INFO, "indexKey": h("k")}, gallery,
            indexed_for(rows, vectors), "retrieval-index-fixture")
        self.assertEqual(payload["expectedRows"], 2)
        self.assertEqual(payload["identity"]["partition"], "validation")
        self.assertEqual(len(payload["fragments"]), 2)
        self.assertTrue(all(fragment["canonicalIndexed"] and fragment["searchable"] and fragment["executable"]
                            for fragment in payload["fragments"]))
        self.assertEqual(payload["vectorSha256"], digest(vector_bytes(vectors)))

    def test_gallery_file_that_is_not_searchable_is_refused(self):
        row = gallery_row("hidden", searchableExecutable=False)
        with self.assertRaisesRegex(ValueError, "not searchable and executable"):
            index_publish_check.language_payload(SNAPSHOT, "python", h("m"), h("model"), INFO,
                                                 [{"fileId": row["fileId"], "row": row}],
                                                 indexed_for([row], [unit(0)]), "run")

    def test_generated_or_vendored_gallery_file_is_refused(self):
        for changes in ({"generated": True}, {"vendor": True}):
            with self.subTest(changes=changes):
                row = gallery_row("flagged", **changes)
                with self.assertRaisesRegex(ValueError, "generated or vendored"):
                    index_publish_check.language_payload(SNAPSHOT, "python", h("m"), h("model"), INFO,
                                                         [{"fileId": row["fileId"], "row": row}],
                                                         indexed_for([row], [unit(0)]), "run")

    def test_train_partition_file_in_a_validation_index_is_refused(self):
        row = gallery_row("train-file", partition="train")
        with self.assertRaisesRegex(ValueError, "outside the validation partition"):
            index_publish_check.language_payload(SNAPSHOT, "python", h("m"), h("model"), INFO,
                                                 [{"fileId": row["fileId"], "row": row}],
                                                 indexed_for([row], [unit(0)]), "run")

    def test_fragment_cap_is_enforced_by_the_writer_not_weakened(self):
        rows = [gallery_row("cap-%d" % index) for index in range(5001)]
        with self.assertRaisesRegex(ValueError, "canonical fragment/chunk cap"):
            index_publish_check.language_payload(SNAPSHOT, "python", h("m"), h("model"), INFO,
                                                 [{"fileId": row["fileId"], "row": row} for row in rows],
                                                 indexed_for(rows, [unit(0)] * len(rows)), "run")

    def test_gallery_entry_that_is_an_alias_of_another_file_is_refused(self):
        row = gallery_row("aliased")
        with self.assertRaisesRegex(ValueError, "alias of another file"):
            index_publish_check.language_payload(SNAPSHOT, "python", h("m"), h("model"), INFO,
                                                 [{"fileId": h("other"), "row": row}],
                                                 indexed_for([row], [unit(0)]), "run")

    def test_final_test_is_never_a_stage_partition(self):
        row = gallery_row("final", partition="final-test")
        with self.assertRaisesRegex(ValueError, "outside the validation partition"):
            index_publish_check._fragment(row, "run")

    # -- inventory: existing sandbox rows must survive a publish -------------------------------
    def test_adding_rows_keeps_existing_rows_unchanged(self):
        before = inventory_fixture()
        after = {section: list(rows) for section, rows in before.items()}
        after["indexes"].append([h("index-b"), h("snapshot"), h("model"), h("gallery-b"), h("representation"),
                                 "validation", 7, h("vector-b"), "READY"])
        after["embeddings"].append([h("index-b"), 7, h("embedding-b")])
        self.assertEqual(index_publish_check.unchanged_existing(before, after),
                         {"unchanged": True, "section": None, "row": None})

    def test_removed_existing_row_is_a_change(self):
        before = inventory_fixture()
        after = {**before, "indexes": []}
        self.assertEqual(index_publish_check.unchanged_existing(before, after),
                         {"unchanged": False, "section": "indexes", "row": before["indexes"][0]})

    def test_changed_index_state_or_vector_digest_is_a_change(self):
        before = inventory_fixture()
        for position, value in ((8, "PARTIAL"), (7, h("other-vector"))):
            with self.subTest(position=position):
                changed = [list(row) for row in before["indexes"]]
                changed[0][position] = value
                result = index_publish_check.unchanged_existing(before, {**before, "indexes": changed})
                self.assertFalse(result["unchanged"])
                self.assertEqual(result["section"], "indexes")

    def test_changed_embedding_digest_is_a_change(self):
        before = inventory_fixture()
        after = {**before, "embeddings": [[h("index-a"), 5, h("tampered")]]}
        result = index_publish_check.unchanged_existing(before, after)
        self.assertEqual(result["section"], "embeddings")
        self.assertFalse(result["unchanged"])

    def test_changed_fragment_digest_is_a_change(self):
        before = inventory_fixture()
        after = {**before, "fragments": [[h("snapshot"), 3, h("tampered")]]}
        self.assertEqual(index_publish_check.unchanged_existing(before, after)["section"], "fragments")

    # -- failed runs: the real stage wrapper writes failed evidence ----------------------------
    def test_publish_stage_exception_writes_failed_evidence_through_run_stage(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            arguments = {"run_id": "run-fixture", "inputs_path": "unused", "config_path": "unused",
                         "prepare_run_id": "prepare-fixture", "train_run_id": "train-fixture",
                         "index_run_id": "index-fixture", "evaluation_run_id": "evaluation-fixture",
                         "runtime_review": None}
            with mock.patch.object(index_publish_check, "RETRIEVAL_ROOT", root), \
                    mock.patch.object(index_publish_check, "run_dir", lambda run_id: root / run_id), \
                    mock.patch.object(index_publish_check, "publish_and_rank_stage",
                                      side_effect=ValueError("runtime review missing")):
                result = index_publish_check.run_stage("publish-and-rank", **arguments)
            text = (root / "run-fixture" / index_verify.DB_EVIDENCE_NAME).read_text(encoding="utf-8")
            evidence = json.loads(text)
        self.assertEqual(result["outcome"], "FAILED_ERROR")
        self.assertEqual(evidence["outcome"], "FAILED_ERROR")
        self.assertEqual(evidence["reason"], "ValueError: runtime review missing")
        self.assertNotIn("SUCCEEDED", text)

    # -- bindings read from files --------------------------------------------------------------
    def test_prepare_binding_requires_an_admitted_prepare_run(self):
        payload = {"schema": prepare.SCHEMA, "runId": "prepare-fixture", "status": "NOT_ADMITTED",
                   "trainingAdmission": {"status": admission.ADMITTED, "totals": {"admittedTriplets": 3}}}
        with self.assertRaisesRegex(ValueError, "not ADMITTED"):
            index_verify.prepare_binding_from_bytes(json.dumps(payload).encode("utf-8"), "prepare-fixture")
        payload["status"] = admission.ADMITTED
        binding, _ = index_verify.prepare_binding_from_bytes(json.dumps(payload).encode("utf-8"), "prepare-fixture")
        self.assertEqual(binding["admittedTriplets"], 3)
        self.assertEqual(sorted(binding), ["admittedTriplets", "prepareDataBytes", "prepareDataSha256", "runId",
                                           "trainingAdmissionSha256"])

    def test_evaluation_evidence_must_name_the_same_runs_and_corpus(self):
        corpus = {"configSha256": h("config")}
        prepare_binding = {"runId": "prepare-fixture"}
        evidence = {"schema": index_verify.EVALUATION_SCHEMA, "outcome": "SUCCEEDED",
                    "identity": {"trainRunId": "train", "indexRunId": "index", "prepare": prepare_binding,
                                 **corpus}}
        raw = json.dumps(evidence).encode("utf-8")
        matching = {"train_run_id": "train", "index_run_id": "index", "prepare_binding": prepare_binding}
        index_verify.verified_evaluation(raw, corpus=corpus, **matching)
        with self.assertRaisesRegex(ValueError, "another training or index run"):
            index_verify.verified_evaluation(raw, corpus=corpus, **{**matching, "train_run_id": "other"})
        with self.assertRaisesRegex(ValueError, "identity drift: configSha256"):
            index_verify.verified_evaluation(raw, **{**matching, "corpus": {"configSha256": h("other")}})

    def test_query_manifest_with_matching_bindings_is_accepted(self):
        manifest, kwargs = query_manifest_fixture()
        index_verify.require_query_manifest(manifest, **kwargs)

    def test_query_manifest_binding_mismatches_are_refused(self):
        cases = [
            ("run id", lambda manifest, kwargs: manifest.update(runId="other-run"),
             "query manifest run id differs from this run"),
            ("prepare binding", lambda manifest, kwargs: manifest.update(prepare={"runId": "other"}),
             "query manifest prepare binding differs"),
            ("corpus identity", lambda manifest, kwargs: manifest.update(corpus={"configSha256": h("other")}),
             "query manifest corpus identity differs"),
        ]
        for field in ("trainingEvidenceSha256", "exportEvidenceSha256", "evaluationEvidenceSha256"):
            cases.append((field, lambda manifest, kwargs, name=field: manifest.update({name: h("changed")}),
                          "query manifest binding differs: " + field))
        for name, mutate, message in cases:
            with self.subTest(name):
                manifest, kwargs = query_manifest_fixture()
                mutate(manifest, kwargs)
                with self.assertRaisesRegex(ValueError, message):
                    index_verify.require_query_manifest(manifest, **kwargs)

    def test_query_count_must_match_the_evaluation_denominator_for_each_label(self):
        for label in index_verify.MODEL_LABELS:
            with self.subTest(label=label, case="different count"):
                manifest, kwargs = query_manifest_fixture()
                kwargs["query_counts"] = {**kwargs["query_counts"], label: 3}
                with self.assertRaisesRegex(ValueError, "query count differs from the evaluation evidence: " + label):
                    index_verify.require_query_manifest(manifest, **kwargs)
        with self.subTest(case="evaluation denominator missing"):
            manifest, kwargs = query_manifest_fixture()
            kwargs["query_counts"] = {label: None for label in index_verify.MODEL_LABELS}
            with self.assertRaisesRegex(ValueError, "query count differs from the evaluation evidence: E0"):
                index_verify.require_query_manifest(manifest, **kwargs)

    def test_query_manifest_run_id_is_checked_before_any_query_file_is_read(self):
        manifest, kwargs = query_manifest_fixture()
        manifest["runId"] = "retrieval-db-other"
        with self.assertRaisesRegex(ValueError, "run id differs"):
            index_verify.require_query_manifest(manifest, **kwargs)

    def test_tampered_query_vector_file_is_refused(self):
        with tempfile.TemporaryDirectory() as temp:
            target = Path(temp)
            manifest = query_manifest_files(target, vector_bytes([unit(0)]))
            vectors = index_publish_check._query_files(target, manifest)
            self.assertEqual(len(vectors["E0"]), 1)
            path = target / index_verify.QUERY_DIR / "E2.fp32le"
            tampered = bytearray(path.read_bytes())
            tampered[0] ^= 0x01
            path.write_bytes(bytes(tampered))
            with self.assertRaisesRegex(ValueError, "query vector bytes drift: E2"):
                index_publish_check._query_files(target, manifest)

    def test_query_file_that_is_not_whole_vectors_is_refused(self):
        with tempfile.TemporaryDirectory() as temp:
            target = Path(temp)
            raw = vector_bytes([unit(0)]) + b"\x00"
            manifest = query_manifest_files(target, raw, vectors=1)
            with self.assertRaisesRegex(ValueError, "not a whole number of vectors: E0"):
                index_publish_check._query_files(target, manifest)

    def test_query_vector_count_must_match_the_manifest_windows_and_vector_count(self):
        with tempfile.TemporaryDirectory() as temp:
            target = Path(temp)
            raw = vector_bytes([unit(0)])
            with self.subTest(case="windows"):
                manifest = query_manifest_files(target, raw, windows=2)
                with self.assertRaisesRegex(ValueError, "count differs from the manifest windows: E0"):
                    index_publish_check._query_files(target, manifest)
            with self.subTest(case="declared vectors"):
                manifest = query_manifest_files(target, raw, vectors=2)
                with self.assertRaisesRegex(ValueError, "count differs from the manifest windows: E0"):
                    index_publish_check._query_files(target, manifest)

    # -- evidence: no DSN, failed runs never succeed ---------------------------------------------
    def test_evidence_never_carries_the_sandbox_password(self):
        secret = "pw-" + "x" * 20
        with mock.patch.dict(os.environ, {"ASSETBLOCK_CODE_INDEX_DSN":
                                          "host=127.0.0.1 port=55433 dbname=d user=u password=" + secret}):
            evidence = index_publish_check.failure_evidence("run-fixture", "connect failed: password=" + secret)
        text = json.dumps(evidence)
        self.assertNotIn(secret, text)
        self.assertNotIn("password=" + secret, text)
        self.assertNotIn("dsn", text.lower())

    def test_failed_run_is_failed_evidence_and_never_a_success_marker(self):
        evidence = index_publish_check.failure_evidence("run-fixture", "index export missing")
        self.assertEqual(evidence["outcome"], "FAILED_ERROR")
        self.assertIs(evidence["CanAuthorizePublication"], False)
        self.assertNotIn("SUCCEEDED", json.dumps(evidence))

    def test_gallery_plan_binds_galleries_to_the_training_stage_keys(self):
        row = gallery_row("bound")
        index = {"files": [row]}
        query = {"queryId": h("q"), "language": "python", "windows": 1, "scoredGallery": [row["fileId"]]}
        expected = {"python": index_verify.gallery_key([{"fileId": row["fileId"], "sha256": row["sha256"]}])}
        for language in LANGUAGES:
            expected.setdefault(language, index_verify.gallery_key([]))
        manifest = {"queries": [query], "galleryKeys": expected, "corpus": {"validationReportSha256": h("report")}}
        plan = index_publish_check.gallery_plan(index, manifest)
        self.assertEqual([item["fileId"] for item in plan["galleries"]["python"]], [row["fileId"]])
        self.assertEqual(plan["reportSha256"], h("report"))
        manifest["galleryKeys"]["python"] = h("wrong")
        with self.assertRaisesRegex(ValueError, "differs from the training stage gallery: python"):
            index_publish_check.gallery_plan(index, manifest)


if __name__ == "__main__":
    unittest.main()
