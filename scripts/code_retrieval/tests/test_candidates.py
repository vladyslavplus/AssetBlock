import tempfile
import unittest
from pathlib import Path

from scripts.code_retrieval import candidates
from scripts.code_retrieval.paths import RETRIEVAL_ROOT
from scripts.code_retrieval.tests import _support as fx

BODIES = [
    b"def alpha(value):\n    total = value + 1\n    return total\n",
    b"def beta(value):\n    count = value + 2\n    return count\n",
    b"def gamma(value):\n    result = value + 3\n    return result\n",
    b"def delta(value):\n    amount = value + 4\n    return amount\n",
]


class CandidateTests(unittest.TestCase):
    def setUp(self):
        RETRIEVAL_ROOT.mkdir(parents=True, exist_ok=True)
        self.temp = tempfile.TemporaryDirectory(dir=RETRIEVAL_ROOT)
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


class RealCorpusCatchBindingTests(unittest.TestCase):
    """Regression for the real ISO8601Utils.parse candidate.

    The catch clause declares `e`; the write to the enclosing `fail` is not a
    declaration, so the candidate proposal must rewrite the catch write together
    with the declaration instead of leaving a reference to a removed local.
    """

    MANIFEST = Path(__file__).resolve().parents[3] / "artifacts/code_retrieval/inputs/operational-corpus-v12.json"
    QUERY = "03f7f7c6b6d53918eba367ade51b140997a469a5d17dbdb16f64a5f46952876d"
    ALIAS = "8142dc08c5959a96daac137de336153285edc25d77cf5877badf8d484519fc20"
    BROKEN_OUTPUT = "1fc76f9bda1075bbc4473fb3be5f3c5fc85fab1efeea416ef5ccac1083a8fafb"
    ORIGINAL_SHA = "b14e19175354d1040468820845e5806aac6d1d612df2c62c6af16c25148ed072"

    def setUp(self):
        if not self.MANIFEST.exists():
            self.skipTest("frozen operational corpus manifest is not present")
        from scripts.code_retrieval import inputs
        from scripts.code_retrieval.corpus_index import build as build_index
        loaded = inputs.load(self.MANIFEST)
        index = build_index(inputs.value(loaded, inputs.SOURCE_MANIFEST),
                            inputs.value(loaded, inputs.SPLIT),
                            inputs.value(loaded, inputs.EXTRACT),
                            [inputs.value(loaded, role) for role in
                             (inputs.REVIEWED_EXPORT, inputs.INDEPENDENT_ACCEPTANCE,
                              inputs.VALIDATION_ACCEPTANCE, inputs.RETRIEVAL_REVIEWED_LABELS)
                             if inputs.value(loaded, role) is not None])
        self.sources_root = loaded["sourcesRoot"]
        self.files = {row["fileId"]: row for row in index["files"]}
        self.fragments = {row["fragmentId"]: row for row in index["fragments"]}
        if self.QUERY not in self.fragments:
            self.skipTest("real ISO8601Utils.parse fragment is not present")

    def propose(self, fragment_id):
        from scripts.code_retrieval.corpus_index import fragment_bytes
        fragment = self.fragments[fragment_id]
        parent = self.files[fragment["fileId"]]
        body = fragment_bytes(self.sources_root, parent, fragment)
        return body, candidates._propose_positive(body, parent, fragment)

    def test_catch_write_is_renamed_with_the_outer_binding(self):
        import base64
        import hashlib
        body, proposal = self.propose(self.QUERY)
        self.assertEqual(hashlib.sha256(body).hexdigest(), self.ORIGINAL_SHA)
        self.assertIsNotNone(proposal)
        record = proposal["fragment"]
        self.assertNotEqual(record["sha256"], self.BROKEN_OUTPUT)
        text = base64.b64decode(record["bodyBase64"]).decode("utf-8")
        self.assertIn("analysis_renamed_local = e;", text)
        self.assertNotIn("fail = e;", text)
        self.assertNotIn("fail", text)
        # The catch parameter is a different binding and stays untouched.
        self.assertIn("catch (IndexOutOfBoundsException | IllegalArgumentException e)", text)

    def test_alias_endpoint_cannot_produce_the_broken_variant(self):
        if self.ALIAS not in self.fragments:
            self.skipTest("alias endpoint id is not present")
        _, proposal = self.propose(self.ALIAS)
        self.assertIsNotNone(proposal)
        self.assertNotEqual(proposal["fragment"]["sha256"], self.BROKEN_OUTPUT)


class RealCorpusTypedLocalTests(unittest.TestCase):
    """Regression for typed locals: a declaration's type is not a local binding.

    The C# SplitPascalCase candidate is the reviewer-accepted positive; its
    proposed output must stay byte-identical and must keep the type spellings.
    """

    MANIFEST = Path(__file__).resolve().parents[3] / "artifacts/code_retrieval/inputs/operational-corpus-v12.json"
    QUERY = "7841bf7a114f885decddcadfeb4a429ed26540905fc497531c23e745890e7f89"
    PARENT = "0e3049176b167098fc028eca9eaaa3c334d6704d24fd77d89ccd000d9b5dc6e1"
    ACCEPTED_OUTPUT = "e6909f259e151171372668486fd76ada3ed5f4d6accf56048e639b7375854aa3"

    def setUp(self):
        if not self.MANIFEST.exists():
            self.skipTest("frozen operational corpus manifest is not present")
        from scripts.code_retrieval import inputs
        from scripts.code_retrieval.corpus_index import build as build_index
        loaded = inputs.load(self.MANIFEST)
        index = build_index(inputs.value(loaded, inputs.SOURCE_MANIFEST),
                            inputs.value(loaded, inputs.SPLIT),
                            inputs.value(loaded, inputs.EXTRACT),
                            [inputs.value(loaded, role) for role in
                             (inputs.REVIEWED_EXPORT, inputs.INDEPENDENT_ACCEPTANCE,
                              inputs.VALIDATION_ACCEPTANCE, inputs.RETRIEVAL_REVIEWED_LABELS)
                             if inputs.value(loaded, role) is not None])
        self.sources_root = loaded["sourcesRoot"]
        self.files = {row["fileId"]: row for row in index["files"]}
        self.fragments = {row["fragmentId"]: row for row in index["fragments"]}
        if self.QUERY not in self.fragments:
            self.skipTest("real SplitPascalCase fragment is not present")

    def test_typed_local_proposal_keeps_the_type_and_the_accepted_output(self):
        import base64
        from scripts.code_retrieval.corpus_index import fragment_bytes
        fragment = self.fragments[self.QUERY]
        parent = self.files[fragment["fileId"]]
        body = fragment_bytes(self.sources_root, parent, fragment)
        proposal = candidates._propose_positive(body, parent, fragment)
        self.assertIsNotNone(proposal)
        record = proposal["fragment"]
        self.assertEqual(record["sha256"], self.ACCEPTED_OUTPUT)
        text = base64.b64decode(record["bodyBase64"]).decode("utf-8")
        # The declared type is spelled unchanged and only the value binding moved.
        self.assertIn("StringBuilder", text)
        self.assertIn("var analysis_renamed_local = new StringBuilder(", text)
        self.assertNotIn("analysis_renamed_local var", text)


if __name__ == "__main__":
    unittest.main()
