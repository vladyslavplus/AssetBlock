"""Candidate anchors, positives and negatives proposed for independent review.

Nothing in this module is a label. Every record is marked CANDIDATE_UNREVIEWED
with a null reviewer, and the composed payload states that relations remain
undecided. A reviewer decides each concrete record; no generated relation is
adopted as independently accepted.

A transformed positive is represented as an explicit derived unit with immutable
bytes, a derivation/parent/source map and its own identity. It is never presented
as an original source slice occupying the anchor's span.
"""

import base64
from collections import defaultdict

from scripts.code_analysis.contracts import canonical, digest

from . import transforms
from .corpus_index import eligible_file, eligible_fragment, fragment_bytes

SCHEMA = "code-retrieval-candidate-labels-v1"
STATUS = "CANDIDATE_FOR_INDEPENDENT_REVIEW"
UNREVIEWED = "CANDIDATE_UNREVIEWED"
DERIVED_DOMAIN = "code-retrieval-derived-fragment-v1"
BODY_ENCODING = "base64"


def _identity(record):
    return digest(canonical(record))


def derived_fragment_id(parent_fragment_id, transformation, version, output_sha256):
    return digest(canonical([DERIVED_DOMAIN, parent_fragment_id, transformation, version, output_sha256]))


def _propose_positive(anchor_body, parent, fragment):
    fragment_file = {**parent, "sha256": fragment["sha256"]}
    proposal = transforms.propose_positive(anchor_body, fragment_file)
    if proposal is None:
        return None
    output = proposal["output"]
    output_sha256 = digest(output)
    fragment_id = derived_fragment_id(fragment["fragmentId"], proposal["transformation"],
                                      proposal["version"], output_sha256)
    derivation = {"transformation": proposal["transformation"], "version": proposal["version"],
                  "changedRanges": proposal["changedRanges"], "rationale": proposal["rationale"],
                  "outputSha256": output_sha256}
    record = {"fragmentId": fragment_id, "derived": True, "fileId": parent["fileId"],
              "parentFragmentId": fragment["fragmentId"], "parentFileId": parent["fileId"],
              "sourceSha256": parent["sha256"], "sha256": output_sha256, "bytes": len(output),
              "bodyBase64": base64.b64encode(output).decode("ascii"),
              "materialization": {"encoding": BODY_ENCODING, "sha256": output_sha256},
              "language": fragment["language"], "dialect": fragment.get("dialect", fragment["language"]),
              "name": fragment.get("name"), "kind": fragment.get("kind"),
              "searchable": True, "executable": True, "partialMLCoverage": False,
              "derivation": derivation, "reviewStatus": UNREVIEWED, "reviewer": None, "reviewedOn": None}
    return {"fragment": record, "body": output.decode("utf-8")}


def _negatives(ordered, parent, fragment, count):
    picked, seen_files, seen_fragments = [], {parent["fileId"]}, {fragment["fragmentId"]}
    seen_hashes = {fragment["sha256"]}
    for candidate_parent, candidate in ordered:
        if len(picked) >= count:
            break
        if candidate["fragmentId"] in seen_fragments or candidate["sha256"] in seen_hashes:
            continue
        if candidate_parent["fileId"] in seen_files:
            continue
        picked.append(candidate)
        seen_files.add(candidate_parent["fileId"])
        seen_fragments.add(candidate["fragmentId"])
        seen_hashes.add(candidate["sha256"])
    return picked


