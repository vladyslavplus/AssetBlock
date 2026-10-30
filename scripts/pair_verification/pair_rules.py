"""Reviewed-pair gates independent of any encoder or GPU runtime."""

from scripts.code_analysis.contracts import canonical, digest
from scripts.code_analysis.splits import normalized_hash, normalized_span_hash
from scripts.code_retrieval import admission, corpus_index

PAIR_DOMAIN = "pair-verification-reviewed-pair-v1"


def canonical_file_id(file_id, aliases):
    seen = set()
    while file_id in aliases:
        if file_id in seen:
            raise ValueError("file alias cycle")
        seen.add(file_id)
        file_id = aliases[file_id]
    return file_id


def _normalized(body, language):
    try:
        return normalized_hash(body, language), None
    except UnicodeDecodeError:
        return None, "UTF8_DECODE_FAILURE"
    except ValueError as exc:
        if "invalid source" in str(exc):
            return None, "PARSE_FAILURE"
        if "empty normalization" in str(exc):
            return None, "EMPTY_NORMALIZATION"
        return None, "NORMALIZATION_FAILURE"


def endpoint_identity(endpoint, *, files, aliases, sources_root=None, normalization_cache=None):
    fragment, parent, body = endpoint["fragment"], endpoint["parent"], endpoint["body"]
    if parent.get("partition") != "train" or fragment.get("fileId") != parent.get("fileId"):
        raise ValueError("reviewed endpoint escaped the train partition")
    if digest(body) != fragment.get("sha256"):
        raise ValueError("reviewed fragment bytes do not match their digest")
    source_raw = None
    if not admission.is_derived(fragment):
        start, end = fragment.get("startByte"), fragment.get("endByte")
        if type(start) is not int or type(end) is not int or end <= start:
            raise ValueError("reviewed fragment span is invalid")
        if sources_root is not None:
            source_raw = corpus_index.source_bytes(sources_root, parent)
            if corpus_index.fragment_bytes(sources_root, parent, fragment) != body:
                raise ValueError("reviewed fragment span bytes differ")
        span = {"kind": "source-span", "startByte": start, "endByte": end}
    else:
        span = {"kind": "derived-output", "startByte": 0, "endByte": len(body),
                "parentFragmentId": fragment.get("parentFragmentId"),
                "derivation": fragment.get("derivation")}
    file_id = canonical_file_id(parent["fileId"], aliases)
    language = fragment.get("language") or parent["language"]
    cache_key = (fragment["fragmentId"], fragment["sha256"], parent["sha256"], language)
    cached = normalization_cache.get(cache_key) if normalization_cache is not None else None
    if cached is None:
        if source_raw is not None and span["kind"] == "source-span":
            try:
                cached = (normalized_span_hash(source_raw, language, span["startByte"], span["endByte"]), None)
            except UnicodeDecodeError:
                cached = (None, "UTF8_DECODE_FAILURE")
            except ValueError as exc:
                cached = (None, "PARSE_FAILURE" if "invalid source" in str(exc) else
                          "SPAN_NORMALIZATION_FAILURE" if "splits a normalized token" in str(exc) else
                          "EMPTY_NORMALIZATION" if "empty normalization" in str(exc) else "NORMALIZATION_FAILURE")
        else:
            cached = _normalized(body, language)
        if normalization_cache is not None:
            normalization_cache[cache_key] = cached
    normalized, normalization_failure = cached
    return {"fragmentId": fragment["fragmentId"], "fragmentSha256": fragment["sha256"],
            "language": language, "partition": parent["partition"], "sourceId": parent["sourceId"],
            "fileId": file_id, "fileSha256": parent["sha256"], "familyId": parent["familyId"],
            "sourcePath": parent["path"],
            "normalizedFileSha256": parent.get("normalizedSha256"), "span": span, "body": body,
            "canonicalParentFragmentId": fragment.get("parentFragmentId") or fragment["fragmentId"],
            "normalizedFragmentSha256": normalized,
            "normalizedFragmentFailure": normalization_failure}


def _pair_id(left, right, label):
    fragments = sorted([[left["fragmentId"], left["fragmentSha256"]],
                        [right["fragmentId"], right["fragmentSha256"]]])
    return digest(canonical([PAIR_DOMAIN, label, fragments]))


def _validate_negative(left, right):
    if left["fileId"] == right["fileId"]:
        raise ValueError("reviewed negative has the same canonical parent file")
    if left["sourceId"] == right["sourceId"] and left["sourcePath"] == right["sourcePath"]:
        raise ValueError("reviewed negative has the same path")
    if left["fragmentSha256"] == right["fragmentSha256"]:
        raise ValueError("reviewed negative has byte-identical content")
    if not left["normalizedFragmentSha256"] or not right["normalizedFragmentSha256"]:
        missing = [name for name, row in (("left", left), ("right", right))
                   if not row["normalizedFragmentSha256"]]
        causes = sorted({row["normalizedFragmentFailure"] or "NORMALIZATION_FAILURE"
                         for row in (left, right) if not row["normalizedFragmentSha256"]})
        raise ValueError("normalized fragment identity unavailable: " + ",".join(missing + causes))
    if left["normalizedFragmentSha256"] == right["normalizedFragmentSha256"]:
        raise ValueError("reviewed negative has normalized-identical content")
    if left["canonicalParentFragmentId"] == right["canonicalParentFragmentId"]:
        raise ValueError("reviewed negative is a derivative of the same canonical fragment")


