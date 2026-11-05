"""Validate-evidence stage: verify the whole chain from bytes and package hashes only.

Chain: pending freeze manifest -> prepared corpus + bound manifest -> candidate artifact
-> result artifact. Every link is recomputed from file bytes; the result is recomputed
from the verified inputs, so a fabricated or edited result cannot pass. Three statuses
stay separate: the run completed, the numeric claim-gate outcome, and reviewer
acceptance, which this stage never derives.
"""

from pathlib import Path

from scripts.code_analysis.contracts import canonical, digest
from scripts.code_retrieval import inputs
from scripts.pair_verification import evidence

from . import (candidates as candidates_module, guards, manifest as manifest_module, prepare, provenance,
               score as score_module)

PACKAGE_SCHEMA = "model-evaluation-evidence-package-v1"
PACKAGE_NAME = "evidence-package.json"
MAX_DOCUMENT_BYTES = 512 * 1024 * 1024
REVIEWER_ACCEPTANCE = "NOT_RECORDED"
# Owner: none. The chain documents carry no reference to withheld material, so the allowlist is empty.
# An entry maps (document, key path without list indexes) to the exact string it may hold.
SEALED_TEXT_ALLOWLIST = {}
EXPECTED_FILES = {"freeze": (manifest_module.MANIFEST_NAME,),
                  "prepare": (prepare.PREPARED_NAME, manifest_module.MANIFEST_NAME),
                  "candidates": (candidates_module.CANDIDATES_NAME,),
                  "score": (score_module.RESULT_NAME,)}


def text_references(value, path=()):
    """Yield ``(key path, text)`` for every dict key and string value; list items show as indexes."""
    if isinstance(value, dict):
        for key, item in value.items():
            yield path + (key,), key
            yield from text_references(item, path + (key,))
    elif isinstance(value, list):
        for index, item in enumerate(value):
            yield from text_references(item, path + ("[%d]" % index,))
    elif isinstance(value, str):
        yield path, value


def scan_sealed_text(documents, allowlist=SEALED_TEXT_ALLOWLIST):
    """Refuse withheld-partition text unless exactly allowlisted by document, key path and value."""
    allowed = []
    for name, document in documents.items():
        for path, text in text_references(document):
            if guards.is_generated(path, text) or not guards.has_sealed_marker(text):
                continue
            normalized = tuple(part for part in path if not part.startswith("["))
            if allowlist.get((name, normalized)) != text:
                raise ValueError("unapproved withheld-partition text in %s at %s" % (name, "/".join(path)))
            allowed.append({"document": name, "keyPath": list(normalized), "referenceOnly": True})
    return allowed


def _directory_files(directory, expected, role):
    names = sorted(entry.name for entry in Path(directory).iterdir())
    if "failed-run.json" in names:
        raise ValueError("%s run is a failed run" % role)
    missing = sorted(set(expected) - set(names))
    if missing:
        raise ValueError("%s run is missing %s" % (role, ", ".join(missing)))
    if set(names) != set(expected):
        raise ValueError("%s run holds unexpected files: %s" % (role, ", ".join(sorted(set(names) - set(expected)))))


def _load(directory, name, role):
    raw = inputs.read_bytes(Path(directory) / name, MAX_DOCUMENT_BYTES)
    if not raw:
        raise ValueError("%s document is empty" % role)
    document = inputs.parse_json(raw, role)
    if canonical(document) != raw:
        raise ValueError("%s document is not canonical" % role)
    return document, raw


def package_statuses(result_document, recomputed_gates):
    """The three independent statuses. Reviewer acceptance is a constant, never a function of the others."""
    return {"runCompleted": result_document["statuses"]["runCompleted"] is True,
            "claimGateOutcome": {name: gates["overall"] for name, gates in recomputed_gates.items()},
            "reviewerAcceptance": REVIEWER_ACCEPTANCE}


