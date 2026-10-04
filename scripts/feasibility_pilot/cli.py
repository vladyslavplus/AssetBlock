"""CLI for the isolated feasibility pilot. No install or training happens on import."""

from __future__ import annotations

import argparse
import json
import sys
import time
from pathlib import Path
from typing import Any

from . import PURPOSE
from .evidence import append_jsonl, stage_record, utc_now, validate_run, write_csv, write_json, write_text
from .extract import expectation_matched, extract_fixture
from .inventory import (
    build_retrieval_triplets,
    extraction_binding_error,
    load_code_fixtures,
    load_license_fixtures,
    load_manifest,
    load_pairs,
    sha256_file,
    successful_extracted_ids,
    training_allowlist,
)
from .paths import ARTIFACT_ROOT, CONFIG_ROOT, FIXTURE_ROOT, PILOT_ROOT, cache_dir, env_dir, run_dir


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(prog="feasibility-pilot")
    parser.add_argument("command")
    parser.add_argument("--config", default=str(CONFIG_ROOT / "pilot.json"))
    parser.add_argument("--run-id", required=False)
    parser.add_argument("--model", choices=["retrieval", "pair"])
    parser.add_argument("--setup", action="store_true")
    args = parser.parse_args(argv)
    if args.command != "self-check" and not args.run_id:
        print("error: --run-id is required", file=sys.stderr)
        return 2
    config = json.loads(Path(args.config).read_text(encoding="utf-8"))
    handlers = {
        "preflight": cmd_preflight,
        "extract": cmd_extract,
        "tools": cmd_tools,
        "frozen": cmd_frozen,
        "train": cmd_train,
        "reload": cmd_reload,
        "summarize": cmd_summarize,
        "validate-evidence": cmd_validate,
        "setup": cmd_setup,
    }
    if args.command not in handlers:
        print(f"unknown command {args.command}", file=sys.stderr)
        return 2
    return handlers[args.command](args, config)


def _root(run_id: str) -> Path:
    path = run_dir(run_id)
    path.mkdir(parents=True, exist_ok=True)
    return path


def _append_stage(root: Path, record: dict[str, Any]) -> None:
    path = root / "stages.json"
    current = json.loads(path.read_text(encoding="utf-8")) if path.exists() else []
    current = [item for item in current if item.get("stage") != record["stage"]]
    current.append(record)
    write_json(path, current, [ARTIFACT_ROOT])


def cmd_setup(args, config) -> int:
    from .env_setup import download_model, setup_dolos_env, setup_ml_env

    ARTIFACT_ROOT.mkdir(parents=True, exist_ok=True)
    ml = setup_ml_env()
    dolos = setup_dolos_env()
    model = download_model()
    write_json(ARTIFACT_ROOT / "setup-result.json", {"ml": ml, "dolos": dolos, "model": model, "scancode": "blocked"}, [ARTIFACT_ROOT])
    return 0 if dolos.get("outcome") == "SUCCEEDED" else 1


