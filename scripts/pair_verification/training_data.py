"""Byte-bound training and validation pair materialization for the pilot CUDA interpreter."""

import json
from pathlib import Path

from scripts.code_analysis.contracts import canonical, digest
from scripts.code_analysis.representations import mapped_chunks
from scripts.code_retrieval import admission, corpus_index, endpoints, index_verify, inputs
from scripts.feasibility_pilot.paths import REPO_ROOT, ensure_inside

from . import bindings, pair_rules
from .input_layout import layout_pair_ids, window_content

PREPARE_ROOT = REPO_ROOT / "artifacts" / "pair_verification" / "runs"
TRAINING_SCHEMA = "pair-verification-training-pairs-v1"
MAX_PAIR_BYTES = 32 * 1024 * 1024


def validate_frozen_config(config):
    architecture = config.get("architecture") or {}
    layout = config.get("layout") or {}
    trainable = config.get("trainableState") or {}
    optimization = config.get("optimization") or {}
    expected = {
        "architecture": (architecture.get("encoder") == "UniXcoder base-nine"
                          and architecture.get("head") == "linear classification head on CLS"
                          and architecture.get("logits") == 2),
        "layout": (layout.get("maximumTokens") == 512 and layout.get("framingTokens") == 5
                   and layout.get("contentTokens") == 507
                   and layout.get("sideBudget") == {"A": 254, "B": 253}),
        "trainable selection": (trainable.get("encoderBlocks") == "last four"
                                 and trainable.get("head") == "trainable"
                                 and trainable.get("allEarlierEncoderParameters") == "frozen"),
        "optimization": (optimization.get("seed") == 42 and optimization.get("optimizer") == "AdamW"
                         and optimization.get("encoderLearningRate") == 2e-5
                         and optimization.get("headLearningRate") == 1e-4
                         and optimization.get("weightDecay") == 0.01
                         and optimization.get("microBatchPairs") == 2
                         and optimization.get("gradientAccumulation") == 8
                         and optimization.get("epochs") == 5
                         and optimization.get("gradientClip") == 1.0
                         and optimization.get("precision") == "CUDA FP16 with loss scaling"),
    }
    changed = [name for name, valid in expected.items() if not valid]
    if changed:
        raise ValueError("frozen experiment configuration drift: " + ", ".join(changed))
    return optimization


