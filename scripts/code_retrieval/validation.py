"""Fail-closed validation-query admission and bounded candidate preparation.

A validation corpus is admitted only from independently reviewed, hash-bound
seed / relevance / gallery / distractor / per-file evidence records that
satisfy the planned per-language floors, file-level review coverage and
held-out separation. Each seed is bound to its unique original source file and
exact source bytes, declares a nonempty relevant target set inside the eligible
same-language gallery, and carries query-relative evidence that covers every
compared file at file level. A transformation acceptance, an empty object or
any self-consistent JSON cannot clear this gate. Until such a corpus is
genuinely reviewed, the stage reports a concrete blocker and proposes bounded
candidate records for review instead of fabricating relevance.
"""

from datetime import date

from scripts.code_analysis.contracts import LANGUAGES, allowed, canonical, digest

from . import admission
from .corpus_index import eligible_fragment, fragment_bytes

SCHEMA = "code-retrieval-validation-corpus-v1"
CANDIDATE_SCHEMA = "code-retrieval-validation-candidates-v1"
REVIEWED_STATUS = "INDEPENDENTLY_REVIEWED"
CANDIDATE_STATUS = "CANDIDATE_FOR_INDEPENDENT_REVIEW"
ADMITTED = "REVIEWED_VALIDATION_CORPUS_PRESENT"
BLOCKER = "BLOCKED_VALIDATION_QUERY_CORPUS_UNAVAILABLE"

# Planned validation floors. TSX coverage has no cap: it is recorded
# explicitly in the report together with strata omissions and dialect limits.
MIN_SEEDS_PER_LANGUAGE = 20
MIN_GALLERY_FILES = 50
MIN_DISTRACTORS_PER_SEED = 40
SCORED_K = 20
VALIDATION_PURPOSE = "evaluationAllowed"
HELD_OUT = ("train", "final-test")
STRATA = ("EXACT_COPY", "COMMENT_FORMAT", "AST_BOUND_RENAME")
EVIDENCE_LABELS = ("RELEVANT", "DISSIMILAR")
WHOLE_FILE = "WHOLE_FILE"

# A derived variant is bound to the version the shared transformations
# module actually emits; declared stratum names alone never manufacture a
# version identity.
STRATA_VERSIONS = {"COMMENT_FORMAT": "comment-format-v1", "AST_BOUND_RENAME": "ast-rename-v4"}
# Issues that disqualify a derived query from counting as a valid,
# independently reviewed nonidentical variant for its canonical seed file.
DERIVED_VARIANT_GATE = frozenset({
    "SEED_UNREVIEWED", "STRATUM_MISMATCH_DERIVED", "STRATUM_DERIVATION_MISMATCH",
    "STRATUM_BYTES_MISMATCH", "DERIVED_OUTPUT_DIGEST_MISSING", "DERIVED_VERSION_UNBOUND",
    "DERIVED_PARENT_UNBOUND", "DERIVED_PARENT_BYTES_UNRESOLVABLE",
    "DERIVED_OUTPUT_IDENTICAL_TO_ORIGINAL", "SEED_FILE_UNBOUND", "SEED_FAMILY_UNBOUND",
    "SEED_SOURCE_BYTES_UNBOUND", "SEED_PARENT_INELIGIBLE", "SEED_PARENT_HELD_OUT_FAMILY",
    "SEED_PARENT_HELD_OUT_CONTENT", "QUERY_FRAGMENT_INELIGIBLE"})


def _valid_date(value):
    if not isinstance(value, str):
        return False
    try:
        date.fromisoformat(value)
        return True
    except ValueError:
        return False


def validation_eligible_file(file, source):
    return (file.get("partition") == "validation"
            and file.get("extraction") == "VALID_EXTRACTED"
            and file.get("searchableExecutable") is True
            and file.get("assignmentReviewed") is True
            and allowed(source.get("rights", {}), VALIDATION_PURPOSE, source, file)
            and not file.get("generated") and not file.get("vendor"))


def _blocker(reason, **extra):
    payload = {"status": BLOCKER, "reason": reason}
    payload.update(extra)
    return payload