def cmd_preflight(args, config) -> int:
    from .env_setup import REQUESTED_DOLOS, REQUESTED_ML, REQUESTED_SCANCODE, collect_host
    from .evidence import archive_attempt
    from .dependency_approval import build_preflight_dependency_manifest, evaluate_installed_scanner

    root = _root(args.run_id)
    archive_attempt(
        root,
        "preflight-before-live-gate-status",
        ["environment.json", "dependency-manifest.json", "input-manifest.json", "stages.json"],
    )
    started = utc_now()
    t0 = time.perf_counter()
    host = collect_host()
    files = load_code_fixtures()
    licenses = load_license_fixtures()
    pairs = load_pairs()
    env = {
        "requested": {"ml": REQUESTED_ML, "dolos": REQUESTED_DOLOS, "scancode": REQUESTED_SCANCODE},
        "observedHost": host,
        "mlPython": str(env_dir("ml") / "Scripts" / "python.exe"),
        "cudaProbePending": True,
    }
    write_json(root / "environment.json", env, [ARTIFACT_ROOT])
    approval = evaluate_installed_scanner()
    write_json(
        root / "dependency-manifest.json",
        build_preflight_dependency_manifest(env["requested"], approval=approval),
        [ARTIFACT_ROOT],
    )
    write_json(
        root / "input-manifest.json",
        {
            "codeCount": len(files),
            "licensePackages": len(licenses),
            "pairCount": len(pairs),
            "manifestSha256": sha256_file(FIXTURE_ROOT / "manifest.json"),
            "pairsSha256": sha256_file(FIXTURE_ROOT / "pairs.json"),
            "configSha256": sha256_file(Path(args.config)),
        },
        [ARTIFACT_ROOT],
    )
    record = stage_record(
        "preflight",
        "SUCCEEDED",
        "COMPLETED",
        mandatory=True,
        reason="host and input identities recorded",
        fixture_scope="all",
        provenance={"config": args.config},
        started=started,
        finished=utc_now(),
        duration_s=time.perf_counter() - t0,
        error=None,
        artifacts=["environment.json", "dependency-manifest.json", "input-manifest.json"],
    )
    _append_stage(root, record)
    return 0


def cmd_extract(args, config) -> int:
    from .evidence import archive_attempt

    root = _root(args.run_id)
    archive_attempt(
        root,
        "extract-before-independent-unrelated-fixtures",
        ["inventory.json", "fragments.jsonl", "coverage.json", "extraction-cases.json", "stages.json", "input-manifest.json"],
    )
    started = utc_now()
    t0 = time.perf_counter()
    files = load_code_fixtures()
    write_json(
        root / "input-manifest.json",
        {
            "codeCount": len(files),
            "licensePackages": len(load_license_fixtures()),
            "pairCount": len(load_pairs()),
            "manifestSha256": sha256_file(FIXTURE_ROOT / "manifest.json"),
            "pairsSha256": sha256_file(FIXTURE_ROOT / "pairs.json"),
            "configSha256": sha256_file(Path(args.config)),
        },
        [ARTIFACT_ROOT],
    )
    cases = []
    fragments = []
    coverage_rows = []
    required_fail = False
    for fixture in files:
        result = extract_fixture(fixture)
        matched = expectation_matched(fixture, result)
        if fixture.required_case and not matched:
            required_fail = True
        if result.get("operationalFailure") and fixture.required_case:
            required_fail = True
        cases.append(
            {
                "fixtureId": fixture.fixture_id,
                "language": fixture.language,
                "ExpectedOutcome": fixture.expected_outcome,
                "ActualOutcome": result.get("actualOutcome"),
                "ExpectationMatched": matched,
                "RequiredCase": fixture.required_case,
                "DiagnosticOnly": fixture.diagnostic_only,
                "TrainingAllowed": fixture.training_allowed,
                "operationalFailure": result.get("operationalFailure", False),
                "diagnostics": result.get("diagnostics", []),
                "omissions": result.get("omissions", []),
                "sourceSha256": fixture.sha256,
            }
        )
        coverage_rows.append({"fixtureId": fixture.fixture_id, **result.get("coverage", {})})
        for fragment in result.get("fragments", []):
            fragments.append(fragment)
    write_json(root / "inventory.json", [{"id": f.fixture_id, "path": f.relative_path, "sha256": f.sha256, "language": f.language} for f in files], [ARTIFACT_ROOT])
    append_jsonl(root / "fragments.jsonl", fragments, [ARTIFACT_ROOT])
    write_json(root / "coverage.json", coverage_rows, [ARTIFACT_ROOT])
    write_json(root / "extraction-cases.json", cases, [ARTIFACT_ROOT])
    outcome = "FAILED" if required_fail else "SUCCEEDED"
    _append_stage(
        root,
        stage_record(
            "extract",
            outcome,
            "COMPLETED",
            mandatory=True,
            reason="required-case mismatch" if required_fail else "required extraction cases matched",
            fixture_scope="code",
            provenance={"parser": "tree-sitter"},
            started=started,
            finished=utc_now(),
            duration_s=time.perf_counter() - t0,
            error=None if not required_fail else "required extraction expectation failed",
            artifacts=["inventory.json", "fragments.jsonl", "coverage.json", "extraction-cases.json"],
        ),
    )
    return 1 if required_fail else 0


