"""Structured stage and per-partition evidence, with no publication authority."""

from datetime import date, datetime, timezone

from .contracts import LANGUAGES, PARTITIONS, sha


def stage(name, outcome, inputs, outputs, gaps=()):
    return {"schema": "code-corpus-v1", "stage": name, "ActualOutcome": outcome,
            "Purpose": "CORPUS_BASELINE", "CanAuthorizePublication": False,
            "SecurityEvidence": "NOT_RUN", "recordedAt": datetime.now(timezone.utc).isoformat(),
            "inputs": inputs, "outputs": outputs, "gaps": list(gaps)}


def reviewed_pair(review, left, right, label):
    if review.get("status") != "REVIEWED" or review.get("rubric") != "implementation-similarity-v1":
        return False
    if not review.get("reviewer") or not review.get("rationale") or not review.get("reviewedOn"):
        return False
    # Shared evidence has an explicit endpoint/hash list; reuse never means cartesian inference.
    return any(p == {"leftId": left["fileId"], "leftSha256": left["sha256"],
                     "rightId": right["fileId"], "rightSha256": right["sha256"], "label": label}
               for p in review.get("pairs", []))


def sufficiency(split, seeds=(), reviews=()):
    files = {f["fileId"]: f for f in split["files"]}
    counts, gaps = [], []
    pair_index = set()
    for review in reviews:
        if (review.get("status") != "REVIEWED" or review.get("rubric") != "implementation-similarity-v1"
                or not review.get("reviewer") or not review.get("rationale")):
            continue
        try:
            date.fromisoformat(review["reviewedOn"])
        except (ValueError, KeyError, TypeError):
            continue
        for pair in review.get("pairs", []):
            if set(pair) == {"leftId", "leftSha256", "rightId", "rightSha256", "label"}:
                pair_index.add(tuple(pair[k] for k in ("leftId", "leftSha256", "rightId", "rightSha256", "label")))

    def symmetric(endpoints):
        return tuple(sorted((endpoints[:2], endpoints[2:4])))

    label_sets = {}
    for pair in pair_index:
        label_sets.setdefault(symmetric(pair[:4]), set()).add(pair[4])
    conflicts = {key for key, labels in label_sets.items() if {'SIMILAR', 'DISSIMILAR'} <= labels}
    for (left_id, left_hash), (right_id, right_hash) in sorted(conflicts):
        gaps.append(f"conflicting reviewed labels: leftId={left_id} leftSha256={left_hash} "
                    f"rightId={right_id} rightSha256={right_hash}; "
                    "SIMILAR and DISSIMILAR require explicit adjudication")

    def reviewed(left, right, label):
        endpoints = (left["fileId"], left["sha256"], right["fileId"], right["sha256"])
        return symmetric(endpoints) not in conflicts and (*endpoints, label) in pair_index

    def executable(f):
        return f.get("extraction") == "VALID_EXTRACTED" and f.get("searchableExecutable") is True

    extraction_pending = any(f.get("extraction") == "PENDING" for f in files.values())
    for language in LANGUAGES:
        for partition in PARTITIONS:
            def eligible(f):
                return (f["language"] == language and f["partition"] == partition and f.get("original") is True
                        and not f.get("generated") and not f.get("vendor")
                        and not f.get("normalizationExcludedReason")
                        and f.get("permissions", {}).get("searchIndexAllowed") is True
                        and f.get("permissions", {}).get("evaluationAllowed") is True)
            candidates = {i: f for i, f in files.items() if eligible(f)}
            # Batch intake readiness is tentative; executable gallery readiness awaits extraction.
            gallery = {i: f for i, f in candidates.items() if executable(f)}
            gallery_groups = {f["duplicateGroup"] for f in gallery.values()}
            valid_seeds, distractors, excluded_seeds = {}, {}, {}
            for seed in seeds:
                query = files.get(seed.get("seedFileId"))
                if not query or not eligible(query):
                    continue
                if not executable(query):
                    reasons = []
                    if query.get("extraction") != "VALID_EXTRACTED":
                        reasons.append("extraction=" + str(query.get("extraction", "MISSING")))
                    if query.get("searchableExecutable") is not True:
                        reasons.append("searchableExecutable is not true")
                    excluded_seeds[query["fileId"]] = {"fileId": query["fileId"], "sha256": query["sha256"],
                                                       "reasons": reasons}
                    continue
                relevant = {i for i in seed.get("relevantFileIds", []) if i in gallery
                            and reviewed(query, gallery[i], "SIMILAR")}
                if not relevant:
                    continue
                sg = query["duplicateGroup"]
                ds = {gallery[i]["duplicateGroup"] for i in seed.get("distractorFileIds", [])
                      if i in gallery and i not in seed.get("relevantFileIds", [])
                      and gallery[i]["duplicateGroup"] not in {gallery[r]["duplicateGroup"] for r in relevant}
                      and gallery[i]["duplicateGroup"] != sg
                      and reviewed(query, gallery[i], "DISSIMILAR")}
                # Repeated seed variants do not union unrelated review lists to inflate one valid case.
                if len(ds) > distractors.get(sg, -1):
                    valid_seeds[sg] = query["fileId"]
                    distractors[sg] = len(ds)
            row = {"language": language, "partition": partition,
                   "intakeGalleryCandidates": len({f["duplicateGroup"] for f in candidates.values()}),
                   "eligibleOriginalGalleryFiles": len(gallery_groups), "uniqueReviewedSeeds": len(valid_seeds),
                   "excludedQuerySeedCount": len(excluded_seeds),
                   "excludedQuerySeeds": [excluded_seeds[i] for i in sorted(excluded_seeds)],
                   "reviewedDistractorsPerSeed": distractors,
                   "families": sorted({f["familyId"] for f in candidates.values()})}
            counts.append(row)
            if len(gallery_groups) < 50:
                gaps.append(f"{language}/{partition}: gallery {len(gallery_groups)}/50")
            if len(valid_seeds) < 20:
                gaps.append(f"{language}/{partition}: reviewed seeds {len(valid_seeds)}/20")
            for sg, count in distractors.items():
                if count < 40:
                    gaps.append(f"{language}/{partition}: seed {sg} distractors {count}/40")
    for language in LANGUAGES:
        families = set().union(*(set(r["families"]) for r in counts if r["language"] == language))
        if len(families) < 3:
            gaps.append(f"{language}: independent eligible families {len(families)}/3")
    if any(not f["assignmentReviewed"] for f in split["families"].values()):
        gaps.append("family assignments require explicit review")
    if any(f.get("normalizationExcludedReason") for f in files.values()):
        gaps.append("normalization exclusions require resolution or reviewed source substitution")
    return {"schema": "code-corpus-v1", "status": "CHANGES_NEEDED" if gaps else "FILE_COUNTS_READY",
            "counts": counts, "gaps": gaps, "extractionDependentCounts": "PENDING" if extraction_pending else "CHECKED",
            "transformations": "PENDING", "baselineMetrics": "PENDING", "CanAuthorizePublication": False}


def dependency_gaps(proposal):
    from scripts.feasibility_pilot.dependency_approval import is_license_allowed, load_root_policy

    gaps = []
    if proposal.get("status") != "APPROVED" or not proposal.get("approvalReference"):
        gaps.append("dependency/provisioning proposal is unapproved")
    if not proposal.get("lockSha256"):
        gaps.append("reviewed hash lock missing")
    else:
        sha(proposal["lockSha256"])
    if not proposal.get("packages"):
        gaps.append("dependency graph missing")
    for p in proposal.get("packages", []):
        if not p.get("terms") or p.get("termsReviewed") is not True or not p.get("artifactSha256"):
            gaps.append(p.get("name", "unknown") + ": missing/unreviewed dependency evidence")
        if not is_license_allowed(p.get("terms"), set(load_root_policy()["allowedLicenses"])):
            if not p.get("scopedExceptionReference"):
                gaps.append(p.get("name", "unknown") + ": nondefault/unknown terms lack scoped exception")
        if p.get("requiresException") and not p.get("scopedExceptionReference"):
            gaps.append(p["name"] + ": reviewed scoped exception missing")
    if proposal.get("nativeTermsComplete") is not True:
        gaps.append("bundled native/transitive terms incomplete")
    return gaps
