"""Read explicitly reviewed manifests without embedding session-specific judgments."""
from datetime import date
from .contracts import canonical, digest, report
from .fragment_labels import RUBRIC


def materialize_label_manifest(extract, manifest):
    if (manifest.get("status") != "REVIEWED" or not manifest.get("reviewer")
            or not manifest.get("rationale")):
        raise ValueError("explicit reviewed label manifest required")
    date.fromisoformat(manifest["reviewedOn"])
    if manifest.get("extractionSha256") != digest(canonical(extract)):
        raise ValueError("reviewed label manifest extraction digest drift")
    files = {row["fileId"]: row for row in extract["files"]}
    relations = {}
    for review in manifest.get("pairReviews") or []:
        if (review.get("status") != "REVIEWED" or review.get("rubric") != RUBRIC
                or not review.get("reviewer") or not review.get("rationale")):
            raise ValueError("pair review provenance missing")
        date.fromisoformat(review["reviewedOn"])
        pairs = review.get("pairs") or []
        if not pairs:
            raise ValueError("pair review requires explicit endpoints")
        for pair in pairs:
            label = pair.get("label")
            if label not in {"SIMILAR", "DISSIMILAR", "UNKNOWN"}:
                raise ValueError("invalid reviewed relation label")
            left, right = files.get(pair.get("leftId")), files.get(pair.get("rightId"))
            if not left or not right or (left["sha256"], right["sha256"]) != (pair.get("leftSha256"), pair.get("rightSha256")):
                raise ValueError("reviewed pair endpoint hash drift")
            key = (left["fileId"], right["fileId"])
            if key in relations and relations[key] != label:
                raise ValueError("contradictory reviewed relation")
            relations[key] = label
    for seed in manifest.get("seeds") or []:
        source = files.get(seed.get("seedFileId"))
        if not source or source["sha256"] != seed.get("sourceSha256"):
            raise ValueError("reviewed seed identity drift")
        for target in seed.get("distractorFileIds") or []:
            if relations.get((source["fileId"], target)) != "DISSIMILAR":
                raise ValueError("reviewed seed lacks explicit distractor relation")
        for target in seed.get("relevantFileIds") or []:
            if relations.get((source["fileId"], target)) != "SIMILAR":
                raise ValueError("reviewed seed lacks explicit positive relation")
    return report({**manifest, "overallReviewerApproval": False, "labelsInferred": False,
                   "reviewManifestSha256": digest(canonical(manifest))})
