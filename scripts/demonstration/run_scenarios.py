"""Load, check and run the demonstration scenario manifest.

Commands (from the repository root):

    list                         show scenarios and lanes
    check                        validate the manifest, referenced tests and forbidden text
    discover [--scenario IDS]    count tests each lane selects (dotnet --list-tests, Playwright --list)
    run --scenario IDS|--all --run-id ID [--lane ID ...] [--include-optional] [--dry-run]

Run status: LANES_PASS means every selected lane passed. It is not scenario acceptance. A run in which some
default lane of a selected scenario was not selected is PARTIAL. A lane FAIL wins over PARTIAL, and an UNKNOWN
(counts not parseable) wins over PARTIAL.

Exit codes:
    0  LANES_PASS (check and discover OK also exit 0)
    1  FAIL: a lane failed, or check found a problem, or discover found a failed probe or an unparsed count
    2  UNKNOWN: a lane exited 0 but its counts were not parseable
    3  refused input: unknown scenario or lane id, lane outside the selected scenarios, empty selection,
       run id reuse or invalid run id
    4  PARTIAL: no lane failed, but some default lane of a selected scenario was not run

Every lane runs as a direct executable with a hidden console (CREATE_NO_WINDOW). A timed-out lane is
stopped through its own process tree only (taskkill /T on the child PID, then the child handle).
A lane that cannot start, or times out, is recorded as FAIL; the run result is still written.
"""

import argparse
import datetime
import json
import os
import re
import shutil
import subprocess
import sys
import time
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]
MANIFEST_PATH = Path(__file__).with_name("scenarios.json")
RUNS_ROOT = REPO_ROOT / "artifacts" / "demonstration" / "runs"
DOC_PATH = REPO_ROOT / "docs" / "demonstration.md"
FIXTURES_ROOT = Path(__file__).with_name("fixtures")
MANIFEST_SCHEMA = "demonstration-scenarios-v1"
RUN_SCHEMA = "demonstration-run-v1"
SCENARIO_IDS = tuple("D%d" % number for number in range(1, 10))
HTTP_TEST_HOST_KIND = "http-postgres-test-host"
POSTGRES_STORE_KIND = "postgres-store"
REAL_SERVER_KINDS = (HTTP_TEST_HOST_KIND, POSTGRES_STORE_KIND)
EVIDENCE_KINDS = (HTTP_TEST_HOST_KIND, POSTGRES_STORE_KIND, "browser-stub", "unit")
EXECUTABLES = ("dotnet", "node")
RUN_ID = re.compile(r"[A-Za-z0-9][A-Za-z0-9_-]{0,79}")
CREATE_NO_WINDOW = getattr(subprocess, "CREATE_NO_WINDOW", 0)
DOTNET_FILTER_PREFIX = "FullyQualifiedName~"
PLAYWRIGHT_CLI = "node_modules/@playwright/test/cli.js"
VITEST_CLI = "node_modules/vitest/vitest.mjs"
TIMEOUT_RANGE = (60, 7200)
SECRET_KEY = re.compile(r"(?i)(password|secret|token|api[_-]?key|credential)")
# Patterns are written so that this file does not contain the literal forbidden words itself.
FORBIDDEN_PATTERNS = (
    r"S[0]", r"S1[0]", r"course[w]ork", r"B[a]tch", r"step[ ]7", r"docs[/\\]ignored", r"sea[l]", r"final[-]test",
)
EXIT_CODES = {"LANES_PASS": 0, "FAIL": 1, "UNKNOWN": 2, "PARTIAL": 4}
DOTNET_SUMMARY = re.compile(r"(?:Passed|Failed)!\s+-\s+Failed:\s*(\d+),\s*Passed:\s*(\d+),\s*Skipped:\s*(\d+),"
                            r"\s*Total:\s*(\d+)")
DOTNET_NO_MATCH = "No test matches the given testcase filter"
DOTNET_LIST_HEADER = "The following Tests are available:"
DOTNET_LIST_ROW = re.compile(r"^    AssetBlock\..+$", re.M)
VITEST_TESTS_LINE = re.compile(r"^\s*Tests\s+(.+?)\s*\((\d+)\)\s*$", re.M)
VITEST_PART = re.compile(r"(\d+) (failed|passed|skipped)")
PLAYWRIGHT_PASSED = re.compile(r"^\s*(\d+) passed\b", re.M)
PLAYWRIGHT_FAILED = re.compile(r"^\s*(\d+) failed\b", re.M)
PLAYWRIGHT_SKIPPED = re.compile(r"^\s*(\d+) skipped\b", re.M)
PLAYWRIGHT_TOTAL = re.compile(r"Total:\s+(\d+) tests? in")


