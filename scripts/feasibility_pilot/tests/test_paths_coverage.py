import unittest
from pathlib import Path

from scripts.feasibility_pilot.extract import union_length
from scripts.feasibility_pilot.inventory import byte_to_line_col
from scripts.feasibility_pilot.paths import PathEscapeError, REPO_ROOT, ensure_inside


class PathTests(unittest.TestCase):
    def test_rejects_escape(self) -> None:
        with self.assertRaises(PathEscapeError):
            ensure_inside(Path("C:/Windows/System32"), REPO_ROOT)

    def test_accepts_inside(self) -> None:
        target = REPO_ROOT / "scripts" / "feasibility_pilot" / "README.md"
        self.assertEqual(ensure_inside(target, REPO_ROOT), target.resolve())


class SliceTests(unittest.TestCase):
    def test_utf8_and_crlf(self) -> None:
        text = "α\r\nbeta"
        encoded = text.encode("utf-8")
        line, col = byte_to_line_col(text, encoded.index(b"b"))
        self.assertEqual(line, 2)
        self.assertEqual(col, 1)


class CoverageTests(unittest.TestCase):
    def test_union_not_sum(self) -> None:
        self.assertEqual(union_length([(0, 10), (5, 12)]), 12)
        self.assertEqual(union_length([(0, 4), (8, 10)]), 6)


if __name__ == "__main__":
    unittest.main()
