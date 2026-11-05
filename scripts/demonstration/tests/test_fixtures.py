import io
import json
import shutil
import tempfile
import unittest
import zipfile
from pathlib import Path

from scripts.demonstration import fixtures
from scripts.demonstration import run_scenarios as runner


class FixtureBuildTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.first = Path(tempfile.mkdtemp(prefix="demo-fixtures-a-"))
        cls.second = Path(tempfile.mkdtemp(prefix="demo-fixtures-b-"))
        cls.manifest_a = fixtures.build(cls.first)
        cls.manifest_b = fixtures.build(cls.second)

    @classmethod
    def tearDownClass(cls):
        shutil.rmtree(cls.first, ignore_errors=True)
        shutil.rmtree(cls.second, ignore_errors=True)

    def test_two_builds_have_identical_archive_hashes(self):
        for fixture_id in fixtures.FIXTURE_IDS:
            name = fixture_id + ".zip"
            with self.subTest(archive=name):
                self.assertEqual((self.first / name).read_bytes(), (self.second / name).read_bytes())
        self.assertEqual(self.manifest_a, self.manifest_b)

    def test_verify_matches_recorded_hashes(self):
        rows = fixtures.verify(self.first)
        self.assertTrue(rows)
        self.assertTrue(all(status == "MATCH" for _, status in rows), rows)

    def test_verify_detects_a_changed_archive(self):
        target = Path(tempfile.mkdtemp(prefix="demo-fixtures-c-"))
        try:
            fixtures.build(target)
            with open(target / "clean-original.zip", "ab") as handle:
                handle.write(b"x")
            rows = dict(fixtures.verify(target))
            self.assertEqual("MISMATCH", rows["clean-original"])
        finally:
            shutil.rmtree(target, ignore_errors=True)

    def test_archives_use_fixed_timestamps_and_sorted_entries(self):
        for fixture_id in fixtures.FIXTURE_IDS:
            with self.subTest(fixture=fixture_id):
                with zipfile.ZipFile(self.first / (fixture_id + ".zip")) as archive:
                    names = [info.filename for info in archive.infolist()]
                    self.assertEqual(sorted(names), names)
                    for info in archive.infolist():
                        self.assertEqual((1980, 1, 1, 0, 0, 0), info.date_time)

    def test_unsafe_archive_contains_a_traversal_entry(self):
        with zipfile.ZipFile(self.first / "unsafe-archive.zip") as archive:
            names = archive.namelist()
        self.assertTrue(any(".." in name.split("/") for name in names), names)

    def test_unsafe_archive_is_not_stored_in_the_source_tree(self):
        self.assertEqual([], list(fixtures.SOURCE_ROOT.rglob("*.zip")))
        self.assertFalse((fixtures.SOURCE_ROOT / "unsafe-archive").exists())

    def test_declaration_fields_follow_the_seller_payload(self):
        declaration = json.loads((self.first / "declaration.json").read_text(encoding="utf-8"))
        expected = {"ownContributionSummary", "ownChanges", "earlierWork", "redistributionAcknowledged",
                    "disclosurePolicyVersion", "components"}
        self.assertEqual(expected, set(declaration))
        component = declaration["components"][0]
        self.assertEqual("MIT", component["license"])
        self.assertTrue(component["sourceUrl"].startswith("https://"))
        self.assertNotIn("0000000", component["knownVersion"])
        self.assertEqual("THIRD_PARTY", component["origin"])

    def test_manifest_records_restriction_and_provenance(self):
        manifest = json.loads((self.first / fixtures.MANIFEST_NAME).read_text(encoding="utf-8"))
        self.assertIn("Never use for research training", manifest["usage_restriction"])
        self.assertEqual(list(fixtures.FIXTURE_IDS), [item["id"] for item in manifest["fixtures"]])
        for item in manifest["fixtures"]:
            self.assertTrue(item["purpose"] and item["rights"] and item["provenance"])

    def test_fixture_sources_have_no_forbidden_text(self):
        for path in fixtures.SOURCE_ROOT.rglob("*"):
            if path.is_file() and "__pycache__" not in path.parts:
                with self.subTest(path=path.name):
                    self.assertEqual([], runner.forbidden_hits(path.name, path.read_text(encoding="utf-8")))

    def test_archive_entries_are_lf_normalized(self):
        with zipfile.ZipFile(io.BytesIO((self.first / "clean-original.zip").read_bytes())) as archive:
            for name in archive.namelist():
                self.assertNotIn(b"\r\n", archive.read(name), name)


if __name__ == "__main__":
    unittest.main()
