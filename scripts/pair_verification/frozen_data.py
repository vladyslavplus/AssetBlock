"""Frozen E0 window encoding for training fragments and mining candidates.

Runs only in ``artifacts/feasibility_pilot/env/ml``. It calls the shared
frozen encoder loader and encoder helper; this module never trains a model.
"""

from scripts.code_analysis import frozen_encoder
from scripts.code_analysis.contracts import LANGUAGES
from scripts.code_analysis.representations import mapped_chunks
from scripts.code_retrieval import admission, checkpoint, corpus_index, index_verify

from .pair_rules import canonical_file_id, endpoint_identity


def _cosine(left, right):
    return sum(a * b for a, b in zip(left, right))


def encode_train_fragments(index, loaded, *, batch_size=8, diagnostics_path=None):
    import torch

    sources = {row["sourceId"]: row for row in index["merged"]["sources"]} if "sources" in index["merged"] else {
        row["sourceId"]: row for row in index["lineage"]["sources"].values()}
    files = {row["fileId"]: row for row in index["files"]}
    split_files = index["lineage"]["splitFiles"]
    fragments = {row["fragmentId"]: row for row in index["fragments"]}
    aliases = index.get("fileAliases") or {}
    held_families, held_normalized = admission.held_out_guard(split_files)
    held_file_ids = {row["fileId"] for row in split_files.values() if row["partition"] != "train"}
    tokenizer, model, load_info, upstream = checkpoint.load_upstream(checkpoint.cuda_device())
    pending, chunks = {}, []
    excluded = {language: {} for language in LANGUAGES}

    def exclude(language, reason):
        rows = excluded[language]
        rows[reason] = rows.get(reason, 0) + 1
    for fragment_id in sorted(fragments):
        fragment = fragments[fragment_id]
        parent = files.get(fragment.get("fileId"))
        if parent is None or parent.get("partition") != "train":
            continue
        source = sources.get(parent.get("sourceId"))
        if source is None or not corpus_index.eligible_file(parent, source) or not corpus_index.eligible_fragment(fragment):
            exclude(parent["language"], "INELIGIBLE_TRAIN_ENDPOINT")
            continue
        canonical_id = canonical_file_id(parent["fileId"], aliases)
        if canonical_id in held_file_ids:
            exclude(parent["language"], "HELD_OUT_FILE_ID")
            continue
        if parent["familyId"] in held_families or (parent["language"], parent.get("normalizedSha256")) in held_normalized:
            exclude(parent["language"], "HELD_OUT_FAMILY_OR_CONTENT")
            continue
        try:
            checked_parent, body = admission.materialize_endpoint(
                loaded["sourcesRoot"], files, fragments, fragment, held_normalized, "train fragment")
            if checked_parent["partition"] != "train":
                raise ValueError("train fragment parent partition changed")
            token_fragment = ({"fragmentId": fragment["fragmentId"], "startByte": 0,
                               "endByte": len(body), "sha256": fragment["sha256"]}
                              if admission.is_derived(fragment) else fragment)
            source_body = body if admission.is_derived(fragment) else corpus_index.source_bytes(
                loaded["sourcesRoot"], checked_parent)
            report = mapped_chunks(source_body, token_fragment, tokenizer)
            identity = endpoint_identity({"fragment": fragment, "parent": checked_parent, "body": body},
                                         files=files, aliases=aliases, sources_root=loaded["sourcesRoot"])
            pending[fragment_id] = {"fragment": fragment, "parent": checked_parent, "body": body,
                                    "chunks": report["chunks"], "originalTokenCount": report["originalTokenCount"],
                                    "omittedTokenCount": report["omittedTokenCount"],
                                    "normalizedFragmentSha256": identity["normalizedFragmentSha256"]}
            chunks.extend(report["chunks"])
        except (ValueError, UnicodeDecodeError) as exc:
            reason = "SOURCE_OR_WINDOW_REFUSED"
            exclude(parent["language"], reason)
    if not pending or not chunks:
        raise ValueError("no eligible train fragments could be frozen-encoded")
    if len(chunks) > 12000:
        raise ValueError("train fragment window count exceeds frozen encoder bound")
    failure_diagnostics = None
    if diagnostics_path is not None:
        from .encoder_diagnostics import capture_encoder_failure

        failure_diagnostics = lambda **details: capture_encoder_failure(diagnostics_path, **details)
    vectors, encode_info = frozen_encoder.encode_chunks(
        model, tokenizer, chunks, batch_size=batch_size, failure_diagnostics=failure_diagnostics)
    vector_map, cursor = {}, 0
    for fragment_id, row in pending.items():
        count = len(row["chunks"])
        vector_map[fragment_id] = vectors[cursor:cursor + count]
        cursor += count
    if cursor != len(vectors):
        raise ValueError("frozen train vector mapping has a row-count mismatch")
    torch.cuda.synchronize()
    environment = {"python": __import__("sys").executable, "torch": torch.__version__,
                   "cudaVersion": torch.version.cuda, "device": torch.cuda.get_device_name(),
                   "deviceType": "cuda", "upstreamRevision": upstream["modelRevision"],
                   "modelLoad": load_info, "encoder": encode_info}
    return {"tokenizer": tokenizer, "model": model, "fragments": pending,
            "vectors": vector_map, "excluded": excluded,
            "facts": {"fragmentCount": len(pending), "windowCount": len(chunks),
                      "dimension": 768, "fullStateUnchanged": encode_info["fullStateUnchanged"],
                      "vectorSha256": encode_info["vectorSha256"]}, "environment": environment}