def load_manifest(path=MANIFEST_PATH):
    return json.loads(Path(path).read_text(encoding="utf-8"))


def lane_tokens(lane):
    """Test-selection tokens a lane names: dotnet filter names, Playwright grep title, Vitest -t name."""
    args = lane["args"]
    if lane["executable"] == "dotnet":
        expression = args[args.index("--filter") + 1] if "--filter" in args else ""
        return [part[len(DOTNET_FILTER_PREFIX):] if part.startswith(DOTNET_FILTER_PREFIX) else part
                for part in expression.split("|") if part]
    if "--grep" in args:
        return [args[args.index("--grep") + 1]]
    if "-t" in args:
        return [args[args.index("-t") + 1]]
    return []


def lane_argv(lane):
    """Argument vector for the lane, with the resolved executable path (no shell)."""
    executable = shutil.which(lane["executable"])
    return [executable or lane["executable"]] + list(lane["args"])


def validate_manifest(manifest, repo_root=REPO_ROOT):
    """Return a list of problems. An empty list means the manifest is valid."""
    problems = []
    if manifest.get("schema") != MANIFEST_SCHEMA:
        problems.append("schema must be %s" % MANIFEST_SCHEMA)
    scenarios = manifest.get("scenarios")
    if not isinstance(scenarios, list):
        return problems + ["scenarios must be a list"]
    ids = [scenario.get("id") for scenario in scenarios]
    if tuple(ids) != SCENARIO_IDS:
        problems.append("scenario ids must be exactly %s in order, found %s" % (", ".join(SCENARIO_IDS), ids))
    from scripts.demonstration.fixtures import FIXTURE_IDS

    lane_ids = set()
    for scenario in scenarios:
        sid = scenario.get("id", "?")
        for field in ("title", "what_it_shows", "intended_live_steps", "lanes_actually_do"):
            if not isinstance(scenario.get(field), str) or not scenario.get(field).strip():
                problems.append("%s: %s must be a non-empty string" % (sid, field))
        if not isinstance(scenario.get("gaps"), list) or not scenario.get("gaps"):
            problems.append("%s: gaps must be a non-empty list" % sid)
        for fixture in scenario.get("live_run_fixtures", []):
            if fixture not in FIXTURE_IDS:
                problems.append("%s: unknown fixture %s" % (sid, fixture))
        lanes = scenario.get("lanes")
        if not isinstance(lanes, list) or not lanes:
            problems.append("%s: lanes must be a non-empty list" % sid)
            continue
        for lane in lanes:
            problems.extend(_validate_lane(sid, lane, lane_ids, repo_root))
    text = json.dumps(manifest, ensure_ascii=False)
    problems.extend(forbidden_hits("scenarios.json", text))
    problems.extend(secret_key_hits(manifest))
    return problems


def _validate_lane(sid, lane, lane_ids, repo_root):
    problems = []
    lid = lane.get("id", "?")
    where = "%s/%s" % (sid, lid)
    if lid in lane_ids:
        problems.append("%s: duplicate lane id" % where)
    lane_ids.add(lid)
    if lane.get("evidence_kind") not in EVIDENCE_KINDS:
        problems.append("%s: evidence_kind must be one of %s" % (where, ", ".join(EVIDENCE_KINDS)))
    cwd = lane.get("cwd", "")
    if not cwd or Path(cwd).is_absolute() or not (repo_root / cwd).is_dir():
        problems.append("%s: cwd must be an existing directory relative to the repository root" % where)
    if lane.get("executable") not in EXECUTABLES:
        problems.append("%s: executable must be one of %s" % (where, ", ".join(EXECUTABLES)))
    args = lane.get("args")
    if not isinstance(args, list) or not args or not all(isinstance(item, str) for item in args):
        problems.append("%s: args must be a non-empty list of strings" % where)
        return problems
    minimum = lane.get("expected_min_tests")
    if not isinstance(minimum, int) or isinstance(minimum, bool) or minimum < 1:
        problems.append("%s: expected_min_tests must be an integer of at least 1" % where)
    timeout = lane.get("timeout_seconds")
    if not isinstance(timeout, int) or not TIMEOUT_RANGE[0] <= timeout <= TIMEOUT_RANGE[1]:
        problems.append("%s: timeout_seconds must be within %s" % (where, TIMEOUT_RANGE))
    if not isinstance(lane.get("optional", False), bool):
        problems.append("%s: optional must be a boolean" % where)
    test_files = lane.get("test_files")
    if not isinstance(test_files, list) or not test_files:
        problems.append("%s: test_files must be a non-empty list" % where)
        test_files = []
    texts = []
    for relative in test_files:
        path = repo_root / relative
        if Path(relative).is_absolute() or not path.is_file():
            problems.append("%s: test file does not exist: %s" % (where, relative))
        else:
            texts.append(path.read_text(encoding="utf-8", errors="replace"))
    problems.extend(_validate_command_shape(where, lane, repo_root))
    tokens = lane_tokens(lane) if isinstance(args, list) else []
    if not tokens:
        problems.append("%s: no test selection token in the command" % where)
    for token in tokens:
        if not any(token in text for text in texts):
            problems.append("%s: token not found in referenced test files: %s" % (where, token))
    return problems