def limitations(manifest, prepared, result, registry_attempts, skipped_restricted=0):
    notes = []
    gate_outcomes = result["statuses"]["claimGateOutcome"]
    product = gate_outcomes.get("PRODUCT", {})
    notes.append("Claim-gate outcomes (PRODUCT): " + ", ".join(
        "%s=%s" % (name, value["status"]) for name, value in sorted(product.items())) + ".")
    unvalidated = sorted({name for pipeline in result["pipelines"].values()
                          for name, group in pipeline["claimGates"]["perGroup"].items()
                          if group["status"] != "VALIDATED"})
    notes.append("Groups not validated for at least one pipeline: %s." % (", ".join(unvalidated) or "none"))
    below = prepared["belowMinimum"]
    short = sorted(language for language, flags in below["languages"].items() if any(flags.values()))
    notes.append("Languages below the frozen minimum counts: %s; languages without queries: %s." % (
        ", ".join(short) or "none", ", ".join(below["missingLanguages"]) or "none"))
    notes.append("Independence of the evaluation set rests on the recorded family partitions and the frozen "
                 "excluded-source list; no owner decision is bound.")
    if registry_attempts > 1:
        notes.append("%d scoring attempts are registered for these models; earlier attempts are retained."
                     % registry_attempts)
    if skipped_restricted:
        notes.append("%d run-root entries with restricted-marked names were skipped by name and not inspected; "
                     "the count is recorded and the names are not." % skipped_restricted)
    notes.append("Reviewer acceptance is %s." % REVIEWER_ACCEPTANCE)
    return notes


def _attempt_records(registry_root, models, registration, prepared):
    """The registry record must equal the embedded registration and match the recomputed hashes."""
    key = guards.model_key(models)
    triples = guards.model_triples(models)
    directory = Path(registry_root) / key
    expected = canonical(registration)
    if not any(path.read_bytes() == expected for path in guards.attempt_files(directory)):
        raise ValueError("result registration differs from the scoring registry record")
    if (registration["corpusIdentitySha256"], registration["labelSha256"], registration["modelKeySha256"],
            registration["packageTripleSha256"], registration["stateTripleSha256"]) != (
            prepare.corpus_identity_sha256(prepared), prepare.label_sha256(prepared), key,
            triples["packageTripleSha256"], triples["stateTripleSha256"]):
        raise ValueError("registration differs from the hashes recomputed from the prepared corpus and models")
    return guards.matching_attempts(registry_root, triples["packageTripleSha256"], triples["stateTripleSha256"])


def attempt_listing(registry_root, run_root, models, registration, attempt_results, prepared):
    """Every registered attempt with its result hash and label hash. Only a sole attempt may stand alone.

    A registered attempt whose run id carries a restricted marker is refused: score refuses such run ids when
    they are given to it, so one in the registry did not come from the pipeline.
    """
    attempts = _attempt_records(registry_root, models, registration, prepared)
    listing = []
    for attempt in attempts:
        if guards.has_sealed_marker(attempt["runId"]):
            raise ValueError("a registered scoring attempt carries a restricted-marked run id; refused")
        found = Path(run_root) / attempt["runId"] / score_module.RESULT_NAME
        recorded = (attempt_results or {}).get(attempt["runId"])
        actual = digest(found.read_bytes()) if found.is_file() else None
        listing.append({"runId": attempt["runId"], "sequence": attempt["sequence"],
                        "manifestSha256": attempt["manifestSha256"], "reason": attempt["reason"],
                        "rerunOf": attempt["rerunOf"], "labelSha256": attempt["labelSha256"],
                        "corpusIdentitySha256": attempt["corpusIdentitySha256"],
                        "resultSha256": actual if actual else recorded})
    if registration["sequence"] > 1 or len(attempts) > 1:
        if set(attempt_results or ()) != {attempt["runId"] for attempt in attempts}:
            raise ValueError("evidence for a later run must list the result hash of every attempt")
        for row in listing:
            if (attempt_results[row["runId"]] != row["resultSha256"]) or row["resultSha256"] is None:
                raise ValueError("attempt result hash differs for " + row["runId"])
    return listing


def unregistered_results(run_root, registered):
    """Run directories holding a result that the registry does not list, and the restricted-named entries skipped.

    A restricted-named entry is skipped by name: it is never stat-ed or opened.
    """
    names, skipped = guards.ordinary_names(run_root)
    found = sorted(name for name in names if (Path(run_root) / name / score_module.RESULT_NAME).is_file()
                   and name not in registered)
    return found, skipped


