import contextlib
import io
from pathlib import Path
import unittest
from unittest.mock import patch

from scripts.pair_verification import cli


class CliTests(unittest.TestCase):
    def test_successful_dry_run_exits_zero(self):
        arguments = [
            "prepare-pairs", "--run-id", "dryrun-99", "--inputs", "inputs.json", "--config", "config.json",
            "--prepare-run-id", "prepare", "--train-run-id", "train", "--index-run-id", "index",
            "--evaluation-run-id", "evaluation", "--database-run-id", "database", "--evidence-run-id",
            "evidence", "--dry-run",
        ]
        with patch.object(cli.prepare, "run", return_value={"outcome": "DRY_RUN", "directory": Path("scratch")}), \
                contextlib.redirect_stdout(io.StringIO()):
            self.assertEqual(cli.main(arguments), 0)

    def test_evaluation_commands_parse_chain_ids(self):
        parser = cli.build_parser()
        evaluate = parser.parse_args(["evaluate", "--run-id", "pairs-evaluate-20000101-01",
                                      "--prepare-run-id", "prepare", "--train-run-id", "train",
                                      "--inputs", "inputs", "--config", "config"])
        evidence = parser.parse_args(["validate-evidence", "--run-id", "pairs-evidence-20000101-01",
                                      "--prepare-run-id", "prepare", "--train-run-id", "train",
                                      "--evaluation-run-id", "evaluate", "--inputs", "inputs",
                                      "--config", "config"])
        self.assertEqual(evaluate.command, "evaluate")
        self.assertEqual(evidence.command, "validate-evidence")
        with self.assertRaises(SystemExit):
            parser.parse_args(["evaluate", "--run-id", "pairs-evaluate-20000101-01"])


if __name__ == "__main__":
    unittest.main()
