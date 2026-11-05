import contextlib
import io
import shutil
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path
from unittest import mock

from scripts.demonstration import run_scenarios as runner


def python_lane(lane_id, code, expected_min=2, timeout=60):
    """A tiny lane that runs the current interpreter. Its output is parsed as a Playwright summary."""
    return {"id": lane_id, "evidence_kind": "unit", "cwd": ".", "executable": sys.executable,
            "args": ["-B", "-c", code], "expected_min_tests": expected_min, "timeout_seconds": timeout,
            "test_files": ["scripts/demonstration/run_scenarios.py"]}


class ParserTests(unittest.TestCase):
    def test_dotnet_summary_counts(self):
        text = "Passed!  - Failed:     0, Passed:     5, Skipped:     1, Total:     6, Duration: 2 s"
        self.assertEqual({"failed": 0, "passed": 5, "skipped": 1, "total": 6}, runner.parse_dotnet_summary(text))

    def test_dotnet_no_matching_filter_counts_zero(self):
        text = "No test matches the given testcase filter `X` in a.dll"
        self.assertEqual(0, runner.parse_dotnet_summary(text)["passed"])

    def test_dotnet_listing_counts_rows_after_header(self):
        text = ("Test run for a.dll\nThe following Tests are available:\n"
                "    AssetBlock.Application.Tests.A.B\n    AssetBlock.Application.Tests.A.C(x: 1)\n")
        self.assertEqual(2, runner.parse_dotnet_listing(text))
        self.assertIsNone(runner.parse_dotnet_listing("no header here"))

    def test_vitest_summary_counts(self):
        text = "      Tests  1 passed | 19 skipped (20)\n"
        counts = runner.parse_vitest_summary(text)
        self.assertEqual((1, 0, 19, 20), (counts["passed"], counts["failed"], counts["skipped"], counts["total"]))
        failed = runner.parse_vitest_summary("      Tests  3 failed | 2 passed (5)")
        self.assertEqual((2, 3), (failed["passed"], failed["failed"]))

    def test_playwright_summary_counts(self):
        text = "Running 26 tests\n\n  1 failed\n  25 passed (1.2m)\n"
        counts = runner.parse_playwright_summary(text)
        self.assertEqual((25, 1), (counts["passed"], counts["failed"]))
        self.assertIsNone(runner.parse_playwright_summary("nothing useful"))


class JudgeTests(unittest.TestCase):
    def test_nonzero_exit_is_fail(self):
        self.assertEqual("FAIL", runner.judge_lane(1, False, {"passed": 9, "failed": 0, "skipped": 0,
                                                              "total": 9}, 1)[0])

    def test_timeout_is_fail_even_with_zero_exit(self):
        self.assertEqual("FAIL", runner.judge_lane(0, True, None, 1)[0])

    def test_unparseable_counts_are_unknown_not_pass(self):
        self.assertEqual("UNKNOWN", runner.judge_lane(0, False, None, 1)[0])

    def test_fewer_than_expected_tests_is_fail(self):
        status, reason = runner.judge_lane(0, False, {"passed": 1, "failed": 0, "skipped": 0, "total": 1}, 2)
        self.assertEqual("FAIL", status)
        self.assertIn("below expected minimum", reason)

    def test_failed_tests_are_fail(self):
        self.assertEqual("FAIL", runner.judge_lane(0, False, {"passed": 5, "failed": 1, "skipped": 0,
                                                              "total": 6}, 1)[0])

    def test_exact_minimum_passes(self):
        self.assertEqual("PASS", runner.judge_lane(0, False, {"passed": 2, "failed": 0, "skipped": 0,
                                                              "total": 2}, 2)[0])


