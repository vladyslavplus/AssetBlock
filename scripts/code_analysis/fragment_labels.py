"""Hash-bound fragment reviews from preserved adoptions; no filename/family inference."""

from datetime import date

from pathlib import Path

from .contracts import digest, report, sha
from .finite_labels import RUBRIC

ALLOWED_RELATION_LABELS = {"SIMILAR", "DISSIMILAR", "UNKNOWN"}


def reviewed_fragment_pair(pair, fragments, label):
    if not pair or pair.get("label") != label:
        return False
    left = fragments.get(pair.get("leftFragmentId"))
    right = fragments.get(pair.get("rightFragmentId"))
    if left is None or right is None:
        return False
    return left.get("sha256") == pair.get("leftSha256") and right.get("sha256") == pair.get("rightSha256")


def relation_index(extract, reviews, extra_fragments=()):
    fragments = {row["fragmentId"]: row for row in extract.get("fragments") or []}
    for row in extra_fragments:
        fragments[row["fragmentId"]] = row
    directed = directed_relation_map(fragments, reviews)
    similar, unknown = {}, {}
    for (left_id, right_id), label in directed.items():
        if label == "SIMILAR":
            similar.setdefault(left_id, set()).add(right_id)
        elif label == "UNKNOWN":
            unknown.setdefault(left_id, set()).add(right_id)
    return {"fragments": fragments, "similar": similar, "unknown": unknown, "directed": directed}


def directed_relation_map(fragments, reviews, gallery_ids=None):
    allowed = set(gallery_ids) if gallery_ids is not None else None
    directed = {}
    for review in reviews or []:
        if review.get("status") != "REVIEWED" or review.get("rubric") != RUBRIC:
            continue
        if not review.get("reviewer") or not review.get("rationale"):
            continue
        for item in review.get("fragmentPairs") or []:
            label = item.get("label")
            if label not in ALLOWED_RELATION_LABELS:
                raise ValueError("invalid fragment relation label")
            left = fragments.get(item.get("leftFragmentId"))
            right = fragments.get(item.get("rightFragmentId"))
            if left is None or right is None:
                raise ValueError("reviewed fragment identity missing")
            if left.get("sha256") != item.get("leftSha256") or right.get("sha256") != item.get("rightSha256"):
                raise ValueError("fragment relation hash drift")
            if allowed and (left["fragmentId"] not in allowed or right["fragmentId"] not in allowed):
                continue
            key = (left["fragmentId"], right["fragmentId"])
            previous = directed.get(key)
            if previous and previous != label:
                raise ValueError("contradictory fragment labels")
            directed[key] = label
            if label == "SIMILAR":
                reverse = (right["fragmentId"], left["fragmentId"])
                previous_reverse = directed.get(reverse)
                if previous_reverse and previous_reverse != "SIMILAR":
                    raise ValueError("contradictory fragment labels")
                directed[reverse] = "SIMILAR"
    return directed


def diagnostic_map_complete(gallery_ids, directed):
    ids = list(gallery_ids)
    if len(ids) < 10 or len(ids) != len(set(ids)):
        return False
    for query in ids:
        for other in ids:
            if query == other:
                continue
            if directed.get((query, other)) not in ALLOWED_RELATION_LABELS:
                return False
    return True


def query_relevance(fragment_id, gallery_ids, relations):
    directed = relations.get("directed") or {}
    relevant = {fragment_id}
    excluded = set()
    unlabeled = []
    for other in gallery_ids:
        if other == fragment_id:
            continue
        label = directed.get((fragment_id, other))
        if label is None:
            unlabeled.append(other)
            excluded.add(other)
        elif label == "UNKNOWN":
            excluded.add(other)
        elif label == "SIMILAR":
            relevant.add(other)
        elif label not in ALLOWED_RELATION_LABELS:
            raise ValueError("invalid fragment relation label")
    gallery = [item for item in gallery_ids if item not in excluded]
    if fragment_id not in gallery:
        gallery = [fragment_id] + [item for item in gallery if item != fragment_id]
    if len(gallery) != len(set(gallery)):
        raise ValueError("duplicate diagnostic gallery identity")
    if not relevant <= set(gallery):
        raise ValueError("reviewed similar endpoints escaped diagnostic gallery")
    return relevant, gallery, bool(unlabeled)


def require_fragment(by_id, fragment_id):
    sha(fragment_id)
    fragment = by_id.get(fragment_id)
    if fragment is None:
        raise ValueError("adopted fragment identity missing from extract")
    sha(fragment["sha256"])
    sha(fragment["fragmentId"])
    return fragment


def pair(left, right, label):
    return {"leftFragmentId": left["fragmentId"], "leftSha256": left["sha256"],
            "rightFragmentId": right["fragmentId"], "rightSha256": right["sha256"], "label": label}



def review(pairs, rationale, *, reviewer, reviewed_on):
    if not reviewer or not rationale:
        raise ValueError("explicit review provenance required")
    date.fromisoformat(reviewed_on)
    return {"status": "REVIEWED", "rubric": RUBRIC, "reviewer": reviewer, "reviewedOn": reviewed_on,
            "rationale": rationale, "fragmentPairs": pairs}


