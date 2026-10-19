"""prepare-data: verify inputs, reconcile labels, propose candidates, report blockers.

The stage is metadata-only. It never imports an ML model, instantiates an
optimizer, fetches source bytes from the network or writes a database. Outputs are
written once per run id; an existing output is an immutable collision.
"""

from scripts.code_analysis.contracts import canonical, digest
from scripts.code_analysis.corpus import write_once

from . import admission, candidates, inputs, validation
from .corpus_index import build as build_index, exclusion_counts
from .paths import RETRIEVAL_ROOT, derived_dir, run_dir

SCHEMA = "code-retrieval-prepare-data-v1"


def _seal_report(loaded, lineage):
    split = inputs.value(loaded, inputs.SPLIT)
    extract = inputs.value(loaded, inputs.EXTRACT)
    manifest = inputs.value(loaded, inputs.SOURCE_MANIFEST)
    seal = inputs.value(loaded, inputs.FINAL_TEST_SEAL)
    split_identity = inputs.identity(loaded, inputs.SPLIT)
    if seal.get("partition") != "final-test":
        raise ValueError("final-test seal names an unexpected partition")
    for flag in ("scored", "ranksInspected", "tuning"):
        if seal.get(flag) is not False:
            raise ValueError("final-test seal was " + flag)
    if seal.get("splitSha256") != split_identity["byteSha256"]:
        raise ValueError("final-test seal split binding drift")
    if seal.get("extractionSha256") != extract.get("extractionSha256"):
        raise ValueError("final-test seal extraction binding drift")
    if seal.get("rightsSha256") != split.get("rightsSha256"):
        raise ValueError("final-test seal rights binding drift")
    manifest_rights = digest(canonical({row["sourceId"]: row.get("rights", {})
                                        for row in manifest["sources"]}))
    if manifest_rights != split.get("rightsSha256"):
        raise ValueError("source manifest and split disagree on rights identity")

    final_ids = set(lineage["finalTestFileIds"])
    if seal.get("fileCount") != len(final_ids):
        raise ValueError("final-test seal file count drift")
    referenced = set(seal.get("galleryFileIds") or [])
    referenced |= set(seal.get("relevance") or {})
    referenced |= {row.get("seedFileId") for row in seal.get("seedIdentities") or []}
    referenced.discard(None)
    if referenced and not referenced <= final_ids:
        raise ValueError("final-test seal references files outside the final-test partition")
    return {"partition": seal["partition"], "fileCount": len(final_ids),
            "seedCount": seal.get("seedCount"), "scored": False, "ranksInspected": False, "tuning": False,
            "splitSha256": seal["splitSha256"], "extractionSha256": seal["extractionSha256"],
            "rightsSha256": seal["rightsSha256"],
            "finalTestMembershipSha256": lineage["finalTestMembershipSha256"]}


def _materialize_derived(index, run_id):
    materialized = []
    for fragment in index["fragments"]:
        if not fragment.get("derivation"):
            continue
        blob = admission.decode_derived(fragment)
        target = derived_dir(run_id) / (fragment["fragmentId"] + ".source")
        write_once(target, blob, RETRIEVAL_ROOT)
        materialized.append({"fragmentId": fragment["fragmentId"], "path": target.name,
                             "sha256": fragment["sha256"], "parentFragmentId": fragment["parentFragmentId"]})
    return materialized


