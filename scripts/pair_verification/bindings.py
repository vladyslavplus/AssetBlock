"""Read-only binding to admitted data and the frozen retrieval evidence chain.

This stage uses the CUDA interpreter for its frozen pair embeddings, but all
input and run evidence checks remain file-only. The membership record is bound
from prepare evidence by reference; its declared artifact is never opened.
"""

from pathlib import Path, PurePosixPath

from scripts.code_analysis.contracts import canonical, digest
from scripts.code_analysis.corpus import ARTIFACT_ROOT
from scripts.code_retrieval import (admission, config as config_module, evidence_package, index_verify,
                                    inputs, prepare, training, validation_plan)
from scripts.code_retrieval.paths import CORPUS_ARTIFACT_ROOT, RETRIEVAL_ROOT, run_dir
from scripts.feasibility_pilot.paths import REPO_ROOT, ensure_inside

CHAIN_KEYS = ("prepare", "train", "index", "evaluation", "database")
ALL_RUN_KEYS = (*CHAIN_KEYS, "evidence")
REQUIRED_INPUTS = (inputs.SOURCE_MANIFEST, inputs.SPLIT, inputs.EXTRACT)
MAX_CHAIN_BYTES = inputs.MAX_ARTIFACT_BYTES


def _artifact_identity(entry):
    return {"relativePath": entry.get("path"), "byteSha256": entry.get("byteSha256"),
            "canonicalSha256": entry.get("canonicalSha256")}


def _declared_identity_matches(entry, expected):
    return isinstance(expected, dict) and all(
        _artifact_identity(entry)[key] == expected[key]
        for key in ("relativePath", "byteSha256", "canonicalSha256"))


def load_without_membership(inputs_path, prepare_payload):
    target = ensure_inside(Path(inputs_path), RETRIEVAL_ROOT)
    raw = inputs.read_bytes(target, inputs.MAX_MANIFEST_BYTES)
    manifest = inputs.parse_json(raw, "inputs manifest")
    manifest_sha = digest(canonical(manifest))
    identity = prepare_payload.get("identity") or {}
    if manifest_sha != identity.get("inputsManifestSha256"):
        raise ValueError("input manifest differs from the admitted prepare run")
    expected = identity.get("inputs") or {}
    entries = manifest.get("artifacts")
    if not isinstance(entries, list):
        raise ValueError("input manifest artifact list is missing")
    if any(isinstance(row, dict) and row.get("role") == inputs.FINAL_TEST_SEAL for row in entries):
        raise ValueError("forbidden input role is declared")
    membership_entries = [row for row in entries if isinstance(row, dict)
                          and row.get("role") == inputs.FINAL_TEST_MEMBERSHIP]
    if len(membership_entries) != 1:
        raise ValueError("membership reference must have one declared input identity")
    membership_reference = expected.get(inputs.FINAL_TEST_MEMBERSHIP)
    if not _declared_identity_matches(membership_entries[0], membership_reference):
        raise ValueError("membership input identity differs from prepare evidence")
    filtered_entries = []
    seen = set()
    for row in entries:
        role = row.get("role") if isinstance(row, dict) else None
        if role == inputs.FINAL_TEST_MEMBERSHIP:
            continue
        if role in seen:
            raise ValueError("duplicate input role in manifest")
        seen.add(role)
        recorded = expected.get(role)
        if not _declared_identity_matches(row, recorded):
            raise ValueError("declared input identity differs from prepare evidence: " + str(role))
        filtered_entries.append(row)
    if seen != set(expected) - {inputs.FINAL_TEST_MEMBERSHIP}:
        raise ValueError("input role membership differs from prepare evidence")
    filtered = {**manifest, "artifacts": filtered_entries}
    loaded = inputs.load_document(filtered, manifest_sha, repo_root=REPO_ROOT,
                                  artifact_root=CORPUS_ARTIFACT_ROOT, required_roles=REQUIRED_INPUTS,
                                  check_membership=False)
    for role, row in loaded["artifacts"].items():
        if inputs.identity(loaded, role) != expected[role]:
            raise ValueError("loaded input bytes differ from prepare evidence: " + role)
    return loaded, {"manifestSha256": manifest_sha, "manifestBytes": len(raw),
                    "manifestByteSha256": digest(raw),
                    "finalTestMembershipReference": membership_reference}