def cmd_tools(args, config) -> int:
    from .tools import dolos_reuse_allowed, evaluate_scancode_report, run_dolos_batch, run_scancode
    from .evidence import archive_attempt

    root = _root(args.run_id)
    started = utc_now()
    t0 = time.perf_counter()
    archive = archive_attempt(
        root,
        "tools-before-independent-unrelated-fixtures",
        [
            "token-results.json",
            "license-results.json",
            "tool-compatibility.json",
            "stages.json",
            "summary.md",
            "evidence-validation.json",
            "decisions.json",
            "compatibility.csv",
            "measurements.csv",
            "scancode.json",
            "input-manifest.json",
            "dependency-manifest.json",
        ],
    )
    files = load_code_fixtures()
    by_lang: dict[str, list[Path]] = {}
    for item in files:
        if item.expected_outcome != "VALID_EXTRACTED" or item.diagnostic_only:
            continue
        by_lang.setdefault(item.dialect if item.dialect == "tsx" else item.language, []).append(item.path)
    tsx_paths = [item.path for item in files if item.dialect == "tsx" and item.expected_outcome == "VALID_EXTRACTED"]
    short = [item for item in files if item.variant == "boilerplate"]
    prior_tokens = root / "token-results.json"
    token_results: list[dict[str, Any]] = []
    reuse_dolos = False
    if prior_tokens.exists():
        try:
            prior = json.loads(prior_tokens.read_text(encoding="utf-8"))
        except json.JSONDecodeError:
            prior = []
        reuse_dolos = bool(prior) and all(
            dolos_reuse_allowed(
                item,
                by_lang.get(item.get("language"), tsx_paths if item.get("language") == "tsx" else []),
            )
            if not item.get("diagnostic")
            else dolos_reuse_allowed(item, [p.path for p in short[:2]] if item.get("note", "").startswith("short-fragment") else tsx_paths)
            for item in prior
        )
        if reuse_dolos:
            token_results = prior
    matrix = []
    exit_fail = False
    if reuse_dolos:
        for item in token_results:
            matrix.append(
                {
                    "capability": "token",
                    "language": item.get("language"),
                    "outcome": item.get("outcome"),
                    "reused": True,
                    "diagnostic": item.get("diagnostic"),
                    "csvSha256": item.get("csvSha256"),
                }
            )
    else:
        token_results = []
        for language, paths in by_lang.items():
            out = root / "dolos" / language
            result = run_dolos_batch(language=language, files=paths, output_dir=out, k=23, window=17, timeout_s=300)
            token_results.append(result)
            matrix.append({"capability": "token", "language": language, "outcome": result["outcome"]})
            if result["outcome"] != "SUCCEEDED":
                exit_fail = True
        if tsx_paths:
            tsx_result = run_dolos_batch(
                language="tsx",
                files=tsx_paths,
                output_dir=root / "dolos" / "tsx",
                k=23,
                window=17,
                timeout_s=300,
            ) | {"diagnostic": True, "note": "tsx parser id; diagnostic fixtures only"}
            token_results.append(tsx_result)
            matrix.append({"capability": "token", "language": "tsx", "outcome": tsx_result["outcome"], "diagnostic": True})
            if tsx_result["outcome"] != "SUCCEEDED":
                exit_fail = True
        if short:
            token_results.append(
                run_dolos_batch(
                    language=short[0].language,
                    files=[item.path for item in short[:2]],
                    output_dir=root / "dolos" / "short-k7",
                    k=7,
                    window=5,
                    timeout_s=300,
                )
                | {"diagnostic": True, "note": "short-fragment k=7/window=5; do not merge with baseline scores"}
            )
    license_dir = FIXTURE_ROOT / "licenses"
    from .dependency_approval import evaluate_installed_scanner

    approval = evaluate_installed_scanner()
    if not approval.get("approved"):
        existing = root / "license-results.json"
        if existing.exists():
            scancode = json.loads(existing.read_text(encoding="utf-8"))
            scancode["policyCompliant"] = False
            scancode["historicalTechnicalEvidence"] = True
            scancode["newScanBlocked"] = True
            scancode["dependencyGaps"] = approval.get("gaps")
            scancode["reason"] = "historical ScanCode output retained; new scanner runs blocked until dependency delta is reviewed"
        else:
            scancode = {
                "outcome": "FAILED",
                "executionState": "SKIPPED_DEPENDENCY",
                "reason": "ScanCode install graph is not fully approved; scanner run blocked",
                "dependencyGaps": approval.get("gaps"),
            }
    else:
        scancode = run_scancode(license_dir, root / "scancode.json", timeout_s=600)
        scancode["policyCompliant"] = False
        scancode["historicalTechnicalEvidence"] = False
        scancode["newScanBlocked"] = False
        scancode["dependencyApproval"] = approval
    packages = load_license_fixtures()
    signals = evaluate_scancode_report(scancode, packages)
    scancode["fixturePackages"] = [{"id": pkg["id"], "name": pkg["name"], "expectedSignals": pkg.get("expectedSignals")} for pkg in packages]
    scancode["signalEvaluation"] = signals
    if scancode.get("outcome") == "SUCCEEDED" and not signals.get("ok"):
        scancode["outcome"] = "FAILED"
        scancode["reason"] = "required license fixture signals missing or scan errors present"
    if scancode.get("outcome") == "SUCCEEDED" and approval.get("approved"):
        scancode["policyCompliant"] = True
    write_json(root / "token-results.json", token_results, [ARTIFACT_ROOT])
    write_json(root / "license-results.json", scancode, [ARTIFACT_ROOT])
    csv_rows = []
    for item in scancode.get("files") or []:
        detections = item.get("licenseDetections") or []
        csv_rows.append(
            {
                "path": item.get("path"),
                "scanErrorCount": len(item.get("scanErrors") or []),
                "licenseDetectionCount": len(detections),
                "copyrightCount": len(item.get("copyrights") or []),
            }
        )
    write_csv(root / "license-detections.csv", csv_rows, ["path", "scanErrorCount", "licenseDetectionCount", "copyrightCount"], [ARTIFACT_ROOT])
    write_json(
        root / "tool-compatibility.json",
        matrix + [{"capability": "license", "tool": "scancode", "outcome": scancode["outcome"], "executionState": scancode.get("executionState")}],
        [ARTIFACT_ROOT],
    )
    from .env_setup import record_model_identity

    identity = record_model_identity(cache_dir() / "unixcoder-base-nine")
    write_json(root / "model-identity.json", identity, [ARTIFACT_ROOT])
    tools_failed = exit_fail or scancode.get("executionState") == "SKIPPED_DEPENDENCY" or scancode.get("outcome") != "SUCCEEDED"
    outcome = "FAILED" if tools_failed else "SUCCEEDED"
    state = "SKIPPED_DEPENDENCY" if scancode.get("executionState") == "SKIPPED_DEPENDENCY" else "COMPLETED"
    dolos_note = "reused prior SUCCEEDED Dolos CSV batches" if reuse_dolos else ("Dolos batches completed independently" if not exit_fail else "one or more Dolos batches failed")
    scancode_note = scancode.get("reason") or scancode.get("outcome")
    _append_stage(
        root,
        stage_record(
            "tools",
            outcome,
            state,
            mandatory=True,
            reason=f"{dolos_note}; ScanCode: {scancode_note}; prior reports archived at {archive.as_posix()}",
            fixture_scope="code+licenses",
            provenance={
                "dolos": "host-node + isolated @dodona/dolos 2.9.3 dist/cli.js",
                "dolosReused": reuse_dolos,
                "scancode": "isolated-prefix scancode-toolkit==32.5.0 without archive extras",
                "modelRevision": identity.get("modelRevision"),
                "gpuWeightBinding": "prior GPU stages used hash-pinned pytorch_model.bin; later model.safetensors cache is not attributed to those runs",
                "attemptArchive": str(archive),
            },
            started=started,
            finished=utc_now(),
            duration_s=time.perf_counter() - t0,
            error=None if not tools_failed else "tool evidence incomplete",
            artifacts=["token-results.json", "license-results.json", "tool-compatibility.json", "model-identity.json", "license-detections.csv", "scancode.json"],
        ),
    )
    return 1 if tools_failed else 0


