import json
import unittest
from pathlib import Path

from scripts.feasibility_pilot.tools import DOLOS_LANGUAGES


class DolosLanguageTests(unittest.TestCase):
    def test_cli_language_ids(self) -> None:
        self.assertEqual(DOLOS_LANGUAGES["csharp"], "c-sharp")
        self.assertEqual(DOLOS_LANGUAGES["tsx"], "tsx")
        self.assertEqual(DOLOS_LANGUAGES["javascript"], "javascript")


class ExceptionPackageTests(unittest.TestCase):
    def test_scancode_exception_is_recorded(self) -> None:
        path = Path(__file__).resolve().parents[1] / "config" / "proposed-dependency-exceptions.json"
        payload = json.loads(path.read_text(encoding="utf-8"))
        self.assertEqual(payload["status"], "RECORDED_IN_ROOT_EXCEPTIONS")
        self.assertFalse(payload["doNotInstallUntilApproved"])
        self.assertEqual(payload["reviewedOn"], "2026-10-04")
        names = {item["name"] for item in payload["exceptions"]}
        self.assertIn("scancode-toolkit", names)
        self.assertIn("certifi", names)
        self.assertIn("extractcode-libarchive", names)
        self.assertIn("typecode-libmagic", names)
        for item in payload["exceptions"]:
            self.assertEqual(item["reviewedOn"], "2026-10-04")
            self.assertEqual(item["ecosystem"], "pypi")


if __name__ == "__main__":
    unittest.main()