def best_window_pair(left_vectors, right_vectors):
    if not left_vectors or not right_vectors:
        raise ValueError("fragment pair lacks frozen windows")
    choices = [(float(_cosine(a, b)), left_index, right_index)
               for left_index, a in enumerate(left_vectors) for right_index, b in enumerate(right_vectors)]
    return min(choices, key=lambda row: (-row[0], row[1], row[2]))


def _candidate_id(anchor_id, fragment_id, catalog):
    from scripts.code_analysis.contracts import canonical, digest

    endpoints = sorted(((anchor_id, catalog[anchor_id]["fragment"]["sha256"]),
                        (fragment_id, catalog[fragment_id]["fragment"]["sha256"])))
    return digest(canonical(["pair-verification-mined-candidate-v1", endpoints]))


def nearest_candidates(anchors, catalog, vectors, reviewed_pairs, *, maximum=400, aliases=None):
    aliases = aliases or {}
    reviewed = {frozenset((row["left"]["fragmentId"], row["right"]["fragmentId"])) for row in reviewed_pairs}
    per_anchor, coverage = [], {}
    for anchor_id in sorted(anchors):
        anchor = catalog.get(anchor_id)
        if anchor is None:
            raise ValueError("reviewed anchor lacks E0 encoding")
        anchor_row = anchor["fragment"]
        candidates, exclusions = [], {}

        def exclude(reason):
            exclusions[reason] = exclusions.get(reason, 0) + 1

        for fragment_id, candidate in catalog.items():
            if fragment_id == anchor_id:
                exclude("SELF")
                continue
            other = candidate["fragment"]
            if (other.get("language") or candidate["parent"]["language"]) != (
                    anchor_row.get("language") or anchor["parent"]["language"]):
                continue
            if canonical_file_id(candidate["parent"]["fileId"], aliases) == canonical_file_id(
                    anchor["parent"]["fileId"], aliases):
                exclude("SAME_CANONICAL_FILE")
                continue
            if (other.get("parentFragmentId") or other["fragmentId"]) == (
                    anchor_row.get("parentFragmentId") or anchor_row["fragmentId"]):
                exclude("SAME_CANONICAL_FRAGMENT")
                continue
            if (candidate["parent"].get("sourceId") == anchor["parent"].get("sourceId")
                    and candidate["parent"].get("path") == anchor["parent"].get("path")):
                exclude("SAME_PATH")
                continue
            if frozenset((anchor_id, fragment_id)) in reviewed:
                exclude("REVIEWED_PAIR")
                continue
            if anchor_row["sha256"] == other["sha256"]:
                exclude("BYTE_IDENTICAL")
                continue
            left_norm = anchor.get("normalizedFragmentSha256")
            right_norm = candidate.get("normalizedFragmentSha256")
            if not left_norm or not right_norm:
                exclude("NORMALIZED_IDENTITY_UNAVAILABLE")
                continue
            if left_norm == right_norm:
                exclude("NORMALIZED_IDENTICAL")
                continue
            score, left_window, right_window = best_window_pair(vectors[anchor_id], vectors[fragment_id])
            candidates.append((score, fragment_id, left_window, right_window))
        candidates.sort(key=lambda row: (-row[0], row[1]))
        language = anchor_row.get("language") or anchor["parent"]["language"]
        coverage[anchor_id] = {"language": language, "candidateCount": len(candidates),
                               "exclusions": exclusions}
        per_anchor.append((anchor_id, candidates))
    selected, selected_pairs, ordinal = [], set(), 0
    duplicate_pairs = 0
    while len(selected) < maximum:
        added = False
        for anchor_id, candidates in per_anchor:
            if ordinal >= len(candidates):
                continue
            score, fragment_id, left_window, right_window = candidates[ordinal]
            pair_key = frozenset((anchor_id, fragment_id))
            if pair_key in selected_pairs:
                duplicate_pairs += 1
                added = True
                continue
            selected_pairs.add(pair_key)
            selected.append({"candidateId": _candidate_id(anchor_id, fragment_id, catalog),
                             "anchorFragmentId": anchor_id, "candidateFragmentId": fragment_id,
                             "rank": ordinal + 1, "selectedWindows": [left_window, right_window],
                             "e0Cosine": score})
            added = True
            if len(selected) == maximum:
                break
        if not added:
            break
        ordinal += 1
    per_language = {}
    for row in coverage.values():
        language = row["language"]
        bucket = per_language.setdefault(language, {"anchors": 0, "anchorsWithCandidates": 0,
                                                       "anchorsWithoutCandidates": 0,
                                                       "candidateCount": 0, "exclusions": {}})
        bucket["anchors"] += 1
        bucket["anchorsWithCandidates"] += int(row["candidateCount"] > 0)
        bucket["anchorsWithoutCandidates"] += int(row["candidateCount"] == 0)
        bucket["candidateCount"] += row["candidateCount"]
        for reason, count in row["exclusions"].items():
            bucket["exclusions"][reason] = bucket["exclusions"].get(reason, 0) + count
    return selected, {"perLanguage": per_language, "duplicatePairsDropped": duplicate_pairs}
