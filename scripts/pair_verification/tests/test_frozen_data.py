import unittest
from unittest.mock import patch
from pathlib import Path
import subprocess

from scripts.code_analysis.contracts import digest
from scripts.code_analysis.splits import normalized_hash
from scripts.pair_verification import frozen_data
from scripts.feasibility_pilot import subprocess_util


def catalog_row(fragment_id, body, *, file_id=None, source_id=None, path=None):
    file_id = file_id or fragment_id
    fragment = {"fragmentId": fragment_id, "fileId": file_id, "sha256": digest(body),
                "language": "python", "startByte": 0, "endByte": len(body)}
    parent = {"fileId": file_id, "sourceId": source_id or ("source-" + fragment_id),
              "path": path or (fragment_id + ".py"), "language": "python", "partition": "train",
              "familyId": "family-" + fragment_id}
    return {"fragment": fragment, "parent": parent, "body": body,
            "normalizedFragmentSha256": normalized_hash(body, "python")}


class FrozenMiningTests(unittest.TestCase):
    def test_bounded_token_runner_receives_window_creation_flags(self):
        completed = subprocess.CompletedProcess(["python"], 0, stdout=b"", stderr=b"")
        with patch.object(subprocess_util.subprocess, "run", return_value=completed) as run:
            result = subprocess_util.run_bounded(
                [str(Path.cwd() / "artifacts/code_analysis/env/ml/Scripts/python.exe")],
                cwd=Path.cwd(), allowed_roots=[Path.cwd()], timeout_s=1, creationflags=123)
        self.assertEqual(result.returncode, 0)
        self.assertEqual(run.call_args.kwargs["creationflags"], 123)

    def test_nearest_candidates_exclude_disallowed_rows_and_deduplicate_unordered_pairs(self):
        catalog = {
            "a": catalog_row("a", b"x = 1", path="same.py", source_id="source-a"),
            "b": catalog_row("b", b"if x: print(x)"),
            "c": catalog_row("c", b"class C: pass"),
            "same-file": catalog_row("same-file", b"z = 9", file_id="alias-b"),
            "same-path": catalog_row("same-path", b"q = 8", path="same.py", source_id="source-a"),
            "same-bytes": catalog_row("same-bytes", b"x = 1"),
            "same-normalized": catalog_row("same-normalized", b"x=1"),
            "reviewed": catalog_row("reviewed", b"raise RuntimeError()"),
        }
        vectors = {key: [[1.0, 0.0]] for key in catalog}
        reviewed = [{"left": {"fragmentId": "a"}, "right": {"fragmentId": "reviewed"}}]
        selected, stats = frozen_data.nearest_candidates(
            ["a", "b"], catalog, vectors, reviewed, aliases={"alias-b": "b"}, maximum=400)
        pair_keys = [frozenset((row["anchorFragmentId"], row["candidateFragmentId"])) for row in selected]
        self.assertEqual(len(pair_keys), len(set(pair_keys)))
        self.assertEqual(stats["duplicatePairsDropped"], 1)
        self.assertIn("SAME_CANONICAL_FILE", stats["perLanguage"]["python"]["exclusions"])
        self.assertIn("SAME_PATH", stats["perLanguage"]["python"]["exclusions"])
        self.assertIn("BYTE_IDENTICAL", stats["perLanguage"]["python"]["exclusions"])
        self.assertIn("NORMALIZED_IDENTICAL", stats["perLanguage"]["python"]["exclusions"])
        self.assertIn("REVIEWED_PAIR", stats["perLanguage"]["python"]["exclusions"])
        self.assertEqual(stats["perLanguage"]["python"]["anchors"], 2)
        self.assertEqual(stats["perLanguage"]["python"]["anchorsWithoutCandidates"], 0)
        self.assertTrue(all("candidateId" in row for row in selected))

