import tempfile
import unittest
from pathlib import Path

from scripts.code_retrieval import candidates
from scripts.code_retrieval.tests import _support as fx

BODIES = [
    b"def alpha(value):\n    total = value + 1\n    return total\n",
    b"def beta(value):\n    count = value + 2\n    return count\n",
    b"def gamma(value):\n    result = value + 3\n    return result\n",
    b"def delta(value):\n    amount = value + 4\n    return amount\n",
]


class CandidateTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.sources_root = Path(self.temp.name) / "sources"
        self.nodes = [fx.node("fixture", "f{}.py".format(i), "python", "train", "fam-train", body)
                      for i, body in enumerate(BODIES)]
        fx.write_sources(self.sources_root, self.nodes)
        self.fragments = [fx.fragment(node) for node in self.nodes]
        self.files = [node["row"] for node in self.nodes]
        self.sources = [fx.source("fixture", [(node["row"]["path"], node["row"]["sha256"], "python")
                                              for node in self.nodes])]
        self.index = {"files": self.files, "fragments": self.fragments}

    def build(self, **overrides):
        options = {"anchors_per_language": 2, "negatives_per_anchor": 2}
        options.update(overrides)
        return candidates.build(self.index, sources=self.sources, sources_root=self.sources_root, **options)

    def test_proposal_is_unreviewed_and_bounded(self):
        proposal = self.build()
        self.assertEqual(proposal["status"], "CANDIDATE_FOR_INDEPENDENT_REVIEW")
        self.assertIsNone(proposal["reviewer"])
        self.assertFalse(proposal["CanAuthorizePublication"])
        self.assertEqual(proposal["proposedTripletCount"], 2)
        self.assertEqual(len(proposal["extraFragments"]), 2)
        for triplet in proposal["proposedTriplets"]:
            self.assertEqual(triplet["status"], "PROPOSED_UNREVIEWED")
            self.assertIsNone(triplet["reviewer"])
            self.assertEqual(len(triplet["negatives"]), 2)

    def test_negatives_are_distinct_files_and_content(self):
        proposal = self.build()
        for language in proposal["languages"].values():
            for anchor in language["anchors"]:
                if not anchor["reviewable"]:
                    continue
                parents = {row["fileId"] for row in anchor["proposedNegatives"]}
                hashes = {row["sha256"] for row in anchor["proposedNegatives"]}
                self.assertEqual(len(parents), 2)
                self.assertEqual(len(hashes), 2)
                self.assertNotIn(anchor["fileId"], parents)

    def test_positive_is_a_derived_unit_not_an_original_slice(self):
        proposal = self.build()
        anchor = proposal["languages"]["python"]["anchors"][0]
        positive = anchor["proposedPositive"]
        self.assertIsNotNone(positive)
        self.assertEqual(positive["parentFragmentId"], anchor["fragmentId"])
        self.assertNotEqual(positive["sha256"], anchor["sha256"])
        record = next(row for row in proposal["extraFragments"] if row["fragmentId"] == positive["fragmentId"])
        self.assertTrue(record["derived"])
        self.assertEqual(record["fileId"], anchor["fileId"])
        self.assertEqual(record["parentFileId"], anchor["fileId"])
        self.assertEqual(record["sourceSha256"], anchor["sourceSha256"])
        self.assertEqual(record["reviewStatus"], "CANDIDATE_UNREVIEWED")
        self.assertIsNone(record["reviewer"])
        self.assertNotIn("startByte", record)
        self.assertEqual(
            record["fragmentId"],
            candidates.derived_fragment_id(anchor["fragmentId"], record["derivation"]["transformation"],
                                           record["derivation"]["version"], record["sha256"]))

    def test_exclusion_removes_covered_anchors(self):
        covered = self.fragments[0]["fragmentId"]
        proposal = self.build(exclude_queries=[covered])
        ids = {row["fragmentId"] for row in proposal["languages"]["python"]["anchors"]}
        self.assertNotIn(covered, ids)

    def test_proposal_is_deterministic(self):
        self.assertEqual(self.build()["proposalSha256"], self.build()["proposalSha256"])


if __name__ == "__main__":
    unittest.main()