def bind_span(fragments, name, start, end, *, source_id=None):
    hits = [f for f in fragments if f.get("name") == name and f["startByte"] == start and f["endByte"] == end]
    if source_id:
        hits = [f for f in hits if f.get("sourceId") == source_id]
    if len(hits) != 1:
        raise ValueError("diagnostic span is missing or duplicated: " + name)
    return hits[0]


def materialize_fragment_reviews(extract, adoption):
    """Validate an explicit reviewed export; never infer relations or create approval."""
    if "pairReviews" not in adoption:
        raise ValueError("explicit reviewed fragment export required; legacy adoptions need offline migration")
    merged = merge_fragment_exports(adoption)
    relation_index(extract, merged["pairReviews"], merged["extraFragments"])
    return merged


def _bound_file_identity(row):
    return (row.get("fileId"), row.get("sha256"), row.get("partition"), row.get("language"))


def _bound_fragment_identity(row):
    return (row.get("fragmentId"), row.get("fileId"), row.get("sha256"), row.get("sourceSha256"))


def _bound_triplet_identity(row):
    return (row.get("language"), row.get("queryFragmentId"), row.get("querySha256"),
            row.get("positive"), tuple((item.get("rightFragmentId"), item.get("rightSha256"), item.get("label"))
                                       for item in row.get("negatives") or []),
            row.get("reviewer"), row.get("rationale"), row.get("status"))


def merge_fragment_exports(*payloads):
    extra_files, extra_fragments, galleries, triplets = {}, {}, {}, {}
    reviews = []
    for payload in payloads:
        for row in payload.get("extraFiles") or []:
            prior = extra_files.get(row["fileId"])
            if prior and _bound_file_identity(prior) != _bound_file_identity(row):
                raise ValueError("merged extra file identity conflict")
            extra_files[row["fileId"]] = row if prior is None else prior
        for row in payload.get("extraFragments") or []:
            prior = extra_fragments.get(row["fragmentId"])
            if prior and _bound_fragment_identity(prior) != _bound_fragment_identity(row):
                raise ValueError("merged extra fragment identity conflict")
            extra_fragments[row["fragmentId"]] = row if prior is None else prior
        for language, ids in (payload.get("diagnosticGalleries") or {}).items():
            if language in galleries and galleries[language] != ids:
                raise ValueError("merged diagnostic gallery identity conflict")
            galleries[language] = ids
        for item in payload.get("trainTriplets") or []:
            key = (item["language"], item["queryFragmentId"])
            prior = triplets.get(key)
            if prior and _bound_triplet_identity(prior) != _bound_triplet_identity(item):
                raise ValueError("merged train triplet identity conflict")
            triplets[key] = item if prior is None else prior
        reviews.extend(payload.get("pairReviews") or [])
    languages = sorted({key[0] for key in triplets})
    return report({
        "status": "READY",
        "pairReviews": reviews,
        "extraFiles": list(extra_files.values()),
        "extraFragments": list(extra_fragments.values()),
        "trainTriplets": [triplets[key] for key in sorted(triplets)],
        "trainTripletLanguages": languages,
        "diagnosticGalleries": galleries,
        "diagnosticGalleryLanguages": sorted(galleries),
        "wholeFileLabelsInherited": False,
        "mergedPayloads": len(payloads),
    })


def candidate_review_package(extract, sources_root, *, languages=("typescript", "csharp"), per_gallery=10, train_units=3):
    """Exact bodies/spans for independent review. No labels and no reused reviewer identity."""
    sources_root = Path(sources_root)
    files = {f["fileId"]: f for f in extract["files"]}
    packages = {}
    for language in languages:
        validation, train = [], []
        for fragment in extract["fragments"]:
            parent = files[fragment["fileId"]]
            if parent["language"] != language or not fragment.get("canonicalIndexed"):
                continue
            path = sources_root / parent["sourceId"] / parent["path"]
            raw = path.read_bytes()
            body = raw[fragment["startByte"]:fragment["endByte"]]
            if digest(body) != fragment["sha256"]:
                raise ValueError("candidate fragment slice drift")
            row = {"fragmentId": fragment["fragmentId"], "fileId": parent["fileId"], "path": parent["path"],
                   "sourceId": parent["sourceId"], "sha256": fragment["sha256"], "sourceSha256": parent["sha256"],
                   "startByte": fragment["startByte"], "endByte": fragment["endByte"], "name": fragment.get("name"),
                   "kind": fragment.get("kind"), "body": body.decode("utf-8"),
                   "label": "CANDIDATE_UNREVIEWED", "reviewer": None}
            if parent["partition"] == "validation" and len(validation) < per_gallery:
                validation.append(row)
            elif parent["partition"] == "train" and len(train) < train_units:
                train.append(row)
        packages[language] = {"status": "CANDIDATE_FOR_INDEPENDENT_REVIEW", "reviewer": None,
                              "validationGalleryCandidates": validation, "trainTripletCandidates": train,
                              "proposedRelations": "unlabeled; do not inherit file-level DISSIMILAR"}
    return report({"languages": packages, "CanAuthorizePublication": False})
