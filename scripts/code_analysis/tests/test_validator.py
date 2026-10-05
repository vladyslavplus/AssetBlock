import copy
import json
import tempfile
import unittest
from pathlib import Path

from scripts.code_analysis.contracts import canonical, digest
from scripts.code_analysis.corpus import ARTIFACT_ROOT
from scripts.code_analysis.evaluation import freeze_protocol
from scripts.code_analysis.finite_labels import FROZEN_PROTOCOL_SHA, RUBRIC
from scripts.code_analysis.reuse import live_index_evidence, reuse_receipt
from scripts.code_analysis.validator import REQUIRED_STAGE_OUTPUTS, seal_final_test, validate_run


def protocol():
    path = Path(__file__).resolve().parents[1] / "config" / "evaluation-protocol.json"
    return json.loads(path.read_bytes())


def sha_row(n=1):
    return digest(canonical(n))


REVIEWER = "unit-test independent fragment reviewer"


def directed_pairs(units, label="DISSIMILAR"):
    pairs = []
    for left in units:
        for right in units:
            if left["fragmentId"] == right["fragmentId"]:
                continue
            pairs.append({
                "leftFragmentId": left["fragmentId"], "leftSha256": left["sha256"],
                "rightFragmentId": right["fragmentId"], "rightSha256": right["sha256"], "label": label,
            })
    return pairs