def _held_out(split_files):
    """Held-out families and normalized content of train/final-test partitions."""
    rows = list(split_files.values())
    families = {row["familyId"] for row in rows if row["partition"] in HELD_OUT}
    normalized = {(row["language"], row.get("normalizedSha256")) for row in rows
                  if row["partition"] in HELD_OUT and row.get("normalizedSha256")}
    return families, normalized


def _tsx_coverage(gallery_files, files):
    """Explicit TSX coverage of the declared gallery; no cap, only a record."""
    tsx = sorted(file_id for file_id in gallery_files
                 if str(files.get(file_id, {}).get("path", "")).lower().endswith(".tsx"))
    return {"galleryTsxFileCount": len(tsx), "exampleFileIds": tsx[:3]}


def _empty_seed_report(issues):
    return {"issues": set(issues), "seedFileId": None, "distractors": 0,
            "galleryFiles": set(), "dialects": set(),
            "scoredGallery": [], "excludedUnknownGalleryFiles": []}


def _check_seed(seed, *, language, files, fragments, sources, sources_root,
                held_families, held_normalized):
    """Validate one declared seed record and return its concrete report."""
    issues = set()
    if not isinstance(seed, dict):
        return _empty_seed_report({"SEED_RECORD_INVALID"})
    query = fragments.get(seed.get("queryFragmentId"))
    if query is None or query.get("sha256") != seed.get("querySha256"):
        return _empty_seed_report({"QUERY_UNBOUND"})
    if seed.get("strata") not in STRATA:
        issues.add("INVALID_STRATA")
    parent = files.get(query.get("fileId"))
    if parent is None or parent.get("language") != language:
        return _empty_seed_report(issues | {"SEED_PARENT_NOT_BOUND"})
    report = _empty_seed_report(issues)
    report["seedFileId"] = parent["fileId"]
    report["dialects"] = {parent.get("dialect", language)}

    # Canonical original-file binding: the seed names its file, family and exact
    # source bytes, and every declared identity must match the authoritative row.
    if seed.get("seedFileId") != parent["fileId"]:
        issues.add("SEED_FILE_UNBOUND")
    if seed.get("familyId") != parent["familyId"]:
        issues.add("SEED_FAMILY_UNBOUND")
    if seed.get("querySourceSha256") != parent.get("sha256"):
        issues.add("SEED_SOURCE_BYTES_UNBOUND")
    if seed.get("reviewStatus") != REVIEWED_STATUS or not seed.get("reviewer"):
        issues.add("SEED_UNREVIEWED")

    # Bind the declared stratum to the actual query identity and derivation.
    # Variants bind output digest, version, and parent to the actual derivative,
    # and identical output is judged against the parent FRAGMENT's own bytes,
    # never the whole source file.
    nonidentical_variant = False
    deriv = query.get("derivation") if isinstance(query, dict) else None
    declared_strata = seed.get("strata")
    if deriv:
        dtrans = deriv.get("transformation") if isinstance(deriv, dict) else None
        if declared_strata == "EXACT_COPY":
            issues.add("STRATUM_MISMATCH_DERIVED")
        elif declared_strata == "COMMENT_FORMAT" and dtrans != "COMMENT_FORMAT":
            issues.add("STRATUM_DERIVATION_MISMATCH")
        elif declared_strata == "AST_BOUND_RENAME" and dtrans != "AST_BOUND_RENAME":
            issues.add("STRATUM_DERIVATION_MISMATCH")
        out_sha = deriv.get("outputSha256") if isinstance(deriv, dict) else None
        if not out_sha:
            issues.add("DERIVED_OUTPUT_DIGEST_MISSING")
        elif query.get("sha256") != out_sha:
            issues.add("STRATUM_BYTES_MISMATCH")
        if isinstance(deriv, dict) and deriv.get("version") != STRATA_VERSIONS.get(declared_strata):
            issues.add("DERIVED_VERSION_UNBOUND")
        parent_fragment = fragments.get(query.get("parentFragmentId"))
        if (parent_fragment is None or parent_fragment.get("derivation")
                or parent_fragment.get("fileId") != query.get("fileId")):
            issues.add("DERIVED_PARENT_UNBOUND")
        else:
            try:
                original = fragment_bytes(sources_root, parent, parent_fragment)
            except ValueError:
                issues.add("DERIVED_PARENT_BYTES_UNRESOLVABLE")
            else:
                if digest(original) == query.get("sha256"):
                    issues.add("DERIVED_OUTPUT_IDENTICAL_TO_ORIGINAL")
    else:
        if declared_strata != "EXACT_COPY":
            issues.add("STRATUM_MISMATCH_ORIGINAL")
    if not validation_eligible_file(parent, sources.get(parent["sourceId"], {})):
        issues.add("SEED_PARENT_INELIGIBLE")
    if parent["familyId"] in held_families:
        issues.add("SEED_PARENT_HELD_OUT_FAMILY")
    if (parent.get("language"), parent.get("normalizedSha256")) in held_normalized:
        issues.add("SEED_PARENT_HELD_OUT_CONTENT")
    if not eligible_fragment(query):
        issues.add("QUERY_FRAGMENT_INELIGIBLE")
    try:
        admission.materialize_endpoint(sources_root, files, fragments, query,
                                       held_normalized, "query")
    except admission.Blocked as exc:
        issues.add("QUERY_BYTES_UNRESOLVABLE"
                   if exc.reason == admission.ENDPOINT_UNRESOLVABLE else exc.reason)
    # A derived query counts as a valid, independently reviewed
    # nonidentical variant only if it clears every identity, eligibility,
    # stratum/derivation-binding and byte-materialization check above.
    if deriv:
        nonidentical_variant = ("SEED_UNREVIEWED" not in issues
                                and not (issues & DERIVED_VARIANT_GATE)
                                and "QUERY_BYTES_UNRESOLVABLE" not in issues
                                and admission.DERIVED_UNVERIFIED not in issues)

    gallery_ids = set()
    for file_id in seed.get("galleryFileIds") or []:
        row = files.get(file_id)
        if row is None:
            issues.add("GALLERY_FILE_UNRESOLVED")
            continue
        gallery_ids.add(row["fileId"])
        if row.get("partition") != "validation" or row["familyId"] in held_families:
            issues.add("GALLERY_HELD_OUT")
        if row.get("language") != language:
            issues.add("GALLERY_LANGUAGE_MISMATCH")
        if not validation_eligible_file(row, sources.get(row["sourceId"], {})):
            issues.add("GALLERY_FILE_INELIGIBLE")
        if (row.get("language"), row.get("normalizedSha256")) in held_normalized:
            issues.add("GALLERY_HELD_OUT_CONTENT")
    if len(gallery_ids) < MIN_GALLERY_FILES or len(gallery_ids) <= SCORED_K:
        issues.add("GALLERY_BELOW_FLOOR")
    report["galleryFiles"] = gallery_ids

    # Every seed has a nonempty relevant target set inside the declared gallery,
    # and the query's own source file is always one of the relevant targets.
    relevant_ids = set()
    for file_id in seed.get("relevantFileIds") or []:
        row = files.get(file_id)
        if row is None:
            issues.add("RELEVANT_TARGET_UNRESOLVED")
            continue
        if row["fileId"] in relevant_ids:
            issues.add("DUPLICATE_RELEVANT_TARGET")
        relevant_ids.add(row["fileId"])
        if row["fileId"] not in gallery_ids:
            issues.add("RELEVANT_TARGET_OUTSIDE_GALLERY")
        if row.get("language") != language or row.get("partition") != "validation":
            issues.add("RELEVANT_TARGET_INELIGIBLE")
    if not relevant_ids:
        issues.add("NO_RELEVANT_TARGET")
    if parent["fileId"] not in relevant_ids:
        issues.add("SOURCE_TARGET_MISSING")

    # Query-relative distractors: distinct reviewed fragments inside the gallery,
    # same language, canonical-distinct parents, materialized from source bytes.
    seen_parents, seen_hashes = set(), set()
    for distractor in seed.get("distractors") or []:
        if not isinstance(distractor, dict):
            issues.add("DISTRACTOR_RECORD_INVALID")
            continue
        fragment = fragments.get(distractor.get("fragmentId"))
        if (fragment is None or fragment.get("sha256") != distractor.get("fragmentSha256")
                or distractor.get("label") != "DISSIMILAR"
                or distractor.get("leftFragmentId") != seed.get("queryFragmentId")):
            issues.add("DISTRACTOR_RECORD_INVALID")
            continue
        if distractor.get("reviewStatus") != REVIEWED_STATUS or not distractor.get("reviewer"):
            issues.add("DISTRACTOR_UNREVIEWED")
        dparent = files.get(fragment.get("fileId"))
        if dparent is None:
            issues.add("DISTRACTOR_PARENT_UNRESOLVED")
            continue
        if dparent["fileId"] not in gallery_ids:
            issues.add("DISTRACTOR_OFF_GALLERY")
        if dparent.get("language") != language:
            issues.add("DISTRACTOR_LANGUAGE_MISMATCH")
        if dparent.get("partition") != "validation" or dparent["familyId"] in held_families:
            issues.add("DISTRACTOR_HELD_OUT")
        if not validation_eligible_file(dparent, sources.get(dparent["sourceId"], {})):
            issues.add("DISTRACTOR_PARENT_INELIGIBLE")
        if dparent["fileId"] in relevant_ids:
            issues.add("DISTRACTOR_IS_RELEVANT")
        if fragment.get("sha256") == query.get("sha256"):
            issues.add("DISTRACTOR_IDENTICAL_TO_QUERY")
        if not eligible_fragment(fragment):
            issues.add("DISTRACTOR_FRAGMENT_INELIGIBLE")
        if dparent["fileId"] in seen_parents or fragment["sha256"] in seen_hashes:
            issues.add("DISTRACTOR_NOT_DISTINCT")
        seen_parents.add(dparent["fileId"])
        seen_hashes.add(fragment["sha256"])
        try:
            fragment_bytes(sources_root, dparent, fragment)
        except ValueError:
            issues.add("DISTRACTOR_BYTES_UNRESOLVABLE")
    if len(seen_parents) < MIN_DISTRACTORS_PER_SEED:
        issues.add("DISTRACTORS_BELOW_FLOOR")
    report["distractors"] = len(seen_parents)

    # File-level evidence: every compared file carries one reviewed record that
    # covers all of the file's indexed original fragments, so a single-fragment
    # label can never stand in for a file-level judgment.
    evidence_by_file = {}
    for row in seed.get("fileEvidence") or []:
        if not isinstance(row, dict):
            issues.add("EVIDENCE_RECORD_INVALID")
            continue
        target = files.get(row.get("fileId"))
        if target is None:
            issues.add("EVIDENCE_FILE_UNRESOLVED")
            continue
        canonical_id = target["fileId"]
        if canonical_id in evidence_by_file:
            issues.add("DUPLICATE_EVIDENCE")
            continue
        evidence_by_file[canonical_id] = row
        if (row.get("reviewStatus") != REVIEWED_STATUS or not row.get("reviewer")
                or not _valid_date(row.get("reviewedOn")) or not row.get("rationale")):
            issues.add("EVIDENCE_UNREVIEWED")
        if row.get("label") not in EVIDENCE_LABELS:
            issues.add("EVIDENCE_INVALID_LABEL")
        if row.get("scope") != WHOLE_FILE:
            issues.add("EVIDENCE_SCOPE_INCOMPLETE")
        if row.get("queryFragmentId") != seed.get("queryFragmentId"):
            issues.add("EVIDENCE_QUERY_MISMATCH")
        if row.get("fileSha256") != target.get("sha256"):
            issues.add("EVIDENCE_HASH_DRIFT")
        covered = row.get("coveredFragmentIds") or []
        expected = sorted(frag_id for frag_id, frag in fragments.items()
                          if frag.get("fileId") == canonical_id and not frag.get("derivation"))
        if len(covered) != len(set(covered)) or sorted(set(covered)) != expected:
            issues.add("EVIDENCE_INCOMPLETE_FILE_COVERAGE")
    compared_files = set(relevant_ids) | seen_parents
    # Establish the scored gallery (only files with complete reviewed evidence)
    scored_gallery = set()
    for canonical_id in sorted(compared_files):
        row = evidence_by_file.get(canonical_id)
        if row is not None:
            scored_gallery.add(canonical_id)
        else:
            issues.add("EVIDENCE_MISSING_FOR_COMPARED_FILE")
    for canonical_id in sorted(compared_files):
        row = evidence_by_file.get(canonical_id)
        if row is None:
            continue
        if canonical_id in relevant_ids and row.get("label") != "RELEVANT":
            issues.add("EVIDENCE_LABEL_CONTRADICTS_RELEVANCE")
        elif canonical_id in seen_parents and row.get("label") != "DISSIMILAR":
            issues.add("EVIDENCE_LABEL_CONTRADICTS_DISTRACTOR")
    for canonical_id in sorted(evidence_by_file):
        if canonical_id not in compared_files:
            issues.add("EVIDENCE_FOR_UNCOMPARED_FILE")
    # Any declared gallery file without evidence is excluded (UNKNOWN),
    # never silently scored as DISSIMILAR or counted toward reviewed floors.
    excluded_unknown = {file_id for file_id in gallery_ids if file_id not in scored_gallery}
    report["scoredGallery"] = sorted(scored_gallery)
    report["scoredGalleryFileCount"] = len(scored_gallery)
    report["excludedUnknownGalleryFiles"] = sorted(excluded_unknown)
    if len(scored_gallery) <= SCORED_K:
        issues.add("SCORED_GALLERY_BELOW_K")
    if not seen_parents <= scored_gallery:
        issues.add("DISTRACTOR_NOT_IN_SCORED_GALLERY")

    report["issues"] = issues
    report["nonidenticalVariant"] = nonidentical_variant
    return report


