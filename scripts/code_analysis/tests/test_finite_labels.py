import json
import unittest
from pathlib import Path

from scripts.code_analysis.contracts import digest
FIXTURE_NAMES = ["fixture_" + str(i) for i in range(9)]
from scripts.code_analysis.finite_labels import expand_minus_pool, file_id


class FiniteLabelTests(unittest.TestCase):
    def test_named_pool_rejects_unlisted_as_negative(self):
        files = []
        rows = []
        for i, name in enumerate(FIXTURE_NAMES, 1):
            raw = digest(name.encode())
            path = "rich/" + name + ".py"
            files.append({"sourceId": "rich", "path": path, "fileId": file_id("rich", "c" * 40, path, raw),
                          "sha256": raw, "commit": "c" * 40, "partition": "train"})
            rows.append({"path": path, "sha256": raw, "sourceText": name})
        package = {"sourceId": "rich", "commit": "c" * 40, "files": rows,
                   "_rawSha256": "2f46661519cd52a70abedfa7b02420be7cf60962b7ac3a00ab8d65eadbef2b2b"}
        with self.assertRaises(ValueError):
            expand_minus_pool(files, package, rows, {1: [1, 2, 3, 4, 5, 6, 7, 8, 9]}, ["p"], package["_rawSha256"])
