"""Admitted validation plan: per-query scored galleries, query bytes and per-language galleries.

The reviewed validation corpus is admitted by validation.admit; its per-query receipts
(scoredGallery, excludedUnknownGalleryFiles) are reused, never re-derived. A query ranks
only its own scored gallery. UNKNOWN gallery files are excluded from ranking and are never
counted as false positives. Only validation-partition files are returned; a train or
final-test file in any gallery or query is refused.
"""

from scripts.code_analysis.contracts import LANGUAGES, canonical, digest

from . import admission, corpus_index, inputs, validation

QUERY_ID_DOMAIN = "retrieval-eval-query-v1"


def query_identity(query_fragment_id, query_sha256):
    return digest(canonical([QUERY_ID_DOMAIN, query_fragment_id, query_sha256]))


def require_validation_row(row, language):
    """Only validation-partition files of the scored language may enter a gallery or a query."""
    if row.get("partition") != "validation" or row.get("language") != language:
        raise ValueError("file escaped the validation partition or language: " + str(row.get("fileId")))


def require_receipt_for_seed(seed, receipt):
    """A receipt must describe its own seed, and every relevant file must sit in the scored gallery."""
    if seed["queryFragmentId"] != receipt["queryFragmentId"] or seed["querySha256"] != receipt["querySha256"]:
        raise ValueError("receipt does not follow its seed record")
    scored = sorted(receipt["scoredGallery"])
    if not set(seed["relevantFileIds"]) <= set(scored):
        raise ValueError("relevant file is outside the scored gallery")
    return scored


def require_gallery_file(row, language, held_families, held_normalized):
    """A gallery file is a validation file of the language, outside every held-out family and content."""
    require_validation_row(row, language)
    if row["familyId"] in held_families or (row["language"], row["normalizedSha256"]) in held_normalized:
        raise ValueError("gallery file overlaps a held-out family or content")


def build(loaded, index, prepare_validation_report):
    """Admit the reviewed validation corpus and return its plan, bound to the prepare run's report."""
    payload = inputs.value(loaded, inputs.RETRIEVAL_VALIDATION_CORPUS)
    sources = inputs.value(loaded, inputs.SOURCE_MANIFEST)["sources"]
    sources_root = loaded["sourcesRoot"]
    report = validation.admit(payload, files=index["files"], fragments=index["fragments"],
                              split_files=index["lineage"]["splitFiles"], sources=sources,
                              sources_root=sources_root, file_aliases=index.get("fileAliases"))
    if report["status"] != validation.ADMITTED:
        raise ValueError("validation corpus is not admitted: " + report["status"])
    if digest(canonical(report)) != digest(canonical(prepare_validation_report)):
        raise ValueError("validation admission differs from the admitted prepare run")
    files = {row["fileId"]: row for row in index["files"]}
    for alias, canonical_id in (index.get("fileAliases") or {}).items():
        if alias not in files and canonical_id in files:
            files[alias] = files[canonical_id]
    fragments = {row["fragmentId"]: row for row in index["fragments"]}
    # Validation queries are checked with the validation rule (train and final-test are held out);
    # the admission-training rule would wrongly treat the scored validation partition as held out.
    held_families, held_normalized = validation.held_out(index["lineage"]["splitFiles"])
    queries = {}
    galleries = {}
    for language in LANGUAGES:
        seeds = payload["languages"][language]["seeds"]
        receipts = report["languages"][language]["queryScoredReceipts"]
        if len(seeds) != len(receipts):
            raise ValueError("seed and receipt counts differ for " + language)
        rows = []
        gallery_ids = set()
        for seed, receipt in zip(seeds, receipts):
            scored = require_receipt_for_seed(seed, receipt)
            fragment = fragments[seed["queryFragmentId"]]
            parent, body = admission.materialize_endpoint(sources_root, files, fragments, fragment,
                                                          held_normalized, "query")
            require_validation_row(parent, language)
            if digest(body) != seed["querySha256"]:
                raise ValueError("query bytes differ from the declared query sha")
            gallery_ids |= set(scored)
            rows.append({"queryId": query_identity(seed["queryFragmentId"], seed["querySha256"]),
                         "queryFragmentId": seed["queryFragmentId"], "querySha256": seed["querySha256"],
                         "language": language, "stratum": seed["strata"], "seedGroup": seed["seedFileId"],
                         "relevantFileIds": sorted(set(seed["relevantFileIds"])), "scoredGallery": scored,
                         "excludedUnknownGalleryFiles": sorted(receipt["excludedUnknownGalleryFiles"]),
                         "endpoint": {"fragment": fragment, "parent": parent, "body": body}})
        queries[language] = rows
        gallery = []
        for file_id in sorted(gallery_ids):
            row = files[file_id]
            require_gallery_file(row, language, held_families, held_normalized)
            gallery.append({"fileId": file_id, "sha256": row["sha256"], "sourceId": row["sourceId"],
                            "path": row["path"], "bytes": row["bytes"], "language": language,
                            "row": row, "body": corpus_index.source_bytes(sources_root, row)})
        galleries[language] = gallery
    return {"report": report, "reportSha256": digest(canonical(report)), "queries": queries, "galleries": galleries}
