"""Admit reviewed pairs and freeze validation candidates with the CUDA interpreter."""

from collections import Counter
from pathlib import Path
import re
import sys

from scripts.code_analysis.contracts import LANGUAGES, canonical, digest
from scripts.code_analysis.corpus import write_once
from scripts.code_retrieval import inputs
from scripts.feasibility_pilot.paths import REPO_ROOT, ensure_inside

from . import bindings, evidence as evidence_contract, experiment, frozen_data, pair_rules, receipt, validation_candidates
from .input_layout import layout_pair_ids, window_content

PAIR_ARTIFACT_ROOT = ensure_inside(REPO_ROOT / "artifacts" / "pair_verification", REPO_ROOT / "artifacts")
PAIR_ROOT = ensure_inside(PAIR_ARTIFACT_ROOT / "runs", PAIR_ARTIFACT_ROOT)
RUN_ID = re.compile(r"[A-Za-z0-9][A-Za-z0-9_-]{0,79}")


def _write_json(path, value):
    raw = canonical(value)
    write_once(path, raw, PAIR_ARTIFACT_ROOT)
    return {"bytes": len(raw), "sha256": digest(raw)}


def _write_completion_marker(directory, evidence_sha256, *, dry_run):
    name = "DRY_RUN" if dry_run else "SUCCEEDED"
    write_once(directory / name, (evidence_sha256 + "\n").encode("ascii"), PAIR_ARTIFACT_ROOT)


def _clean_error(error):
    message = str(error).splitlines()[0] if str(error) else "preparation failed"
    message = re.sub(r"[A-Za-z]:\\[^\s:]+", "[path]", message)
    message = re.sub(r"\b(?:postgres|postgresql)://[^\s]+", "[secret]", message, flags=re.I)
    return re.sub(r"[^A-Za-z0-9 _.,;:/()'-]", "?", message)[:240]


def _make_training_rows(records, encoded, tokenizer):
    rows, truncation, counts, per_language_total = [], Counter(), Counter(), Counter()
    mode_id = tokenizer.convert_tokens_to_ids("<encoder-only>")
    for record in records:
        left, right = record["left"], record["right"]
        left_fragment = encoded["fragments"][left["fragmentId"]]
        right_fragment = encoded["fragments"][right["fragmentId"]]
        score, left_window, right_window = frozen_data.best_window_pair(
            encoded["vectors"][left["fragmentId"]], encoded["vectors"][right["fragmentId"]])
        left_token_ids = window_content(left_fragment["chunks"][left_window])
        right_token_ids = window_content(right_fragment["chunks"][right_window])
        pair_layout = layout_pair_ids(
            left_token_ids, right_token_ids, cls_id=tokenizer.cls_token_id, mode_id=mode_id,
            sep_id=tokenizer.sep_token_id)
        pair = {key: value for key, value in record.items() if key not in {"left", "right"}}
        pair.update({"left": {key: value for key, value in left.items() if key != "body"},
                     "right": {key: value for key, value in right.items() if key != "body"},
                     "windowCounts": {"left": len(encoded["vectors"][left["fragmentId"]]),
                                      "right": len(encoded["vectors"][right["fragmentId"]])},
                     "selectedWindowPair": {"left": left_window, "right": right_window,
                                             "e0Cosine": score},
                     "tokenCounts": {"leftWindow": len(left_token_ids), "rightWindow": len(right_token_ids),
                                     "leftFragment": left_fragment["originalTokenCount"],
                                     "rightFragment": right_fragment["originalTokenCount"]},
                     "layout": {"maximumTokens": pair_layout.max_tokens,
                                "contentBudget": pair_layout.content_budget,
                                "sideBudget": {"left": pair_layout.a_budget, "right": pair_layout.b_budget},
                                "framingTokens": pair_layout.framing_tokens,
                                "retainedTokens": {"left": pair_layout.a_retained_tokens,
                                                   "right": pair_layout.b_retained_tokens},
                                "truncated": pair_layout.truncated}})
        rows.append(pair)
        truncation[record["language"]] += int(pair_layout.truncated)
        per_language_total[record["language"]] += 1
        counts[(record["language"], record["kind"], record["label"])] += 1
    return rows, {"pairsPerLanguageKindLabel": {"/".join(key): value for key, value in sorted(counts.items())},
                  "truncatedPairsPerLanguage": dict(sorted(truncation.items())),
                  "truncatedSharePerLanguage": {language: truncation[language] / total
                                                 for language, total in sorted(per_language_total.items())},
                  "truncatedShareOverall": sum(truncation.values()) / len(rows) if rows else 0.0,
                  "pairCount": len(rows)}


