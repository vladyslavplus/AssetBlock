"""prepare-data: verify inputs, reconcile labels, propose candidates, report blockers.

The stage is metadata-only. It never imports an ML model, instantiates an
optimizer, fetches source bytes from the network or writes a database. Outputs are
written once per run id; an existing output is an immutable collision.
"""

from scripts.code_analysis.contracts import canonical
from scripts.code_analysis.corpus import write_once

from . import admission, candidates, inputs, validation
from .corpus_index import build as build_index, exclusion_counts
from .paths import RETRIEVAL_ROOT, derived_dir, run_dir

SCHEMA = "code-retrieval-prepare-data-v1"


def membership_summary(membership):
    """The non-relevance report fields of an authenticated final-test membership value."""
    if any(membership.get(flag) is not False for flag in ("scored", "ranksInspected", "tuning")):
        raise ValueError("final-test membership non-relevance flags must remain false")
    return {"schema": membership["schema"], "partition": membership["partition"],
            "fileCount": membership["fileCount"],
            "finalTestMembershipSha256": membership["finalTestMembershipSha256"],
            "rightsSha256": membership["rightsSha256"],
            "scored": False, "ranksInspected": False, "tuning": False}


def _membership_report(loaded, lineage):
    membership = inputs.value(loaded, inputs.FINAL_TEST_MEMBERSHIP)
    if membership.get("finalTestFileIds") != lineage["finalTestFileIds"]:
        raise ValueError("final-test membership changed after authenticated input loading")
    if membership.get("finalTestMembershipSha256") != lineage["finalTestMembershipSha256"]:
        raise ValueError("final-test membership digest drift")
    return membership_summary(membership)


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


def reviewed_index(loaded):
    """Corpus index over every reviewed payload; shared by prepare-data and training."""
    payloads = [inputs.value(loaded, role) for role in (inputs.REVIEWED_EXPORT,
                                                        inputs.INDEPENDENT_ACCEPTANCE,
                                                        inputs.VALIDATION_ACCEPTANCE,
                                                        inputs.RETRIEVAL_REVIEWED_LABELS)]
    payloads = [row for row in payloads if row is not None]
    if not payloads:
        raise ValueError("prepare-data requires at least one reviewed fragment export")
    return build_index(inputs.value(loaded, inputs.SOURCE_MANIFEST), inputs.value(loaded, inputs.SPLIT),
                       inputs.value(loaded, inputs.EXTRACT), payloads)


def run(*, loaded, config_loaded, run_id):
    config = config_loaded["config"]
    source_manifest = inputs.value(loaded, inputs.SOURCE_MANIFEST)
    index = reviewed_index(loaded)
    membership = _membership_report(loaded, index["lineage"])
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

    payload = {"schema": SCHEMA, "status": status, "runId": run_id, "identity": identity,
               "finalTestMembership": membership,
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