def cmd_frozen(args, config) -> int:
    import torch

    from .evidence import archive_attempt
    from .frozen import run_frozen

    root = _root(args.run_id)
    archive_attempt(root, "frozen-before-independent-unrelated-fixtures", ["frozen-retrieval.json", "stages.json"])
    started = utc_now()
    t0 = time.perf_counter()
    if not torch.cuda.is_available():
        _append_stage(
            root,
            stage_record("frozen", "FAILED", "SKIPPED_DEPENDENCY", mandatory=True, reason="CUDA unavailable", fixture_scope="valid-training-subset", provenance={}, started=started, finished=utc_now(), duration_s=0, error="no cuda", artifacts=["frozen-retrieval.json"]),
        )
        write_json(root / "frozen-retrieval.json", {"outcome": "FAILED", "reason": "CUDA unavailable"}, [ARTIFACT_ROOT])
        return 1
    files = {item.fixture_id: item for item in load_code_fixtures()}
    fragments = []
    extract_path = root / "fragments.jsonl"
    if not extract_path.exists():
        write_json(root / "frozen-retrieval.json", {"outcome": "FAILED", "reason": "extract fragments missing"}, [ARTIFACT_ROOT])
        return 1
    for line in extract_path.read_text(encoding="utf-8").splitlines():
        row = json.loads(line)
        fixture = files.get(row["fixture_id"])
        if not fixture or fixture.expected_outcome != "VALID_EXTRACTED" or fixture.diagnostic_only:
            continue
        if row.get("kind") in {"module_chunk"}:
            continue
        fragments.append(
            {
                "fixtureId": fixture.fixture_id,
                "fragmentId": row["fragment_id"],
                "language": fixture.language,
                "text": row["raw_source"],
            }
        )
    model_dir = Path(config["modelLocalDir"])
    device = torch.device("cuda")
    result = run_frozen(model_dir=model_dir, fragments=fragments[:40], max_length=int(config.get("retrievalMaxLength", 256)), device=device)
    result["Purpose"] = PURPOSE
    result["CanAuthorizePublication"] = False
    result["SecurityEvidence"] = "NOT_RUN"
    write_json(root / "frozen-retrieval.json", result, [ARTIFACT_ROOT])
    ok = bool(result.get("finite") and result.get("frozenInvariant"))
    _append_stage(
        root,
        stage_record(
            "frozen",
            "SUCCEEDED" if ok else "FAILED",
            "COMPLETED",
            mandatory=True,
            reason="frozen encoder baseline",
            fixture_scope="valid fragments",
            provenance={
                "model": config.get("modelId"),
                "modelRevision": config.get("modelRevision"),
                "weightSha256": config.get("weightSha256"),
                "gpuRerun": "not required; weights hash already matched the pinned revision",
            },
            started=started,
            finished=utc_now(),
            duration_s=time.perf_counter() - t0,
            error=None if ok else "frozen baseline failed",
            artifacts=["frozen-retrieval.json"],
        ),
    )
    return 0 if ok else 1


