import json
import tempfile
import unittest
from pathlib import Path

from scripts.code_retrieval import corpus_index, inputs
from scripts.code_retrieval.tests import _support as fx

class InputManifestTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.corpus = self.root / "runs" / "corpus-fixture"
        (self.corpus / "sources").mkdir(parents=True)
        node = fx.node("fixture", "final.py", "python", "final-test", "fam-final", fx.python_body("final"))
        self.values = dict(zip(("source-manifest", "split", "extract"),
                               fx.corpus([node], [], sources=[fx.source("fixture", [
                                   (node["row"]["path"], node["row"]["sha256"], "python")])])) )
        self.values["reviewed-export"] = {"schema": "code-corpus-v1", "extraFiles": [], "extraFragments": []}
        self.names = {"source-manifest": "source-manifest.json", "split": "split.json",
                      "extract": "extract.json", "reviewed-export": "reviewed-export.json"}
        for role, name in self.names.items():
            (self.corpus / name).write_bytes(fx.canon(self.values[role]))
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
        entries = [self.entry(role) for role in self.names]
        by_role = {entry["role"]: entry for entry in entries}
        pins = {role: {"relativePath": by_role[role]["path"],
                       "bytes": len((self.corpus / self.names[role]).read_bytes()),
                       "byteSha256": by_role[role]["byteSha256"],
                       "canonicalSha256": by_role[role]["canonicalSha256"]}
                for role in ("source-manifest", "split", "extract")}
        value = corpus_index.build_final_test_membership(
            self.values["source-manifest"], self.values["split"], self.values["extract"], pins)
        entries.append(self.declare(inputs.FINAL_TEST_MEMBERSHIP, value))
        return entries

    def test_valid_manifest_resolves_every_artifact(self):
        loaded = self.write(self.all_entries())
        self.assertEqual(set(loaded["artifacts"]), set(self.names) | {inputs.FINAL_TEST_MEMBERSHIP})
        self.assertEqual(loaded["artifacts"]["extract"]["value"], self.values["extract"])
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
        entries = [row for row in self.all_entries() if row["role"] != inputs.FINAL_TEST_MEMBERSHIP]
        with self.assertRaisesRegex(ValueError, "required roles"):
            self.write(entries)

    def test_seal_role_is_rejected_before_declared_path_is_opened(self):
        entries = self.all_entries()
        entries.append({"role": inputs.FINAL_TEST_SEAL, "path": "runs/corpus-fixture/absent-seal.json"})
        with self.assertRaisesRegex(ValueError, "final-test-seal role is forbidden"):
            self.write(entries)

    def test_json_duplicate_members_are_rejected(self):
        with self.assertRaisesRegex(ValueError, "duplicate JSON member"):
            inputs.parse_json(b'{"schema":"one","schema":"two"}', "fixture")

    def test_json_nonfinite_numbers_are_rejected(self):
        with self.assertRaisesRegex(ValueError, "non-finite JSON number"):
            inputs.parse_json(b'{"value":NaN}', "fixture")

    def test_membership_rejects_foreign_gallery_id(self):
        entries = self.all_entries()
        membership = json.loads((self.corpus / (inputs.FINAL_TEST_MEMBERSHIP + ".json")).read_bytes())
        membership["galleryFileIds"] = ["foreign"]
        entries[-1] = self.declare(inputs.FINAL_TEST_MEMBERSHIP, membership)
        with self.assertRaisesRegex(ValueError, "gallery ownership|disagrees"):
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

    def test_assembly_derivation_role_requires_its_schema_and_keys(self):
        self.assertIn(inputs.ASSEMBLY_DERIVATION, inputs.ROLES)
        self.assertEqual(inputs.ROLE_SCHEMAS[inputs.ASSEMBLY_DERIVATION], inputs.ASSEMBLY_DERIVATION_SCHEMA)
        self.assertEqual(inputs.ROLE_REQUIRED_KEYS[inputs.ASSEMBLY_DERIVATION], ("outputOwners", "canonicalSha256"))

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
