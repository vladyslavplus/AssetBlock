"""Strict binding for independent labels on a frozen candidate package."""

from datetime import date

from scripts.code_analysis.contracts import digest, sha
from scripts.code_retrieval import inputs

SCHEMA = "pair-verification-review-receipt-v1"
LABELS = {"DISSIMILAR", "SIMILAR", "AMBIGUOUS"}


def load_receipt(raw, *, candidate_package_raw, candidate_package):
    receipt = inputs.parse_json(raw, "review receipt")
    if not isinstance(receipt, dict) or receipt.get("schema") != SCHEMA:
        raise ValueError("review receipt schema mismatch")
    package_sha = digest(candidate_package_raw)
    if receipt.get("candidatePackageSha256") != package_sha:
        raise ValueError("review receipt candidate package hash mismatch")
    rows = receipt.get("labels")
    if not isinstance(rows, list):
        raise ValueError("review receipt labels are missing")
    package_rows = {row["candidateId"]: row for row in candidate_package.get("candidates", [])}
    bound, seen = {}, set()
    for row in rows:
        if not isinstance(row, dict):
            raise ValueError("review receipt row must be an object")
        candidate_id = row.get("candidateId")
        if not isinstance(candidate_id, str) or candidate_id not in package_rows or candidate_id in seen:
            raise ValueError("review receipt candidate identity is missing, unknown or duplicated")
        seen.add(candidate_id)
        candidate = package_rows[candidate_id]
        left_sha, right_sha = row.get("leftFragmentSha256"), row.get("rightFragmentSha256")
        sha(left_sha)
        sha(right_sha)
        if left_sha != candidate["left"]["fragmentSha256"] or right_sha != candidate["right"]["fragmentSha256"]:
            raise ValueError("review receipt fragment hash binding mismatch")
        label = row.get("label")
        if label not in LABELS:
            raise ValueError("review receipt label is invalid")
        rationale, session, reviewed_on = row.get("rationale"), row.get("reviewerSessionId"), row.get("reviewedOn")
        if not isinstance(rationale, str) or not rationale.strip():
            raise ValueError("review receipt rationale is required")
        if not isinstance(session, str) or not session.strip():
            raise ValueError("review receipt session identity is required")
        if not isinstance(reviewed_on, str):
            raise ValueError("review receipt date is required")
        try:
            date.fromisoformat(reviewed_on)
        except ValueError as exc:
            raise ValueError("review receipt date is invalid") from exc
        bound[candidate_id] = {"label": label, "rationale": rationale.strip(),
                               "reviewerSessionId": session.strip(), "reviewedOn": reviewed_on}
    return {"sha256": digest(raw), "labels": bound, "candidatePackageSha256": package_sha}


def admit_dissimilar(candidates, receipt, *, limit_per_anchor=2):
    if type(limit_per_anchor) is not int or limit_per_anchor < 0:
        raise ValueError("review limit is invalid")
    if receipt is None:
        return [], {"noReceipt": len(candidates), "ambiguous": 0, "similar": 0,
                    "anchorReviewCap": 0, "admitted": 0}
    by_id = receipt["labels"]
    ordered = sorted(candidates, key=lambda row: (row["anchorFragmentId"], row["rank"], row["candidateId"]))
    counts, admitted, excluded = {}, [], {"noReceipt": 0, "ambiguous": 0, "similar": 0, "anchorReviewCap": 0}
    for candidate in ordered:
        label = by_id.get(candidate["candidateId"])
        if label is None:
            excluded["noReceipt"] += 1
            continue
        if label["label"] == "AMBIGUOUS":
            excluded["ambiguous"] += 1
            continue
        if label["label"] == "SIMILAR":
            excluded["similar"] += 1
            continue
        anchor = candidate["anchorFragmentId"]
        if counts.get(anchor, 0) >= limit_per_anchor:
            excluded["anchorReviewCap"] += 1
            continue
        counts[anchor] = counts.get(anchor, 0) + 1
        admitted.append({**candidate, "review": label})
    excluded["admitted"] = len(admitted)
    return admitted, excluded