def cmd_train(args, config) -> int:
    from .evidence import archive_attempt
    from .train import train_pair, train_retrieval

    if args.model not in {"retrieval", "pair"}:
        print("error: --model retrieval|pair", file=sys.stderr)
        return 2
    root = _root(args.run_id)
    started = utc_now()
    t0 = time.perf_counter()
    archive_attempt(
        root,
        f"train-{args.model}-before-independent-unrelated-fixtures",
        [f"train-{args.model}.json", "measurements.csv", "summary.md", "evidence-validation.json"],
    )
    files = {item.fixture_id: item for item in load_code_fixtures()}
    pairs = load_pairs()
    bind_error = extraction_binding_error(root, files.values())
    cases = []
    if (root / "extraction-cases.json").exists() and bind_error is None:
        cases = json.loads((root / "extraction-cases.json").read_text(encoding="utf-8"))
    extracted_ok = successful_extracted_ids(cases) if bind_error is None else set()
    allowed = training_allowlist(files.values(), pairs, extracted_ok)
    texts = {fid: files[fid].path.read_text(encoding="utf-8") for fid in allowed if fid in files}
    model_dir = Path(config["modelLocalDir"])
    skip_reason = bind_error
    if args.model == "retrieval":
        triplets = build_retrieval_triplets(pairs, texts, allowed, files.values())
        if skip_reason or len(triplets) < 1:
            reason = skip_reason or "not enough complete query-relative retrieval triplets"
            result = {"outcome": "FAILED", "executionState": "SKIPPED_DEPENDENCY", "reason": reason, "updates": 0, "triplets": triplets}
            write_json(root / "train-retrieval.json", result, [ARTIFACT_ROOT])
            _append_stage(
                root,
                stage_record("train-retrieval", "FAILED", "SKIPPED_DEPENDENCY", mandatory=True, reason=reason, fixture_scope="training allowlist", provenance={"model": config.get("modelId")}, started=started, finished=utc_now(), duration_s=time.perf_counter() - t0, error=reason, artifacts=["train-retrieval.json"]),
            )
            return 1
        result = train_retrieval(
            model_dir=model_dir,
            output_dir=root / "checkpoints" / "retrieval",
            triplets=[{"query": item["query"], "positive": item["positive"], "negativeA": item["negativeA"], "negativeB": item["negativeB"]} for item in triplets],
            max_length=int(config.get("retrievalMaxLength", 256)),
            last_blocks=2,
        )
        result["triplets"] = [
            {
                "language": item.get("language"),
                "queryId": item.get("queryId"),
                "positiveId": item.get("positiveId"),
                "negativeAId": item.get("negativeAId"),
                "negativeBId": item.get("negativeBId"),
                "positivePairId": item.get("positivePairId"),
                "negativeAPairId": item.get("negativeAPairId"),
                "negativeBPairId": item.get("negativeBPairId"),
                "querySha256": item.get("querySha256"),
                "positiveSha256": item.get("positiveSha256"),
                "negativeASha256": item.get("negativeASha256"),
                "negativeBSha256": item.get("negativeBSha256"),
            }
            for item in triplets
        ]
        write_json(root / "train-retrieval.json", result, [ARTIFACT_ROOT])
        stage = "train-retrieval"
        artifact = "train-retrieval.json"
    else:
        labeled = []
        for pair in pairs:
            if not pair.training_allowed or pair.diagnostic_only:
                continue
            if pair.left_id not in texts or pair.right_id not in texts:
                continue
            labeled.append(
                {
                    "left": texts[pair.left_id],
                    "right": texts[pair.right_id],
                    "label": 1 if pair.label == "positive" else 0,
                    "altRight": next(
                        (texts[fid] for fid, item in files.items() if item.variant in {"unrelated", "unrelated2"} and fid in texts and fid not in {pair.left_id, pair.right_id}),
                        None,
                    ),
                }
            )
        labeled = [item for item in labeled if item["altRight"]]
        if skip_reason or len(labeled) < 2:
            reason = skip_reason or "not enough extracted VALID_EXTRACTED pairs"
            result = {"outcome": "FAILED", "executionState": "SKIPPED_DEPENDENCY", "reason": reason, "updates": 0}
            write_json(root / "train-pair.json", result, [ARTIFACT_ROOT])
            _append_stage(
                root,
                stage_record("train-pair", "FAILED", "SKIPPED_DEPENDENCY", mandatory=True, reason=reason, fixture_scope="training allowlist", provenance={"model": config.get("modelId")}, started=started, finished=utc_now(), duration_s=time.perf_counter() - t0, error=reason, artifacts=["train-pair.json"]),
            )
            return 1
        result = train_pair(
            model_dir=model_dir,
            output_dir=root / "checkpoints" / "pair",
            pairs=labeled,
            max_length=int(config.get("pairMaxLength", 512)),
            last_blocks=2,
        )
        write_json(root / "train-pair.json", result, [ARTIFACT_ROOT])
        stage = "train-pair"
        artifact = "train-pair.json"
    ok = result.get("outcome") == "SUCCEEDED" and int(result.get("updates") or 0) >= 5
    gpu = result.get("gpu") or {}
    _append_stage(
        root,
        stage_record(
            stage,
            "SUCCEEDED" if ok else "FAILED",
            "COMPLETED",
            mandatory=True,
            reason=result.get("architecture", ""),
            fixture_scope="training allowlist intersected with VALID_EXTRACTED",
            provenance={
                "model": config.get("modelId"),
                "modelRevision": config.get("modelRevision"),
                "weightSha256": config.get("weightSha256"),
                "trainingPeakBytes": (gpu.get("training") or {}).get("maxAllocatedBytes"),
                "probeIsNotTrainingPeak": True,
            },
            started=started,
            finished=utc_now(),
            duration_s=float((result.get("timings") or {}).get("trainingSeconds") or (time.perf_counter() - t0)),
            error=None if ok else "training feasibility failed",
            artifacts=[artifact],
        ),
    )
    return 0 if ok else 1


