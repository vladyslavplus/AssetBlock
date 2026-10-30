from pathlib import Path
from unittest.mock import patch

import unittest

from scripts.code_analysis.corpus import write_once


class ImmutableOutputTests(unittest.TestCase):
 def test_write_once_refuses_collision(self):
    root = Path.cwd() / "virtual-output"
    target = root / "evidence.json"
    contents = {}
    def open_path(path, mode="r", *args, **kwargs):
        if mode == "xb":
            if path in contents:
                raise FileExistsError(str(path))
            import io
            class Writer(io.BytesIO):
                def close(self):
                    contents[path] = self.getvalue()
                    super().close()
            return Writer()
        raise AssertionError("unexpected output mode")
    with patch.object(Path, "mkdir"), patch.object(Path, "open", open_path):
        write_once(target, b"first", root)
        with self.assertRaises(FileExistsError):
            write_once(target, b"second", root)
    self.assertEqual(contents[target], b"first")
