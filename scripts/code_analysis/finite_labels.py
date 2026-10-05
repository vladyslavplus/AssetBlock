"""Expand preserved finite registries into hash-bound pair records.

Unlisted relationships stay UNKNOWN. Filenames, families, and hashes never invent labels.
"""

from datetime import date

from .contracts import canonical, digest, sha

FROZEN_PROTOCOL_SHA = "c0c6f349f1b0e2d2b8d0db47c2c40ad1fecac4f2157c4c6338a8b3ed57b99899"
RUBRIC = "implementation-similarity-v1"


def utf16_len(text):
    return len(text.encode("utf-16-le")) // 2


def file_id(source_id, commit, path, sha256):
    sha(sha256)
    return digest(canonical([source_id, commit, path, sha256]))


def bind_files(extract_files, source_id, path, sha256):
    matches = [row for row in extract_files if row.get("sourceId") == source_id and row.get("path") == path]
    if len(matches) != 1:
        raise ValueError("endpoint requires exactly one full-path match")
    row = matches[0]
    sha(row["fileId"])
    sha(row["sha256"])
    if row["sha256"] != sha256:
        raise ValueError("endpoint source hash mismatch")
    return {key: row[key] for key in ("fileId", "sha256", "sourceId", "path", "partition")}


def package_row(package, path):
    matches = [row for row in package["files"] if row["path"] == path]
    if len(matches) != 1:
        raise ValueError("package path is missing or duplicated: " + path)
    return matches[0]


def named_rows(package, names, prefix, suffix=".py"):
    rows = []
    for name in names:
        path = prefix + name + suffix
        row = package_row(package, path)
        rows.append(row)
    if len(rows) != len(names):
        raise ValueError("named registry cardinality drift")
    return rows


def ordered_valid(package, *, exclude_paths=(), require_ast=True):
    rows = []
    for row in package["files"]:
        if row["path"] in exclude_paths:
            continue
        precheck = row.get("sourceParserPrecheck")
        if precheck not in (None, "VALID") and precheck != "VALID":
            continue
        if require_ast and row.get("astExecutableNodeCandidates") == 0:
            continue
        rows.append(row)
    return rows


def by_utf16(package, *, exclude_paths=()):
    rows = [row for row in package["files"] if row["path"] not in exclude_paths]
    return sorted(rows, key=lambda row: (utf16_len(row["sourceText"]), row["path"]))


def one_based(rows, index):
    if type(index) is not int or not 1 <= index <= len(rows):
        raise ValueError("registry index out of range")
    return rows[index - 1]


def parse_int_list(text):
    return [int(part) for part in text.split(",") if part]


def seed_exclusions(block):
    mapping = {}
    for line in block.strip().splitlines():
        key, values = line.split(":", 1)
        mapping[int(key.strip().lstrip("G"))] = parse_int_list(values.strip())
    return mapping


def similar_self(left):
    return {"leftId": left["fileId"], "leftSha256": left["sha256"],
            "rightId": left["fileId"], "rightSha256": left["sha256"], "label": "SIMILAR"}


def dissimilar(left, right):
    return {"leftId": left["fileId"], "leftSha256": left["sha256"],
            "rightId": right["fileId"], "rightSha256": right["sha256"], "label": "DISSIMILAR"}


def review_record(pairs, rationale, package_sha, *, reviewer, reviewed_on, extra=None):
    if not reviewer or not rationale:
        raise ValueError("explicit review provenance required")
    record = {"status": "REVIEWED", "rubric": RUBRIC, "reviewer": reviewer,
              "reviewedOn": reviewed_on, "rationale": rationale, "pairs": pairs,
              "packageSha256": package_sha,
              "protocolSha256": FROZEN_PROTOCOL_SHA, "humanReview": False}
    if extra:
        record.update(extra)
    date.fromisoformat(record["reviewedOn"])
    return record


def bound(extract_files, package, row):
    return bind_files(extract_files, package["sourceId"], row["path"], row["sha256"])


def expand_minus_pool(extract_files, package, pool, seeds, profiles, package_sha, *, expected_negatives=40):
    if len(seeds) != 20 or len(profiles) != 20:
        raise ValueError("finite seed/profile cardinality")
    reviews, seed_rows, withheld = [], [], []
    for (seed_index, excluded), profile in zip(seeds.items(), profiles):
        query_row = one_based(pool, seed_index)
        left = bound(extract_files, package, query_row)
        keep = []
        omit = []
        for index, row in enumerate(pool, 1):
            target = bound(extract_files, package, row)
            if index in excluded:
                omit.append(target)
            elif target["fileId"] != left["fileId"]:
                keep.append(target)
        if len(keep) != expected_negatives:
            raise ValueError(package["sourceId"] + " retained distractors " + str(len(keep)))
        if len({item["fileId"] for item in keep}) != expected_negatives:
            raise ValueError("duplicate retained distractor identity")
        pairs = [similar_self(left)] + [dissimilar(left, right) for right in keep]
        rationale = ("Query core: " + profile + ". Independently inspected retained "
                     + str(expected_negatives) + " endpoint implementations lack this concrete procedure; "
                     "generic imports/loops/guards/wrappers discounted. Withheld/omitted endpoints remain "
                     "UNKNOWN. No execution or complete transitive inspection.")
        reviews.append(review_record(pairs, rationale, package_sha, reviewer=package["reviewer"], reviewed_on=package["reviewedOn"]))
        seed_rows.append({"seedFileId": left["fileId"], "sourcePath": left["path"], "sourceSha256": left["sha256"],
                          "sourceId": package["sourceId"], "relevantFileIds": [left["fileId"]],
                          "distractorFileIds": [item["fileId"] for item in keep]})
        withheld.append({"seedFileId": left["fileId"], "unknownEndpoints": omit})
    return reviews, seed_rows, withheld


def require_package(package, expected_sha):
    raw_sha = package.get("_rawSha256")
    if raw_sha != expected_sha:
        raise ValueError("source package digest drift: " + package.get("sourceId", "unknown"))
    sha(expected_sha)
    return expected_sha