def reviewed_pair_records(triplets, *, files, aliases, sources_root, split_files):
    by_id = {row["fileId"]: row for row in files}
    held_files = {row["fileId"] for row in split_files.values() if row.get("partition") != "train"}
    held_families, held_normalized = admission.held_out_guard(split_files)
    records, pair_index, exclusions, excluded_negatives = [], {}, {}, []
    normalization_cache = {}

    def reject(reason, left=None, right=None, label=None):
        exclusions[reason] = exclusions.get(reason, 0) + 1
        if label == "DISSIMILAR" and left is not None and right is not None:
            details = {"pairId": _pair_id(left, right, label), "language": left["language"], "reason": reason,
                                       "leftFragmentId": left["fragmentId"],
                                       "leftFragmentSha256": left["fragmentSha256"],
                                       "rightFragmentId": right["fragmentId"],
                                       "rightFragmentSha256": right["fragmentSha256"]}
            if "normalized fragment identity unavailable" in reason:
                details["normalizationFailures"] = [
                    {"fragmentId": row["fragmentId"],
                     "reason": row["normalizedFragmentFailure"] or "NORMALIZATION_FAILURE"}
                    for row in (left, right) if not row["normalizedFragmentSha256"]]
            excluded_negatives.append(details)

    for triplet in triplets:
        endpoints = triplet["endpoints"]
        if len(endpoints) < 2 or endpoints[0].get("label") not in (None, "anchor"):
            raise ValueError("reviewed triplet endpoint roles are invalid")
        left = endpoint_identity(endpoints[0], files=by_id, aliases=aliases, sources_root=sources_root,
                                 normalization_cache=normalization_cache)
        for position, right_endpoint in enumerate(endpoints[1:], start=1):
            right = endpoint_identity(right_endpoint, files=by_id, aliases=aliases, sources_root=sources_root,
                                      normalization_cache=normalization_cache)
            expected_role = "positive" if position == 1 else "negative"
            role = right_endpoint.get("label")
            if role == "unknown":
                reject("UNKNOWN_LABEL", left, right)
                continue
            if role not in (None, expected_role):
                reject("ENDPOINT_ROLE_MISMATCH", left, right)
                continue
            pair_label = "SIMILAR" if expected_role == "positive" else "DISSIMILAR"
            if left["language"] != right["language"] or left["partition"] != "train" or right["partition"] != "train":
                reject("PURPOSE_PARTITION_OR_LANGUAGE", left, right, pair_label)
                continue
            if left["fileId"] in held_files or right["fileId"] in held_files:
                reject("HELD_OUT_FILE_ID", left, right, pair_label)
                continue
            if left["familyId"] in held_families or right["familyId"] in held_families:
                reject("HELD_OUT_FAMILY", left, right, pair_label)
                continue
            if ((left["language"], left["normalizedFileSha256"]) in held_normalized
                    or (right["language"], right["normalizedFileSha256"]) in held_normalized):
                reject("HELD_OUT_NORMALIZED_FILE", left, right, pair_label)
                continue
            if pair_label == "DISSIMILAR":
                try:
                    _validate_negative(left, right)
                except ValueError as exc:
                    reject(str(exc), left, right, pair_label)
                    continue
            key = tuple(sorted((left["fragmentId"], right["fragmentId"])))
            if key in pair_index:
                previous = pair_index[key]
                if previous["label"] != pair_label:
                    raise ValueError("CONFLICTING_PAIR_LABEL")
                previous["reviewedContexts"].append(triplet["queryFragmentId"])
                previous["duplicateReviewCount"] += 1
                reject("DUPLICATE_REVIEW_COLLAPSED")
                continue
            transform = (right_endpoint["fragment"].get("derivation") or {}).get("transformation")
            kind = transform if pair_label == "SIMILAR" and transform else pair_label
            row = {"pairId": _pair_id(left, right, pair_label), "label": pair_label, "kind": kind,
                   "language": left["language"], "left": left, "right": right,
                   "tripletAnchorFragmentId": triplet["queryFragmentId"],
                   "reviewedContexts": [triplet["queryFragmentId"]], "duplicateReviewCount": 0,
                   "reviewedOn": triplet.get("reviewedOn")}
            pair_index[key] = row
            records.append(row)
    exclusions["excludedNegativePairs"] = excluded_negatives
    return records, exclusions


def cap_identical_positives(records):
    positives = [row for row in records if row["label"] == "SIMILAR"]
    negatives = [row for row in records if row["label"] == "DISSIMILAR"]
    identical = [row for row in positives if row["left"]["fragmentSha256"] == row["right"]["fragmentSha256"]]
    nonidentical = [row for row in positives if row not in identical]
    maximum = len(nonidentical) // 9
    ordered = sorted(identical, key=lambda row: (row["left"]["fragmentSha256"],
                                                 row["right"]["fragmentSha256"], row["pairId"]))
    kept_ids = {row["pairId"] for row in ordered[-maximum:]} if maximum else set()
    kept = [row for row in positives if row not in identical or row["pairId"] in kept_ids]
    dropped = len(identical) - len(kept_ids)
    return sorted(kept + negatives, key=lambda row: row["pairId"]), {
        "positiveBefore": len(positives), "positiveAfter": len(kept),
        "byteIdenticalBefore": len(identical), "byteIdenticalAfter": len(kept_ids),
        "byteIdenticalDropped": dropped,
        "byteIdenticalShareAfter": len(kept_ids) / len(kept) if kept else 0.0}


def positive_identity_counts(records):
    counts = {}
    for row in records:
        if row["label"] != "SIMILAR":
            continue
        language = row["language"]
        bucket = counts.setdefault(language, {"byteIdentical": 0, "nonIdentical": 0})
        key = "byteIdentical" if row["left"]["fragmentSha256"] == row["right"]["fragmentSha256"] else "nonIdentical"
        bucket[key] += 1
    return counts
