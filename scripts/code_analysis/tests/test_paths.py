import os
import subprocess
import tempfile
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import patch

from scripts.feasibility_pilot.paths import PathEscapeError, ensure_inside, _is_reparse_point
from scripts.code_analysis.corpus import ARTIFACT_ROOT


class OriginalPathTests(unittest.TestCase):
    def setUp(self):
        ARTIFACT_ROOT.mkdir(parents=True, exist_ok=True)
        self.temp = tempfile.TemporaryDirectory(dir=ARTIFACT_ROOT)
        self.addCleanup(self.temp.cleanup)
        self.base = Path(self.temp.name)
        self.root = self.base / "allowed"
        self.root.mkdir()
        self.file = self.root / "plain.txt"
        self.file.write_text("plain", encoding="utf-8")
        self.outside = self.base / "outside"
        self.outside.mkdir()
        (self.outside / "plain.txt").write_text("outside", encoding="utf-8")

    def link(self, name, target, directory=False):
        link = self.root / name
        try:
            link.symlink_to(target, target_is_directory=directory)
        except OSError as exc:
            self.skipTest("native symlink creation unavailable: " + str(exc))
        return link

    def reject(self, path, root=None):
        with self.assertRaises(PathEscapeError):
            ensure_inside(path, root or self.root)

    def test_ordinary_and_new_suffix(self):
        self.assertEqual(ensure_inside(self.file, self.root), self.file.resolve())
        target = self.root / "new" / "nested" / "output.json"
        self.assertEqual(ensure_inside(target, self.root), target.resolve())

    def test_inward_final_native(self):
        self.reject(self.link("inward", self.file))

    def test_outward_final_native(self):
        self.reject(self.link("outward", self.outside / "plain.txt"))

    def test_inward_intermediate_native(self):
        self.reject(self.link("intermediate", self.root, True) / "plain.txt")

    def test_outward_intermediate_native(self):
        self.reject(self.link("intermediate", self.outside, True) / "plain.txt")

    def test_linked_root_native(self):
        root = self.link("root-link", self.outside, True)
        self.reject(root / "plain.txt", root)

    def test_dangling_native(self):
        self.reject(self.link("dangling", self.root / "missing"))

    def test_dangling_intermediate_native(self):
        self.reject(self.link("dangling", self.root / "missing", True) / "new.txt")

    def test_traversal_and_traversal_root(self):
        self.reject(self.root / "child" / ".." / "plain.txt")
        self.reject(self.file, self.root / "child" / "..")

    def test_reparse_attribute_logic_mocked(self):
        with patch.object(Path, "lstat", return_value=SimpleNamespace(st_mode=0o040755, st_file_attributes=0x400)):
            self.assertTrue(_is_reparse_point(self.root))
            self.reject(self.file)

    def test_original_component_checks_mocked(self):
        for linked, supplied, root in ((self.file, self.file, self.root),
                                       (self.root, self.file, self.root),
                                       (self.root / "missing", self.root / "missing/new/output", self.root)):
            with self.subTest(component=linked), patch(
                    "scripts.feasibility_pilot.paths._is_reparse_point", side_effect=lambda p: p == linked):
                self.reject(supplied, root)

    @unittest.skipUnless(os.name == "nt", "Windows junction native case requires Windows")
    def test_junction_native(self):
        self.check_junction(self.outside)

    @unittest.skipUnless(os.name == "nt", "Windows junction native case requires Windows")
    def test_inward_junction_native(self):
        self.check_junction(self.root)

    def check_junction(self, target):
        junction = self.root / "junction"
        # Fixed temp workspace paths, no shell-generated filesystem deletion.
        command = "New-Item -ItemType Junction -Path '{}' -Target '{}' | Out-Null".format(
            str(junction).replace("'", "''"), str(target).replace("'", "''"))
        result = subprocess.run(["powershell", "-NoProfile", "-Command", command], capture_output=True, check=False)
        if result.returncode or not junction.exists():
            self.skipTest("native junction creation unavailable: " + result.stderr.decode(errors="replace"))
        try:
            self.reject(junction / "plain.txt")
            self.reject(junction / "plain.txt", junction)
        finally:
            junction.rmdir()
