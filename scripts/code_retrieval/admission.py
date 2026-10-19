"""Fail-closed training admission over explicit, independently reviewed train triplets.

Counts come only from reviewed explicit triplets that pass the structural gate,
reconciliation with reviewed pair assertions, endpoint disjointness, complete
review provenance, the purpose/partition/endpoint gate, and full materialization
of every endpoint from real source bytes (or an independently accepted derived
unit). Missing, contradictory or held-out-overlapping metadata is quarantined;
insufficient reviewed volume is reported as a concrete blocker and is never padded
with unlabeled, UNKNOWN or self-generated relations.
"""

import base64
from datetime import date

from scripts.code_analysis.contracts import LANGUAGES, digest
from scripts.code_analysis.finite_labels import RUBRIC
from scripts.code_analysis.fragment_labels import directed_relation_map
from scripts.code_analysis.splits import normalized_hash
from scripts.code_analysis.validator import reviewed_triplet_eligible

from .corpus_index import eligible_file, eligible_fragment, fragment_bytes, source_bytes

ADMITTED = "ADMITTED"
INSUFFICIENT = "BLOCKED_INSUFFICIENT_REVIEWED_TRAIN_LABELS"
HELD_OUT = ("validation", "final-test")

INELIGIBLE_TRIPLET = "INELIGIBLE_REVIEWED_TRIPLET"
INELIGIBLE_PURPOSE = "INELIGIBLE_PURPOSE_PARTITION_OR_COVERAGE"
IDENTICAL_NEGATIVE = "NEGATIVE_BYTE_IDENTICAL_TO_ANCHOR"
ENDPOINT_UNRESOLVABLE = "ENDPOINT_SOURCE_UNRESOLVABLE"
DERIVED_UNVERIFIED = "DERIVED_POSITIVE_NOT_INDEPENDENTLY_ACCEPTED"
NON_DISJOINT = "POSITIVE_NEGATIVE_NOT_DISJOINT"
DUPLICATE_NEGATIVE = "DUPLICATE_NEGATIVE_CONTENT"
SHARED_NEGATIVE_PARENT = "NEGATIVE_PARENT_NOT_DISTINCT"
CONTRADICTORY_PAIR = "CONTRADICTS_REVIEWED_PAIR_ASSERTION"
UNKNOWN_ENDPOINT = "EXPLICIT_UNKNOWN_ASSERTED"
MISSING_PROVENANCE = "INCOMPLETE_REVIEW_PROVENANCE"
TOTAL_CAP = "TOTAL_TRIPLET_CAP_EXCEEDED"


class _Blocked(Exception):
    def __init__(self, reason):
        super().__init__(reason)
        self.reason = reason


def held_out_guard(split_files):
    """Held-out families and normalized content are read from the authoritative split."""
    rows = list(split_files.values())
    families = {row["familyId"] for row in rows if row["partition"] in HELD_OUT}
    normalized = {(row["language"], row["normalizedSha256"]) for row in rows
                  if row["partition"] in HELD_OUT and row.get("normalizedSha256")}
    return families, normalized


def pair_assertions(fragments, reviews):
    """Directed reviewed pair labels plus the review record that asserted each one."""
    directed = directed_relation_map(fragments, reviews)
    provenance = {}
    for review in reviews or []:
        if review.get("status") != "REVIEWED" or review.get("rubric") != RUBRIC:
            continue
        if not review.get("reviewer") or not review.get("rationale"):
            continue
        for item in review.get("fragmentPairs") or []:
            key = (item.get("leftFragmentId"), item.get("rightFragmentId"))
            provenance.setdefault(key, review)
    return directed, provenance


def _valid_date(value):
    if not isinstance(value, str):
        return False
    try:
        date.fromisoformat(value)
        return True
    except ValueError:
        return False


def _relations(item):
    relations = [("SIMILAR", item["queryFragmentId"], item["positive"]["rightFragmentId"])]
    relations.extend((row.get("label"), item["queryFragmentId"], row["rightFragmentId"])
                     for row in item["negatives"])
    return relations


def _asserted_label(directed, left, right):
    """A reviewed assertion in either orientation; rubric labels are symmetric."""
    asserted = directed.get((left, right))
    if asserted is None:
        asserted = directed.get((right, left))
    return asserted