def _validate_command_shape(where, lane, repo_root):
    problems = []
    args = lane["args"]
    cwd = repo_root / lane.get("cwd", "")
    if lane.get("executable") == "dotnet":
        if len(args) < 2 or args[0] != "test" or not args[1].endswith(".csproj"):
            problems.append("%s: dotnet args must start with test and a csproj path" % where)
        elif not (cwd / args[1]).is_file():
            problems.append("%s: csproj does not exist: %s" % (where, args[1]))
        if "--filter" not in args:
            problems.append("%s: dotnet lane needs --filter" % where)
        if "--no-restore" not in args:
            problems.append("%s: dotnet lane must pass --no-restore" % where)
    elif lane.get("executable") == "node":
        if args[0] == PLAYWRIGHT_CLI:
            if len(args) < 4 or args[1] != "test" or not args[2].endswith(".spec.ts") or "--grep" not in args:
                problems.append("%s: Playwright args must be cli test <spec> --grep <title>" % where)
        elif args[0] == VITEST_CLI:
            if len(args) < 4 or args[1] != "run" or "-t" not in args:
                problems.append("%s: Vitest args must be vitest run <file> -t <name>" % where)
        else:
            problems.append("%s: node lane must run the Playwright or Vitest CLI" % where)
    return problems


def forbidden_hits(label, text):
    hits = []
    for pattern in FORBIDDEN_PATTERNS:
        if re.search(pattern, text):
            hits.append("%s: forbidden text matches pattern %s" % (label, pattern))
    return hits


def secret_key_hits(value, path=""):
    hits = []
    if isinstance(value, dict):
        for key, item in value.items():
            if SECRET_KEY.search(key):
                hits.append("secret-like key at %s%s" % (path, key))
            hits.extend(secret_key_hits(item, path + key + "."))
    elif isinstance(value, list):
        for item in value:
            hits.extend(secret_key_hits(item, path))
    return hits


def parse_dotnet_summary(text):
    if DOTNET_NO_MATCH in text:
        return {"passed": 0, "failed": 0, "skipped": 0, "total": 0}
    matches = DOTNET_SUMMARY.findall(text)
    if not matches:
        return None
    totals = {"failed": 0, "passed": 0, "skipped": 0, "total": 0}
    for failed, passed, skipped, total in matches:
        totals["failed"] += int(failed)
        totals["passed"] += int(passed)
        totals["skipped"] += int(skipped)
        totals["total"] += int(total)
    return totals


def parse_dotnet_listing(text):
    if DOTNET_LIST_HEADER not in text:
        return None
    return len(DOTNET_LIST_ROW.findall(text.split(DOTNET_LIST_HEADER, 1)[1]))


def parse_vitest_summary(text):
    lines = VITEST_TESTS_LINE.findall(text)
    if not lines:
        return None
    body, total = lines[-1]
    counts = {"failed": 0, "passed": 0, "skipped": 0, "total": int(total)}
    for number, kind in VITEST_PART.findall(body):
        counts[kind] = int(number)
    return counts