def load_bound_prepare(*, prepare_run_id, inputs_path, config_path):
    directory = ensure_inside(PREPARE_ROOT / prepare_run_id, PREPARE_ROOT)
    evidence_path = ensure_inside(directory / "evidence.json", directory)
    evidence_raw = inputs.read_bytes(evidence_path, MAX_PAIR_BYTES)
    evidence = inputs.parse_json(evidence_raw, "pair preparation evidence")
    evidence_sha256 = digest(evidence_raw)
    if evidence.get("outcome") != "SUCCEEDED" or evidence.get("runId") != prepare_run_id:
        raise ValueError("pair preparation is not a successful bound run")
    marker = inputs.read_bytes(directory / "SUCCEEDED", 128)
    if marker != (evidence_sha256 + "\n").encode("ascii"):
        raise ValueError("pair preparation success marker differs from evidence")
    identities = evidence.get("identities") or {}
    training_path = ensure_inside(directory / "training-pairs.json", directory)
    training_raw = inputs.read_bytes(training_path, MAX_PAIR_BYTES)
    if digest(training_raw) != identities.get("trainingPairsSha256"):
        raise ValueError("training-pair bytes differ from preparation evidence")
    training = inputs.parse_json(training_raw, "training pairs")
    frozen_config_path = ensure_inside(directory / "experiment-config.json", directory)
    config_raw = inputs.read_bytes(frozen_config_path, MAX_PAIR_BYTES)
    if digest(config_raw) != identities.get("configSha256"):
        raise ValueError("frozen experiment configuration bytes differ")
    config = inputs.parse_json(config_raw, "frozen experiment configuration")
    optimization = validate_frozen_config(config)
    chain = identities.get("chain")
    if not isinstance(chain, dict) or set(chain) != set(bindings.ALL_RUN_KEYS):
        raise ValueError("preparation evidence lacks the complete predecessor chain")
    expected_config = REPO_ROOT / "scripts" / "code_retrieval" / "config" / "training-config.json"
    if Path(config_path).resolve() != expected_config.resolve():
        raise ValueError("training configuration path differs from the pinned predecessor")
    bound = bindings.bind_predecessors(inputs_path=inputs_path, config_path=config_path,
                                      run_ids=chain)
    if digest(bound["prepareRaw"]) != identities.get("prepareDataSha256"):
        raise ValueError("preparation predecessor bytes differ")
    if bound["inputIdentity"] != identities.get("inputs"):
        raise ValueError("preparation input identities differ")
    if (training.get("schema") != TRAINING_SCHEMA or training.get("partition") != "train"
            or training.get("pairCount") != len(training.get("pairs") or [])):
        raise ValueError("training pair schema, partition, or count mismatch")
    validation_path = ensure_inside(directory / "validation-candidates.json", directory)
    validation_raw = inputs.read_bytes(validation_path, MAX_PAIR_BYTES)
    if digest(validation_raw) != identities.get("candidateArtifactSha256"):
        raise ValueError("validation candidate bytes differ from preparation evidence")
    validation = inputs.parse_json(validation_raw, "validation candidates")
    bound["validationCandidateArtifact"] = validation
    return {"directory": directory, "evidence": evidence, "evidenceRaw": evidence_raw,
            "evidenceSha256": evidence_sha256, "training": training, "trainingRaw": training_raw,
            "trainingSha256": digest(training_raw), "config": config, "configRaw": config_raw,
            "configSha256": digest(config_raw), "optimization": optimization,
            "validation": validation, "validationSha256": digest(validation_raw), "bound": bound}


def _materialize_endpoint(stored, bound, *, held_normalized, files, fragments, aliases):
    fragment = fragments.get(stored.get("fragmentId"))
    if fragment is None:
        raise ValueError("training endpoint fragment is not admitted")
    if fragment.get("fileId") != stored.get("fileId") and pair_rules.canonical_file_id(
            fragment.get("fileId"), aliases) != stored.get("fileId"):
        raise ValueError("training endpoint parent identity drift")
    parent, body = admission.materialize_endpoint(
        bound["loaded"]["sourcesRoot"], files, fragments, fragment, held_normalized, "training pair")
    identity = pair_rules.endpoint_identity({"fragment": fragment, "parent": parent, "body": body},
                                            files=files, aliases=aliases,
                                            sources_root=bound["loaded"]["sourcesRoot"])
    identity.pop("body")
    if identity != stored:
        raise ValueError("training endpoint source, span, or hash binding drift")
    return {"fragment": fragment, "parent": parent, "body": body}


def _require_prepare_pair_count(actual_count, source):
    evidence_count = (source.get("evidence", {}).get("counts") or {}).get("trainingPairs")
    artifact_count = (source.get("training") or {}).get("pairCount")
    if (type(evidence_count) is not int or evidence_count < 0
            or artifact_count != evidence_count or actual_count != evidence_count):
        raise ValueError("materialized training pair count differs from preparation evidence")
    return evidence_count