class ValidatorGateTests(unittest.TestCase):
    def setUp(self):
        ARTIFACT_ROOT.mkdir(parents=True, exist_ok=True)
        self.temp = tempfile.TemporaryDirectory(dir=ARTIFACT_ROOT)
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.protocol = protocol()
        self.assertEqual(freeze_protocol(self.protocol)["sha256"], FROZEN_PROTOCOL_SHA)
        languages = ["javascript", "typescript", "python", "java", "csharp"]
        files, fragments, extra_files, extra_fragments, triplets, diagnostics, reviews = [], [], [], [], [], {}, []
        for language in languages:
            val_units = []
            for index in range(10):
                file_id = sha_row((language, "val-file", index))
                fragment_id = sha_row((language, "val-frag", index))
                body = sha_row((language, "val-sha", index))
                files.append({"fileId": file_id, "language": language, "partition": "validation", "sha256": body})
                unit = {"fragmentId": fragment_id, "fileId": file_id, "sha256": body}
                fragments.append(unit)
                val_units.append(unit)
            diagnostics[language] = [row["fragmentId"] for row in val_units]
            reviews.append({"status": "REVIEWED", "rubric": RUBRIC, "reviewer": REVIEWER, "rationale": "diagnostic",
                            "fragmentPairs": directed_pairs(val_units)})
            train_files = []
            train_frags = []
            for name in ("original", "neg1", "neg2"):
                file_id = sha_row((language, "train-file", name))
                fragment_id = sha_row((language, "train-frag", name))
                body = sha_row((language, "train-sha", name))
                train_files.append({"fileId": file_id, "language": language, "partition": "train", "sha256": body})
                train_frags.append({"fragmentId": fragment_id, "fileId": file_id, "sha256": body})
            files.extend(train_files)
            fragments.extend(train_frags)
            query_file = {"fileId": sha_row((language, "query-file")), "language": language, "partition": "train",
                          "sha256": sha_row((language, "query-src"))}
            query_frag = {"fragmentId": sha_row((language, "query-frag")), "fileId": query_file["fileId"],
                          "sha256": sha_row((language, "query-sha"))}
            extra_files.append(query_file)
            extra_fragments.append(query_frag)
            original, first, second = train_frags
            positive = {"leftFragmentId": query_frag["fragmentId"], "leftSha256": query_frag["sha256"],
                        "rightFragmentId": original["fragmentId"], "rightSha256": original["sha256"], "label": "SIMILAR"}
            negatives = [
                {"leftFragmentId": query_frag["fragmentId"], "leftSha256": query_frag["sha256"],
                 "rightFragmentId": first["fragmentId"], "rightSha256": first["sha256"], "label": "DISSIMILAR"},
                {"leftFragmentId": query_frag["fragmentId"], "leftSha256": query_frag["sha256"],
                 "rightFragmentId": second["fragmentId"], "rightSha256": second["sha256"], "label": "DISSIMILAR"},
            ]
            triplets.append({"language": language, "partition": "train", "rubric": RUBRIC, "status": "REVIEWED",
                             "reviewer": REVIEWER, "rationale": "hash-bound train triplet",
                             "queryFragmentId": query_frag["fragmentId"], "querySha256": query_frag["sha256"],
                             "positive": positive, "negatives": negatives})
        self.extract = {"fragments": fragments, "files": files, "extractionSha256": sha_row(9)}
        self.labels = {"seeds": [{"seedFileId": sha_row(i)} for i in range(300)],
                       "finiteReviewedNegativePairs": 12000,
                       "diagnosticGalleries": diagnostics, "pairReviews": reviews}
        self.frozen = {"fullVectorSha256": sha_row("v"), "finalTestScored": False, "noOptimizerCreated": True,
                       "modelKey": sha_row("model"),
                       "partitions": [{"partition": "train", "indexKey": sha_row("train-idx")},
                                      {"partition": "validation", "indexKey": sha_row("val-idx")},
                                      {"partition": "final-test", "indexKey": sha_row("ft-idx")}]}
        language_metrics = [{"language": language, "recall": 1.0, "mrr": 1.0, "recall5": 1.0, "recall10": 1.0,
                             "recall20": 1.0, "mrr20": 1.0} for language in languages]
        union = [{"language": language, "tokenRecall5": 1.0, "tokenRecall10": 1.0, "tokenRecall20": 1.0,
                  "tokenMrr20": 1.0, "unionRecall40": 1.0} for language in languages]
        self.evaluation = {"partition": "validation", "finalTestScored": False, "corpusMinimaMet": True,
                           "primaryStatus": "PRIMARY", "languages": language_metrics,
                           "equalLanguage": {"recall": 1.0}, "candidateUnit": "source-file",
                           "method": "cpu-rank", "latency": {"count": 30}, "union": union,
                           "identityBaselineOnly": False, "transformationStrataStatus": "SCORED",
                           "scoredTransformationStrata": ["EXACT_COPY", "COMMENT_FORMAT", "AST_BOUND_RENAME"],
                           "partitions": {"train": {"partition": "train"}, "validation": {"partition": "validation"}}}
        self.token = {"status": "SUCCEEDED", "k": 23, "window": 17, "boundPairCount": 3}
        self.scancode = {"outcome": "SUCCEEDED", "coverageComplete": True}
        self.export = {"status": "READY", "trainTriplets": triplets, "extraFragments": extra_fragments,
                       "extraFiles": extra_files, "diagnosticGalleries": diagnostics, "pairReviews": reviews}
        self.seal = {"scored": False, "seedIdentities": [{"seedFileId": sha_row(1)}],
                     "querySha256s": [sha_row(2)], "relevance": {sha_row(1): [sha_row(3)]}}
        self.sufficiency = {"status": "FILE_COUNTS_READY"}
        self.vector_verification = {"status": "VERIFIED", "fullVectorSha256": sha_row("v")}
        live = {"status": "READY_VERIFIED", "modelKey": sha_row("model"), "partitions": {
            "train": {"indexKey": sha_row("train-idx"), "modelKey": sha_row("model"), "state": "READY"},
            "validation": {"indexKey": sha_row("val-idx"), "modelKey": sha_row("model"), "state": "READY"},
            "final-test": {"indexKey": sha_row("ft-idx"), "modelKey": sha_row("model"), "state": "READY"},
        }}
        self.index_check = live_index_evidence(live, frozen=self.frozen, reused={"status": "REUSED"})
        self.stage_payloads = {
            "intake": {"status": "RECORDED"},
            "split": {"status": "RECORDED"},
            "extract": self.extract,
            "export": self.export,
            "summarize": self.sufficiency,
            "frozen": self.frozen,
            "token-baseline": self.token,
            "evaluate": self.evaluation,
            "license-review": self.scancode,
            "index-check": self.index_check,
            "seal-final-test": self.seal,
        }
        self.stage_records = {}
        for name in REQUIRED_STAGE_OUTPUTS:
            self._write_stage(name, self.stage_payloads[name])

    def _stage_filename(self, name):
        outputs = REQUIRED_STAGE_OUTPUTS[name]
        return "scancode.json" if name == "license-review" else outputs[0]

    def _write_stage(self, name, payload):
        filename = self._stage_filename(name)
        path = self.root / filename
        if payload is None:
            if path.exists():
                path.unlink()
            self.stage_records[name] = {"ActualOutcome": "FAILED", "outputs": {}}
            return
        raw = canonical(payload)
        path.write_bytes(raw)
        self.stage_records[name] = {"ActualOutcome": "SUCCEEDED", "outputs": {filename: digest(raw)}}

    def valid(self, rewrite=True, **overrides):
        payload = dict(protocol=self.protocol, extract=self.extract, labels=self.labels, frozen=self.frozen,
                       evaluation=self.evaluation, token=self.token, scancode=self.scancode, export=self.export,
                       seal=self.seal, sufficiency=self.sufficiency, vector_verification=self.vector_verification,
                       index_check=self.index_check, stage_records=self.stage_records)
        payload.update(overrides)
        mapping = {
            "extract": "extract", "frozen": "frozen", "evaluation": "evaluate", "token": "token-baseline",
            "scancode": "license-review", "export": "export", "seal": "seal-final-test",
            "sufficiency": "summarize", "index_check": "index-check",
        }
        if rewrite:
            for name, default in self.stage_payloads.items():
                self._write_stage(name, default)
            for field, stage in mapping.items():
                if field in overrides:
                    self._write_stage(stage, payload[field])
            payload["stage_records"] = self.stage_records
        return validate_run(self.root, **payload)

    def test_complete_payload_is_ready(self):
        result = self.valid()
        self.assertEqual(result["status"], "READY_FOR_REVIEW", result["gaps"])

    def test_missing_mandatory_stages_are_changes_needed(self):
        for field in ("frozen", "evaluation", "token", "scancode", "export", "seal"):
            with self.subTest(field=field):
                result = self.valid(**{field: None})
                self.assertEqual(result["status"], "CHANGES_NEEDED")
                self.assertTrue(result["gaps"])

    def test_unreviewed_or_stale_hashes_block_ready(self):
        export = copy.deepcopy(self.export)
        export["trainTriplets"][0]["reviewer"] = None
        self.assertEqual(self.valid(export=export)["status"], "CHANGES_NEEDED")
        export = copy.deepcopy(self.export)
        export["trainTriplets"][1]["positive"]["rightSha256"] = sha_row("stale")
        self.assertEqual(self.valid(export=export)["status"], "CHANGES_NEEDED")

    def test_duplicated_negatives_and_gallery_ids_block(self):
        export = copy.deepcopy(self.export)
        first = export["trainTriplets"][0]
        first["negatives"][1] = copy.deepcopy(first["negatives"][0])
        self.assertEqual(self.valid(export=export)["status"], "CHANGES_NEEDED")
        export = copy.deepcopy(self.export)
        ids = export["diagnosticGalleries"]["python"]
        export["diagnosticGalleries"]["python"] = [ids[0]] * 10
        self.assertEqual(self.valid(export=export)["status"], "CHANGES_NEEDED")

    def test_sparse_diagnostic_chain_is_not_complete(self):
        export = copy.deepcopy(self.export)
        units = [row for row in self.extract["fragments"]
                 if row["fragmentId"] in export["diagnosticGalleries"]["python"]]
        chain = []
        for left, right in zip(units, units[1:]):
            chain.append({"leftFragmentId": left["fragmentId"], "leftSha256": left["sha256"],
                          "rightFragmentId": right["fragmentId"], "rightSha256": right["sha256"], "label": "DISSIMILAR"})
        export["pairReviews"] = [{"status": "REVIEWED", "rubric": RUBRIC, "reviewer": REVIEWER,
                                  "rationale": "sparse", "fragmentPairs": chain}]
        self.assertEqual(self.valid(export=export)["status"], "CHANGES_NEEDED")
        stale = copy.deepcopy(self.export)
        stale["pairReviews"] = copy.deepcopy(self.export["pairReviews"])
        stale["pairReviews"][2]["fragmentPairs"][0]["rightSha256"] = sha_row("stale-counterpart")
        self.assertEqual(self.valid(export=stale)["status"], "CHANGES_NEEDED")

    def test_wrong_language_or_partition_blocks(self):
        export = copy.deepcopy(self.export)
        export["extraFiles"][0]["language"] = "python"
        self.assertEqual(self.valid(export=export)["status"], "CHANGES_NEEDED")
        extract = copy.deepcopy(self.extract)
        extract["files"][0]["partition"] = "train"
        self.assertEqual(self.valid(extract=extract)["status"], "CHANGES_NEEDED")

    def test_missing_stage_output_and_changed_bytes_block(self):
        (self.root / "evaluate.json").unlink()
        self.assertEqual(self.valid(rewrite=False)["status"], "CHANGES_NEEDED")
        (self.root / "evaluate.json").write_bytes(canonical({"tampered": True}))
        self.assertEqual(self.valid(rewrite=False)["status"], "CHANGES_NEEDED")

    def test_linked_reuse_receipt_is_blocked(self):
        parent_tmp = tempfile.TemporaryDirectory(dir=ARTIFACT_ROOT / "runs")
        self.addCleanup(parent_tmp.cleanup)
        parent_dir = Path(parent_tmp.name)
        original = canonical({"parent": True})
        (parent_dir / "frozen.json").write_bytes(original)
        receipt = reuse_receipt(parent_run_id=parent_dir.name, parent_output="frozen.json",
                                parent_sha256=digest(original))
        receipt["linkedOutput"] = True
        raw = canonical(receipt)
        (self.root / "frozen.json").write_bytes(raw)
        records = copy.deepcopy(self.stage_records)
        records["frozen"]["outputs"]["frozen.json"] = digest(raw)
        self.assertEqual(self.valid(rewrite=False, stage_records=records)["status"], "CHANGES_NEEDED")

    def test_missing_stage_and_unverified_index_block(self):
        records = copy.deepcopy(self.stage_records)
        del records["index-check"]
        (self.root / "index-check.json").unlink()
        self.assertEqual(self.valid(rewrite=False, stage_records=records)["status"], "CHANGES_NEEDED")
        self.assertEqual(self.valid(index_check={"status": "UNVERIFIED"})["status"], "CHANGES_NEEDED")
        nested = {"status": "REUSED", "indexCheck": self.index_check}
        self.assertEqual(self.valid(index_check=nested)["status"], "CHANGES_NEEDED")
        wrong = copy.deepcopy(self.index_check)
        wrong["indexKeys"]["train"] = sha_row("other-key")
        wrong["partitions"]["train"]["indexKey"] = sha_row("other-key")
        self.assertEqual(self.valid(index_check=wrong)["status"], "CHANGES_NEEDED")
        evaluation = copy.deepcopy(self.evaluation)
        evaluation["identityBaselineOnly"] = True
        evaluation["transformationStrataStatus"] = "OMITTED_NOT_REEMBEDDED"
        self.assertEqual(self.valid(evaluation=evaluation)["status"], "CHANGES_NEEDED")

    def test_language_flag_without_endpoints_does_not_pass(self):
        labels = copy.deepcopy(self.labels)
        labels["trainTripletLanguages"] = ["javascript", "typescript", "python", "java", "csharp"]
        labels["diagnosticGalleryLanguages"] = labels["trainTripletLanguages"]
        result = self.valid(export={"status": "READY", "trainTriplets": []}, labels=labels)
        self.assertEqual(result["status"], "CHANGES_NEEDED")
        self.assertTrue(any("triplet" in gap or "diagnostic" in gap for gap in result["gaps"]))

    def test_vector_hash_mismatch_blocks(self):
        result = self.valid(vector_verification={"status": "VERIFIED", "fullVectorSha256": sha_row("other")})
        self.assertEqual(result["status"], "CHANGES_NEEDED")

    def test_argument_payload_mismatch_blocks(self):
        other = copy.deepcopy(self.evaluation)
        other["method"] = "unbound-metrics"
        result = self.valid(rewrite=False, evaluation=other)
        self.assertEqual(result["status"], "CHANGES_NEEDED")
        self.assertTrue(any("evaluation payload digest drift" in gap for gap in result["gaps"]))

    def test_receipt_parent_payload_mismatch_blocks(self):
        parent_tmp = tempfile.TemporaryDirectory(dir=ARTIFACT_ROOT / "runs")
        self.addCleanup(parent_tmp.cleanup)
        parent_dir = Path(parent_tmp.name)
        (parent_dir / "frozen.json").write_bytes(canonical(self.frozen))
        receipt = reuse_receipt(parent_run_id=parent_dir.name, parent_output="frozen.json",
                                parent_sha256=sha_row("other-parent"))
        raw = canonical(receipt)
        (self.root / "frozen.json").write_bytes(raw)
        records = copy.deepcopy(self.stage_records)
        records["frozen"]["outputs"]["frozen.json"] = digest(raw)
        result = self.valid(rewrite=False, stage_records=records)
        self.assertEqual(result["status"], "CHANGES_NEEDED")
        self.assertTrue(any("reuse parent" in gap for gap in result["gaps"]))

    def test_matching_reuse_receipt_resolves_parent_payload(self):
        parent_tmp = tempfile.TemporaryDirectory(dir=ARTIFACT_ROOT / "runs")
        self.addCleanup(parent_tmp.cleanup)
        parent_dir = Path(parent_tmp.name)
        parent_raw = canonical(self.frozen)
        (parent_dir / "frozen.json").write_bytes(parent_raw)
        receipt = reuse_receipt(parent_run_id=parent_dir.name, parent_output="frozen.json",
                                parent_sha256=digest(parent_raw))
        raw = canonical(receipt)
        (self.root / "frozen.json").write_bytes(raw)
        records = copy.deepcopy(self.stage_records)
        records["frozen"]["outputs"]["frozen.json"] = digest(raw)
        result = self.valid(rewrite=False, frozen=self.frozen, stage_records=records)
        self.assertEqual(result["status"], "READY_FOR_REVIEW", result["gaps"])
        self.assertFalse(receipt.get("gpuRerun"))
        self.assertFalse(receipt.get("scancodeRerun"))


