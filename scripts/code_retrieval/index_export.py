"""export-index: one immutable validation-partition vector index per compared encoder.

Runs in the training interpreter (CUDA). Binds to the admitted prepare run and the verified
training evidence, runs the strengthened reference-probe check before any embedding, then
encodes the union of every seed's scored gallery per language with each of E0..E3 through
one encode function. Each index carries a model key and per-language index keys. Failed runs
keep failed evidence and never write a SUCCEEDED marker.
"""

import sys
import time

import torch

from scripts.code_analysis.contracts import canonical, digest
from scripts.code_analysis.corpus import write_once
from scripts.code_analysis.frozen_encoder import encode_chunks
from scripts.code_analysis.representations import CHUNKING, mapped_chunks, vector_bytes

from . import checkpoint, config as config_module, environment_probe, inputs, prepare, retrieval_models, training
from . import validation_plan
from .index_verify import (EMBEDDING_DIMENSIONS, EXPORT_EVIDENCE, EXPORT_SCHEMA, INDEX_DIR, INDEX_SCHEMA, MANIFEST_NAME,
                           gallery_key, index_key, model_key, protocol_sha256, require_training_binding)
from .paths import RETRIEVAL_ROOT, run_dir

REFERENCE_PROBE = "reference-probe.json"
LIVE_PROBE = "live-probe.json"
LIKE_FOR_LIKE_SAMPLE = 16
LIKE_FOR_LIKE_TOLERANCE = 1e-6


def gallery_windows(rows, tokenizer):
    """Chunk-manifest rows and per-file coverage for whole-file gallery units."""
    chunk_rows, file_rows = [], []
    for row in rows:
        fragment = {"fragmentId": row["fileId"], "startByte": 0, "endByte": len(row["body"]), "sha256": row["sha256"]}
        mapped = mapped_chunks(row["body"], fragment, tokenizer)
        file_rows.append({"fileId": row["fileId"], "chunkCount": len(mapped["chunks"]),
                          "originalTokenCount": mapped["originalTokenCount"],
                          "retainedUniqueTokenCount": mapped["retainedUniqueTokenCount"],
                          "omittedTokenCount": mapped["omittedTokenCount"],
                          "partialMLCoverage": mapped["partialMLCoverage"]})
        for chunk in mapped["chunks"]:
            chunk_rows.append({"fileId": row["fileId"], "fragmentId": row["fileId"],
                               "chunkOrdinal": chunk["chunkOrdinal"], "tokenStart": chunk["tokenStart"],
                               "tokenEnd": chunk["tokenEnd"], "totalInputTokens": chunk["totalInputTokens"],
                               "representationSha256": chunk["representationSha256"],
                               "inputIds": chunk["inputIds"]})
    return chunk_rows, file_rows


def write_index(run, label, identity, galleries_by_language, encoded, corpus_identity):
    """Write-once per-model directory: FP32 vectors, chunk manifests and an index manifest."""
    directory = run / INDEX_DIR / label
    languages = {}
    for language, gallery in galleries_by_language.items():
        chunk_rows, file_rows, vectors, seconds = encoded[language]
        raw = vector_bytes(vectors)
        chunks = canonical([{key: value for key, value in row.items() if key != "inputIds"} for row in chunk_rows])
        write_once(directory / (language + ".fp32le"), raw, RETRIEVAL_ROOT)
        write_once(directory / (language + ".chunks.json"), chunks, RETRIEVAL_ROOT)
        languages[language] = {
            "rows": len(chunk_rows), "vectorBytes": len(raw), "vectorSha256": digest(raw),
            "chunkManifestBytes": len(chunks), "chunkManifestSha256": digest(chunks),
            "galleryFiles": len(gallery), "galleryKey": gallery_key(gallery),
            "indexKey": index_key(identity["modelKey"], gallery, corpus_identity),
            "partialMLCoverageFiles": sum(row["partialMLCoverage"] for row in file_rows),
            "omittedTokenCount": sum(row["omittedTokenCount"] for row in file_rows),
            "embedSeconds": seconds}
    manifest = {"schema": INDEX_SCHEMA, "label": label, "kind": identity["kind"], "partition": "validation",
                "modelKey": identity["modelKey"], "identity": identity["fields"],
                "dimension": EMBEDDING_DIMENSIONS,
                "chunking": CHUNKING, "languages": languages, "corpus": corpus_identity}
    write_once(directory / MANIFEST_NAME, canonical(manifest), RETRIEVAL_ROOT)
    return manifest


def like_for_like(frozen, rows, device):
    """E0 vectors from the shared encode path equal frozen_encoder.encode_chunks on the same chunks."""
    chunks = [{"inputIds": row["inputIds"], "framingTokens": 4} for row in rows[:LIKE_FOR_LIKE_SAMPLE]]
    ours = retrieval_models.encode(frozen["model"], frozen["tokenizer"], chunks, device)
    theirs, _ = encode_chunks(frozen["model"], frozen["tokenizer"], chunks, batch_size=1)
    difference = max(abs(a - b) for left, right in zip(ours, theirs) for a, b in zip(left, right))
    return {"sampleChunks": len(chunks), "maxAbsDifference": difference, "tolerance": LIKE_FOR_LIKE_TOLERANCE,
            "result": "MATCH" if difference <= LIKE_FOR_LIKE_TOLERANCE else "MISMATCH"}