def admit(payload, *, files, fragments, split_files, sources, sources_root, file_aliases=None):
    """Admit a reviewed validation corpus or return a concrete blocker."""
    counts = {}
    for row in (split_files or {}).values():
        if row["partition"] == "validation":
            counts[row["language"]] = counts.get(row["language"], 0) + 1
    if not isinstance(payload, dict):
        return _blocker("no reviewed validation corpus artifact is declared",
                        validationFileCounts=counts)
    if payload.get("schema") != SCHEMA or payload.get("status") != REVIEWED_STATUS:
        return _blocker("declared validation artifact is not an independently reviewed corpus",
                        validationFileCounts=counts)
    if not payload.get("reviewer") or not payload.get("rationale") or not _valid_date(payload.get("reviewedOn")):
        return _blocker("validation corpus lacks complete review provenance",
                        validationFileCounts=counts)

    files = {row["fileId"]: row for row in files}
    for alias, canonical_id in (file_aliases or {}).items():
        if alias not in files and canonical_id in files:
            files[alias] = files[canonical_id]
    fragments = {row["fragmentId"]: row for row in fragments}
    sources = {row["sourceId"]: row for row in sources}
    held_families, held_normalized = _held_out(split_files)
    languages = payload.get("languages")
    if not isinstance(languages, dict):
        return _blocker("validation corpus declares no per-language seeds", validationFileCounts=counts)

    per_language, distinct_gallery, ok = {}, set(), True
    strata_omissions, dialect_limits, tsx_coverage = {}, {}, {}
    for language in LANGUAGES:
        seeds = (languages.get(language) or {}).get("seeds") or []
        issues, seed_files, gallery_files, dialects = set(), set(), set(), set()
        strata_counts, distractor_total = {}, 0
        receipts, variant_files, scored_union, excluded_union = [], set(), set(), set()
        for seed in seeds:
            report = _check_seed(seed, language=language, files=files, fragments=fragments,
                                 sources=sources, sources_root=sources_root,
                                 held_families=held_families, held_normalized=held_normalized)
            issues |= report["issues"]
            if report["seedFileId"] is not None:
                seed_files.add(report["seedFileId"])
                # Nonidentical variant coverage is counted per canonical
                # original seed file; just naming a stratum never counts.
                if report.get("nonidenticalVariant"):
                    variant_files.add(report["seedFileId"])
                # The per-query scored allowlist and UNKNOWN exclusions are
                # published verbatim, bound to this query's identity; a file that
                # is UNKNOWN for one query stays fully available to another.
                receipts.append({
                    "queryFragmentId": seed.get("queryFragmentId") if isinstance(seed, dict) else None,
                    "querySha256": seed.get("querySha256") if isinstance(seed, dict) else None,
                    "stratum": seed.get("strata") if isinstance(seed, dict) else None,
                    "seedFileId": report["seedFileId"],
                    "scoredGalleryFileCount": len(report["scoredGallery"]),
                    "scoredGallery": report["scoredGallery"],
                    "excludedUnknownGalleryFiles": report["excludedUnknownGalleryFiles"]})
            gallery_files |= report["galleryFiles"]
            dialects |= report["dialects"]
            distractor_total += report["distractors"]
            scored_union |= set(report["scoredGallery"])
            excluded_union |= set(report["excludedUnknownGalleryFiles"])
            strata = seed.get("strata") if isinstance(seed, dict) else None
            strata_counts[strata] = strata_counts.get(strata, 0) + 1
        # The seed floor counts unique original seed files, never fragment ids
        # or reviewed fileId aliases of one source file.
        if len(seed_files) < MIN_SEEDS_PER_LANGUAGE:
            issues.add("INSUFFICIENT_SEED_FILES")
        # Every canonical original seed file needs one valid reviewed
        # nonidentical variant; exact copies remain optional controls.
        uncovered = sorted(seed_files - variant_files)
        if uncovered:
            issues.add("NONIDENTICAL_VARIANT_UNCOVERED")
        per_language[language] = {"declaredSeeds": len(seeds),
                                  "distinctSeedFiles": len(seed_files),
                                  "nonidenticalVariantFiles": len(variant_files),
                                  "seedFilesWithoutNonidenticalVariant": uncovered,
                                  "galleryFileCount": len(gallery_files),
                                  "distractors": distractor_total,
                                  "strataCounts": dict(sorted(strata_counts.items())),
                                  "queryScoredReceipts": receipts,
                                  "descriptiveScoredUnionFileCount": len(scored_union),
                                  "descriptiveUnknownUnionFileCount": len(excluded_union),
                                  "issues": sorted(issues)}
        distinct_gallery |= gallery_files
        strata_omissions[language] = [row for row in STRATA if not strata_counts.get(row)]
        dialect_limits[language] = sorted(dialects)
        tsx_coverage[language] = _tsx_coverage(gallery_files, files)
        if issues:
            ok = False
    report = {"validationFileCounts": counts, "languages": per_language,
              "distinctGalleryFiles": len(distinct_gallery),
              "strataOmissions": strata_omissions, "dialectLimits": dialect_limits,
              "tsxCoverage": tsx_coverage,
              "floors": {"seedsPerLanguage": MIN_SEEDS_PER_LANGUAGE,
                         "galleryFiles": MIN_GALLERY_FILES,
                         "distractorsPerSeed": MIN_DISTRACTORS_PER_SEED, "scoredK": SCORED_K}}
    if not ok:
        return _blocker("reviewed validation corpus is below the planned floors or unbound",
                        **report)
    return {"status": ADMITTED, "reviewer": payload["reviewer"], "reviewedOn": payload["reviewedOn"],
            "protocolSha256": digest(canonical(payload)), **report}


