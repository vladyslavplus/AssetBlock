import inspect
import os
from pathlib import Path
import subprocess
import sys
import unittest
from unittest.mock import patch

from scripts.code_analysis import baselines
from scripts.feasibility_pilot import subprocess_util, tools


class HiddenSubprocessDefaultTests(unittest.TestCase):
    def test_token_helpers_defer_to_bounded_hidden_window_default(self):
        self.assertIsNone(inspect.signature(tools.run_dolos_batch).parameters["creationflags"].default)
        self.assertIsNone(inspect.signature(baselines.token_stage).parameters["creationflags"].default)

    def test_bounded_default_uses_hidden_window_flag(self):
        completed = subprocess.CompletedProcess([sys.executable], 0, stdout=b"", stderr=b"")
        with patch.object(subprocess_util.subprocess, "run", return_value=completed) as run:
            subprocess_util.run_bounded(
                [sys.executable], cwd=Path.cwd(), allowed_roots=[Path.cwd(), Path(sys.executable).parent],
                timeout_s=1)
        expected = subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0
        self.assertEqual(run.call_args.kwargs["creationflags"], expected)


if __name__ == "__main__":
    unittest.main()
