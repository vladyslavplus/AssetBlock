import io
import tempfile
import unittest
from pathlib import Path

from scripts.code_retrieval import corpus_index
from scripts.code_retrieval.paths import RETRIEVAL_ROOT
from scripts.code_retrieval.tests import _support as fx

LANGUAGE = "python"
CAP = corpus_index.SOURCE_READ_CAP


class _RecordingStream(io.BytesIO):
    """A contained stream that records every requested read size."""

    def __init__(self, data):
        super().__init__(data)
        self.requests = []

    def read(self, size=-1):
        self.requests.append(size)
        return super().read(size)


class BoundedReadTests(unittest.TestCase):
    def setUp(self):
        RETRIEVAL_ROOT.mkdir(parents=True, exist_ok=True)
        self.temp = tempfile.TemporaryDirectory(dir=RETRIEVAL_ROOT)
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)

    def test_source_read_requests_cap_plus_one_from_the_stream(self):
        # An exactly-cap-sized source proves the bounded read: the stream is asked
        # for SOURCE_READ_CAP + 1 bytes and yields exactly SOURCE_READ_CAP.
        stream = _RecordingStream(b"z" * CAP)
        raw = corpus_index.read_capped(stream, "fixture/over.py")
        self.assertEqual(stream.requests, [CAP + 1])
        self.assertEqual(len(raw), CAP)

    def test_over_cap_stream_is_refused(self):
        stream = _RecordingStream(b"z" * (CAP + 1))
        with self.assertRaisesRegex(ValueError, "read cap"):
            corpus_index.read_capped(stream, "fixture/over.py")

    def test_over_cap_source_file_is_refused(self):
        node = fx.node("fixture", "big.py", LANGUAGE, "train", "fam-train", b"x" * (CAP + 1))
        sources_root = self.root / "sources"
        fx.write_sources(sources_root, [node])
        with self.assertRaisesRegex(ValueError, "read cap"):
            corpus_index.source_bytes(sources_root, node["row"])

    def test_stream_longer_than_declared_length_is_drift(self):
        body = fx.python_body("q")
        node = fx.node("fixture", "q.py", LANGUAGE, "train", "fam-train", body)
        sources_root = self.root / "sources"
        fx.write_sources(sources_root, [node])
        target = sources_root / "fixture" / "q.py"
        target.write_bytes(body + b"\n# appended\n")
        with self.assertRaisesRegex(ValueError, "byte length drift"):
            corpus_index.source_bytes(sources_root, node["row"])


class LineageIdentityTests(unittest.TestCase):
    def setUp(self):
        RETRIEVAL_ROOT.mkdir(parents=True, exist_ok=True)
        self.temp = tempfile.TemporaryDirectory(dir=RETRIEVAL_ROOT)
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.nodes = [fx.node("fixture", "q.py", LANGUAGE, "train", "fam-train", fx.python_body("q")),
                      fx.node("fixture", "v.py", LANGUAGE, "validation", "fam-val", fx.python_body("v")),
                      fx.node("fixture", "ft.py", LANGUAGE, "final-test", "fam-ft", fx.python_body("ft"))]
        self.q, self.v, self.ft = (fx.fragment(self.nodes[0]), fx.fragment(self.nodes[1]),
                                   fx.fragment(self.nodes[2]))
        self.fragments = [self.q, self.v, self.ft]
        self.manifest, self.split, self.extract = fx.corpus(
            self.nodes, self.fragments,
            sources=[fx.source("fixture", [(row["row"]["path"], row["row"]["sha256"], LANGUAGE)
                                           for row in self.nodes])])

    def index(self, triplets=(), **kwargs):
        return fx.index(self.manifest, self.split, self.extract, triplets, **kwargs)

    def test_alias_extra_file_resolves_to_the_canonical_source_row(self):
        alias = {**self.nodes[1]["row"], "fileId": "alias-v"}
        index = self.index(extra_files=[alias])
        self.assertEqual(index["fileAliases"], {"alias-v": self.nodes[1]["row"]["fileId"]})
        self.assertNotIn("alias-v", {row["fileId"] for row in index["files"]})
        self.assertEqual(len(index["files"]), len(self.nodes))

    def test_alias_extra_file_with_changed_identity_is_rejected(self):
        alias = {**self.nodes[1]["row"], "fileId": "alias-v", "sha256": "0" * 64}
        with self.assertRaisesRegex(ValueError, "changed identity"):
            self.index(extra_files=[alias])

    def test_alias_extra_fragment_is_remapped_to_the_canonical_file(self):
        alias = {**self.nodes[1]["row"], "fileId": "alias-v"}
        extra = fx.fragment(self.nodes[1], fragment_id="alias-fragment",
                            patch={"fileId": "alias-v"})
        index = self.index(extra_files=[alias], extra_fragments=[extra])
        rows = {row["fragmentId"]: row for row in index["fragments"]}
        self.assertEqual(rows["alias-fragment"]["fileId"], self.nodes[1]["row"]["fileId"])

    def test_extra_file_family_must_cover_its_source(self):
        stray_node = fx.node("fixture", "stray.py", LANGUAGE, "train", "fam-stray",
                             fx.python_body("stray"))
        paths = [(row["row"]["path"], row["row"]["sha256"], LANGUAGE) for row in self.nodes]
        paths.append((stray_node["row"]["path"], stray_node["row"]["sha256"], LANGUAGE))
        families = {row["familyId"]: {"partition": row["partition"], "sources": [row["sourceId"]],
                                      "assignmentReviewed": True}
                    for row in (node["row"] for node in self.nodes)}
        families["fam-stray"] = {"partition": "train", "sources": ["other-source"],
                                 "assignmentReviewed": True}
        manifest, split, extract = fx.corpus(self.nodes, self.fragments,
                                             sources=[fx.source("fixture", paths)],
                                             families=families)
        with self.assertRaisesRegex(ValueError, "does not cover its source"):
            fx.index(manifest, split, extract, [], extra_files=[stray_node["row"]])

    def test_split_family_must_cover_the_file_source(self):
        families = dict(self.split["families"])
        families["fam-val"] = {"partition": "validation", "sources": ["other-source"],
                               "assignmentReviewed": True}
        with self.assertRaisesRegex(ValueError, "does not cover"):
            corpus_index.build(self.manifest, {**self.split, "families": families},
                               self.extract, [])

    def test_split_repeats_of_one_original_file_are_rejected(self):
        duplicate = fx.node("fixture", "q.py", LANGUAGE, "train", "fam-train",
                            fx.python_body("q"), file_id="duplicate-q")
        manifest, split, extract = fx.corpus(
            self.nodes + [duplicate], self.fragments,
            sources=[fx.source("fixture", [(row["row"]["path"], row["row"]["sha256"], LANGUAGE)
                                           for row in self.nodes])])
        with self.assertRaisesRegex(ValueError, "repeats an original source file identity"):
            corpus_index.build(manifest, split, extract, [])


if __name__ == "__main__":
    unittest.main()
