import json
import tempfile
import unittest
from pathlib import Path

from scripts.code_analysis.contracts import digest
from scripts.code_retrieval import config as config_module, inputs, prepare
from scripts.code_retrieval.paths import RETRIEVAL_ROOT
from scripts.code_retrieval.tests import _support as fx

CONFIG_PATH = Path(__file__).resolve().parents[1] / "config" / "training-config.json"


class PrepareTests(unittest.TestCase):
    def setUp(self):
        RETRIEVAL_ROOT.mkdir(parents=True, exist_ok=True)
        self.temp = tempfile.TemporaryDirectory(dir=RETRIEVAL_ROOT)
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.run_id = "prepare-fixture-" + self.root.name
        self.corpus = self.root / "runs" / "corpus-fixture"

        nodes = [fx.node("fixture", "q.py", "python", "train", "fam-train", fx.python_body("q")),
                 fx.node("fixture", "v.py", "python", "validation", "fam-held", fx.python_body("v")),
                 fx.node("fixture", "t.py", "python", "final-test", "fam-final", fx.python_body("t"))]
        fx.write_sources(self.corpus / "sources", nodes)
        fragment = fx.fragment(nodes[0])
        manifest, split, extract = fx.corpus(nodes, [fragment],
                                             sources=[fx.source("fixture", [(node["row"]["path"], node["row"]["sha256"],
                                                                             "python") for node in nodes])])
        self.values = {"source-manifest": manifest, "split": split, "extract": extract,
                       "reviewed-export": {"schema": "code-corpus-v1", "pairReviews": [], "extraFiles": [],
                                           "extraFragments": [], "trainTriplets": []}}
        self.names, entries = {}, []
        for role, value in self.values.items():
            name = role + ".json"
            raw = fx.canon(value)
            (self.corpus / name).write_bytes(raw)
            self.names[role] = name
            entries.append(self.entry(role, name, raw, value))
        seal = {"schema": "code-corpus-v1", "partition": "final-test", "fileCount": 1, "seedCount": 0,
                "splitSha256": digest((self.corpus / self.names["split"]).read_bytes()),
                "extractionSha256": extract["extractionSha256"], "rightsSha256": split["rightsSha256"],
                "scored": False, "ranksInspected": False, "tuning": False}
        raw = fx.canon(seal)
        (self.corpus / "final-test-seal.json").write_bytes(raw)
        self.names["final-test-seal"] = "final-test-seal.json"
        entries.append(self.entry("final-test-seal", "final-test-seal.json", raw, seal))
        self.manifest = self.root / "inputs.json"
        self.write_manifest(entries)

    def entry(self, role, name, raw, value):
        return {"role": role, "path": "runs/corpus-fixture/" + name, "byteSha256": fx.sha(raw),
                "canonicalSha256": digest(fx.canon(value))}

    def write_manifest(self, entries):
        self.manifest.write_bytes(fx.canon({"schema": "code-retrieval-inputs-v1",
                                            "corpusRunRoot": "runs/corpus-fixture", "artifacts": entries}))

    def run_stage(self):
        loaded = inputs.load(self.manifest, artifact_root=self.root)
        config_loaded = config_module.load(CONFIG_PATH)
        return prepare.run(loaded=loaded, config_loaded=config_loaded, run_id=self.run_id)

    def clean(self, result):
        self.addCleanup(lambda: __import__("shutil").rmtree(result["directory"], ignore_errors=True))

    def test_prepare_reports_blockers_and_binds_the_seal(self):
        result = self.run_stage()
        self.clean(result)
        payload = result["payload"]
        self.assertEqual(payload["status"], "BLOCKED_INSUFFICIENT_REVIEWED_TRAIN_LABELS")
        self.assertEqual(payload["validationQueries"]["status"], "BLOCKED_VALIDATION_QUERY_CORPUS_UNAVAILABLE")
        self.assertEqual(payload["seal"]["fileCount"], 1)
        self.assertFalse(payload["CanAuthorizePublication"])
        self.assertIn("protocolSha256", payload["identity"])
        for name in ("prepare-data.json", "candidate-labels.json", "validation-candidates.json"):
            self.assertTrue((result["directory"] / name).is_file())

    def test_output_collision_is_refused(self):
        result = self.run_stage()
        self.clean(result)
        with self.assertRaises(FileExistsError):
            self.run_stage()

    def test_seal_split_binding_drift_fails_closed(self):
        seal = {"schema": "code-corpus-v1", "partition": "final-test", "fileCount": 1, "seedCount": 0,
                "splitSha256": "0" * 64, "extractionSha256": self.values["extract"]["extractionSha256"],
                "rightsSha256": self.values["split"]["rightsSha256"], "scored": False,
                "ranksInspected": False, "tuning": False}
        raw = fx.canon(seal)
        (self.corpus / "final-test-seal.json").write_bytes(raw)
        entries = json.loads(self.manifest.read_bytes())["artifacts"]
        for row in entries:
            if row["role"] == "final-test-seal":
                row["byteSha256"] = fx.sha(raw)
                row["canonicalSha256"] = digest(fx.canon(seal))
        self.write_manifest(entries)
        with self.assertRaisesRegex(ValueError, "split binding"):
            self.run_stage()

    def test_seal_referencing_a_non_final_file_fails_closed(self):
        seal = {"schema": "code-corpus-v1", "partition": "final-test", "fileCount": 1, "seedCount": 0,
                "splitSha256": digest((self.corpus / self.names["split"]).read_bytes()),
                "extractionSha256": self.values["extract"]["extractionSha256"],
                "rightsSha256": self.values["split"]["rightsSha256"], "scored": False,
                "ranksInspected": False, "tuning": False,
                "galleryFileIds": [self.values["split"]["files"][0]["fileId"]]}
        raw = fx.canon(seal)
        (self.corpus / "final-test-seal.json").write_bytes(raw)
        entries = json.loads(self.manifest.read_bytes())["artifacts"]
        for row in entries:
            if row["role"] == "final-test-seal":
                row["byteSha256"] = fx.sha(raw)
                row["canonicalSha256"] = digest(fx.canon(seal))
        self.write_manifest(entries)
        with self.assertRaisesRegex(ValueError, "outside the final-test"):
            self.run_stage()


if __name__ == "__main__":
    unittest.main()