def run(*, run_id, freeze_run_id, prepare_run_id, candidates_run_id, score_run_id, run_root=guards.RUN_ROOT,
        registry_root=guards.REGISTRY_ROOT, capture=provenance.capture, require_clean=False,
        attempt_results=None):
    ids = [run_id, freeze_run_id, prepare_run_id, candidates_run_id, score_run_id]
    guards.refuse_sealed(ids=ids)
    target = guards.run_dir(run_id, run_root)
    dirs = {"freeze": guards.run_dir(freeze_run_id, run_root), "prepare": guards.run_dir(prepare_run_id, run_root),
            "candidates": guards.run_dir(candidates_run_id, run_root), "score": guards.run_dir(score_run_id, run_root)}
    for role, directory in dirs.items():
        _directory_files(directory, EXPECTED_FILES[role], role)
    pending, pending_raw = _load(dirs["freeze"], manifest_module.MANIFEST_NAME, "freeze manifest")
    bound, bound_raw = _load(dirs["prepare"], manifest_module.MANIFEST_NAME, "bound manifest")
    prepared, prepared_raw = _load(dirs["prepare"], prepare.PREPARED_NAME, "prepared corpus")
    cands, cands_raw = _load(dirs["candidates"], candidates_module.CANDIDATES_NAME, "candidate artifact")
    result, result_raw = _load(dirs["score"], score_module.RESULT_NAME, "result artifact")
    for document in (pending, bound):
        manifest_module.validate_manifest(document)
    shas = {"pending": digest(pending_raw), "bound": digest(bound_raw), "prepared": digest(prepared_raw),
            "candidates": digest(cands_raw), "result": digest(result_raw)}
    links = [
        (pending["corpus"]["status"] == manifest_module.CORPUS_PENDING, "freeze manifest is not pending"),
        (bound["corpus"]["status"] == manifest_module.CORPUS_BOUND, "bound manifest does not bind a corpus"),
        (shas["pending"] == bound["corpus"]["identity"]["previousManifestSha256"],
         "bound manifest does not reference the freeze manifest"),
        (shas["pending"] == prepared["freezeManifestSha256"], "prepared corpus does not reference the freeze manifest"),
        (shas["prepared"] == bound["corpus"]["identity"]["corpusManifestSha256"],
         "prepared corpus differs from the bound corpus identity"),
        (cands["freezeManifestSha256"] == shas["bound"], "candidate artifact is bound to another manifest"),
        (cands["preparedCorpusSha256"] == shas["prepared"], "candidate artifact is bound to another corpus"),
        (result["freezeManifestSha256"] == shas["bound"], "result is bound to another manifest"),
        (result["preparedCorpusSha256"] == shas["prepared"], "result is bound to another corpus"),
        (result["candidatesSha256"] == shas["candidates"], "result is bound to another candidate artifact"),
        (result["runId"] == score_run_id, "result run id differs"),
        (result["registration"]["manifestSha256"] == shas["bound"], "registration is bound to another manifest"),
        (result["statuses"]["runCompleted"] is True, "result does not record a completed run"),
        (result["statuses"]["reviewerAcceptance"] == REVIEWER_ACCEPTANCE,
         "reviewer acceptance must stay NOT_RECORDED"),
    ]
    for ok, message in links:
        if not ok:
            raise ValueError(message)
    documents = {"freezeManifest": pending, "boundManifest": bound, "preparedCorpus": prepared,
                 "candidates": cands, "result": result}
    references = scan_sealed_text(documents)
    pipeline_results = score_module._compute(bound, prepared, cands)
    expected = score_module.build_outcome(
        run_id=score_run_id, manifest_sha=shas["bound"], prepared_sha=shas["prepared"],
        candidates_sha256=shas["candidates"], registration=result["registration"],
        pipeline_results=pipeline_results, code_identity=result["codeIdentity"])
    if canonical(expected) != result_raw:
        raise ValueError("result differs from its recomputation from the verified inputs")
    if candidates_module.check_identities(bound, cands["modelIdentities"]) != cands["device"]:
        raise ValueError("candidate artifact records another device than its model identities")
    score_module.check_pooling(bound, prepared, cands)
    attempts = attempt_listing(registry_root, run_root, bound["models"], result["registration"],
                               attempt_results, prepared)
    registered = guards.registered_run_ids(registry_root)
    unregistered, skipped_restricted = unregistered_results(run_root, registered)
    if unregistered:
        raise ValueError("score results exist that are not in the scoring registry: " + ", ".join(unregistered))
    identity = capture()
    if require_clean and (result["codeIdentity"]["dirty"] or cands["codeIdentity"]["dirty"]):
        raise ValueError("scoring ran on a dirty working tree")
    files = [{"role": role, "relativePath": "%s/%s" % (run, name),
              "sha256": digest(raw), "bytes": len(raw)}
             for role, run, name, raw in (
                 ("freezeManifest", freeze_run_id, manifest_module.MANIFEST_NAME, pending_raw),
                 ("boundManifest", prepare_run_id, manifest_module.MANIFEST_NAME, bound_raw),
                 ("preparedCorpus", prepare_run_id, prepare.PREPARED_NAME, prepared_raw),
                 ("candidates", candidates_run_id, candidates_module.CANDIDATES_NAME, cands_raw),
                 ("result", score_run_id, score_module.RESULT_NAME, result_raw))]
    package = {
        "schema": PACKAGE_SCHEMA, "runId": run_id,
        "chain": {"freeze": freeze_run_id, "prepare": prepare_run_id, "candidates": candidates_run_id,
                  "score": score_run_id},
        "files": files,
        "hashes": {"freezeManifestSha256": shas["pending"], "boundManifestSha256": shas["bound"],
                   "preparedCorpusSha256": shas["prepared"], "candidatesSha256": shas["candidates"],
                   "resultSha256": shas["result"]},
        "models": bound["models"],
        "statuses": package_statuses(result, {name: value["claimGates"] for name, value in pipeline_results.items()}),
        "codeIdentity": {"candidates": cands["codeIdentity"], "score": result["codeIdentity"],
                         "validation": identity,
                         "scoringAndValidationSourceMatch": result["codeIdentity"]["sourceClosureSha256"]
                         == identity["sourceClosureSha256"],
                         "cleanAtScoring": not (result["codeIdentity"]["dirty"] or cands["codeIdentity"]["dirty"])},
        "reconstruction": [
            {"stage": "freeze", "runId": freeze_run_id, "command": "freeze --run-id <id> --inputs <inputs.json>",
             "outputSha256": shas["pending"]},
            {"stage": "prepare", "runId": prepare_run_id,
             "command": "prepare --run-id <id> --manifest-run-id <freeze> --manifest-sha256 <hash> "
                        "--corpus <corpus.json>",
             "outputSha256": {"preparedCorpus": shas["prepared"], "boundManifest": shas["bound"]}},
            {"stage": "candidates", "runId": candidates_run_id,
             "command": "candidates --run-id <id> --prepare-run-id <prepare> --manifest-sha256 <hash> "
                        "--content-root <dir> --components <json>",
             "outputSha256": shas["candidates"]},
            {"stage": "score", "runId": score_run_id,
             "command": "score --run-id <id> --prepare-run-id <prepare> --candidates-run-id <candidates> "
                        "--manifest-sha256 <hash> --candidates-sha256 <hash>",
             "outputSha256": shas["result"]}],
        "withheldTextReferences": references, "scoringAttempts": attempts,
        "limitations": limitations(bound, prepared, result, len(attempts), skipped_restricted),
        "skippedRestrictedNamedRunEntries": skipped_restricted}
    evidence.validate(package)
    raw = canonical(package)
    guards.write_output(Path(target) / PACKAGE_NAME, raw, run_root)
    return {"outcome": "SUCCEEDED", "runId": run_id, "directory": target, "packageSha256": digest(raw),
            "statuses": package["statuses"], "skippedRestrictedNamedRunEntries": skipped_restricted}