class SealTests(unittest.TestCase):
    def test_label_or_query_change_changes_seal(self):
        split = {"rightsSha256": sha_row("r"), "files": []}
        extract = {"extractionSha256": sha_row("e"),
                   "files": [{"fileId": sha_row("f"), "partition": "final-test", "sha256": sha_row("q"),
                              "searchableExecutable": True, "familyId": sha_row("fam")}]}
        labels = {"seeds": [{"seedFileId": sha_row("f"), "relevantFileIds": [sha_row("f")], "distractorFileIds": []}]}
        first = seal_final_test(split, labels, extract, protocol=protocol())
        labels2 = copy.deepcopy(labels)
        labels2["seeds"][0]["relevantFileIds"] = [sha_row("other")]
        second = seal_final_test(split, labels2, extract, protocol=protocol())
        self.assertNotEqual(first["sealSha256"], second["sealSha256"])
        extract2 = copy.deepcopy(extract)
        extract2["files"][0]["sha256"] = sha_row("changed-query")
        third = seal_final_test(split, labels, extract2, protocol=protocol())
        self.assertNotEqual(first["sealSha256"], third["sealSha256"])
        again = seal_final_test(split, labels, extract, protocol=protocol())
        self.assertEqual(first["sealSha256"], again["sealSha256"])