def build(index, *, sources, sources_root, anchors_per_language, negatives_per_anchor, exclude_queries=()):
    sources = {row["sourceId"]: row for row in sources}
    files = {row["fileId"]: row for row in index["files"]}
    exclude_queries = set(exclude_queries)
    by_language = defaultdict(list)
    for fragment in index["fragments"]:
        if fragment.get("derivation"):
            continue
        parent = files.get(fragment["fileId"])
        if parent is None:
            raise ValueError("fragment parent file missing from index")
        source = sources.get(parent["sourceId"])
        if source is None:
            raise ValueError("fragment source missing from configuration")
        if not eligible_file(parent, source) or not eligible_fragment(fragment):
            continue
        by_language[parent["language"]].append((parent, fragment))

    languages, extra_fragments, proposed_pairs = {}, [], 0
    for language in sorted(by_language):
        ordered = sorted(by_language[language],
                         key=lambda item: (item[0]["fileId"], item[1]["startByte"], item[1]["endByte"]))
        anchors, seen_files = [], set()
        for parent, fragment in ordered:
            if len(anchors) >= anchors_per_language:
                break
            if parent["fileId"] in seen_files or fragment["fragmentId"] in exclude_queries:
                continue
            anchor_body = fragment_bytes(sources_root, parent, fragment)
            positive = _propose_positive(anchor_body, parent, fragment)
            negatives = _negatives(ordered, parent, fragment, negatives_per_anchor)
            anchor = {"language": language, "fileId": parent["fileId"], "familyId": parent["familyId"],
                      "sourceId": parent["sourceId"], "path": parent["path"],
                      "fragmentId": fragment["fragmentId"], "sha256": fragment["sha256"],
                      "sourceSha256": parent["sha256"], "startByte": fragment["startByte"],
                      "endByte": fragment["endByte"], "name": fragment.get("name"),
                      "kind": fragment.get("kind"), "dialect": fragment.get("dialect", language),
                      "body": anchor_body.decode("utf-8"),
                      "proposedPositive": ({"fragmentId": positive["fragment"]["fragmentId"],
                                            "parentFragmentId": fragment["fragmentId"],
                                            "sha256": positive["fragment"]["sha256"],
                                            "outputSha256": positive["fragment"]["derivation"]["outputSha256"],
                                            "transformation": positive["fragment"]["derivation"]["transformation"],
                                            "version": positive["fragment"]["derivation"]["version"],
                                            "changedRanges": positive["fragment"]["derivation"]["changedRanges"],
                                            "rationale": positive["fragment"]["derivation"]["rationale"],
                                            "body": positive["body"], "reviewStatus": UNREVIEWED,
                                            "reviewer": None} if positive else None),
                      "positiveUnavailableReason": (None if positive else
                                                    "no reviewed transformation candidate applied"),
                      "proposedNegatives": [{"fragmentId": row["fragmentId"], "fileId": row["fileId"],
                                             "familyId": files[row["fileId"]]["familyId"],
                                             "sourceId": files[row["fileId"]]["sourceId"],
                                             "path": files[row["fileId"]]["path"], "sha256": row["sha256"],
                                             "sourceSha256": row["sourceSha256"], "name": row.get("name"),
                                             "kind": row.get("kind"), "startByte": row["startByte"],
                                             "endByte": row["endByte"], "body": None,
                                             "reviewStatus": UNREVIEWED, "reviewer": None}
                                            for row in negatives],
                      "reviewStatus": UNREVIEWED, "reviewer": None}
            anchor["anchorId"] = _identity({key: anchor[key] for key in
                                            ("language", "fileId", "fragmentId", "sha256")})
            anchor["reviewable"] = (positive is not None and len(negatives) == negatives_per_anchor)
            if anchor["reviewable"]:
                extra_fragments.append(positive["fragment"])
                proposed_pairs += 1 + len(negatives)
            seen_files.add(parent["fileId"])
            anchors.append(anchor)
        languages[language] = {"language": language,
                               "anchorFileCount": len(anchors),
                               "reviewableAnchorCount": sum(1 for row in anchors if row["reviewable"]),
                               "nonIdenticalPositiveCount": sum(1 for row in anchors if row["proposedPositive"]),
                               "anchors": anchors}

    triplets = []
    for language in sorted(languages):
        for anchor in languages[language]["anchors"]:
            if not anchor["reviewable"]:
                continue
            positive = anchor["proposedPositive"]
            triplets.append({"language": language, "partition": "train",
                             "rubric": "implementation-similarity-v1",
                             "queryFragmentId": anchor["fragmentId"], "querySha256": anchor["sha256"],
                             "positive": {"label": "SIMILAR", "leftFragmentId": anchor["fragmentId"],
                                          "leftSha256": anchor["sha256"],
                                          "rightFragmentId": positive["fragmentId"],
                                          "rightSha256": positive["sha256"]},
                             "negatives": [{"label": "DISSIMILAR", "leftFragmentId": anchor["fragmentId"],
                                            "leftSha256": anchor["sha256"],
                                            "rightFragmentId": row["fragmentId"],
                                            "rightSha256": row["sha256"]}
                                           for row in anchor["proposedNegatives"]],
                             "status": "PROPOSED_UNREVIEWED", "reviewer": None, "rationale": None})

    payload = {"schema": SCHEMA, "status": STATUS, "reviewer": None,
               "anchorsPerLanguage": anchors_per_language, "negativesPerAnchor": negatives_per_anchor,
               "languages": languages, "extraFiles": [], "extraFragments": extra_fragments,
               "proposedPairCount": proposed_pairs, "proposedTriplets": triplets,
               "proposedTripletCount": len(triplets), "CanAuthorizePublication": False,
               "reviewInstruction": ("Review each proposed triplet, positive and negative against the "
                                     "query; accept, relabel or reject with your own reviewer identity, "
                                     "rationale and review date. Return explicit trainTriplets plus the "
                                     "accepted derived extraFragments. No relation is accepted here.")}
    payload["proposalSha256"] = digest(canonical(payload))
    return payload