def _candidate_package(selected, catalog):
    candidates = []
    for row in selected:
        left = catalog[row["anchorFragmentId"]]
        right = catalog[row["candidateFragmentId"]]
        candidate_id = row["candidateId"]
        def endpoint(item):
            fragment, parent = item["fragment"], item["parent"]
            body = item["body"]
            start = fragment.get("startByte", 0) if fragment.get("derivation") is None else 0
            end = fragment.get("endByte", len(body)) if fragment.get("derivation") is None else len(body)
            return {"fragmentId": fragment["fragmentId"], "fragmentSha256": fragment["sha256"],
                    "language": parent["language"], "sourcePath": parent["path"],
                    "sourceId": parent["sourceId"], "fileId": parent["fileId"],
                    "fileSha256": parent["sha256"], "familyId": parent["familyId"],
                    "span": {"startByte": start, "endByte": end},
                    "sourceText": body.decode("utf-8")}
        candidates.append({"candidateId": candidate_id, "rank": row["rank"],
                           "anchorFragmentId": row["anchorFragmentId"],
                           "left": endpoint(left), "right": endpoint(right),
                           "similarityScore": row.get("e0Cosine"), "rationale": ""})
    return {"schema": "pair-verification-mined-candidate-package-v1", "candidateMaximum": 400,
            "candidateCount": len(candidates), "candidates": candidates}


def _require_regenerated_package(prior_package, generated_raw):
    if canonical(prior_package) != generated_raw:
        raise ValueError("regenerated candidate package differs from the receipt package")


def _mined_pair(candidate, catalog, review, *, sources_root, aliases):
    left = catalog[candidate["anchorFragmentId"]]
    right = catalog[candidate["candidateFragmentId"]]
    left_identity = pair_rules.endpoint_identity({"fragment": left["fragment"], "parent": left["parent"],
                                                   "body": left["body"]}, files={}, aliases=aliases,
                                                   sources_root=sources_root)
    right_identity = pair_rules.endpoint_identity({"fragment": right["fragment"], "parent": right["parent"],
                                                    "body": right["body"]}, files={}, aliases=aliases,
                                                    sources_root=sources_root)
    pair_rules._validate_negative(left_identity, right_identity)
    pair_id = pair_rules._pair_id(left_identity, right_identity, "DISSIMILAR")
    return {"pairId": pair_id, "label": "DISSIMILAR", "kind": "MINED_REVIEWED_DISSIMILAR",
            "language": left["parent"]["language"], "left": left_identity, "right": right_identity,
            "tripletAnchorFragmentId": candidate["anchorFragmentId"], "review": review}