def parse_playwright_summary(text):
    passed = PLAYWRIGHT_PASSED.findall(text)
    failed = PLAYWRIGHT_FAILED.findall(text)
    skipped = PLAYWRIGHT_SKIPPED.findall(text)
    if not passed and not failed:
        return None
    counts = {"passed": int(passed[-1]) if passed else 0, "failed": int(failed[-1]) if failed else 0,
              "skipped": int(skipped[-1]) if skipped else 0}
    counts["total"] = counts["passed"] + counts["failed"] + counts["skipped"]
    return counts


def parse_counts(lane, text):
    if lane["executable"] == "dotnet":
        return parse_dotnet_summary(text)
    if lane["args"][0] == VITEST_CLI:
        return parse_vitest_summary(text)
    return parse_playwright_summary(text)


def judge_lane(exit_code, timed_out, counts, expected_min):
    """Return (status, reason). Only exit 0 with parsed counts meeting the minimum is PASS."""
    if timed_out:
        return "FAIL", "timeout"
    if exit_code is None:
        return "FAIL", "process did not start"
    if exit_code != 0:
        return "FAIL", "native exit code %d" % exit_code
    if counts is None:
        return "UNKNOWN", "test counts not parseable from output"
    if counts["failed"] > 0:
        return "FAIL", "%d failed tests" % counts["failed"]
    if counts["passed"] < expected_min:
        return "FAIL", "passed %d below expected minimum %d" % (counts["passed"], expected_min)
    return "PASS", "ok"


def hidden_run(argv, cwd, out_path, err_path, timeout_seconds):
    """Run argv directly with a hidden console. Return (exit_code, timed_out, duration_seconds)."""
    started = time.monotonic()
    with open(out_path, "wb") as out_file, open(err_path, "wb") as err_file:
        proc = subprocess.Popen(argv, cwd=str(cwd), stdin=subprocess.DEVNULL, stdout=out_file, stderr=err_file,
                                creationflags=CREATE_NO_WINDOW)
        timed_out = False
        try:
            exit_code = proc.wait(timeout=timeout_seconds)
        except subprocess.TimeoutExpired:
            timed_out = True
            terminate_own_tree(proc)
            exit_code = proc.wait()
    return exit_code, timed_out, time.monotonic() - started


def terminate_own_tree(proc):
    """Stop the child started by this runner and its descendants. Never touches other processes."""
    taskkill = shutil.which("taskkill") if os.name == "nt" else None
    if taskkill and proc.poll() is None:
        subprocess.run([taskkill, "/PID", str(proc.pid), "/T", "/F"], stdin=subprocess.DEVNULL,
                       stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, creationflags=CREATE_NO_WINDOW,
                       check=False)
    if proc.poll() is None:
        proc.kill()


def container_runtime_probe(timeout_seconds=30):
    """Return (available, reason) from one ``docker version`` call with a hidden console."""
    docker = shutil.which("docker")
    if docker is None:
        return False, "docker executable not found"
    try:
        probe = subprocess.run([docker, "version", "--format", "{{.Server.Version}}"], stdin=subprocess.DEVNULL,
                               stdout=subprocess.PIPE, stderr=subprocess.PIPE, creationflags=CREATE_NO_WINDOW,
                               check=False, timeout=timeout_seconds)
    except subprocess.TimeoutExpired:
        return False, "docker version timed out"
    if probe.returncode != 0:
        return False, "docker version exited with code %d" % probe.returncode
    return True, probe.stdout.decode("utf-8", "replace").strip()


def unavailable_record(lane, reason):
    return {"id": lane["id"], "evidence_kind": lane["evidence_kind"], "cwd": lane["cwd"],
            "argv": lane_argv(lane), "expected_min_tests": lane["expected_min_tests"], "stdout_log": None,
            "stderr_log": None, "exit_code": None, "timed_out": False, "duration_seconds": 0.0, "counts": None,
            "status": "FAIL", "reason": "container runtime unavailable: " + reason}