def _reconcile(item, directed, conflicts):
    """A structural triplet may never supersede a contrary reviewed assertion."""
    for label, left, right in _relations(item):
        asserted = _asserted_label(directed, left, right)
        if asserted is not None:
            if asserted == "UNKNOWN":
                return UNKNOWN_ENDPOINT
            if asserted != label:
                return CONTRADICTORY_PAIR
        if frozenset((left, right)) in conflicts:
            return CONTRADICTORY_PAIR
    return None


def combined_pair_conflicts(directed, triplets, files, fragments):
    """Unordered fragment pairs where reviewed pairs and eligible triplets disagree.

    Every relation is reconciled as an unordered pair, so an assertion made in
    either orientation is compared. Relations a single triplet asserts about
    itself are left to endpoint disjointness; only cross-origin disagreement
    (reviewed pair vs triplet, or triplet vs triplet) quarantines here. Every
    triplet asserting a contradictory pair is quarantined deterministically.
    """
    origins = {}
    for (left, right), label in directed.items():
        key = frozenset((left, right))
        if len(key) != 2 or label not in ("SIMILAR", "DISSIMILAR"):
            continue
        origins.setdefault(key, {}).setdefault("pairReviews", set()).add(label)
    for item in triplets or ():
        if not reviewed_triplet_eligible(item, files, fragments):
            continue
        query = fragments.get(item.get("queryFragmentId"))
        if (query is None or item.get("querySha256") != query.get("sha256")
                or is_derived(query)):
            continue
        origin = "triplet:" + str(item.get("queryFragmentId"))
        for label, left, right in _relations(item):
            if label not in ("SIMILAR", "DISSIMILAR"):
                continue
            key = frozenset((left, right))
            if len(key) != 2:
                continue
            origins.setdefault(key, {}).setdefault(origin, set()).add(label)
    conflicts = set()
    for key, by_origin in origins.items():
        groups = list(by_origin.values())
        if any(first != second
               for index, group in enumerate(groups)
               for other in groups[index + 1:]
               for first in group
               for second in other):
            conflicts.add(key)
    return conflicts


def _triplet_provenance(item, directed, provenance):
    """A valid review date, from the triplet itself or every dated corroborating pair."""
    if not item.get("reviewer") or not item.get("rationale"):
        return None
    if _valid_date(item.get("reviewedOn")):
        return item["reviewedOn"]
    relations = _relations(item)
    for label, left, right in relations:
        if directed.get((left, right)) != label:
            return None
        review = provenance.get((left, right))
        if review is None or not _valid_date(review.get("reviewedOn")):
            return None
    return provenance[(relations[0][1], relations[0][2])]["reviewedOn"]


def _derived_accepted(fragment):
    return (fragment.get("reviewStatus") == "REVIEWED" and bool(fragment.get("reviewer"))
            and _valid_date(fragment.get("reviewedOn")) and isinstance(fragment.get("derivation"), dict))


def decode_derived(fragment):
    """Decode an immutable derived artifact and verify its declared identity."""
    materialization = fragment.get("materialization")
    if not isinstance(materialization, dict) or materialization.get("encoding") != "base64":
        raise _Blocked(DERIVED_UNVERIFIED)
    try:
        blob = base64.b64decode(fragment["bodyBase64"], validate=True)
    except (KeyError, TypeError, ValueError):
        raise _Blocked(DERIVED_UNVERIFIED)
    if (digest(blob) != fragment.get("sha256") or len(blob) != fragment.get("bytes")
            or materialization.get("sha256") != fragment.get("sha256")):
        raise _Blocked(DERIVED_UNVERIFIED)
    if not isinstance(fragment.get("parentFragmentId"), str) or not fragment["parentFragmentId"]:
        raise _Blocked(DERIVED_UNVERIFIED)
    return blob


def is_derived(fragment):
    return isinstance(fragment.get("derivation"), dict)


