import base64
import copy
import struct
import tempfile
import unittest
from pathlib import Path

try:
    from transformers import RobertaTokenizerFast
    from scripts.code_analysis.contracts import digest
    from scripts.code_analysis.frozen_encoder import state_hash
    from scripts.code_retrieval import environment_probe as probe_module
    from scripts.code_retrieval.tests import _tiny_encoder as tiny
    TORCH_IMPORT_ERROR = None
except ImportError as exc:  # torch-less interpreters skip the probe checks
    TORCH_IMPORT_ERROR = exc

SOURCE = b"def f(value):\n    return value + 1\n"
NON_ASCII = "def g(value):\n    return 'caf\u00e9' + str(value)\n".encode("utf-8")
PINNED_PYTHON = "D:\\Programming\\asblock\\artifacts\\code_analysis\\env\\ml\\Scripts\\python.exe"


def endpoint_of(path, body, language="python", partition="train"):
    file_row = {"fileId": "file-" + path, "sourceId": "src", "path": path, "sha256": digest(body),
                "bytes": len(body), "partition": partition}
    fragment = {"fragmentId": "frag-" + path, "fileId": file_row["fileId"], "sha256": digest(body),
                "sourceSha256": digest(body), "startByte": 0, "endByte": len(body)}
    return {"label": "anchor", "fragment": fragment, "parent": file_row, "body": body, "language": language}


@unittest.skipIf(TORCH_IMPORT_ERROR is not None, "torch/transformers not importable")
class EnvironmentProbeTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.sources = self.root / "sources"
        bodies = {"a.py": SOURCE, "b.py": NON_ASCII}
        for path, body in bodies.items():
            (self.sources / "src").mkdir(parents=True, exist_ok=True)
            (self.sources / "src" / path).write_bytes(body)
        support = self.root / "upstream"
        tiny.write_upstream(support)
        self.tokenizer = RobertaTokenizerFast.from_pretrained(str(support), local_files_only=True)
        self.model = tiny.build_model(support, seed=0)
        self.triplets = [
            {"queryFragmentId": "frag-a.py", "language": "python", "endpoints": [endpoint_of("a.py", SOURCE)]},
            {"queryFragmentId": "frag-b.py", "language": "java", "endpoints": [endpoint_of("b.py", NON_ASCII, "java")]},
        ]
        self.picks = probe_module.select_samples(self.triplets, self.sources, self.tokenizer)
        self.records = [probe_module.sample_record(pick["roles"], pick["triplet"], self.sources, self.tokenizer,
                                                   self.model) for pick in self.picks]
        self.state = state_hash(self.model)
        self.live = probe_module.probe_document(self.records, self.state)
        self.reference = self.pinned_reference(self.live)

    def pinned_reference(self, live):
        """The same document as the pinned CPU interpreter would write, interpreter facts forged for the test."""
        reference = copy.deepcopy(live)
        reference["interpreter"] = dict(live["interpreter"], python=PINNED_PYTHON, torch="2.13.0+cpu",
                                        torchCudaBuild=None, cudaAvailable=False,
                                        transformers="5.17.0", tokenizers="0.23.2", safetensors="0.8.0")
        return reference

    def tamper_vector(self, document, delta, index=0):
        tampered = copy.deepcopy(document)
        record = tampered["samples"][index]
        raw = bytearray(base64.b64decode(record["vectorBase64"]))
        struct.pack_into("<f", raw, 0, struct.unpack_from("<f", raw, 0)[0] + delta)
        record["vectorBase64"] = base64.b64encode(bytes(raw)).decode("ascii")
        record["vectorSha256"] = digest(bytes(raw))
        return tampered

    def test_selection_covers_each_language_multi_window_and_non_ascii(self):
        roles = [role for pick in self.picks for role in pick["roles"]]
        self.assertIn("language:python", roles)
        self.assertIn("language:java", roles)
        self.assertIn("nonAscii", roles)
        self.assertEqual(len({pick["triplet"]["endpoints"][0]["fragment"]["fragmentId"] for pick in self.picks}),
                         len(self.picks))

    def test_sample_probe_runs_on_the_tiny_cpu_encoder_deterministically(self):
        again = probe_module.sample_record(self.picks[0]["roles"], self.picks[0]["triplet"], self.sources,
                                           self.tokenizer, self.model)
        self.assertEqual(again, self.records[0])
        self.assertEqual(self.records[0]["vectorDimension"], 768)
        self.assertEqual(self.live["schema"], probe_module.PROBE_SCHEMA)

    def test_matching_probes_pass(self):
        result = probe_module.compare(self.reference, self.live)
        self.assertEqual(result["result"], "MATCH")
        self.assertEqual(result["samplesCompared"], len(self.records))
        self.assertEqual(result["vectorMaxAbsDifference"], 0.0)

    def test_vector_within_tolerance_still_matches(self):
        result = probe_module.compare(self.reference, self.tamper_vector(self.live, 1e-6))
        self.assertLessEqual(result["vectorMaxAbsDifference"], probe_module.PROBE_TOLERANCE)

    def test_changed_token_id_is_refused(self):
        live = copy.deepcopy(self.live)
        live["samples"][0]["tokenIdsSha256"] = "0" * 64
        with self.assertRaisesRegex(ValueError, "tokenIdsSha256"):
            probe_module.compare(self.reference, live)

    def test_changed_vector_beyond_tolerance_is_refused(self):
        with self.assertRaisesRegex(ValueError, "probe vectors differ"):
            probe_module.compare(self.reference, self.tamper_vector(self.live, 1e-3))

    def test_different_sample_endpoint_is_refused(self):
        live = copy.deepcopy(self.live)
        live["samples"][0]["sample"]["fragmentId"] = "other-fragment"
        with self.assertRaisesRegex(ValueError, "sample endpoint differs"):
            probe_module.compare(self.reference, live)

    def test_changed_state_sha_is_refused(self):
        live = dict(self.live, loadedFullStateSha256="0" * 64)
        with self.assertRaisesRegex(ValueError, "loadedFullStateSha256"):
            probe_module.compare(self.reference, live)

    def test_reference_must_come_from_the_pinned_cpu_interpreter(self):
        reference = copy.deepcopy(self.reference)
        reference["interpreter"]["python"] = "D:\\Programming\\asblock\\artifacts\\feasibility_pilot\\env\\ml\\Scripts\\python.exe"
        with self.assertRaisesRegex(ValueError, "pinned interpreter"):
            probe_module.compare(reference, self.live)

    def test_reference_must_be_the_locked_versions(self):
        reference = copy.deepcopy(self.reference)
        reference["interpreter"]["transformers"] = "4.57.1"
        with self.assertRaisesRegex(ValueError, "locked version"):
            probe_module.compare(reference, self.live)

    def test_live_interpreter_may_differ_only_in_listed_fields(self):
        live = copy.deepcopy(self.live)
        live["interpreter"]["torch"] = "2.14.1+cu130"  # listed: allowed
        self.assertIn("torch", probe_module.compare(self.reference, live)["interpreterDifferences"])
        live["interpreter"]["numpy"] = "0.0.0"  # not listed: refused
        with self.assertRaisesRegex(ValueError, "not allowed to differ: numpy"):
            probe_module.compare(self.reference, live)

    def test_probe_file_is_write_once(self):
        out = self.root / "probe.json"
        probe_module.write_probe_file(out, self.live)
        with self.assertRaises(FileExistsError):
            probe_module.write_probe_file(out, self.live)
        self.assertTrue(out.read_bytes().endswith(b"\n"))


if __name__ == "__main__":
    unittest.main()