def run(*, loaded, config_loaded, run_id):
    config = config_loaded["config"]
    source_manifest = inputs.value(loaded, inputs.SOURCE_MANIFEST)
    split = inputs.value(loaded, inputs.SPLIT)
    extract = inputs.value(loaded, inputs.EXTRACT)
    payloads = [inputs.value(loaded, role) for role in (inputs.REVIEWED_EXPORT,
                                                        inputs.INDEPENDENT_ACCEPTANCE,
                                                        inputs.VALIDATION_ACCEPTANCE,
                                                        inputs.RETRIEVAL_REVIEWED_LABELS)]
    payloads = [row for row in payloads if row is not None]
    if not payloads:
        raise ValueError("prepare-data requires at least one reviewed fragment export")

    index = build_index(source_manifest, split, extract, payloads)
    seal = _seal_report(loaded, index["lineage"])
    training = admission.evaluate(index, config, sources=source_manifest["sources"],
                                  sources_root=loaded["sourcesRoot"])
    contribution = candidates.build(index, sources=source_manifest["sources"], sources_root=loaded["sourcesRoot"],
                                    anchors_per_language=config["proposal"]["anchorsPerLanguage"],
                                    negatives_per_anchor=config["proposal"]["negativesPerAnchor"],
                                    exclude_queries=[row["queryFragmentId"] for row in index["triplets"]])
    # Only the complete reviewed retrieval-validation-corpus role can clear the
    # validation gate; the historical validation-acceptance role stays a
    # transformation record contributing to the corpus index above.
    validation_corpus = inputs.value(loaded, inputs.RETRIEVAL_VALIDATION_CORPUS)
    validation_report = validation.admit(validation_corpus, files=index["files"],
                                         fragments=index["fragments"],
                                         split_files=index["lineage"]["splitFiles"],
                                         sources=source_manifest["sources"],
                                         sources_root=loaded["sourcesRoot"],
                                         file_aliases=index.get("fileAliases"))
    validation_candidates = validation.propose(index, sources=source_manifest["sources"],
                                               sources_root=loaded["sourcesRoot"], seeds_per_language=2)
    materialized = _materialize_derived(index, run_id)

    status = training["status"]
    if status == admission.ADMITTED and validation_report["status"] != validation.ADMITTED:
        status = validation_report["status"]

    identity = {"inputsManifestSha256": loaded["manifestSha256"],
                "configSha256": config_loaded["configSha256"],
                "configByteSha256": config_loaded["byteSha256"],
                "corpusRunRoot": str(loaded["corpusRunRoot"].relative_to(loaded["artifactRoot"])).replace("\\", "/"),
                "inputs": {role: inputs.identity(loaded, role) for role in sorted(loaded["artifacts"])}}
    if "protocolSha256" in config_loaded:
        identity["protocolSha256"] = config_loaded["protocolSha256"]

    payload = {"schema": SCHEMA, "status": status, "runId": run_id, "identity": identity, "seal": seal,
               "trainingAdmission": training, "validationQueries": validation_report,
               "corpusIndex": {"files": len(index["files"]), "fragments": len(index["fragments"]),
                               "derivedFragments": len(materialized),
                               "reviewedPairs": len(index["pairs"]), "reviewedTriplets": len(index["triplets"]),
                               "pairExcluded": exclusion_counts(index["pairExcluded"])},
               "materializedDerived": materialized,
               "candidatePackage": {"proposalSha256": contribution["proposalSha256"],
                                    "proposedPairCount": contribution["proposedPairCount"],
                                    "anchorsPerLanguage": contribution["anchorsPerLanguage"],
                                    "languages": {language: {"anchorFileCount": row["anchorFileCount"],
                                                             "reviewableAnchorCount": row["reviewableAnchorCount"],
                                                             "nonIdenticalPositiveCount": row["nonIdenticalPositiveCount"]}
                                                  for language, row in contribution["languages"].items()}},
               "CanAuthorizePublication": False, "SecurityEvidence": "NOT_RUN",
               "Purpose": "CODE_RETRIEVAL_DATA_ADMISSION"}
    target = run_dir(run_id)
    write_once(target / "prepare-data.json", canonical(payload), RETRIEVAL_ROOT)
    write_once(target / "candidate-labels.json", canonical(contribution), RETRIEVAL_ROOT)
    write_once(target / "validation-candidates.json", canonical(validation_candidates), RETRIEVAL_ROOT)
    return {"payload": payload, "candidates": contribution, "validation": validation_candidates,
            "directory": target}