def cmd_reload(args, config) -> int:
    from .evidence import archive_attempt
    from .train import reload_in_fresh_process

    root = _root(args.run_id)
    started = utc_now()
    t0 = time.perf_counter()
    kind = args.model
    archive_attempt(root, f"reload-{kind}-before-independent-unrelated-fixtures", [f"reload-{kind}.json", "measurements.csv", "summary.md", "evidence-validation.json"])
    ckpt = root / "checkpoints" / kind / "checkpoint"
    ref = root / "checkpoints" / kind / "eval-reference.json"
    py = env_dir("ml") / "Scripts" / "python.exe"
    result = reload_in_fresh_process(kind=kind, checkpoint=ckpt, reference=ref, model_dir=Path(config["modelLocalDir"]), max_length=int(config.get("retrievalMaxLength" if kind == "retrieval" else "pairMaxLength", 256)), python_exe=py)
    name = f"reload-{kind}.json"
    write_json(root / name, result, [ARTIFACT_ROOT])
    ok = bool(result.get("matched") and result.get("finite") and not result.get("fallbackUsed"))
    _append_stage(
        root,
        stage_record(f"reload-{kind}", "SUCCEEDED" if ok else "FAILED", "COMPLETED", mandatory=True, reason="fresh-process reload", fixture_scope="checkpoint", provenance={}, started=started, finished=utc_now(), duration_s=time.perf_counter() - t0, error=None if ok else "reload mismatch", artifacts=[name]),
    )
    return 0 if ok else 1


