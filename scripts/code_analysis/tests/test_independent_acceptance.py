import copy
import unittest
from scripts.code_analysis.contracts import canonical, digest
from scripts.code_analysis.independent_acceptance import materialize_label_manifest


class IndependentAcceptanceTests(unittest.TestCase):
    def setUp(self):
        self.extract = {"files": [{"fileId": "a"*64, "sha256": "b"*64},
                                   {"fileId": "c"*64, "sha256": "d"*64}]}
        positive = {"leftId": "a"*64, "leftSha256": "b"*64,
                    "rightId": "a"*64, "rightSha256": "b"*64, "label": "SIMILAR"}
        negative = {**positive, "rightId": "c"*64, "rightSha256": "d"*64, "label": "DISSIMILAR"}
        self.manifest = {"status": "REVIEWED", "reviewer": "Authored fixture reviewer",
            "rationale": "Authored independent endpoint fixtures", "reviewedOn": "2026-10-05",
            "extractionSha256": digest(canonical(self.extract)),
            "pairReviews": [{"status": "REVIEWED", "reviewer": "Fixture pair reviewer",
                "rationale": "Explicit fixture labels", "reviewedOn": "2026-10-05",
                "rubric": "implementation-similarity-v1", "pairs": [positive, negative]}],
            "seeds": [{"seedFileId": "a"*64, "sourceSha256": "b"*64,
                       "relevantFileIds": ["a"*64], "distractorFileIds": ["c"*64]}]}

    def test_preserves_review_provenance_without_granting_approval(self):
        result = materialize_label_manifest(self.extract, self.manifest)
        self.assertEqual(result["pairReviews"], self.manifest["pairReviews"])
        self.assertFalse(result["overallReviewerApproval"])
        self.assertFalse(result["labelsInferred"])

    def test_rejects_stale_extract_unreviewed_and_changed_endpoints(self):
        for mutate in (lambda m: m.update(extractionSha256="f"*64),
                       lambda m: m.update(status="DRAFT_UNREVIEWED"),
                       lambda m: m["pairReviews"][0]["pairs"][1].update(rightSha256="f"*64)):
            manifest = copy.deepcopy(self.manifest)
            mutate(manifest)
            with self.assertRaises(ValueError):
                materialize_label_manifest(self.extract, manifest)

    def test_unknown_or_conflicting_labels_never_supply_negatives(self):
        manifest = copy.deepcopy(self.manifest)
        manifest["pairReviews"][0]["pairs"][1]["label"] = "UNKNOWN"
        with self.assertRaisesRegex(ValueError, "distractor relation"):
            materialize_label_manifest(self.extract, manifest)
        manifest = copy.deepcopy(self.manifest)
        conflict = {**manifest["pairReviews"][0]["pairs"][1], "label": "SIMILAR"}
        manifest["pairReviews"][0]["pairs"].append(conflict)
        with self.assertRaisesRegex(ValueError, "contradictory"):
            materialize_label_manifest(self.extract, manifest)