def propose(index, *, sources, sources_root, seeds_per_language=2, gallery_files=MIN_GALLERY_FILES,
            distractors_per_seed=MIN_DISTRACTORS_PER_SEED):
    """Bounded candidate seeds/gallery/distractors/evidence from permitted validation sources."""
    sources = {row["sourceId"]: row for row in sources}
    files = {row["fileId"]: row for row in index["files"]}
    split_files = index["lineage"]["splitFiles"]
    held_families, _ = _held_out(split_files)
    coverage = {}
    for row in index["fragments"]:
        if not row.get("derivation"):
            coverage.setdefault(row["fileId"], set()).add(row["fragmentId"])
    by_language = {}
    for fragment in index["fragments"]:
        if fragment.get("derivation"):
            continue
        parent = files.get(fragment["fileId"])
        if parent is None or parent["familyId"] in held_families:
            continue
        if not validation_eligible_file(parent, sources.get(parent["sourceId"], {})) or not eligible_fragment(fragment):
            continue
        by_language.setdefault(parent["language"], []).append((parent, fragment))

    def evidence_row(parent, fragment, label):
        return {"fileId": parent["fileId"], "fileSha256": parent["sha256"], "label": label,
                "scope": WHOLE_FILE, "coveredFragmentIds": sorted(coverage.get(parent["fileId"], set())),
                "queryFragmentId": fragment["fragmentId"],
                "reviewStatus": "CANDIDATE_UNREVIEWED", "reviewer": None, "reviewedOn": None,
                "rationale": None}

    languages = {}
    for language in sorted(by_language):
        ordered = sorted(by_language[language], key=lambda item: (item[0]["fileId"], item[1]["startByte"]))
        gallery_pool = sorted({parent["fileId"]: parent for parent, _ in ordered}.values(),
                              key=lambda row: row["fileId"])
        seeds, seen_files = [], set()
        for parent, fragment in ordered:
            if len(seeds) >= seeds_per_language:
                break
            if parent["fileId"] in seen_files:
                continue
            try:
                body = fragment_bytes(sources_root, parent, fragment)
            except ValueError:
                continue
            # The seed's own file is a known relevant target, so it stays inside
            # the declared gallery instead of being pre-excluded from it.
            others = [row for row in gallery_pool if row["fileId"] != parent["fileId"]][:gallery_files - 1]
            if len(others) + 1 < gallery_files:
                continue
            gallery_ids = [row["fileId"] for row in others] + [parent["fileId"]]
            distractors, seen_parents, seen_hashes, evidence = [], set(), set(), []
            evidence.append(evidence_row(parent, fragment, "RELEVANT"))
            for dparent, cfragment in ordered:
                if len(distractors) >= distractors_per_seed:
                    break
                if (dparent["fileId"] == parent["fileId"] or dparent["fileId"] in seen_parents
                        or cfragment["sha256"] in seen_hashes or cfragment["sha256"] == fragment["sha256"]
                        or dparent["fileId"] not in gallery_ids):
                    continue
                distractors.append({"fragmentId": cfragment["fragmentId"], "fragmentSha256": cfragment["sha256"],
                                    "leftFragmentId": fragment["fragmentId"], "parentFileId": dparent["fileId"],
                                    "label": "DISSIMILAR", "reviewStatus": "CANDIDATE_UNREVIEWED", "reviewer": None})
                evidence.append(evidence_row(dparent, cfragment, "DISSIMILAR"))
                seen_parents.add(dparent["fileId"])
                seen_hashes.add(cfragment["sha256"])
            if len(distractors) < distractors_per_seed:
                continue
            seeds.append({"queryFragmentId": fragment["fragmentId"], "querySha256": fragment["sha256"],
                          "seedFileId": parent["fileId"], "familyId": parent["familyId"],
                          "querySourceSha256": parent["sha256"],
                          "language": language, "strata": "EXACT_COPY",
                          "body": body.decode("utf-8"), "galleryFileIds": gallery_ids,
                          "relevantFileIds": [parent["fileId"]], "fileEvidence": evidence,
                          "distractors": distractors, "reviewStatus": "CANDIDATE_UNREVIEWED", "reviewer": None})
            seen_files.add(parent["fileId"])
        languages[language] = {"seeds": seeds, "seedCount": len(seeds),
                               "galleryPoolSize": len(gallery_pool)}
    payload = {"schema": CANDIDATE_SCHEMA, "status": CANDIDATE_STATUS, "reviewer": None,
               "seedsPerLanguage": seeds_per_language, "galleryFilesPerSeed": gallery_files,
               "distractorsPerSeed": distractors_per_seed, "languages": languages,
               "plannedFloors": {"seedsPerLanguage": MIN_SEEDS_PER_LANGUAGE,
                                 "galleryFiles": MIN_GALLERY_FILES,
                                 "distractorsPerSeed": MIN_DISTRACTORS_PER_SEED, "scoredK": SCORED_K},
               "reviewInstruction": ("Assign the relations yourself from the exact bodies/spans; accept, "
                                     "relabel or reject each seed, distractor and per-file evidence record "
                                     "with your reviewer identity, rationale and review date. Cover every "
                                     "compared file at file level: a single-fragment label never establishes "
                                     "a whole-file judgment. No relation is accepted here."),
               "CanAuthorizePublication": False}
    payload["proposalSha256"] = digest(canonical(payload))
    return payload