def bind_predecessors(*, inputs_path, config_path, run_ids):
    if set(run_ids) != set(ALL_RUN_KEYS):
        raise ValueError("complete predecessor run identifiers are required")
    prepare_path = run_dir(run_ids["prepare"]) / index_verify.PREPARE_DATA
    prepare_raw = inputs.read_bytes(prepare_path, MAX_CHAIN_BYTES)
    prepare_binding, prepare_payload = index_verify.prepare_binding_from_bytes(prepare_raw, run_ids["prepare"])
    config_file = ensure_inside(Path(config_path), REPO_ROOT / "scripts" / "code_retrieval" / "config")
    config_loaded = config_module.load(config_file)
    if config_loaded["byteSha256"] != (prepare_payload.get("identity") or {}).get("configByteSha256"):
        raise ValueError("configuration bytes differ from the admitted prepare run")
    if config_loaded["configSha256"] != (prepare_payload.get("identity") or {}).get("configSha256"):
        raise ValueError("configuration identity differs from the admitted prepare run")
    if config_loaded["protocolSha256"] != (prepare_payload.get("identity") or {}).get("protocolSha256"):
        raise ValueError("admission protocol differs from the admitted prepare run")
    loaded, input_identity = load_without_membership(inputs_path, prepare_payload)
    identity = prepare_payload["identity"]
    for name, value in (("inputsManifestSha256", loaded["manifestSha256"]),
                        ("configSha256", config_loaded["configSha256"]),
                        ("configByteSha256", config_loaded["byteSha256"]),
                        ("protocolSha256", config_loaded["protocolSha256"])):
        if identity.get(name) != value:
            raise ValueError("prepare identity drift: " + name)
    index = prepare.reviewed_index(loaded)
    source_rows = inputs.value(loaded, inputs.SOURCE_MANIFEST)["sources"]
    recomputed = admission.evaluate(index, config_loaded["config"], sources=source_rows,
                                    sources_root=loaded["sourcesRoot"])
    triplets = admission.admitted_triplets(index, config_loaded["config"], sources=source_rows,
                                           sources_root=loaded["sourcesRoot"])
    if (recomputed["status"] != admission.ADMITTED
            or digest(canonical(recomputed)) != digest(canonical(prepare_payload.get("trainingAdmission")))):
        raise ValueError("recomputed reviewed admission differs from prepare evidence")
    if len(triplets) != prepare_binding["admittedTriplets"]:
        raise ValueError("admitted triplet denominator differs from prepare evidence")
    model = index_verify.upstream_identity()
    if config_loaded["config"]["training"]["upstreamModelRevision"] != model["modelRevision"]:
        raise ValueError("configured model revision differs from pinned identity")
    expectations = index_verify.load_training_evidence(run_dir(run_ids["train"]))
    index_verify.require_training_binding(expectations, loaded=loaded, config_loaded=config_loaded,
                                          prepare_binding=prepare_binding)
    validation_report = prepare_payload.get("validationQueries")
    validation = validation_plan.build(loaded, index, validation_report)
    corpus = index_verify.corpus_identity(loaded, config_loaded, validation, prepare_binding)
    export_raw = inputs.read_bytes(run_dir(run_ids["index"]) / index_verify.EXPORT_EVIDENCE, MAX_CHAIN_BYTES)
    export = inputs.parse_json(export_raw, "export evidence")
    index_verify.require_export_evidence(export, index_run_id=run_ids["index"], train_run_id=run_ids["train"],
                                         expectations=expectations, corpus=corpus)
    evaluation_raw = inputs.read_bytes(run_dir(run_ids["evaluation"]) / index_verify.EVALUATION_EVIDENCE,
                                       MAX_CHAIN_BYTES)
    evaluation = index_verify.verified_evaluation(evaluation_raw, train_run_id=run_ids["train"],
                                                  index_run_id=run_ids["index"],
                                                  prepare_binding=prepare_binding, corpus=corpus)
    database_root = run_dir(run_ids["database"])
    query_raw = inputs.read_bytes(database_root / index_verify.QUERY_DIR / index_verify.QUERY_MANIFEST,
                                  MAX_CHAIN_BYTES)
    query_manifest = inputs.parse_json(query_raw, "query-vector manifest")
    database_raw = inputs.read_bytes(database_root / index_verify.DB_EVIDENCE_NAME, MAX_CHAIN_BYTES)
    database = inputs.parse_json(database_raw, "database evidence")
    if database.get("outcome") != "SUCCEEDED" or database.get("runId") != run_ids["database"]:
        raise ValueError("database evidence is not a successful recorded run")
    index_verify.require_query_manifest(
        query_manifest, run_id=run_ids["database"], prepare_binding=prepare_binding, corpus=corpus,
        evidence_digests={"trainingEvidenceSha256": expectations["evidenceSha256"],
                          "exportEvidenceSha256": digest(export_raw),
                          "evaluationEvidenceSha256": digest(evaluation_raw)},
        query_counts={label: (evaluation.get("denominators", {}).get(label) or {}).get("queries")
                      for label in index_verify.MODEL_LABELS})
    query_vectors = index_verify.query_vector_files(database_root / index_verify.QUERY_DIR, query_manifest,
                                                     inputs.MAX_ARTIFACT_BYTES)
    for label in index_verify.MODEL_LABELS:
        info = (evaluation.get("denominators") or {}).get(label) or {}
        if info.get("queries") != 201:
            raise ValueError("evaluation query denominator differs from 201: " + label)
    package_path = run_dir(run_ids["evidence"]) / evidence_package.PACKAGE_NAME
    package_raw = inputs.read_bytes(package_path, MAX_CHAIN_BYTES)
    package = inputs.parse_json(package_raw, "evidence package")
    if package.get("outcome") != "SUCCEEDED" or package.get("runId") != run_ids["evidence"]:
        raise ValueError("predecessor evidence package is not successful")
    package_chain = package.get("chain") or {}
    for key in CHAIN_KEYS:
        if package_chain.get(key) != run_ids[key]:
            raise ValueError("evidence package chain identity differs: " + key)
    identities = package.get("identities") or {}
    if identities.get("agreementDetailsSha256") != database.get("agreementDetailsSha256"):
        raise ValueError("database agreement hash differs from evidence package")
    agreement_path = database_root / index_verify.DB_AGREEMENT_NAME
    agreement_raw = inputs.read_bytes(agreement_path, MAX_CHAIN_BYTES)
    if digest(agreement_raw) != identities.get("agreementDetailsSha256"):
        raise ValueError("database agreement bytes differ from evidence package")
    agreement = inputs.parse_json(agreement_raw, "database agreement")
    if agreement.get("runId") != run_ids["database"]:
        raise ValueError("database agreement run identity differs")
    verify_chain_bytes({**run_ids, "evidencePackage": package}, inputs_path, config_path)
    return {"chain": dict(run_ids), "prepareRaw": prepare_raw,
            "preparePayload": prepare_payload, "prepareBinding": prepare_binding,
            "configLoaded": config_loaded, "inputIdentity": input_identity, "loaded": loaded,
            "index": index, "triplets": triplets, "expectations": expectations,
            "validation": validation, "corpus": corpus, "export": export,
            "exportRaw": export_raw, "evaluation": evaluation, "evaluationRaw": evaluation_raw,
            "database": database, "databaseRaw": database_raw, "queryManifest": query_manifest,
            "queryRaw": query_raw, "queryVectorsRaw": query_vectors,
            "agreement": agreement, "agreementRaw": agreement_raw,
            "agreementSha256": digest(agreement_raw), "evidencePackage": package,
            "evidencePackageRaw": package_raw, "upstream": model}


def verify_chain_bytes(chain, inputs_path, config_path):
    """Use the predecessor guard without writing any evidence or materializing held-out sources."""
    runtime_relative = (chain["evidencePackage"].get("runtimeReview") or {}).get("path")
    if not isinstance(runtime_relative, str):
        raise ValueError("predecessor runtime review identity is missing")
    runtime_path = ensure_inside(REPO_ROOT / Path(*PurePosixPath(runtime_relative).parts),
                                 ARTIFACT_ROOT / "runs")
    return evidence_package.verify_chain(
        {key: chain[key] for key in CHAIN_KEYS}, inputs_path=Path(inputs_path),
        config_path=Path(config_path), runtime_review=runtime_path)
