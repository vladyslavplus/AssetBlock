import contextlib
import tempfile
import unittest
from pathlib import Path
from unittest import mock

from scripts.model_evaluation import guards, validation

SPIED = ("is_file", "is_dir", "stat", "exists", "open", "read_bytes", "read_text")


def _recording(name, original, touched):
    def wrapper(path, *args, **kwargs):
        touched.append((name, str(path)))
        return original(path, *args, **kwargs)
    return wrapper


def run_recording_path_access(call):
    """Run ``call`` and return (its result, every Path access as (method, path text))."""
    touched = []
    with contextlib.ExitStack() as stack:
        for name in SPIED:
            stack.enter_context(mock.patch.object(Path, name, _recording(name, getattr(Path, name), touched)))
        result = call()
    return result, touched


class ReadClosureTests(unittest.TestCase):
    """Restricted-named entries are skipped by name, before any stat or open."""

    def setUp(self):
        temp = tempfile.TemporaryDirectory()
        self.addCleanup(temp.cleanup)
        self.root = Path(temp.name)
        self.run_root = self.root / "runs"
        self.registry = self.root / "registry"
        self.run_root.mkdir()
        self.registry.mkdir()
        self.decoy = "decoy-" + guards.SEAL + "-run"
        for base in (self.run_root, self.registry):
            decoy = base / self.decoy
            decoy.mkdir()
            (decoy / "score-result.json").write_text("{}", encoding="utf-8")
            (decoy / "attempt-0001.json").write_text("{}", encoding="utf-8")
        ordinary = self.run_root / "score-01"
        ordinary.mkdir()
        (ordinary / "score-result.json").write_text("{}", encoding="utf-8")

    def test_restricted_named_run_entry_is_never_stat_or_opened(self):
        (names, skipped), touched = run_recording_path_access(
            lambda: validation.unregistered_results(self.run_root, registered=set()))
        self.assertEqual(names, ["score-01"])
        self.assertEqual(skipped, 1)
        self.assertFalse([item for item in touched if self.decoy in item[1]], touched)
        self.assertTrue([item for item in touched if "score-01" in item[1]])

    def test_restricted_named_registry_directory_is_never_read(self):
        registered, touched = run_recording_path_access(lambda: guards.registered_run_ids(self.registry))
        self.assertEqual(registered, set())
        self.assertFalse([item for item in touched if self.decoy in item[1]], touched)

    def test_ordinary_names_separates_kept_and_skipped_entries(self):
        kept, skipped = guards.ordinary_names(self.run_root)
        self.assertEqual(kept, ["score-01"])
        self.assertEqual(skipped, 1)
        self.assertEqual(guards.ordinary_names(self.root / "missing"), ([], 0))


if __name__ == "__main__":
    unittest.main()
