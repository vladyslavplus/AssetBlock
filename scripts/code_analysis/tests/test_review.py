import unittest

from scripts.code_analysis.review import bind_pair, endpoint


class EndpointBindingTests(unittest.TestCase):
    def setUp(self):
        self.files = [{"sourceId": "fluentvalidation", "path": "src/FluentValidation/Validators/" + name,
                       "fileId": str(i) * 64, "sha256": str(i + 4) * 64,
                       "commit": "a" * 40, "partition": "validation"}
                      for i, name in enumerate(("NotNullValidator.cs", "NullValidator.cs",
                                                "NotEmptyValidator.cs", "EmptyValidator.cs"), 1)]

    def spec(self, index):
        row = self.files[index]
        return {"source_id": row["sourceId"], "path": row["path"],
                "expected_id": row["fileId"], "expected_sha256": row["sha256"]}

    def test_null_and_empty_paths_bind_distinct_expected_files(self):
        for first, second in ((0, 1), (2, 3)):
            left, right = bind_pair(self.files, self.spec(first), self.spec(second), require_distinct=True)
            self.assertEqual(left["path"], self.files[first]["path"])
            self.assertEqual(right["path"], self.files[second]["path"])
            self.assertNotEqual(left["fileId"], right["fileId"])

    def test_missing_and_ambiguous_full_paths_fail(self):
        with self.assertRaisesRegex(ValueError, "exactly one"):
            endpoint(self.files, "fluentvalidation", "NullValidator.cs")
        with self.assertRaisesRegex(ValueError, "exactly one"):
            endpoint(self.files + [self.files[1]], "fluentvalidation", self.files[1]["path"])

    def test_expected_identity_and_hash_cannot_drift(self):
        for key in ("expected_id", "expected_sha256"):
            spec = self.spec(1)
            spec[key] = "f" * 64
            with self.assertRaises(ValueError):
                endpoint(self.files, **spec)

    def test_cross_file_intent_rejects_self_pair_without_banning_derivatives(self):
        with self.assertRaisesRegex(ValueError, "same endpoint"):
            bind_pair(self.files, self.spec(0), self.spec(0), require_distinct=True)
        left, right = bind_pair(self.files, self.spec(0), self.spec(0))
        self.assertEqual(left, right)

    def test_partition_mismatch_fails(self):
        self.files[1]["partition"] = "train"
        with self.assertRaisesRegex(ValueError, "cross partitions"):
            bind_pair(self.files, self.spec(0), self.spec(1))