def encode_galleries(model_entry, galleries, device):
    """Every language's gallery windows through the one encode function, with timing per language."""
    encoded = {}
    for language, gallery in galleries.items():
        started = time.perf_counter()
        chunk_rows, file_rows = gallery_windows(gallery, model_entry["tokenizer"])
        vectors = retrieval_models.encode(model_entry["model"], model_entry["tokenizer"], chunk_rows, device)
        torch.cuda.synchronize(device)
        encoded[language] = (chunk_rows, file_rows, vectors, time.perf_counter() - started)
    return encoded


def run(*, run_id, inputs_path, config_path, prepare_run_id, train_run_id, reference_probe_path):
    loaded = inputs.load(inputs_path)
    config_loaded = config_module.load(config_path)
    index = prepare.reviewed_index(loaded)
    prepare_binding, triplets, prepare_payload = training.admitted_binding(loaded, config_loaded, prepare_run_id, index)
    expectations = checkpoint.load_training_evidence(run_dir(train_run_id))
    require_training_binding(expectations, loaded=loaded, config_loaded=config_loaded,
                             prepare_binding=prepare_binding)
    reference_raw = inputs.read_bytes(reference_probe_path, inputs.MAX_ARTIFACT_BYTES)
    reference_probe = inputs.parse_json(reference_raw, "reference probe")
    plan = validation_plan.build(loaded, index, prepare_payload["validationQueries"])
    live_probe = environment_probe.build_probe(triplets, loaded["sourcesRoot"])
    comparison = environment_probe.compare(reference_probe, live_probe)
    device = checkpoint.cuda_device()
    checkpoint.configure_numerics()
    target = run_dir(run_id)
    target.mkdir(parents=True, exist_ok=False)
    return _export(run_id=run_id, target=target, loaded=loaded, config_loaded=config_loaded, plan=plan,
                   prepare_binding=prepare_binding, expectations=expectations, train_run_id=train_run_id,
                   reference_raw=reference_raw, reference_probe=reference_probe, live_probe=live_probe,
                   comparison=comparison, device=device)


def _export(*, run_id, target, loaded, config_loaded, plan, prepare_binding, expectations, train_run_id,
            reference_raw, reference_probe, live_probe, comparison, device):
    started = time.monotonic()
    corpus = {"inputsManifestSha256": loaded["manifestSha256"], "prepareRunId": prepare_binding["runId"],
              "validationReportSha256": plan["reportSha256"], "protocolSha256": protocol_sha256(),
              "configSha256": config_loaded["configSha256"]}
    galleries = plan["galleries"]
    models, failure, like = {}, None, None
    try:
        write_once(target / REFERENCE_PROBE, reference_raw, RETRIEVAL_ROOT)
        write_once(target / LIVE_PROBE, canonical(live_probe), RETRIEVAL_ROOT)
        for label in ["E0", "E1", "E2", "E3"]:
            entry = retrieval_models.frozen_model(device) if label == "E0" else \
                retrieval_models.trained_model(run_dir(train_run_id), int(label[1]), expectations, device)
            identity = {"modelKey": model_key(entry["identity"]), "kind": entry["identity"]["kind"],
                        "fields": entry["identity"]}
            torch.cuda.reset_peak_memory_stats(device)
            encoded = encode_galleries(entry, galleries, device)
            if label == "E0":
                like = like_for_like(entry, next(iter(encoded.values()))[0], device)
                if like["result"] != "MATCH":
                    raise ValueError("E0 shared encode path differs from encode_chunks: " + str(like))
            manifest = write_index(target, label, identity, galleries, encoded, corpus)
            models[label] = {"modelKey": identity["modelKey"], "languages": manifest["languages"],
                             "peakAllocatedVRAM": torch.cuda.max_memory_allocated(device),
                             "peakReservedVRAM": torch.cuda.max_memory_reserved(device),
                             "workingSet": training.process_working_set()}
            del entry
            torch.cuda.empty_cache()
    except Exception as exc:  # the evidence must record every failed run
        failure = ("FAILED_ERROR", type(exc).__name__ + ": " + str(exc))
    outcome = "SUCCEEDED" if failure is None else failure[0]
    evidence = {"schema": EXPORT_SCHEMA, "runId": run_id, "outcome": outcome,
                "reason": None if failure is None else failure[1], "Purpose": "CODE_RETRIEVAL_VALIDATION_INDEX",
                "CanAuthorizePublication": False, "SecurityEvidence": "NOT_RUN",
                "identity": {**corpus, "prepare": prepare_binding,
                             "trainingEvidenceSha256": expectations["evidenceSha256"],
                             "trainRunId": train_run_id,
                             "checkpointManifestSha256": {
                                 label: expectations["epochs"].get(str(n), {}).get("checkpointManifestSha256")
                                 for n, label in ((1, "E1"), (2, "E2"), (3, "E3"))}},
                "referenceProbe": {"referenceSha256": digest(reference_raw), "comparison": comparison,
                                   "reference": reference_probe, "live": live_probe},
                "likeForLike": like, "models": models,
                "galleryFiles": {language: len(rows) for language, rows in galleries.items()},
                "queryCounts": {language: len(rows) for language, rows in plan["queries"].items()},
                "environment": {"python": sys.executable, "torch": torch.__version__,
                                **training.device_facts(device)},
                "elapsedSeconds": round(time.monotonic() - started, 3)}
    write_once(target / EXPORT_EVIDENCE, canonical(evidence), RETRIEVAL_ROOT)
    return {"command": "export-index", "runId": run_id, "outcome": outcome, "directory": str(target)}
