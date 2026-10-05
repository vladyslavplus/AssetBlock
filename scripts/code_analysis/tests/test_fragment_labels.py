import tempfile
import unittest
from pathlib import Path
from scripts.code_analysis.contracts import digest
from scripts.code_analysis.fragment_labels import materialize_fragment_reviews, merge_fragment_exports
from scripts.code_analysis.reuse import parent_root, reuse_frozen


class FragmentReviewTests(unittest.TestCase):
    def test_explicit_relations_preserved_without_automatic_negatives(self):
        file = {"fileId": "a"*64, "sha256": "b"*64, "partition": "train", "language": "python"}
        fragments = [{"fragmentId": "c"*64, "fileId": file["fileId"], "sha256": "d"*64},
                     {"fragmentId": "e"*64, "fileId": file["fileId"], "sha256": "f"*64}]
        relation = {"leftFragmentId": "c"*64, "leftSha256": "d"*64,
                    "rightFragmentId": "e"*64, "rightSha256": "f"*64, "label": "UNKNOWN"}
        review = {"status": "REVIEWED", "reviewer": "Authored fixture reviewer",
                  "rationale": "Intentionally unknown", "reviewedOn": "2026-10-05",
                  "rubric": "implementation-similarity-v1", "fragmentPairs": [relation]}
        extract = {"files": [file], "fragments": fragments}
        result = materialize_fragment_reviews(extract, {"pairReviews": [review]})
        self.assertEqual(result["pairReviews"], [review])
        self.assertEqual(result["trainTriplets"], [])
        self.assertFalse(result["wholeFileLabelsInherited"])
        with self.assertRaisesRegex(ValueError, "explicit reviewed"):
            materialize_fragment_reviews(extract, {"correspondences": []})
        relation["rightSha256"] = "0"*64
        with self.assertRaisesRegex(ValueError, "hash drift"):
            materialize_fragment_reviews(extract, {"pairReviews": [review]})

    def test_merge_rejects_conflicting_endpoint_identity(self):
        file = {"fileId": "a"*64, "sha256": "b"*64, "partition": "train", "language": "python"}
        with self.assertRaisesRegex(ValueError, "identity conflict"):
            merge_fragment_exports({"extraFiles": [file]}, {"extraFiles": [{**file, "sha256": "c"*64}]})

    def test_explicit_parent_required_and_extract_drift_rejected(self):
        with self.assertRaisesRegex(ValueError, "explicit parent"):
            parent_root()
        from scripts.code_analysis.corpus import ARTIFACT_ROOT
        ARTIFACT_ROOT.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(dir=ARTIFACT_ROOT) as temporary:
            from unittest.mock import patch
            root = Path(temporary)
            (root/'frozen.json').write_text('{}')
            (root/'extract.json').write_text('{"files":[]}')
            with patch('scripts.code_analysis.reuse.ARTIFACT_ROOT', root):
                with self.assertRaisesRegex(ValueError, 'identical extract'):
                    reuse_frozen({"files": [{"changed": True}]}, {}, parent=root)