class LaneExecutionTests(unittest.TestCase):
    def setUp(self):
        self.tmp = Path(tempfile.mkdtemp(prefix="demo-runner-test-"))

    def tearDown(self):
        shutil.rmtree(self.tmp, ignore_errors=True)

    def test_passing_lane_writes_logs_and_passes(self):
        lane = python_lane("t-pass", "print('  3 passed (0.1s)')")
        record = runner.run_lane(lane, self.tmp)
        self.assertEqual("PASS", record["status"])
        self.assertEqual(0, record["exit_code"])
        self.assertTrue(Path(record["stdout_log"]).is_file())

    def test_native_exit_code_is_recorded(self):
        record = runner.run_lane(python_lane("t-exit", "import sys; print('  3 passed'); sys.exit(5)"), self.tmp)
        self.assertEqual("FAIL", record["status"])
        self.assertEqual(5, record["exit_code"])

    def test_too_few_tests_is_fail(self):
        record = runner.run_lane(python_lane("t-min", "print('  1 passed (0.1s)')", expected_min=2), self.tmp)
        self.assertEqual("FAIL", record["status"])

    def test_timeout_stops_the_child_and_fails(self):
        record = runner.run_lane(python_lane("t-timeout", "import time; time.sleep(60)", timeout=1), self.tmp)
        self.assertTrue(record["timed_out"])
        self.assertEqual("FAIL", record["status"])

    def test_missing_executable_fails_without_starting(self):
        lane = python_lane("t-missing", "pass")
        lane["executable"] = "assetblock-no-such-binary-xyz"
        record = runner.run_lane(lane, self.tmp)
        self.assertEqual("FAIL", record["status"])
        self.assertIsNone(record["exit_code"])

    def test_dry_run_vectors_are_printed_without_execution(self):
        manifest = runner.load_manifest()
        scenario = manifest["scenarios"][0]
        lane = scenario["lanes"][0]
        argv = runner.lane_argv(lane)
        self.assertIn("--no-restore", argv)
        self.assertIn("FullyQualifiedName~PublicSurfaceVersionIntegrationTests", argv)
        with contextlib.redirect_stdout(io.StringIO()) as printed:
            self.assertEqual(0, runner.main(["run", "--scenario", "D1", "--dry-run"]))
        self.assertIn("--no-restore", printed.getvalue())
        self.assertIn("node_modules/@playwright/test/cli.js", printed.getvalue())

    def test_run_id_reuse_is_refused(self):
        scenario = {"id": "D1", "title": "t", "lanes": [python_lane("reuse-1", "print('  3 passed (0.1s)')")]}
        manifest = {"schema": runner.MANIFEST_SCHEMA, "scenarios": [scenario]}
        runner.run_selected(manifest, [scenario], [], False, "reuse-check-1", runs_root=self.tmp)
        with self.assertRaises(ValueError):
            runner.run_selected(manifest, [scenario], [], False, "reuse-check-1", runs_root=self.tmp)

    def test_invalid_run_id_is_refused(self):
        with self.assertRaises(ValueError):
            runner.run_selected({}, [], [], False, "../escape", runs_root=self.tmp)


class ContainerPreflightTests(unittest.TestCase):
    def setUp(self):
        self.tmp = Path(tempfile.mkdtemp(prefix="demo-preflight-test-"))

    def tearDown(self):
        shutil.rmtree(self.tmp, ignore_errors=True)

    def real_server_lane(self, lane_id):
        return {"id": lane_id, "evidence_kind": "http-postgres-test-host", "cwd": ".", "executable": "dotnet",
                "args": ["test", "missing.csproj", "--no-restore", "--filter", "FullyQualifiedName~X"],
                "expected_min_tests": 1, "timeout_seconds": 60, "test_files": ["scripts/demonstration/run_scenarios.py"]}

    def test_unavailable_container_runtime_fails_real_server_lanes_without_starting_them(self):
        calls = []

        def probe():
            calls.append(1)
            return False, "daemon not running"

        scenario = {"id": "D1", "title": "t", "lanes": [self.real_server_lane("rs-1"), self.real_server_lane("rs-2")]}
        document = runner.run_selected(manifest_of(scenario), [scenario], [], False, "preflight-1", runs_root=self.tmp, probe=probe)
        self.assertEqual(calls, [1])
        self.assertEqual(document["overall"], "FAIL")
        for lane in document["scenarios"][0]["lanes"]:
            self.assertEqual(lane["status"], "FAIL")
            self.assertEqual(lane["reason"], "container runtime unavailable: daemon not running")
            self.assertIsNone(lane["exit_code"])
        self.assertFalse(list((self.tmp / "preflight-1").rglob("*.log")))

    def test_unit_only_run_never_probes_the_container_runtime(self):
        calls = []
        scenario = {"id": "D3", "title": "t", "lanes": [python_lane("unit-1", "print('  3 passed (0.1s)')")]}
        document = runner.run_selected(manifest_of(scenario), [scenario], [], False, "preflight-2", runs_root=self.tmp,
                                       probe=lambda: calls.append(1) or (False, "never"))
        self.assertEqual(calls, [])
        self.assertEqual(document["scenarios"][0]["lanes"][0]["status"], "PASS")


