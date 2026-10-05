import copy
import unittest

from scripts.code_analysis.contracts import LANGUAGES, PARTITIONS, canonical, digest
from scripts.code_analysis.evidence import sufficiency
from scripts.code_analysis.splits import assign, normalized_hash


def file(i, sid="a", language="python", partition="train"):
    return {"fileId": str(i), "sourceId": sid, "sha256": digest(str(i).encode()),
            "normalizedSha256": digest(("norm" + str(i)).encode()), "language": language,
            "partition": partition, "original": True, "extraction": "VALID_EXTRACTED",
            "searchableExecutable": True, "duplicateGroup": str(i), "familyId": language + partition,
            "permissions": {"searchIndexAllowed": True, "evaluationAllowed": True}}


def full_fixture():
    rows, seeds, pairs = [], [], []
    for language in LANGUAGES:
        for partition in PARTITIONS:
            local = [file(f"{language}-{partition}-{i}", language=language, partition=partition) for i in range(60)]
            rows.extend(local)
            for q in local[:20]:
                # Same original is relevant for a future permitted query; authored review fixture only.
                seeds.append({"seedFileId": q["fileId"], "relevantFileIds": [q["fileId"]],
                              "distractorFileIds": [d["fileId"] for d in local[20:]]})
                for other, label in [(q, "SIMILAR")] + [(d, "DISSIMILAR") for d in local[20:]]:
                    pairs.append({"leftId": q["fileId"], "leftSha256": q["sha256"],
                                  "rightId": other["fileId"], "rightSha256": other["sha256"], "label": label})
    split = {"files": rows, "families": {l+p: {"assignmentReviewed": True} for l in LANGUAGES for p in PARTITIONS}}
    review = {"status": "REVIEWED", "reviewer": "authored-test-reviewer", "reviewedOn": "2026-10-04",
              "rubric": "implementation-similarity-v1", "rationale": "explicit authored fixture pairs", "pairs": pairs}
    return split, seeds, [review]


class SplitTests(unittest.TestCase):
    def test_forks_cross_source_duplicates_and_order(self):
        config = {"sources": [{"sourceId": sid, "repository": "example/" + sid,
                              "lineage": ["example/shared"] if sid in ("a", "b") else ["example/" + sid]}
                             for sid in ("a", "b", "c", "d")]}
        rows = [file(i, sid) for i, sid in enumerate(("a", "b", "c", "d"))]
        rows[2]["sha256"] = rows[1]["sha256"]
        rows[3]["normalizedSha256"] = rows[2]["normalizedSha256"]
        result = assign(config, rows)
        self.assertEqual(len(result["families"]), 1)
        self.assertEqual(len({r["partition"] for r in result["files"]}), 1)
        self.assertEqual(result, assign({"sources": list(reversed(config["sources"]))}, list(reversed(rows))))

    def test_conflicting_partition_assignment_blocks(self):
        config = {"sources": [{"sourceId": sid, "repository": "example/"+sid,
                              "lineage": ["shared"], "partition": part}
                             for sid, part in (("a", "train"), ("b", "validation"))]}
        with self.assertRaisesRegex(ValueError, "conflicting"):
            assign(config, [file(1, "a"), file(2, "b")])

    def test_missing_normalization_requires_explicit_exclusion(self):
        config = {"sources": [{"sourceId": "a", "repository": "example/a", "lineage": ["example/a"]}]}
        row = file(1)
        row["normalizedSha256"] = None
        with self.assertRaises(ValueError):
            assign(config, [row])
        row["normalizationExcludedReason"] = "authored invalid-source exclusion"
        result = assign(config, [row])
        self.assertEqual(result["duplicateDecisions"][0]["kind"], "normalization-exclusion")

    def test_ast_normalization_preserves_literal_and_operator(self):
        left = b"def sum_values(a):\n    return a + 1\n"
        renamed = b"# comment\ndef add_values(b):\n    return b + 1\n"
        self.assertEqual(normalized_hash(left, "python"), normalized_hash(renamed, "python"))
        self.assertNotEqual(normalized_hash(left, "python"), normalized_hash(left.replace(b"+", b"-"), "python"))
        self.assertNotEqual(normalized_hash(left, "python"), normalized_hash(left.replace(b"1", b"2"), "python"))
        with self.assertRaises(ValueError):
            normalized_hash(b"def broken(\n", "python")