def git_state(repo_root=REPO_ROOT):
    """Git HEAD and dirty-path count. Never raises: an error is recorded in the result instead."""
    git = shutil.which("git")
    if git is None:
        return {"head": None, "dirty": None, "dirtyPathCount": None, "error": "git not found"}

    def capture(arguments):
        return subprocess.run([git, *arguments], cwd=str(repo_root), stdin=subprocess.DEVNULL,
                              stdout=subprocess.PIPE, stderr=subprocess.PIPE, creationflags=CREATE_NO_WINDOW,
                              check=False, timeout=60)

    try:
        head = capture(["rev-parse", "HEAD"])
        status = capture(["status", "--porcelain"])
    except (OSError, subprocess.TimeoutExpired) as exc:
        return {"head": None, "dirty": None, "dirtyPathCount": None, "error": "git call failed: %s" % exc}
    paths = [line for line in status.stdout.decode("utf-8", "replace").splitlines() if line]
    return {"head": head.stdout.decode().strip() if head.returncode == 0 else None,
            "dirty": bool(paths), "dirtyPathCount": len(paths)}


def run_lane(lane, scenario_dir, repo_root=REPO_ROOT):
    """Execute one lane, write its logs under scenario_dir and return its result record."""
    scenario_dir = Path(scenario_dir)
    scenario_dir.mkdir(parents=True, exist_ok=True)
    out_path = scenario_dir / ("%s.out.log" % lane["id"])
    err_path = scenario_dir / ("%s.err.log" % lane["id"])
    argv = lane_argv(lane)
    record = {"id": lane["id"], "evidence_kind": lane["evidence_kind"], "cwd": lane["cwd"],
              "argv": argv, "expected_min_tests": lane["expected_min_tests"],
              "stdout_log": str(out_path), "stderr_log": str(err_path)}
    if shutil.which(lane["executable"]) is None:
        record.update({"exit_code": None, "timed_out": False, "duration_seconds": 0.0, "counts": None,
                       "status": "FAIL", "reason": "executable not found on PATH"})
        return record
    try:
        exit_code, timed_out, duration = hidden_run(argv, repo_root / lane["cwd"], out_path, err_path,
                                                    lane["timeout_seconds"])
    except (OSError, subprocess.TimeoutExpired) as exc:
        record.update({"exit_code": None, "timed_out": False, "duration_seconds": 0.0, "counts": None,
                       "status": "FAIL", "reason": "lane could not run: %s" % exc})
        return record
    text = out_path.read_text(encoding="utf-8", errors="replace")
    counts = parse_counts(lane, text)
    status, reason = judge_lane(exit_code, timed_out, counts, lane["expected_min_tests"])
    record.update({"exit_code": exit_code, "timed_out": timed_out, "duration_seconds": round(duration, 1),
                   "counts": counts, "status": status, "reason": reason})
    return record


def select_scenarios(manifest, scenario_arg, all_flag):
    scenarios = manifest["scenarios"]
    if all_flag:
        return scenarios
    wanted = [item.strip() for item in (scenario_arg or "").split(",") if item.strip()]
    unknown = [item for item in wanted if item not in SCENARIO_IDS]
    if unknown or not wanted:
        raise ValueError("unknown or missing scenario ids: %s" % (unknown or "none given"))
    return [scenario for scenario in scenarios if scenario["id"] in wanted]


def select_lanes(scenario, lane_ids, include_optional):
    chosen = []
    for lane in scenario["lanes"]:
        if lane_ids:
            if lane["id"] in lane_ids:
                chosen.append(lane)
        elif include_optional or not lane.get("optional", False):
            chosen.append(lane)
    return chosen


def check_selection(manifest, scenarios, lane_ids, include_optional):
    """Refuse lane ids that do not exist, lanes outside the selected scenarios, and an empty selection."""
    known = {lane["id"] for scenario in manifest["scenarios"] for lane in scenario["lanes"]}
    in_selection = {lane["id"] for scenario in scenarios for lane in scenario["lanes"]}
    for lane_id in lane_ids:
        if lane_id not in known:
            raise ValueError("unknown lane id: %s" % lane_id)
        if lane_id not in in_selection:
            raise ValueError("lane %s belongs to a scenario that is not selected" % lane_id)
    chosen = sum(len(select_lanes(scenario, lane_ids, include_optional)) for scenario in scenarios)
    if chosen == 0:
        raise ValueError("selection is empty: no lane would run")


def scenario_status(scenario, lane_records, selected_lanes):
    """FAIL or UNKNOWN from the records; NOT_RUN if nothing selected; PARTIAL if a default lane was left out."""
    statuses = [record["status"] for record in lane_records]
    if "FAIL" in statuses:
        return "FAIL"
    if "UNKNOWN" in statuses:
        return "UNKNOWN"
    if not selected_lanes:
        return "NOT_RUN"
    selected_ids = {lane["id"] for lane in selected_lanes}
    left_out = [lane for lane in scenario["lanes"]
                if not lane.get("optional", False) and lane["id"] not in selected_ids]
    return "PARTIAL" if left_out else "PASS"