def scenario_of(sid, *lanes):
    return {"id": sid, "title": "t", "lanes": list(lanes)}


def manifest_of(*scenarios):
    return {"schema": runner.MANIFEST_SCHEMA, "scenarios": list(scenarios)}


class SelectionAndStatusTests(unittest.TestCase):
    def setUp(self):
        self.tmp = Path(tempfile.mkdtemp(prefix="demo-selection-test-"))
        self.passing = "print('  3 passed (0.1s)')"

    def tearDown(self):
        shutil.rmtree(self.tmp, ignore_errors=True)

    def test_unknown_lane_id_is_refused(self):
        s1 = scenario_of("D1", python_lane("a1", self.passing))
        with self.assertRaisesRegex(ValueError, "unknown lane id"):
            runner.check_selection(manifest_of(s1), [s1], ["no-such-lane"], False)

    def test_lane_of_an_unselected_scenario_is_refused(self):
        s1 = scenario_of("D1", python_lane("a1", self.passing))
        s2 = scenario_of("D2", python_lane("b1", self.passing))
        with self.assertRaisesRegex(ValueError, "not selected"):
            runner.check_selection(manifest_of(s1, s2), [s1], ["b1"], False)

    def test_empty_selection_is_refused(self):
        s1 = scenario_of("D1", python_lane("a1", self.passing) | {"optional": True})
        with self.assertRaisesRegex(ValueError, "selection is empty"):
            runner.check_selection(manifest_of(s1), [s1], [], False)

    def test_lane_only_run_over_several_scenarios_is_partial(self):
        s1 = scenario_of("D1", python_lane("a1", self.passing))
        s2 = scenario_of("D2", python_lane("b1", self.passing))
        document = runner.run_selected(manifest_of(s1, s2), [s1, s2], ["a1"], False, "partial-1",
                                       runs_root=self.tmp)
        self.assertEqual("PARTIAL", document["overall"])
        self.assertEqual(4, runner.exit_code_for(document))
        self.assertEqual("NOT_RUN", document["scenarios"][1]["status"])

    def test_one_lane_of_a_scenario_leaves_it_partial(self):
        s1 = scenario_of("D1", python_lane("a1", self.passing), python_lane("a2", self.passing))
        document = runner.run_selected(manifest_of(s1), [s1], ["a1"], False, "partial-2", runs_root=self.tmp)
        self.assertEqual("PARTIAL", document["overall"])
        self.assertEqual("PARTIAL", document["scenarios"][0]["status"])

    def test_full_passing_selection_is_lanes_pass(self):
        s1 = scenario_of("D1", python_lane("a1", self.passing))
        document = runner.run_selected(manifest_of(s1), [s1], [], False, "pass-1", runs_root=self.tmp)
        self.assertEqual("LANES_PASS", document["overall"])
        self.assertEqual(0, runner.exit_code_for(document))

    def test_failing_lane_wins_over_partial(self):
        s1 = scenario_of("D1", python_lane("f1", "import sys; sys.exit(3)"))
        s2 = scenario_of("D2", python_lane("b1", self.passing))
        document = runner.run_selected(manifest_of(s1, s2), [s1, s2], ["f1"], False, "fail-1", runs_root=self.tmp)
        self.assertEqual("FAIL", document["overall"])
        self.assertEqual(1, runner.exit_code_for(document))

    def test_exit_codes_are_documented_values(self):
        self.assertEqual(0, runner.exit_code_for({"overall": "LANES_PASS"}))
        self.assertEqual(1, runner.exit_code_for({"overall": "FAIL"}))
        self.assertEqual(2, runner.exit_code_for({"overall": "UNKNOWN"}))
        self.assertEqual(4, runner.exit_code_for({"overall": "PARTIAL"}))

    def test_main_refuses_unknown_lane_without_creating_a_run(self):
        with contextlib.redirect_stderr(io.StringIO()):
            code = runner.main(["run", "--all", "--lane", "no-such-lane", "--run-id", "refused-main-1"])
        self.assertEqual(3, code)
        self.assertFalse((runner.RUNS_ROOT / "refused-main-1").exists())

    def test_main_refuses_a_lane_outside_the_selected_scenario(self):
        with contextlib.redirect_stderr(io.StringIO()):
            code = runner.main(["run", "--scenario", "D1", "--lane", "d9-canary-cross-seller",
                                "--run-id", "refused-main-2"])
        self.assertEqual(3, code)
        self.assertFalse((runner.RUNS_ROOT / "refused-main-2").exists())