class CountTests(unittest.TestCase):
    def setUp(self):
        self.split, self.seeds, self.reviews = full_fixture()

    def check(self):
        return sufficiency(self.split, self.seeds, self.reviews)

    def test_all_partitions_positive_fixture(self):
        self.assertEqual(self.check()["status"], "FILE_COUNTS_READY")

    def test_failed_pending_or_nonsearchable_seeds_with_valid_gallery(self):
        for extraction, searchable in (("FAILED", True), ("PENDING", True), ("VALID_EXTRACTED", False)):
            with self.subTest(extraction=extraction, searchable=searchable):
                self.split, self.seeds, self.reviews = full_fixture()
                # Keep all 60 valid gallery originals; add 20 independent invalid queries.
                gallery = self.split["files"][:60]
                seeds = []
                for i in range(20):
                    query = file(f"invalid-query-{i}", language="javascript")
                    query.update(extraction=extraction, searchableExecutable=searchable)
                    self.split["files"].append(query)
                    seeds.append({"seedFileId": query["fileId"], "relevantFileIds": [gallery[i]["fileId"]],
                                  "distractorFileIds": [d["fileId"] for d in gallery[20:]]})
                    for other, label in [(gallery[i], "SIMILAR")] + [(d, "DISSIMILAR") for d in gallery[20:]]:
                        self.reviews[0]["pairs"].append({"leftId": query["fileId"], "leftSha256": query["sha256"],
                                                        "rightId": other["fileId"], "rightSha256": other["sha256"],
                                                        "label": label})
                self.seeds[:20] = seeds
                result = self.check()
                row = result["counts"][0]
                self.assertGreaterEqual(row["eligibleOriginalGalleryFiles"], 50)
                self.assertEqual(row["uniqueReviewedSeeds"], 0)
                self.assertEqual(result["status"], "CHANGES_NEEDED")
                self.assertEqual(row["excludedQuerySeedCount"], 20)
                self.assertTrue(all(s["reasons"] for s in row["excludedQuerySeeds"]))
                self.assertEqual(result["extractionDependentCounts"], "PENDING" if extraction == "PENDING" else "CHECKED")
                for query in self.split["files"][-20:]:
                    query.update(extraction="VALID_EXTRACTED", searchableExecutable=True)
                self.assertEqual(self.check()["status"], "FILE_COUNTS_READY")

    def test_conflicting_only_positive_does_not_confirm_seed(self):
        pair = next(p for p in self.reviews[0]["pairs"] if p["label"] == "SIMILAR")
        self.reviews[0]["pairs"].append(pair | {"label": "DISSIMILAR"})
        result = self.check()
        self.assertEqual(result["counts"][0]["uniqueReviewedSeeds"], 19)
        self.assertEqual(result["status"], "CHANGES_NEEDED")
        conflicts = [g for g in result["gaps"] if "conflicting reviewed labels" in g]
        self.assertEqual(len(conflicts), 1)
        for key in ("leftId", "leftSha256", "rightId", "rightSha256"):
            self.assertIn(pair[key], conflicts[0])
        self.reviews[0]["pairs"].reverse()
        self.assertEqual(self.check(), result)
        # Only explicit removal/adjudication of the conflicting evidence restores the gate.
        self.reviews[0]["pairs"].remove(pair | {"label": "DISSIMILAR"})
        self.assertEqual(self.check()["status"], "FILE_COUNTS_READY")

    def test_insufficient_gallery_not_fragments(self):
        self.split["files"] = self.split["files"][:5]
        for row in self.split["files"]:
            row["fragmentCount"] = 1000
        self.assertEqual(self.check()["status"], "CHANGES_NEEDED")

    def test_reverse_conflict_quarantines_both_directions_order_independently(self):
        pair = next(p for p in self.reviews[0]['pairs'] if p['label'] == 'SIMILAR')
        reverse = {'leftId':pair['rightId'],'leftSha256':pair['rightSha256'],
                   'rightId':pair['leftId'],'rightSha256':pair['leftSha256'],'label':'DISSIMILAR'}
        self.reviews[0]['pairs'].append(reverse)
        result=self.check()
        self.assertEqual(result['counts'][0]['uniqueReviewedSeeds'],19)
        self.assertEqual(len([g for g in result['gaps'] if 'conflicting reviewed labels' in g]),1)
        self.reviews[0]['pairs'].reverse()
        self.assertEqual(self.check(),result)
        self.reviews[0]['pairs'].remove(reverse)
        self.reviews[0]['pairs'].append(reverse | {'label':'SIMILAR'})
        self.assertEqual(self.check()['status'],'FILE_COUNTS_READY')

    def test_reverse_distractor_conflict_removes_negative(self):
        pair=next(p for p in self.reviews[0]['pairs'] if p['label']=='DISSIMILAR')
        self.reviews[0]['pairs'].append({'leftId':pair['rightId'],'leftSha256':pair['rightSha256'],
                                       'rightId':pair['leftId'],'rightSha256':pair['leftSha256'],'label':'SIMILAR'})
        result=self.check()
        self.assertIn(39,result['counts'][0]['reviewedDistractorsPerSeed'].values())

    def test_repeated_seed_variants_count_once(self):
        self.seeds = [self.seeds[0]] * 20
        first = self.check()["counts"][0]
        self.assertEqual(first["uniqueReviewedSeeds"], 1)

    def test_pooled_partitions_cannot_meet_counts(self):
        self.seeds = [s for s in self.seeds if not s["seedFileId"].startswith("python-validation-")]
        rows = self.check()["counts"]
        python_rows = [r for r in rows if r["language"] == "python"]
        self.assertEqual([r["uniqueReviewedSeeds"] for r in python_rows], [20, 0, 20])
        self.assertEqual(self.check()["status"], "CHANGES_NEEDED")

    def test_duplicate_gallery_inflation(self):
        for f in self.split["files"]:
            if f["language"] == "javascript" and f["partition"] == "train":
                f["duplicateGroup"] = "same"
        self.assertEqual(self.check()["counts"][0]["eligibleOriginalGalleryFiles"], 1)

    def test_missing_distractors_and_unknown_labels(self):
        self.seeds[0]["distractorFileIds"] = []
        self.assertTrue(any("distractors 0/40" in g for g in self.check()["gaps"]))
        self.reviews[0]["status"] = "UNREVIEWED"
        self.assertEqual(self.check()["counts"][0]["uniqueReviewedSeeds"], 0)

    def test_hash_bound_shared_review_and_contradiction(self):
        pair = next(p for p in self.reviews[0]["pairs"] if p["label"] == "DISSIMILAR")
        self.reviews[0]["pairs"].append(pair | {"label": "SIMILAR"})
        first = self.check()["counts"][0]
        self.assertIn(39, first["reviewedDistractorsPerSeed"].values())
        self.assertTrue(any("conflicting reviewed labels" in g for g in self.check()["gaps"]))

    def test_search_rights_extraction_and_generated_filters(self):
        for f in self.split["files"]:
            f["permissions"]["evaluationAllowed"] = False
        self.assertTrue(all(r["eligibleOriginalGalleryFiles"] == 0 for r in self.check()["counts"]))
        for f in self.split["files"]:
            f["permissions"]["evaluationAllowed"] = True
            f["extraction"] = "PENDING"
        result = self.check()
        self.assertEqual(result["extractionDependentCounts"], "PENDING")
        self.assertTrue(all(r["eligibleOriginalGalleryFiles"] == 0 for r in result["counts"]))
        self.assertTrue(all(r["uniqueReviewedSeeds"] == 0 for r in result["counts"]))
        self.assertTrue(all(r["excludedQuerySeedCount"] == 20 for r in result["counts"]))