def materialize_training_examples(source, tokenizer):
    rows = source["training"]["pairs"]
    bound = source["bound"]
    files = {row["fileId"]: row for row in bound["index"]["files"]}
    fragments = {row["fragmentId"]: row for row in bound["index"]["fragments"]}
    aliases = bound["index"].get("fileAliases") or {}
    held_families, held_normalized = admission.held_out_guard(bound["index"]["lineage"]["splitFiles"])
    mode_id = tokenizer.convert_tokens_to_ids("<encoder-only>")
    examples, seen = [], set()
    truncation = {}
    for row in rows:
        if row.get("pairId") in seen or row.get("label") not in ("SIMILAR", "DISSIMILAR"):
            raise ValueError("training pair identity or reviewed label is invalid")
        seen.add(row["pairId"])
        left, right = row["left"], row["right"]
        if (left.get("partition") != "train" or right.get("partition") != "train"
                or left.get("language") != right.get("language") or left.get("familyId") in held_families
                or right.get("familyId") in held_families
                or (left.get("language"), left.get("normalizedFileSha256")) in held_normalized
                or (right.get("language"), right.get("normalizedFileSha256")) in held_normalized):
            raise ValueError("training pair crossed its partition or held-out boundary")
        left_endpoint = _materialize_endpoint(left, bound, held_normalized=held_normalized,
                                              files=files, fragments=fragments, aliases=aliases)
        right_endpoint = _materialize_endpoint(right, bound, held_normalized=held_normalized,
                                               files=files, fragments=fragments, aliases=aliases)
        if row["label"] == "DISSIMILAR":
            pair_rules._validate_negative(
                {**left, "body": left_endpoint["body"]}, {**right, "body": right_endpoint["body"]})
        if row["pairId"] != pair_rules._pair_id(left, right, row["label"]):
            raise ValueError("training pair identifier differs from bound endpoint identities")
        left_windows = endpoints.endpoint_windows(left_endpoint, bound["loaded"]["sourcesRoot"], tokenizer)
        right_windows = endpoints.endpoint_windows(right_endpoint, bound["loaded"]["sourcesRoot"], tokenizer)
        if (len(left_windows) != row["windowCounts"]["left"]
                or len(right_windows) != row["windowCounts"]["right"]):
            raise ValueError("training endpoint window count drift")
        selected = row.get("selectedWindowPair") or {}
        left_ordinal, right_ordinal = selected.get("left"), selected.get("right")
        if (type(left_ordinal) is not int or type(right_ordinal) is not int
                or not 0 <= left_ordinal < len(left_windows) or not 0 <= right_ordinal < len(right_windows)):
            raise ValueError("selected training window pair is invalid")
        a_ids = window_content(left_windows[left_ordinal])
        b_ids = window_content(right_windows[right_ordinal])
        layout = layout_pair_ids(a_ids, b_ids, cls_id=tokenizer.cls_token_id,
                                 mode_id=mode_id, sep_id=tokenizer.sep_token_id)
        swapped_layout = layout_pair_ids(b_ids, a_ids, cls_id=tokenizer.cls_token_id,
                                         mode_id=mode_id, sep_id=tokenizer.sep_token_id)
        recorded = row.get("layout") or {}
        if (layout.truncated != recorded.get("truncated")
                or layout.a_retained_tokens != recorded.get("retainedTokens", {}).get("left")
                or layout.b_retained_tokens != recorded.get("retainedTokens", {}).get("right")
                or len(a_ids) != row.get("tokenCounts", {}).get("leftWindow")
                or len(b_ids) != row.get("tokenCounts", {}).get("rightWindow")):
            raise ValueError("training pair token layout differs from the frozen preparation")
        truncation[row["language"]] = truncation.get(row["language"], 0) + int(layout.truncated)
        examples.append({"pairId": row["pairId"], "language": row["language"],
                         "kind": row["kind"], "label": 1 if row["label"] == "SIMILAR" else 0,
                         "aIds": tuple(a_ids), "bIds": tuple(b_ids),
                         "inputIds": layout.input_ids, "swappedInputIds": swapped_layout.input_ids,
                         "layoutTruncated": layout.truncated})
    _require_prepare_pair_count(len(examples), source)
    return examples, {"pairCount": len(examples), "truncatedPairsPerLanguage": truncation,
                     "maximumSequenceLength": max(5 + len(row["aIds"]) + len(row["bIds"]) for row in examples)}


