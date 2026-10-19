import json
import tempfile
import unittest
from pathlib import Path

from scripts.code_retrieval import inputs
from scripts.code_retrieval.paths import RETRIEVAL_ROOT
from scripts.code_retrieval.tests import _support as fx

VALUES = {
    "source-manifest": {"schema": "code-corpus-v1", "sources": []},
    "split": {"schema": "code-corpus-v1", "files": [], "families": {"fam": {"partition": "train"}},
              "rightsSha256": fx.sha(b"rights"), "normalization": {"algorithm": "ast-leaves-identifiers-v1"}},
    "extract": {"schema": "code-corpus-v1", "files": [], "fragments": [], "extractionSha256": fx.sha(b"x")},
    "final-test-seal": {"schema": "code-corpus-v1", "partition": "final-test", "fileCount": 1,
                        "splitSha256": fx.sha(b"s"), "extractionSha256": fx.sha(b"x"),
                        "rightsSha256": fx.sha(b"r"), "scored": False, "ranksInspected": False,
                        "tuning": False},
    "reviewed-export": {"schema": "code-corpus-v1", "extraFiles": [], "extraFragments": []},
}


class InputManifestTests(unittest.TestCase):
    def setUp(self):
        RETRIEVAL_ROOT.mkdir(parents=True, exist_ok=True)
        self.temp = tempfile.TemporaryDirectory(dir=RETRIEVAL_ROOT)
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.corpus = self.root / "runs" / "corpus-fixture"
        (self.corpus / "sources").mkdir(parents=True)
        self.names = {"source-manifest": "source-manifest.json", "split": "split.json",
                      "extract": "extract.json", "final-test-seal": "final-test-seal.json",
                      "reviewed-export": "reviewed-export.json"}
        for role, name in self.names.items():
            (self.corpus / name).write_bytes(fx.canon(VALUES[role]))
        self.manifest = self.root / "inputs.json"

    def entry(self, role, **extra):
        name = self.names[role]
        raw = (self.corpus / name).read_bytes()
        return {"role": role, "path": "runs/corpus-fixture/" + name, "byteSha256": fx.sha(raw),
                "canonicalSha256": fx.sha(fx.canon(json.loads(raw))), **extra}

    def write(self, entries):
        self.manifest.write_bytes(fx.canon({"schema": "code-retrieval-inputs-v1",
                                            "corpusRunRoot": "runs/corpus-fixture", "artifacts": entries}))
        return inputs.load(self.manifest, artifact_root=self.root)

    def declare(self, role, value):
        """Write an additional artifact under a new role and return its manifest entry."""
        name = role + ".json"
        raw = fx.canon(value)
        (self.corpus / name).write_bytes(raw)
        return {"role": role, "path": "runs/corpus-fixture/" + name, "byteSha256": fx.sha(raw),
                "canonicalSha256": fx.sha(fx.canon(value))}

    def all_entries(self):
        return [self.entry(role) for role in self.names]

    def test_valid_manifest_resolves_every_artifact(self):
        loaded = self.write(self.all_entries())
        self.assertEqual(set(loaded["artifacts"]), set(self.names))
        self.assertEqual(loaded["artifacts"]["extract"]["value"], VALUES["extract"])
        self.assertTrue(loaded["sourcesRoot"].is_dir())

    def test_byte_digest_drift_fails_closed(self):
        entries = self.all_entries()
        entries[0]["byteSha256"] = "0" * 64
        with self.assertRaisesRegex(ValueError, "byte digest drift"):
            self.write(entries)

    def test_canonical_digest_drift_fails_closed(self):
        entries = self.all_entries()
        entries[1]["canonicalSha256"] = "1" * 64
        with self.assertRaisesRegex(ValueError, "canonical digest drift"):
            self.write(entries)

    def test_missing_declared_byte_digest_fails_closed(self):
        entries = self.all_entries()
        del entries[2]["byteSha256"]
        with self.assertRaisesRegex(ValueError, "declared byte digest"):
            self.write(entries)

    def test_missing_declared_canonical_digest_fails_closed(self):
        entries = self.all_entries()
        del entries[3]["canonicalSha256"]
        with self.assertRaisesRegex(ValueError, "declared canonical digest"):
            self.write(entries)

    def test_missing_declared_artifact_fails_closed(self):
        entries = self.all_entries()
        entries[2]["path"] = "runs/corpus-fixture/absent.json"
        with self.assertRaisesRegex(ValueError, "declared input missing"):
            self.write(entries)

    def test_traversal_path_is_rejected(self):
        entries = self.all_entries()
        entries[0]["path"] = "../escape.json"
        with self.assertRaisesRegex(ValueError, "invalid|absolute|traversal"):
            self.write(entries)

    def test_unknown_role_is_rejected(self):
        entries = self.all_entries()
        entries[0]["role"] = "mystery"
        with self.assertRaisesRegex(ValueError, "unknown input role"):
            self.write(entries)

    def test_missing_required_role_is_rejected(self):
        entries = [row for row in self.all_entries() if row["role"] != "final-test-seal"]
        with self.assertRaisesRegex(ValueError, "required roles"):
            self.write(entries)

    def test_schema_mismatch_is_rejected(self):
        self.manifest.write_bytes(fx.canon({"schema": "other", "artifacts": []}))
        with self.assertRaisesRegex(ValueError, "schema"):
            inputs.load(self.manifest, artifact_root=self.root)

    def test_wrong_inner_role_schema_is_rejected(self):
        (self.corpus / self.names["source-manifest"]).write_bytes(fx.canon({"schema": "nope"}))
        with self.assertRaisesRegex(ValueError, "unexpected schema"):
            self.write(self.all_entries())

    def test_missing_role_key_is_rejected(self):
        value = {"schema": "code-corpus-v1", "files": [], "fragments": []}
        (self.corpus / self.names["extract"]).write_bytes(fx.canon(value))
        with self.assertRaisesRegex(ValueError, "missing key"):
            self.write(self.all_entries())

    # -- retrieval-validation-corpus role ---------------------------------
    def test_validation_corpus_role_schema_matches_the_admission_schema(self):
        from scripts.code_retrieval import validation
        self.assertEqual(inputs.ROLE_SCHEMAS[inputs.RETRIEVAL_VALIDATION_CORPUS], validation.SCHEMA)
        self.assertEqual(inputs.ROLE_REQUIRED_KEYS[inputs.RETRIEVAL_VALIDATION_CORPUS], ("languages",))

    def test_validation_acceptance_role_stays_transformation_only(self):
        from scripts.code_retrieval import validation
        self.assertEqual(inputs.ROLE_SCHEMAS[inputs.VALIDATION_ACCEPTANCE], "code-corpus-v1")
        self.assertNotEqual(inputs.ROLE_SCHEMAS[inputs.VALIDATION_ACCEPTANCE], validation.SCHEMA)

    def test_validation_corpus_role_loads_with_its_exact_schema(self):
        value = {"schema": "code-retrieval-validation-corpus-v1", "languages": {}}
        entries = self.all_entries() + [self.declare(inputs.RETRIEVAL_VALIDATION_CORPUS, value)]
        loaded = self.write(entries)
        self.assertEqual(loaded["artifacts"][inputs.RETRIEVAL_VALIDATION_CORPUS]["value"], value)

    def test_validation_corpus_role_rejects_a_corpus_schema_payload(self):
        value = {"schema": "code-corpus-v1", "languages": {}}
        entries = self.all_entries() + [self.declare(inputs.RETRIEVAL_VALIDATION_CORPUS, value)]
        with self.assertRaisesRegex(ValueError, "unexpected schema for role retrieval-validation-corpus"):
            self.write(entries)

    def test_validation_corpus_role_requires_languages(self):
        value = {"schema": "code-retrieval-validation-corpus-v1", "status": "INDEPENDENTLY_REVIEWED"}
        entries = self.all_entries() + [self.declare(inputs.RETRIEVAL_VALIDATION_CORPUS, value)]
        with self.assertRaisesRegex(ValueError, "missing key languages"):
            self.write(entries)


if __name__ == "__main__":
    unittest.main()