def _materialize(sources_root, files, fragments, fragment, held_normalized, label):
    parent = files.get(fragment.get("fileId"))
    if parent is None:
        raise _Blocked(ENDPOINT_UNRESOLVABLE)
    if is_derived(fragment):
        if not _derived_accepted(fragment):
            raise _Blocked(DERIVED_UNVERIFIED)
        blob = decode_derived(fragment)
        language = fragment.get("language") or parent.get("language")
        # Held-out overlap is checked on the derived content spliced into its own
        # parent file, so a fragment that is not independently parseable still
        # yields the same normalization the corpus uses for whole files.
        anchor = fragments.get(fragment.get("parentFragmentId"))
        if anchor is None or anchor.get("fileId") != fragment.get("fileId"):
            raise _Blocked(DERIVED_UNVERIFIED)
        try:
            whole = source_bytes(sources_root, parent)
        except ValueError:
            raise _Blocked(ENDPOINT_UNRESOLVABLE)
        start, end = anchor.get("startByte"), anchor.get("endByte")
        if not isinstance(start, int) or not isinstance(end, int) or end > len(whole):
            raise _Blocked(DERIVED_UNVERIFIED)
        spliced = whole[:start] + blob + whole[end:]
        try:
            normalized = normalized_hash(spliced, language)
        except ValueError:
            raise _Blocked(DERIVED_UNVERIFIED)
        if (language, normalized) in held_normalized:
            raise _Blocked(DERIVED_UNVERIFIED)
    else:
        try:
            blob = fragment_bytes(sources_root, parent, fragment)
        except ValueError:
            raise _Blocked(ENDPOINT_UNRESOLVABLE)
    if not eligible_fragment(fragment):
        raise _Blocked(INELIGIBLE_PURPOSE)
    return parent, blob


# Public aliases for callers materializing reviewed endpoints outside this module
# (validation-query admission), so the same fail-closed checks apply everywhere.
Blocked = _Blocked


def materialize_endpoint(sources_root, files, fragments, fragment, held_normalized, label="endpoint"):
    """Materialize a reviewed endpoint or raise Blocked with the concrete reason."""
    return _materialize(sources_root, files, fragments, fragment, held_normalized, label)


def _endpoint_file(files, fragment, label):
    row = files.get(fragment.get("fileId"))
    if row is None:
        raise ValueError("{} endpoint parent file missing".format(label))
    return row


def _guard_endpoint(held_families, held_normalized, file_row, label):
    if file_row["familyId"] in held_families:
        raise ValueError("{} endpoint family overlaps a held-out family".format(label))
    if (file_row["language"], file_row["normalizedSha256"]) in held_normalized:
        raise ValueError("{} endpoint content overlaps held-out content".format(label))


def _rejection(sources, files, held_families, held_normalized, query, positive, negatives):
    for label, fragment in [("anchor", query), ("positive", positive)]:
        file_row = _endpoint_file(files, fragment, label)
        if not eligible_file(file_row, sources[file_row["sourceId"]]):
            return INELIGIBLE_PURPOSE
        _guard_endpoint(held_families, held_normalized, file_row, label)
    for negative in negatives:
        if negative["sha256"] == query["sha256"]:
            return IDENTICAL_NEGATIVE
        file_row = _endpoint_file(files, negative, "negative")
        if not eligible_file(file_row, sources[file_row["sourceId"]]):
            return INELIGIBLE_PURPOSE
        _guard_endpoint(held_families, held_normalized, file_row, "negative")
    return None


def _disjointness(files, query, positive, negatives):
    """Endpoint IDs, bytes and canonical original parents must all be distinct."""
    negative_ids = {row["fragmentId"] for row in negatives}
    if positive["fragmentId"] in negative_ids:
        return NON_DISJOINT
    negative_shas = {row["sha256"] for row in negatives}
    if positive["sha256"] in negative_shas:
        return NON_DISJOINT
    if len(negative_shas) != len(negatives):
        return DUPLICATE_NEGATIVE
    parent_ids = [(files.get(row.get("fileId")) or row).get("fileId") for row in negatives]
    query_parent = (files.get(query.get("fileId")) or query).get("fileId")
    if len(set(parent_ids)) != len(parent_ids) or query_parent in parent_ids:
        return SHARED_NEGATIVE_PARENT
    return None


