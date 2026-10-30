import base64
import struct
import tempfile
import unittest
from pathlib import Path

try:
    from transformers import RobertaTokenizerFast
    from scripts.code_analysis.contracts import digest
    from scripts.code_analysis.frozen_encoder import state_hash
    from scripts.code_retrieval import environment_probe
    from scripts.code_retrieval.tests import _tiny_encoder as tiny
    TORCH_IMPORT_ERROR = None
except ImportError as exc:  # torch-less interpreters skip the probe checks
    TORCH_IMPORT_ERROR = exc

SOURCE = b"def f(value):\n    return value + 1\n"


@unittest.skipIf(TORCH_IMPORT_ERROR is not None, "torch/transformers not importable")
class EnvironmentProbeTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.sources = self.root / "sources"
        (self.sources / "src").mkdir(parents=True)
        (self.sources / "src" / "a.py").write_bytes(SOURCE)
        support = self.root / "upstream"
        tiny.write_upstream(support)
        self.tokenizer = RobertaTokenizerFast.from_pretrained(str(support), local_files_only=True)
        self.model = tiny.build_model(support, seed=0)
        self.triplet = self.triplet_fixture()
        self.probe = self.probe_of(self.triplet)

    def triplet_fixture(self):
        file_row = {"fileId": "file-1", "sourceId": "src", "path": "a.py", "sha256": digest(SOURCE),
                    "bytes": len(SOURCE), "partition": "train"}
        fragment = {"fragmentId": "frag-1", "fileId": "file-1", "sha256": digest(SOURCE),
                    "sourceSha256": digest(SOURCE), "startByte": 0, "endByte": len(SOURCE)}
        endpoint = {"label": "anchor", "fragment": fragment, "parent": file_row, "body": SOURCE}
        return {"queryFragmentId": "frag-1", "language": "python", "endpoints": [endpoint]}

    def probe_of(self, triplet):
        return environment_probe.sample_probe(triplet, self.sources, self.tokenizer, self.model,
                                              loaded_state_sha256=state_hash(self.model))

    def tamper_vector(self, probe, delta):
        raw = bytearray(base64.b64decode(probe["vectorBase64"]))
        value = struct.unpack_from("<f", raw, 0)[0] + delta
        struct.pack_into("<f", raw, 0, value)
        tampered = dict(probe)
        tampered["vectorBase64"] = base64.b64encode(bytes(raw)).decode("ascii")
        tampered["vectorSha256"] = digest(bytes(raw))
        return tampered

    def test_probe_runs_on_the_tiny_cpu_encoder_deterministically(self):
        self.assertEqual(self.probe["schema"], environment_probe.PROBE_SCHEMA)
        self.assertEqual(self.probe["windowCount"], 1)
        self.assertEqual(self.probe["vectorDimension"], 768)
        self.assertEqual(self.probe["device"], "cpu")
        self.assertEqual(self.probe, self.probe_of(self.triplet))

    def test_matching_probes_pass_with_zero_difference(self):
        result = environment_probe.compare(self.probe, dict(self.probe))
        self.assertEqual(result["result"], "MATCH")
        self.assertEqual(result["vectorMaxAbsDifference"], 0.0)
        self.assertEqual(result["tolerance"], environment_probe.PROBE_TOLERANCE)

    def test_vector_within_tolerance_still_matches(self):
        result = environment_probe.compare(self.probe, self.tamper_vector(self.probe, 1e-6))
        self.assertLessEqual(result["vectorMaxAbsDifference"], environment_probe.PROBE_TOLERANCE)

    def test_changed_token_id_is_refused(self):
        live = dict(self.probe, tokenIdsSha256="0" * 64)
        with self.assertRaisesRegex(ValueError, "tokenIdsSha256"):
            environment_probe.compare(self.probe, live)

    def test_changed_vector_beyond_tolerance_is_refused(self):
        with self.assertRaisesRegex(ValueError, "probe vectors differ"):
            environment_probe.compare(self.probe, self.tamper_vector(self.probe, 1e-3))

    def test_different_sample_endpoint_is_refused(self):
        live = dict(self.probe, sample=dict(self.probe["sample"], fragmentId="other-fragment"))
        with self.assertRaisesRegex(ValueError, "sample endpoint differs"):
            environment_probe.compare(self.probe, live)

    def test_changed_state_sha_is_refused(self):
        live = dict(self.probe, loadedFullStateSha256="0" * 64)
        with self.assertRaisesRegex(ValueError, "loadedFullStateSha256"):
            environment_probe.compare(self.probe, live)

    def test_wrong_schema_is_refused(self):
        with self.assertRaisesRegex(ValueError, "schema"):
            environment_probe.compare({"schema": "other"}, self.probe)

    def test_probe_file_is_write_once(self):
        out = self.root / "probe.json"
        environment_probe.write_probe_file(out, self.probe)
        with self.assertRaises(FileExistsError):
            environment_probe.write_probe_file(out, self.probe)
        self.assertTrue(out.read_bytes().endswith(b"\n"))


if __name__ == "__main__":
    unittest.main()
