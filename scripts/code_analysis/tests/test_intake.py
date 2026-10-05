import copy
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

from scripts.code_analysis.contracts import allowed, digest, relative, validate_config
from scripts.code_analysis.corpus import ARTIFACT_ROOT, NoRedirect, fetch_blob, intake, write_once
from scripts.code_analysis.evidence import dependency_gaps


def config():
    return {"schema": "code-corpus-v1", "sources": [{"sourceId": "one", "repository": "example/one",
             "commit": "a" * 40, "lineage": ["example/one"],
             "files": [{"path": "src/plain.py", "sha256": digest(b"return_value = 1\n"), "language": "python"}],
             "notices": [{"path": "LICENSE", "sha256": digest(b"license evidence\n")}]}]}


class IntakeTests(unittest.TestCase):
    def setUp(self):
        self.config = config()
        ARTIFACT_ROOT.mkdir(parents=True, exist_ok=True)
        self.temp = tempfile.TemporaryDirectory(dir=ARTIFACT_ROOT)
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)

    @staticmethod
    def fetch(url):
        return b"license evidence\n" if url.endswith("LICENSE") else b"return_value = 1\n"

    def test_intake_offline_hashes_and_no_execution(self):
        with patch("subprocess.run", side_effect=AssertionError("intake must not execute")):
            result = intake(self.config, self.root, fetch=self.fetch)
            self.assertEqual(result, intake(self.config, self.root, offline=True))
            self.assertFalse(result["files"][0]["permissions"]["trainingAllowed"])

    def test_hash_drift_fetch(self):
        with self.assertRaisesRegex(ValueError, "hash drift"):
            intake(self.config, self.root, fetch=lambda _: b"drift")

    def test_hash_drift_offline(self):
        intake(self.config, self.root, fetch=self.fetch)
        (self.root / "sources/one/src/plain.py").write_bytes(b"changed")
        with self.assertRaisesRegex(ValueError, "hash drift"):
            intake(self.config, self.root, offline=True)

    def test_missing_notice(self):
        self.config["sources"][0]["notices"] = []
        with self.assertRaises(ValueError):
            validate_config(self.config)

    def test_missing_notice_offline(self):
        intake(self.config, self.root, fetch=self.fetch)
        (self.root / "sources/one/LICENSE").unlink()
        with self.assertRaises(FileNotFoundError):
            intake(self.config, self.root, offline=True)

    def test_redirect_rejected(self):
        for url in ("https://raw.githubusercontent.com/other/blob", "https://evil.example/file"):
            with self.assertRaisesRegex(ValueError, "redirect rejected"):
                NoRedirect().redirect_request(None, None, 302, None, {}, url)

    def test_url_host_revision_and_encoded_traversal(self):
        for url in ("http://raw.githubusercontent.com/o/r/" + "a"*40 + "/file",
                    "https://evil.example/o/r/" + "a"*40 + "/file",
                    "https://raw.githubusercontent.com/o/r/main/file",
                    "https://raw.githubusercontent.com/o/r/" + "a"*40 + "/%2e%2e/file"):
            with self.subTest(url=url), self.assertRaises(ValueError):
                fetch_blob(url)

    def test_paths_and_commit_are_immutable(self):
        for path in ("../x", "src/../x", "/x", "C:/x", "a\\b", "a%2fb", "a//b", "CON.txt", "x."):
            with self.subTest(path=path), self.assertRaises(ValueError):
                relative(path)
        self.config["sources"][0]["commit"] = "main"
        with self.assertRaises(ValueError):
            validate_config(self.config)

    def test_original_and_source_caps_before_fetch(self):
        too_many = config()
        first = too_many["sources"][0]
        first["files"] = [first["files"][0] | {"path": "src/file" + str(i) + ".py"} for i in range(1001)]
        with self.assertRaisesRegex(ValueError, "original file cap"):
            validate_config(too_many)
        too_many = config()
        too_many["sources"] *= 26
        with self.assertRaisesRegex(ValueError, "family cap"):
            validate_config(too_many)

    def test_notice_material_and_total_caps(self):
        with patch.dict("scripts.code_analysis.corpus.CAPS", {"fileBytes": 4}):
            with self.assertRaisesRegex(ValueError, "oversized"):
                intake(self.config, self.root, fetch=self.fetch)
        with patch.dict("scripts.code_analysis.corpus.CAPS", {"totalBytes": 4}):
            with self.assertRaisesRegex(ValueError, "total intake"):
                intake(self.config, self.root, fetch=self.fetch)

    def test_exclusive_snapshot(self):
        p = self.root / "payload.json"
        write_once(p, b"first", ARTIFACT_ROOT)
        with self.assertRaises(FileExistsError):
            write_once(p, b"second", ARTIFACT_ROOT)
        self.assertEqual(p.read_bytes(), b"first")

    def test_rights_missing_unknown_and_search_only(self):
        s = self.config["sources"][0]
        f = s["files"][0]
        r = {"status": "REVIEWED", "reviewer": "authored-test-reviewer", "reviewedOn": "2026-10-04",
             "rationale": "authored test grant", "terms": "MIT", "commit": s["commit"],
             "noticeHashes": {n["path"]: n["sha256"] for n in s["notices"]},
             "fileHashes": {f["path"]: f["sha256"]}, "grants": {"searchIndexAllowed": True}}
        self.assertTrue(allowed(r, "searchIndexAllowed", s, f))
        self.assertFalse(allowed(r, "trainingAllowed", s, f))
        for mutation in ({"status": "UNREVIEWED"}, {"reviewer": None}, {"noticeHashes": {}}, {"fileHashes": {}},
                         {"commit": "b" * 40}, {"reviewedOn": "not-a-date"}, {"grants": {"searchIndexAllowed": "true"}}):
            self.assertFalse(allowed(r | mutation, "searchIndexAllowed", s, f))

    def test_missing_unreviewed_dependency_evidence(self):
        self.assertGreater(len(dependency_gaps({})), 0)
        proposal = {"status": "PROPOSED_NOT_APPROVED", "packages": [{"name": "psycopg", "terms": None}]}
        self.assertTrue(any("missing/unreviewed" in g for g in dependency_gaps(proposal)))