class RobustnessTests(unittest.TestCase):
    def setUp(self):
        self.tmp = Path(tempfile.mkdtemp(prefix="demo-robust-test-"))

    def tearDown(self):
        shutil.rmtree(self.tmp, ignore_errors=True)

    def test_lane_that_cannot_start_is_recorded_as_fail_and_the_result_is_written(self):
        s1 = scenario_of("D1", python_lane("os-1", "print('  3 passed (0.1s)')"))
        with mock.patch.object(runner, "hidden_run", side_effect=OSError("boom")):
            document = runner.run_selected(manifest_of(s1), [s1], [], False, "oserror-1", runs_root=self.tmp)
        lane = document["scenarios"][0]["lanes"][0]
        self.assertEqual("FAIL", lane["status"])
        self.assertIn("could not run", lane["reason"])
        self.assertEqual("FAIL", document["overall"])
        self.assertTrue((self.tmp / "oserror-1" / "result.json").is_file())

    def test_git_state_failure_is_recorded_not_raised(self):
        with mock.patch.object(runner.subprocess, "run", side_effect=subprocess.TimeoutExpired("git", 60)):
            state = runner.git_state(self.tmp)
        self.assertIsNone(state["head"])
        self.assertIn("error", state)

    @staticmethod
    def _fake_probe(exit_code, output):
        def fake(argv, cwd, out_path, err_path, timeout_seconds):
            Path(out_path).write_text(output, encoding="utf-8")
            Path(err_path).write_text("", encoding="utf-8")
            return exit_code, False, 0.0
        return fake

    def test_discover_exits_nonzero_on_a_failed_probe(self):
        s1 = scenario_of("D1", python_lane("p1", "pass"))
        with mock.patch.object(runner, "hidden_run", side_effect=self._fake_probe(1, "")), \
                mock.patch.object(runner, "REPO_ROOT", self.tmp), contextlib.redirect_stdout(io.StringIO()):
            self.assertEqual(1, runner.cmd_discover(manifest_of(s1), [s1], [], False))

    def test_discover_exits_nonzero_on_an_unparsed_count(self):
        s1 = scenario_of("D1", python_lane("p1", "pass"))
        with mock.patch.object(runner, "hidden_run", side_effect=self._fake_probe(0, "no summary here")), \
                mock.patch.object(runner, "REPO_ROOT", self.tmp), contextlib.redirect_stdout(io.StringIO()):
            self.assertEqual(1, runner.cmd_discover(manifest_of(s1), [s1], [], False))


if __name__ == "__main__":
    unittest.main()