def evaluate(index, config, *, sources, sources_root):
    """Return per-language admitted counts or a concrete blocker status."""
    sources = {row["sourceId"]: row for row in sources}
    files = {row["fileId"]: row for row in index["files"]}
    fragments = {row["fragmentId"]: row for row in index["fragments"]}
    split_files = index["lineage"]["splitFiles"]
    held_families, held_normalized = held_out_guard(split_files)
    directed, provenance = pair_assertions(fragments, index["merged"]["pairReviews"])
    conflicts = combined_pair_conflicts(directed, index["triplets"], files, fragments)

    admission = config["admission"]
    floor = admission["minAnchorsPerLanguage"]
    cap = admission["maxAnchorsPerLanguage"]
    non_identical_floor = admission["minNonIdenticalPositiveAnchorsPerLanguage"]
    minimum_negatives = admission["minNegativesPerAnchor"]
    total_cap = config["training"]["maxAdmittedTriplets"]

    buckets = {language: {"anchorFiles": set(), "nonIdenticalFiles": set(), "dialects": set(),
                          "reviewedAnchors": 0} for language in LANGUAGES}
    excluded = []
    for item in index["triplets"]:
        query_id = item.get("queryFragmentId")
        if not reviewed_triplet_eligible(item, files, fragments):
            excluded.append({"reason": INELIGIBLE_TRIPLET, "queryFragmentId": query_id})
            continue
        query = fragments[item["queryFragmentId"]]
        if item.get("querySha256") != query["sha256"]:
            raise ValueError("reviewed triplet query hash is not bound")
        if is_derived(query):
            excluded.append({"reason": INELIGIBLE_TRIPLET, "queryFragmentId": query_id})
            continue
        reason = _reconcile(item, directed, conflicts)
        if reason is not None:
            excluded.append({"reason": reason, "queryFragmentId": query_id})
            continue
        if _triplet_provenance(item, directed, provenance) is None:
            excluded.append({"reason": MISSING_PROVENANCE, "queryFragmentId": query_id})
            continue
        positive = fragments[item["positive"]["rightFragmentId"]]
        negatives = [fragments[row["rightFragmentId"]] for row in item["negatives"]]
        if len(negatives) < minimum_negatives:
            raise ValueError("reviewed triplet lacks query-relative negatives")
        if len({row["fragmentId"] for row in negatives}) != len(negatives):
            raise ValueError("reviewed negatives are not distinct fragments")
        reason = _disjointness(files, query, positive, negatives)
        if reason is not None:
            excluded.append({"reason": reason, "queryFragmentId": query_id})
            continue
        reason = _rejection(sources, files, held_families, held_normalized, query, positive, negatives)
        if reason is not None:
            excluded.append({"reason": reason, "queryFragmentId": query_id})
            continue
        for label, fragment in (("anchor", query), ("positive", positive),
                                *[("negative", row) for row in negatives]):
            try:
                _materialize(sources_root, files, fragments, fragment, held_normalized, label)
            except _Blocked as exc:
                excluded.append({"reason": exc.reason, "queryFragmentId": query_id})
                break
        else:
            parent = files[query["fileId"]]
            bucket = buckets[item["language"]]
            bucket["anchorFiles"].add(parent["fileId"])
            if positive["sha256"] != query["sha256"]:
                bucket["nonIdenticalFiles"].add(parent["fileId"])
            bucket["dialects"].add(parent.get("dialect", item["language"]))
            bucket["reviewedAnchors"] += 1

    total_reviewed = sum(v["reviewedAnchors"] for v in buckets.values())
    languages, admitted = {}, True
    for language in LANGUAGES:
        bucket = buckets[language]
        anchors = len(bucket["anchorFiles"])
        non_identical = len(bucket["nonIdenticalFiles"])
        issues = []
        if anchors > cap:
            issues.append("ANCHOR_CAP_EXCEEDED")
        if anchors < floor:
            issues.append("INSUFFICIENT_ANCHOR_FILES")
        if non_identical < non_identical_floor:
            issues.append("INSUFFICIENT_NON_IDENTICAL_POSITIVES")
        if issues:
            admitted = False
        languages[language] = {"distinctAnchorFiles": anchors,
                               "distinctNonIdenticalPositiveAnchorFiles": non_identical,
                               "reviewedAnchors": bucket["reviewedAnchors"],
                               "dialects": sorted(bucket["dialects"]), "issues": issues}
    if total_reviewed > total_cap:
        admitted = False
        languages["_totals"] = {"issues": [TOTAL_CAP]}
    return {"status": ADMITTED if admitted else INSUFFICIENT, "languages": languages,
            "totals": {"reviewedTriplets": len(index["triplets"]),
                       "admittedTriplets": total_reviewed,
                       "distinctAnchorFiles": sum(len(value["anchorFiles"]) for value in buckets.values()),
                       "maxAdmittedTriplets": total_cap, "capExceeded": total_reviewed > total_cap},
            "excluded": excluded,
            "floors": {"minAnchorsPerLanguage": floor, "maxAnchorsPerLanguage": cap,
                       "minNonIdenticalPositiveAnchorsPerLanguage": non_identical_floor,
                       "minNegativesPerAnchor": minimum_negatives}}
