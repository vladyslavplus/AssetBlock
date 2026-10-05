"""Bounded corpus intake and eligibility-gated source stages; no training."""

import argparse
import json
import re
from pathlib import Path

from scripts.feasibility_pilot.paths import ARTIFACT_ROOT as PILOT_ROOT
from scripts.feasibility_pilot.paths import ensure_inside

from .contracts import canonical, digest, report, validate_config
from .corpus import ARTIFACT_ROOT, intake, write_once
from .evidence import dependency_gaps, stage, sufficiency
from .splits import assign, normalized_hash, parser_identity

CONFIG_ROOT = Path(__file__).absolute().parent / "config"


def read(path, *roots):
    checked = ensure_inside(Path(path), *roots)
    with checked.open("rb") as stream:
        raw = stream.read(8 * 1024 * 1024 + 1)
    if len(raw) > 8 * 1024 * 1024:
        raise ValueError("manifest byte cap exceeded")
    return json.loads(raw)


def verified_split(config, root):
    split = read(root / "split.json", ARTIFACT_ROOT)
    verified = intake(config, root, offline=True)
    if verified != read(root / "intake.json", ARTIFACT_ROOT):
        raise ValueError("intake manifest drift")
    rebuilt = []
    for file in verified["files"]:
        path = ensure_inside(root / "sources" / file["sourceId"] / file["path"], ARTIFACT_ROOT)
        rebuilt.append({**file, "normalizedSha256": (None if file.get("normalizationExcludedReason") else
                        normalized_hash(path.read_bytes(), file.get("dialect", file["language"])))})
    expected = report({**assign(config, rebuilt), "configSha256": digest(canonical(config)),
                      "snapshotId": verified["snapshotId"], "rightsSha256": verified["rightsSha256"],
                      "parserIdentity": parser_identity()})
    if expected != split:
        raise ValueError("split/hash evidence drift")
    return split


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("stage", choices=("preflight", "intake", "verify-offline", "split", "extract", "derive", "export", "summarize", "materialize-labels", "token-baseline", "license-review", "embed-frozen", "index-build", "index-restore", "index-check", "evaluate", "seal-final-test", "validate-evidence"))
    parser.add_argument("--run-id", required=True)
    parser.add_argument("--config", default=str(CONFIG_ROOT / "sources.json"))
    parser.add_argument("--parent-run-id", help="Explicit frozen representation run to reuse")
    parser.add_argument("--runtime-review", help="Reviewed dependency evidence directory under artifacts/code_analysis")
    parser.add_argument("--labels", help="Hash-bound reviewed label manifest under artifacts/code_analysis")
    args = parser.parse_args(argv)
    if not re.fullmatch(r"[a-zA-Z0-9][a-zA-Z0-9_-]{0,79}", args.run_id):
        parser.error("invalid run id")
    root = ARTIFACT_ROOT / "runs" / args.run_id
    ensure_inside(root, ARTIFACT_ROOT)
    record_path = root / (args.stage + "-stage.json")
    if record_path.exists():
        parser.error("stage attempt already exists; use a new run id")
    inputs, outputs = {}, {}
    try:
        config = read(args.config, CONFIG_ROOT, ARTIFACT_ROOT)
        validate_config(config)
        inputs["configSha256"] = digest(canonical(config))
        source_manifest = root / "source-manifest.json"
        if source_manifest.exists():
            if read(source_manifest, ARTIFACT_ROOT) != config:
                raise ValueError("immutable source/rights manifest drift")
        else:
            write_once(source_manifest, canonical(config), ARTIFACT_ROOT)
        inputs["sourceManifestSha256"] = inputs["configSha256"]
        if args.stage == "preflight":
            proposal = read(CONFIG_ROOT / "index-proposal.json", CONFIG_ROOT)
            payload = {"sources": len(config["sources"]), "dependencyGaps": dependency_gaps(proposal),
                       "runtime": "stdlib intake; existing parser environment needed only for grouping"}
        elif args.stage in {"intake", "verify-offline"}:
            payload = intake(config, root, offline=args.stage == "verify-offline")
            if args.stage == "verify-offline":
                original = read(root / "intake.json", ARTIFACT_ROOT)
                if payload != original:
                    raise ValueError("offline manifest differs from immutable intake")
        elif args.stage == "split":
            inventory = read(root / "intake.json", ARTIFACT_ROOT)
            verified = intake(config, root, offline=True)
            if inventory != verified:
                raise ValueError("intake config/hash binding changed")
            inputs["intakeSha256"] = digest(canonical(inventory))
            for file in inventory["files"]:
                path = ensure_inside(root / "sources" / file["sourceId"] / file["path"], ARTIFACT_ROOT)
                file["normalizedSha256"] = (None if file.get("normalizationExcludedReason") else
                                            normalized_hash(path.read_bytes(), file.get("dialect", file["language"])))
            payload = assign(config, inventory["files"])
            payload["configSha256"] = inputs["configSha256"]
            payload["schema"] = "code-corpus-v1"
            payload["snapshotId"] = inventory["snapshotId"]
            payload["rightsSha256"] = inventory["rightsSha256"]
            payload["parserIdentity"] = parser_identity()
        elif args.stage in {"extract", "derive", "export"}:
            from .pipeline import extraction_stage, derivation_stage, export_stage
            split = verified_split(config, root)
            inputs["splitSha256"] = digest(canonical(split))
            if args.stage == "extract":
                payload = extraction_stage(config, split, root)
            else:
                extracted = read(root / "extract.json", ARTIFACT_ROOT)
                recorded = read(root / "extract-stage.json", ARTIFACT_ROOT)
                if recorded["ActualOutcome"] != "SUCCEEDED" or recorded["outputs"].get("extract.json") != digest(canonical(extracted)):
                    raise ValueError("extraction evidence hash drift")
                inputs["extractionSha256"] = digest(canonical(extracted))
                payload = derivation_stage(config, extracted, root) if args.stage == "derive" else export_stage(config, extracted)
        elif args.stage == "summarize":
            split = verified_split(config, root)
            inputs["splitSha256"] = digest(canonical(split))
            if (root / "extract.json").exists():
                extracted = read(root / "extract.json", ARTIFACT_ROOT)
                record = read(root / "extract-stage.json", ARTIFACT_ROOT)
                if record["outputs"].get("extract.json") != digest(canonical(extracted)):
                    raise ValueError("extraction evidence hash drift")
                split = {**split, "files": extracted["files"]}
            labels = config
            if (root / "materialize-labels.json").exists():
                labels = read(root / "materialize-labels.json", ARTIFACT_ROOT)
            payload = sufficiency(split, labels.get("seeds", []), labels.get("pairReviews", []))
        elif args.stage == "materialize-labels":
            from .independent_acceptance import materialize_label_manifest
            if not args.labels:
                raise ValueError("materialize-labels requires an explicit reviewed --labels manifest")
            extracted = read(root / "extract.json", ARTIFACT_ROOT)
            payload = materialize_label_manifest(extracted, read(args.labels, ARTIFACT_ROOT))
        elif args.stage == "token-baseline":
            from .baselines import exact_copy_query_id, token_corpus
            extracted = read(root / "extract.json", ARTIFACT_ROOT)
            labels = read(root / "materialize-labels.json", ARTIFACT_ROOT) if (root / "materialize-labels.json").exists() else {"seeds": []}
            files = {row["fileId"]: row for row in extracted["files"]}
            queries = []
            for seed in labels.get("seeds") or []:
                row = files.get(seed.get("seedFileId"))
                if not row or row["partition"] not in ("train", "validation"):
                    continue
                queries.append({"queryId": exact_copy_query_id(row["fileId"]), "sha256": row["sha256"],
                                "language": row["language"], "partition": row["partition"],
                                "absolutePath": str(root / "sources" / row["sourceId"] / row["path"])})
            payload = token_corpus(extracted, root / "sources", PILOT_ROOT / "runs/code-analysis-token" / args.run_id,
                                   queries=queries)
        elif args.stage == "license-review":
            from .baselines import license_stage
            expected = [file["path"] for source in config["sources"] for file in source["files"] + source["notices"]]
            payload = license_stage(root / "sources", PILOT_ROOT / "runs/code-analysis-scancode" / (args.run_id + ".json"),
                                    expected_paths=expected)
        elif args.stage == "evaluate":
            from .evaluate_run import combine_partition_evaluations, evaluate_root
            token = read(root / "token-baseline.json", ARTIFACT_ROOT)
            labels = read(root / "materialize-labels.json", ARTIFACT_ROOT) if (root / "materialize-labels.json").exists() else config
            sufficiency_payload = read(root / "summarize.json", ARTIFACT_ROOT) if (root / "summarize.json").exists() else None
            minima = bool(sufficiency_payload and sufficiency_payload.get("status") == "FILE_COUNTS_READY")
            errors = []
            parts = {}
            for partition in ("train", "validation"):
                try:
                    parts[partition] = evaluate_root(root, partition=partition, token=token,
                                                     labels=labels, corpus_minima_met=minima,
                                                     vector_root=parent_root(args.parent_run_id) if args.parent_run_id else root)
                except ValueError as exc:
                    errors.append(partition + ": " + str(exc))
            if "train" not in parts or "validation" not in parts:
                raise ValueError("evaluation missing train/validation: " + "; ".join(errors))
            payload = {**parts["validation"], "partitions": {name: value for name, value in parts.items()},
                       "partitionOmissions": errors, "corpusMinimaMet": minima,
                       "finalTestScored": False, **combine_partition_evaluations(parts)}
        elif args.stage == "seal-final-test":
            from .validator import seal_final_test
            extracted = read(root / "extract.json", ARTIFACT_ROOT)
            labels = read(root / "materialize-labels.json", ARTIFACT_ROOT)
            protocol = read(CONFIG_ROOT / "evaluation-protocol.json", CONFIG_ROOT)
            queries = sorted((root / "queries").glob("*.source")) if (root / "queries").exists() else []
            payload = seal_final_test(verified_split(config, root), labels, extracted, protocol=protocol,
                                      query_artifacts=queries, rights_sha256=extracted.get("rightsSha256"))
        elif args.stage == "validate-evidence":
            from .reuse import parent_root, verify_vector_artifacts
            from .validator import REQUIRED_STAGE_OUTPUTS, validate_run
            protocol = read(CONFIG_ROOT / "evaluation-protocol.json", CONFIG_ROOT)
            labels = read(root / "materialize-labels.json", ARTIFACT_ROOT) if (root / "materialize-labels.json").exists() else {"seeds": [], "finiteReviewedNegativePairs": 0}
            fragment_export = read(root / "fragment-export.json", ARTIFACT_ROOT) if (root / "fragment-export.json").exists() else None
            vector_root = root if (root / "representations/frozen-vectors.fp32le").exists() else parent_root(args.parent_run_id)
            stage_records = {}
            for name in REQUIRED_STAGE_OUTPUTS:
                record_file = root / (name + "-stage.json")
                if record_file.exists():
                    stage_records[name] = read(record_file, ARTIFACT_ROOT)
            from .validator import resolve_stage_outputs
            resolved, _resolve_gaps = resolve_stage_outputs(root, stage_records)
            frozen_payload = (resolved.get("frozen") or {}).get("payload")
            vector_verification = verify_vector_artifacts(vector_root, frozen_payload) if frozen_payload else None
            payload = validate_run(root, protocol=protocol, labels=labels, fragment_export=fragment_export,
                                   vector_verification=vector_verification, stage_records=stage_records)
        elif args.stage == "embed-frozen":
            from .reuse import reuse_frozen
            protocol = read(CONFIG_ROOT / "evaluation-protocol.json", CONFIG_ROOT)
            extracted = read(root / "extract.json", ARTIFACT_ROOT)
            payload = reuse_frozen(extracted, protocol, parent=parent_root(args.parent_run_id))
        elif args.stage == "index-build":
            raise ValueError("index rebuild refused; use index-check against the dedicated sandbox")
        elif args.stage == "index-restore":
            from .reuse import parent_root, restore_live_index, reuse_frozen
            from .sandbox import runtime_review_root
            protocol = read(CONFIG_ROOT / "evaluation-protocol.json", CONFIG_ROOT)
            extracted = read(root / "extract.json", ARTIFACT_ROOT)
            reuse_frozen(extracted, protocol, parent=parent_root(args.parent_run_id))
            frozen = read(parent_root(args.parent_run_id) / "frozen.json", ARTIFACT_ROOT)
            review = runtime_review_root(args.runtime_review)
            if review is None:
                raise ValueError("index restore requires current runtime approval package, supplement, wheels, and root-approval")
            payload = restore_live_index(frozen, parent=parent_root(args.parent_run_id), extract=extracted,
                                         runtime_root=Path.cwd(), runtime_review=review)
            if payload.get("status") != "READY_VERIFIED":
                raise ValueError(payload.get("reason") or "live index restore unverified")
        elif args.stage == "index-check":
            from .reuse import check_live_index, live_index_evidence, parent_root, reuse_frozen
            protocol = read(CONFIG_ROOT / "evaluation-protocol.json", CONFIG_ROOT)
            extracted = read(root / "extract.json", ARTIFACT_ROOT)
            reused = reuse_frozen(extracted, protocol, parent=parent_root(args.parent_run_id))
            frozen = read(parent_root(args.parent_run_id) / "frozen.json", ARTIFACT_ROOT)
            live = check_live_index(frozen, parent=parent_root(args.parent_run_id), extract=extracted)
            payload = live_index_evidence(live, frozen=frozen, reused=reused)
            if payload.get("status") != "READY_VERIFIED":
                raise ValueError(payload.get("reason") or "live index unverified")
        else:
            raise ValueError("unhandled stage")
        payload = report(payload)
        output = root / (args.stage + ".json")
        write_once(output, canonical(payload), ARTIFACT_ROOT)
        outputs[output.name] = digest(canonical(payload))
        write_once(record_path, canonical(stage(args.stage, "SUCCEEDED", inputs, outputs)), ARTIFACT_ROOT)
        print(json.dumps({"stage": args.stage, "output": str(output), "status": payload.get("status", "RECORDED")}))
        return 0
    except (OSError, ValueError, KeyError, TypeError, ImportError) as exc:
        write_once(record_path, canonical(stage(args.stage, "FAILED", inputs, outputs, [str(exc)])), ARTIFACT_ROOT)
        print(json.dumps({"stage": args.stage, "status": "FAILED", "error": str(exc)}))
        return 1