def run_overall(scenario_statuses):
    """LANES_PASS only when every selected scenario passed in full. FAIL and UNKNOWN take precedence."""
    if "FAIL" in scenario_statuses:
        return "FAIL"
    if "UNKNOWN" in scenario_statuses:
        return "UNKNOWN"
    if any(status in ("PARTIAL", "NOT_RUN") for status in scenario_statuses):
        return "PARTIAL"
    return "LANES_PASS"


def run_selected(manifest, scenarios, lane_ids, include_optional, run_id, runs_root=RUNS_ROOT,
                 repo_root=REPO_ROOT, probe=container_runtime_probe):
    if not RUN_ID.fullmatch(run_id or ""):
        raise ValueError("run id must match %s" % RUN_ID.pattern)
    run_dir = Path(runs_root) / run_id
    if run_dir.exists():
        raise ValueError("run id already exists, refusing to reuse: %s" % run_dir)
    check_selection(manifest, scenarios, lane_ids, include_optional)
    run_dir.mkdir(parents=True)
    started = datetime.datetime.now(datetime.timezone.utc).isoformat()
    results = []
    runtime = None  # probed once, and only when a real-server lane is selected
    for scenario in scenarios:
        lanes = select_lanes(scenario, lane_ids, include_optional)
        scenario_dir = run_dir / scenario["id"]
        lane_records = []
        for lane in lanes:
            if lane["evidence_kind"] in REAL_SERVER_KINDS:
                if runtime is None:
                    runtime = probe()
                if not runtime[0]:
                    lane_records.append(unavailable_record(lane, runtime[1]))
                    continue
            lane_records.append(run_lane(lane, scenario_dir, repo_root))
        results.append({"id": scenario["id"], "title": scenario["title"],
                        "status": scenario_status(scenario, lane_records, lanes), "lanes": lane_records})
    overall = run_overall([result["status"] for result in results])
    document = {"schema": RUN_SCHEMA, "run_id": run_id, "started_utc": started,
                "finished_utc": datetime.datetime.now(datetime.timezone.utc).isoformat(),
                "git": git_state(repo_root), "manifest_sha256": manifest_digest(),
                "overall": overall, "scenarios": results}
    (run_dir / "result.json").write_text(json.dumps(document, indent=2, ensure_ascii=False) + "\n",
                                         encoding="utf-8")
    return document


def manifest_digest():
    import hashlib
    return hashlib.sha256(MANIFEST_PATH.read_bytes()).hexdigest()


def exit_code_for(document):
    return EXIT_CODES[document["overall"]]


def _print_table(rows, header):
    widths = [max(len(str(row[index])) for row in [header, *rows]) for index in range(len(header))]
    for row in [header, *rows]:
        print("  ".join(str(cell).ljust(widths[index]) for index, cell in enumerate(row)))


def cmd_list(manifest):
    rows = []
    for scenario in manifest["scenarios"]:
        for lane in scenario["lanes"]:
            rows.append((scenario["id"], lane["id"], lane["evidence_kind"], "optional" if lane.get("optional") else "default",
                         lane["expected_min_tests"], lane["cwd"], lane["executable"]))
    _print_table(rows, ("scenario", "lane", "evidence", "class", "min", "cwd", "exe"))
    return 0


def cmd_check(manifest, scenario_ids=None):
    problems = validate_manifest(manifest)
    for path in sorted(FIXTURES_ROOT.rglob("*")):
        if path.is_file() and "__pycache__" not in path.parts and path.suffix.lower() not in (".zip",):
            problems.extend(forbidden_hits(str(path.relative_to(REPO_ROOT)).replace("\\", "/"),
                                           path.read_text(encoding="utf-8", errors="replace")))
    if DOC_PATH.is_file():
        problems.extend(forbidden_hits("docs/demonstration.md", DOC_PATH.read_text(encoding="utf-8")))
    if problems:
        for problem in problems:
            print("PROBLEM " + problem)
        return 1
    lane_count = sum(len(scenario["lanes"]) for scenario in manifest["scenarios"])
    print("OK %d scenarios, %d lanes, all test files and tokens resolve" % (len(manifest["scenarios"]), lane_count))
    return 0