def run(*, run_id, inputs_path, config_path, prepare_run_id, train_run_id, index_run_id,
        evaluation_run_id, database_run_id, evidence_run_id, receipt_path=None, candidate_package_path=None,
        dry_run=False):
    if not isinstance(run_id, str) or not RUN_ID.fullmatch(run_id):
        raise ValueError("invalid run id")
    if type(dry_run) is not bool:
        raise ValueError("dry-run flag must be boolean")
    if bool(receipt_path) != bool(candidate_package_path):
        raise ValueError("receipt and prior candidate package must be supplied together")
    if dry_run and not re.fullmatch(r"dryrun-[0-9]{2}", run_id):
        raise ValueError("dry-run id must use a fresh dryrun number")
    root = PAIR_ARTIFACT_ROOT if dry_run else PAIR_ROOT
    directory = ensure_inside(root / run_id, root)
    if directory.exists():
        raise ValueError("immutable output collision")
    directory.mkdir(parents=True, exist_ok=False)
    ids = {"prepare": prepare_run_id, "train": train_run_id, "index": index_run_id,
           "evaluation": evaluation_run_id, "database": database_run_id, "evidence": evidence_run_id}
    try:
        bound = bindings.bind_predecessors(inputs_path=inputs_path, config_path=config_path, run_ids=ids)
        files = bound["index"]["files"]
        aliases = bound["index"].get("fileAliases") or {}
        reviewed, pair_exclusions = pair_rules.reviewed_pair_records(
            bound["triplets"], files=files, aliases=aliases,
            sources_root=bound["loaded"]["sourcesRoot"], split_files=bound["index"]["lineage"]["splitFiles"])
        reviewed, identical_stats = pair_rules.cap_identical_positives(reviewed)
        identical_stats["perLanguage"] = pair_rules.positive_identity_counts(reviewed)
        encoded = frozen_data.encode_train_fragments(
            bound["index"], bound["loaded"], diagnostics_path=directory / "encoder-failure-diagnostics.json")
        anchor_ids = sorted({row["tripletAnchorFragmentId"] for row in reviewed})
        mined, mining_stats = frozen_data.nearest_candidates(anchor_ids, encoded["fragments"], encoded["vectors"],
                                                             reviewed, aliases=aliases, maximum=400)
        package_value = _candidate_package(mined, encoded["fragments"])
        package_raw = canonical(package_value)
        receipt_binding = None
        mined_negatives, receipt_exclusions = [], {"noReceipt": len(mined), "ambiguous": 0, "similar": 0,
                                                   "anchorReviewCap": 0, "admitted": 0}
        if receipt_path:
            prior_raw = inputs.read_bytes(candidate_package_path, inputs.MAX_ARTIFACT_BYTES)
            prior_package = inputs.parse_json(prior_raw, "candidate package")
            _require_regenerated_package(prior_package, package_raw)
            receipt_raw = inputs.read_bytes(receipt_path, inputs.MAX_ARTIFACT_BYTES)
            receipt_binding = receipt.load_receipt(receipt_raw, candidate_package_raw=prior_raw,
                                                   candidate_package=prior_package)
            admitted, receipt_exclusions = receipt.admit_dissimilar(mined, receipt_binding)
            mined_negatives = [_mined_pair(row, encoded["fragments"], receipt_binding["labels"][row["candidateId"]],
                                           sources_root=bound["loaded"]["sourcesRoot"], aliases=aliases)
                               for row in admitted]
        training_records = []
        training_records.extend(reviewed)
        training_records.extend(mined_negatives)
        training_records, _ = pair_rules.cap_identical_positives(training_records)
        pair_ids = [row["pairId"] for row in training_records]
        if len(pair_ids) != len(set(pair_ids)):
            raise ValueError("training pair identifiers are not unique")
        training_rows, training_stats = _make_training_rows(training_records, encoded, encoded["tokenizer"])

        token_run_id = ("pairs-dryrun-" if dry_run else "pairs-token-") + run_id
        token_root = REPO_ROOT / "artifacts" / "code_analysis" / "runs" / token_run_id
        pilot_token_root = ensure_inside(REPO_ROOT / "artifacts/feasibility_pilot/runs" / token_run_id,
                                         REPO_ROOT / "artifacts/feasibility_pilot")
        if token_root.exists() or pilot_token_root.exists():
            raise ValueError("token output collision")
        candidate_artifact, _, candidate_stats = validation_candidates.build(
            bound, token_run_id=token_run_id, tokenizer=encoded["tokenizer"])
        candidate_file_hash = _write_json(directory / "validation-candidates.json", candidate_artifact)
        training_file_hash = _write_json(directory / "training-pairs.json", {
            "schema": "pair-verification-training-pairs-v1", "partition": "train",
            "pairCount": len(training_rows), "pairs": training_rows})
        package_file_hash = _write_json(directory / "pair-review-candidate-package.json", package_value)
        if package_file_hash["sha256"] != digest(package_raw):
            raise ValueError("candidate package canonical identity changed during output")
        receipt_hash = receipt_binding["sha256"] if receipt_binding else "none"
        token_evidence = candidate_stats["token"]
        config_value, config_raw, config_hash = experiment.build(
            identity={"chain": bound["chain"], "inputs": bound["inputIdentity"],
                      "evidencePackageSha256": digest(bound["evidencePackageRaw"]),
                      "prepareDataSha256": digest(bound["prepareRaw"]),
                      "trainingEvidenceSha256": bound["expectations"]["evidenceSha256"],
                      "indexExportEvidenceSha256": digest(bound["exportRaw"]),
                      "evaluationEvidenceSha256": digest(bound["evaluationRaw"]),
                      "databaseAgreementSha256": bound["agreementSha256"],
                      "upstreamModelRevision": bound["upstream"]["modelRevision"]},
            validation_identity={"candidateArtifactSha256": candidate_file_hash["sha256"],
                                 "queryCount": 201,
                                 "aggregateMetrics": candidate_artifact["aggregateMetrics"],
                                 "aggregateMetricTolerance": candidate_artifact["aggregateMetricTolerance"],
                                 "provenance": candidate_artifact["provenance"]},
            token_evidence={"status": token_evidence["status"],
                            "normalizedBoundPairCount": token_evidence["boundPairCount"],
                            "rawOutputSha256": token_evidence["rawOutputSha256"],
                            "outputDirectories": candidate_stats["outputDirectories"],
                            "provenance": "recomputed; aggregate-verified; not persisted by the earlier run"})
        config_value["candidateContract"]["semanticProvenance"] = (
            "recomputed twice; aggregate-verified; not persisted by the earlier run")
        config_raw = canonical(config_value)
        config_hash = digest(config_raw)
        config_identity = _write_json(directory / "experiment-config.json", config_value)
        if config_identity["sha256"] != config_hash:
            raise ValueError("frozen configuration hash mismatch")
        counts = Counter((row["language"], row["kind"], row["label"]) for row in training_rows)
        outcome = "DRY_RUN" if dry_run else "SUCCEEDED"
        evidence = {"schema": "pair-verification-prepare-evidence-v1", "runId": run_id,
                    "outcome": outcome, "Purpose": "LOCAL_PAIR_VERIFICATION_RESEARCH",
                    "CanAuthorizePublication": False,
                    "identities": {"chain": bound["chain"], "inputs": bound["inputIdentity"],
                                   "prepareDataSha256": digest(bound["prepareRaw"]),
                                   "configSha256": config_identity["sha256"],
                                   "candidateArtifactSha256": candidate_file_hash["sha256"],
                                   "trainingPairsSha256": training_file_hash["sha256"],
                                   "candidatePackageSha256": package_file_hash["sha256"],
                                   "receiptSha256": receipt_hash,
                                   "finalTestMembershipReference": bound["inputIdentity"]["finalTestMembershipReference"]},
                    "counts": {"admittedTriplets": len(bound["triplets"]), "reviewedPairsAfterCap": len(reviewed),
                               "trainingPairs": len(training_rows),
                               "trainingPairsPerLanguageKindLabel": {"/".join(key): value
                                                                     for key, value in sorted(counts.items())},
                               "reviewedNegativePairsPerLanguage": {
                                   language: sum(1 for row in training_rows
                                                 if row["language"] == language and row["label"] == "DISSIMILAR")
                                   for language in LANGUAGES},
                               "excludedReviewedNegativesPerLanguage": {
                                   language: sum(1 for row in pair_exclusions.get("excludedNegativePairs", [])
                                                 if row["language"] == language)
                                   for language in LANGUAGES},
                               "normalizationFailureByLanguage": {
                                   language: sum(1 for row in pair_exclusions.get("excludedNegativePairs", [])
                                                 if row["language"] == language
                                                 and "normalizationFailures" in row)
                                   for language in LANGUAGES},
                               "identicalPositiveCap": identical_stats,
                               "minedCandidates": len(mined), "minedNegativesAdmitted": len(mined_negatives),
                               "miningAnchorCoveragePerLanguage": mining_stats["perLanguage"],
                               "miningDuplicatePairsDropped": mining_stats["duplicatePairsDropped"],
                               "encodedExclusionsPerLanguage": encoded["excluded"],
                               "receiptExclusions": receipt_exclusions,
                               "candidateListsPerModel": 201,
                               "validationCandidatesPerLanguageStratum": candidate_artifact[
                                   "countsPerModelLanguageStratum"],
                               "unknownExcludedPerModel": candidate_stats["unknownExcludedPerModel"]},
                    "exclusions": pair_exclusions,
                    "truncation": {"training": training_stats,
                                   "validationWindowPairs": candidate_artifact["truncationWindowPairCount"],
                                   "structurallyZero": "Encoded windows have at most 252 content tokens; pair sides allow 254 and 253 within the 507-token budget."},
                    "candidateContract": candidate_artifact["provenance"],
                    "tokenOutputDirectories": candidate_stats["outputDirectories"],
                    "aggregateMetrics": candidate_artifact["aggregateMetrics"],
                    "environment": {"pythonVersion": sys.version.split()[0],
                                    "torchVersion": encoded["environment"]["torch"],
                                    "cudaVersion": encoded["environment"]["cudaVersion"],
                                    "deviceType": encoded["environment"]["deviceType"],
                                    "modelRevision": encoded["environment"]["upstreamRevision"],
                                    "fragmentCount": encoded["facts"]["fragmentCount"],
                                    "windowCount": encoded["facts"]["windowCount"]}}
        evidence_hash = evidence_contract.validate(evidence)
        evidence_identity = _write_json(directory / "evidence.json", evidence)
        if evidence_identity["sha256"] != evidence_hash:
            raise ValueError("evidence canonical identity changed during output")
        _write_completion_marker(directory, evidence_identity["sha256"], dry_run=dry_run)
        return {"outcome": outcome, "directory": directory, "evidenceSha256": evidence_identity["sha256"],
                "configSha256": config_identity["sha256"], "counts": evidence["counts"]}
    except Exception as exc:
        if (directory / "evidence.json").exists():
            raise RuntimeError("run failed after canonical evidence was written") from exc
        failed = {"schema": "pair-verification-prepare-evidence-v1", "runId": run_id,
                  "outcome": "DRY_RUN_FAILED" if dry_run else "FAILED",
                  "Purpose": "LOCAL_PAIR_VERIFICATION_RESEARCH",
                  "CanAuthorizePublication": False, "reason": _clean_error(exc)}
        evidence_contract.validate(failed)
        _write_json(directory / "evidence.json", failed)
        raise