def cmd_summarize(args, config) -> int:
    from .evidence import archive_attempt, evaluate_dependency_gate

    root = _root(args.run_id)
    archive_attempt(
        root,
        "summarize-before-live-gate-status",
        ["summary.md", "decisions.json", "compatibility.csv", "measurements.csv", "evidence-validation.json"],
    )
    stages = json.loads((root / "stages.json").read_text(encoding="utf-8")) if (root / "stages.json").exists() else []
    by_stage = {item["stage"]: item for item in stages}
    approval = evaluate_dependency_gate(root, by_stage)
    lines = [
        "# Feasibility pilot summary",
        "",
        f"RunId: {args.run_id}",
        f"Purpose: {PURPOSE}",
        "CanAuthorizePublication: false",
        "SecurityEvidence: NOT_RUN",
        "",
        "## Dependency approval",
        "pnpm deps:check is not evidence for the isolated pip/ScanCode graph.",
        f"Current scanner environment ready: {approval.get('environmentReady')}",
        f"Scanner attempt policy-compliant: {approval.get('scannerAttemptPolicyCompliant')}",
        f"Dependency completion approved: {approval.get('approved')}",
        f"Unapproved or unknown packages: {len(approval.get('gaps') or [])}",
        "Dolos CSV and GPU stages are reused when inputs, model hashes, and implementation are unchanged.",
        "",
        "## Stages",
    ]
    for item in stages:
        lines.append(f"- {item['stage']}: {item['outcome']} ({item['executionState']}) — {item.get('reason')}")
    write_text(root / "summary.md", "\n".join(lines) + "\n", [ARTIFACT_ROOT])
    write_csv(root / "compatibility.csv", [{"stage": i["stage"], "outcome": i["outcome"], "state": i["executionState"]} for i in stages], ["stage", "outcome", "state"], [ARTIFACT_ROOT])
    write_csv(root / "measurements.csv", [{"stage": i["stage"], "durationSeconds": i.get("durationSeconds")} for i in stages], ["stage", "durationSeconds"], [ARTIFACT_ROOT])
    write_json(
        root / "decisions.json",
        {
            "Purpose": PURPOSE,
            "CanAuthorizePublication": False,
            "SecurityEvidence": "NOT_RUN",
            "dependencyApproval": approval,
            "policyCompliantCompletion": bool(approval.get("approved")),
            "stages": stages,
        },
        [ARTIFACT_ROOT],
    )
    _append_stage(
        root,
        stage_record("summarize", "SUCCEEDED", "COMPLETED", mandatory=True, reason="summary written", fixture_scope="run", provenance={}, started=utc_now(), finished=utc_now(), duration_s=0, error=None, artifacts=["summary.md", "compatibility.csv", "measurements.csv", "decisions.json"]),
    )
    return 0


def cmd_validate(args, config) -> int:
    from .evidence import archive_attempt, validate_run
    from .paths import ARTIFACT_ROOT

    root = _root(args.run_id)
    archive_attempt(
        root,
        "prior-validation",
        [
            "evidence-validation.json",
            "summary.md",
            "decisions.json",
            "compatibility.csv",
            "measurements.csv",
            "dependency-manifest.json",
        ],
    )
    result = validate_run(args.run_id)
    write_json(root / "evidence-validation.json", result, [ARTIFACT_ROOT])
    write_json(root / "dependency-approval.json", result.get("dependencyApproval") or {}, [ARTIFACT_ROOT])
    print(json.dumps({"status": result["status"], "runId": args.run_id, "dependencyApproved": (result.get("dependencyApproval") or {}).get("approved")}, indent=2))
    return 0 if result["status"] == "READY_FOR_REVIEW" else 1


def _triplets(pairs, texts, allowed) -> list[dict[str, str]]:
    from .inventory import build_retrieval_triplets

    return build_retrieval_triplets(pairs, texts, allowed)


if __name__ == "__main__":
    raise SystemExit(main())