def build_validation_windows(bound, tokenizer):
    """Yield token-window pairs only from the hashed validation candidate artifact."""
    index = bound["index"]
    manifests, indexes = {}, {}
    for label in ("E0", "E3"):
        manifests[label], indexes[label] = index_verify.load_index(
            bound["chain"]["index"], label, bound["validation"], bound["corpus"],
            bound["export"]["models"][label]["modelKey"], bound["export"])
    files = {row["fileId"]: row for row in index["files"]}
    sources_root = bound["loaded"]["sourcesRoot"]
    query_rows = {row["queryId"]: row for language in bound["validation"]["queries"].values()
                  for row in language}
    query_cache, file_cache = {}, {}
    mode_id = tokenizer.convert_tokens_to_ids("<encoder-only>")
    for candidate_list in bound["validationCandidateArtifact"]["candidates"]:
        query = query_rows.get(candidate_list["queryId"])
        if query is None or query["language"] != candidate_list["language"]:
            raise ValueError("validation candidate query identity drift")
        query_id = query["queryId"]
        if query_id not in query_cache:
            query_cache[query_id] = endpoints.endpoint_windows(query["endpoint"], sources_root, tokenizer)
        query_windows = query_cache[query_id]
        indexed = indexes["E0"][candidate_list["language"]]
        for file_id in candidate_list["candidateFileIds"]:
            rows = [row for row in indexed if row["fileId"] == file_id]
            if not rows:
                raise ValueError("validation candidate file has no verified E0 windows")
            file_windows = []
            for indexed_row in rows:
                file_id = indexed_row["fileId"]
                if file_id not in file_cache:
                    file_row = files.get(file_id)
                    if file_row is None:
                        raise ValueError("validation index window lacks its bound file")
                    raw = corpus_index.source_bytes(sources_root, file_row)
                    fragment = {"fragmentId": file_id, "startByte": 0,
                                "endByte": len(raw), "sha256": file_row["sha256"]}
                    mapped = mapped_chunks(raw, fragment, tokenizer)
                    file_cache[file_id] = mapped["chunks"]
                chunks = file_cache[file_id]
                ordinal = indexed_row["chunkOrdinal"]
                if not 0 <= ordinal < len(chunks):
                    raise ValueError("validation index window ordinal drift")
                window = chunks[ordinal]
                if (window["tokenStart"] != indexed_row["tokenStart"]
                        or window["tokenEnd"] != indexed_row["tokenEnd"]):
                    raise ValueError("validation index window token boundaries drift")
                file_windows.append(window)
            recorded = candidate_list["windowPairs"].get(file_id)
            expected_pairs = min(3, len(query_windows) * len(file_windows))
            if not isinstance(recorded, list) or len(recorded) != expected_pairs:
                raise ValueError("validation candidate window-pair count differs from the highest-cosine contract")
            pairs = []
            for row in recorded:
                qindex, findex = row["queryWindow"], row["fileWindow"]
                if not (0 <= qindex < len(query_windows) and 0 <= findex < len(file_windows)):
                    raise ValueError("validation window-pair ordinal drift")
                left_ids, right_ids = window_content(query_windows[qindex]), window_content(file_windows[findex])
                layout = layout_pair_ids(left_ids, right_ids, cls_id=tokenizer.cls_token_id,
                                         mode_id=mode_id, sep_id=tokenizer.sep_token_id)
                if (len(query_windows) != row["queryWindowCount"]
                        or len(file_windows) != row["fileWindowCount"]
                        or len(left_ids) != row["queryContentTokens"]
                        or len(right_ids) != row["fileContentTokens"]
                        or layout.truncated != row["layoutTruncated"]):
                    raise ValueError("validation window-pair token layout drift")
                pairs.append({"inputIds": layout.input_ids,
                              "swappedInputIds": layout_pair_ids(
                                  right_ids, left_ids, cls_id=tokenizer.cls_token_id,
                                  mode_id=mode_id, sep_id=tokenizer.sep_token_id).input_ids,
                              "truncated": layout.truncated})
            yield {"model": candidate_list["model"], "queryId": query_id,
                   "fileId": file_id, "language": candidate_list["language"],
                   "stratum": candidate_list["stratum"], "seedGroup": candidate_list["seedGroup"],
                   "relevance": candidate_list["relevance"][file_id], "windowPairs": pairs}