def cmd_discover(manifest, scenarios, lane_ids, include_optional):
    rows = []
    failed = False
    for scenario in scenarios:
        for lane in select_lanes(scenario, lane_ids, include_optional):
            argv = lane_argv(lane)
            if lane["executable"] == "dotnet":
                probe = argv + ["--list-tests"]
                parser = parse_dotnet_listing
            elif lane["args"][0] == PLAYWRIGHT_CLI:
                probe = argv + ["--list"]
                parser = lambda text: int(PLAYWRIGHT_TOTAL.search(text).group(1)) if PLAYWRIGHT_TOTAL.search(text) else None
            else:
                probe = argv
                parser = lambda text: (parse_vitest_summary(text) or {}).get("passed")
            out_path = REPO_ROOT / "artifacts" / "demonstration" / "discover" / ("%s.out.log" % lane["id"])
            err_path = out_path.with_suffix(".err.log")
            out_path.parent.mkdir(parents=True, exist_ok=True)
            try:
                exit_code, timed_out, _ = hidden_run(probe, REPO_ROOT / lane["cwd"], out_path, err_path,
                                                     lane["timeout_seconds"])
            except (OSError, subprocess.TimeoutExpired) as exc:
                exit_code, timed_out = None, False
                print("PROBE FAILED %s: %s" % (lane["id"], exc), file=sys.stderr)
            found = parser(out_path.read_text(encoding="utf-8", errors="replace")) if exit_code is not None else None
            if exit_code != 0 or found is None:
                failed = True
            rows.append((scenario["id"], lane["id"], exit_code, found, lane["expected_min_tests"]))
    _print_table(rows, ("scenario", "lane", "exit", "discovered", "manifest_min"))
    return 1 if failed else 0


def build_parser():
    parser = argparse.ArgumentParser(prog="run_scenarios")
    sub = parser.add_subparsers(dest="command", required=True)
    sub.add_parser("list")
    sub.add_parser("check")
    discover = sub.add_parser("discover")
    discover.add_argument("--scenario")
    discover.add_argument("--all", action="store_true")
    discover.add_argument("--lane", action="append", default=[])
    discover.add_argument("--include-optional", action="store_true")
    run = sub.add_parser("run")
    run.add_argument("--scenario")
    run.add_argument("--all", action="store_true")
    run.add_argument("--run-id")
    run.add_argument("--lane", action="append", default=[])
    run.add_argument("--include-optional", action="store_true")
    run.add_argument("--dry-run", action="store_true")
    return parser


def main(argv=None):
    args = build_parser().parse_args(argv)
    manifest = load_manifest()
    if args.command == "list":
        return cmd_list(manifest)
    if args.command == "check":
        return cmd_check(manifest)
    try:
        scenarios = select_scenarios(manifest, getattr(args, "scenario", None), getattr(args, "all", False))
        if args.command == "discover":
            check_selection(manifest, scenarios, args.lane, args.include_optional)
    except ValueError as exc:
        print("REFUSED %s" % exc, file=sys.stderr)
        return 3
    if args.command == "discover":
        return cmd_discover(manifest, scenarios, args.lane, args.include_optional)
    if args.dry_run:
        try:
            check_selection(manifest, scenarios, args.lane, args.include_optional)
        except ValueError as exc:
            print("REFUSED %s" % exc, file=sys.stderr)
            return 3
        for scenario in scenarios:
            for lane in select_lanes(scenario, args.lane, args.include_optional):
                print("%s %s [%s] cwd=%s" % (scenario["id"], lane["id"], lane["evidence_kind"], lane["cwd"]))
                print("  " + subprocess.list2cmdline(lane_argv(lane)))
        return 0
    try:
        document = run_selected(manifest, scenarios, args.lane, args.include_optional, args.run_id)
    except ValueError as exc:
        print("REFUSED %s" % exc, file=sys.stderr)
        return 3
    for scenario in document["scenarios"]:
        print("%s %s" % (scenario["id"], scenario["status"]))
        for lane in scenario["lanes"]:
            print("  %s %s exit=%s counts=%s (%s)" % (lane["id"], lane["status"], lane["exit_code"],
                                                       lane["counts"], lane["reason"]))
    print("overall %s; result %s" % (document["overall"], RUNS_ROOT / args.run_id / "result.json"))
    return exit_code_for(document)


if __name__ == "__main__":
    sys.exit(main())
