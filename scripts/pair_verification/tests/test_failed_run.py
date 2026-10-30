import json
from pathlib import Path
from unittest.mock import patch

import unittest
import tempfile

from scripts.pair_verification import prepare


class FailedRunTests(unittest.TestCase):
 def test_failed_run_writes_failure_evidence_without_success_marker(self):
    writes = {}
    def save(path, data, root):
        writes[Path(path).name] = data
    with patch.object(prepare, "PAIR_ROOT", Path("virtual/root")), \
         patch.object(prepare, "PAIR_ARTIFACT_ROOT", Path("virtual")), \
         patch.object(prepare, "ensure_inside", side_effect=lambda path, *_: Path(path)), \
         patch.object(Path, "exists", return_value=False), patch.object(Path, "mkdir"), \
         patch.object(prepare, "write_once", side_effect=save), \
         patch.object(prepare.bindings, "bind_predecessors",
                      side_effect=ValueError("synthetic refusal")):
        with self.assertRaisesRegex(ValueError, "synthetic refusal"):
            prepare.run(run_id="failure-check", inputs_path="unused", config_path="unused",
                        prepare_run_id="p", train_run_id="t", index_run_id="i", evaluation_run_id="e",
                        database_run_id="d", evidence_run_id="v")
    evidence = json.loads(writes["evidence.json"])
    self.assertEqual(evidence["outcome"], "FAILED")
    self.assertNotIn("SUCCEEDED", writes)

 def test_prepare_directory_collision_refuses_before_binding(self):
    with tempfile.TemporaryDirectory() as temp:
        root = Path(temp)
        (root / "collision-check").mkdir()
        with patch.object(prepare, "PAIR_ROOT", root), \
             patch.object(prepare.bindings, "bind_predecessors") as bind:
            with self.assertRaisesRegex(ValueError, "immutable output collision"):
                prepare.run(run_id="collision-check", inputs_path="unused", config_path="unused",
                            prepare_run_id="p", train_run_id="t", index_run_id="i", evaluation_run_id="e",
                            database_run_id="d", evidence_run_id="v")
            bind.assert_not_called()

 def test_marker_collision_after_evidence_is_not_swallowed(self):
    with patch.object(prepare, "write_once", side_effect=FileExistsError("marker collision")):
        with self.assertRaisesRegex(FileExistsError, "marker collision"):
            prepare._write_completion_marker(Path("run"), "a" * 64, dry_run=False)
