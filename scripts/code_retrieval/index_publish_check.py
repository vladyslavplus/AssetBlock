"""index-publish-check: publish the validation indexes to the owned pgvector sandbox and compare ranking.

Two stages, two interpreters. Keep each interpreter requirement explicit:

- ``--stage query-vectors`` runs in the training interpreter (artifacts/feasibility_pilot/env/ml,
  CUDA, torch and tree-sitter). It recomputes the full training and prepare binding, builds the
  admitted validation plan, verifies the export evidence and every export index, re-encodes every
  validation query with each compared encoder (E0..E3) and writes write-once FP32 query-vector files
  plus a manifest (galleries, query order and digests) into the new run directory.
- ``--stage publish-and-rank`` runs in the index interpreter (artifacts/code_analysis/env/index,
  psycopg, NO torch and NO tree-sitter). It re-verifies the bindings from files, rebuilds the galleries
  from the verified query manifest and binds them to the export index keys, publishes new index keys
  only through ``index_writer.publish_index`` (after the runtime gate), then runs the exact database
  ranking for every model and query and compares it with the CPU ranking over the same index files.

The publish stage never imports torch, training, checkpoint, retrieval_eval or validation_plan; the
guards it uses live in ``index_verify``, which is torch-free.
"""

import os
import struct
import sys
import time

from scripts.code_analysis import sandbox
from scripts.code_analysis.code_index import exact_query
from scripts.code_analysis.contracts import LANGUAGES, canonical, digest
from scripts.code_analysis.corpus import write_once
from scripts.code_analysis.evaluation import cpu_rank, timed_percentiles
from scripts.code_analysis.index_writer import prepare_payload, publish_index
from scripts.code_analysis.representations import vector_bytes
from scripts.feasibility_pilot.paths import REPO_ROOT

from . import admission, config as config_module, index_verify, inputs, prepare
from .index_verify import EMBEDDING_DIMENSIONS, EVALUATION_SCHEMA, TRAINED_EPOCHS
from .paths import RETRIEVAL_ROOT, run_dir

STAGES = ("query-vectors", "publish-and-rank")
EVIDENCE_SCHEMA = "code-retrieval-db-check-evidence-v1"
QUERY_SCHEMA = "code-retrieval-query-vectors-v1"
PURPOSE = "CODE_RETRIEVAL_DB_INDEX_CHECK"
QUERY_DIR = "query-vectors"
QUERY_MANIFEST = "manifest.json"
EVIDENCE = "db-check-evidence.json"
AGREEMENT = "db-agreement.json"
FAILURE = "query-vectors-failure.json"
PREPARE_DATA = "prepare-data.json"
TRAIN_EVIDENCE = "train-evidence.json"
EVALUATION_EVIDENCE = "evaluation-evidence.json"
MODEL_LABELS = ("E0", "E1", "E2", "E3")
TOP = 40
WARMUP = 3
TIE_TOLERANCE = 1e-5
DISTANCE_TOLERANCE = 1e-5
PROVENANCE_PREFIX = "export-index"
SNAPSHOT_DOMAIN = "retrieval-db-validation-snapshot-v1"
MAX_BYTES = inputs.MAX_ARTIFACT_BYTES


def _safe(text):
    """Remove any sandbox password from text that could reach evidence."""
    dsn = os.environ.get("ASSETBLOCK_CODE_INDEX_DSN") or ""
    if "password=" in dsn:
        secret = dsn.split("password=", 1)[1].split()[0]
        text = text.replace(secret, "<redacted>")
    return text


def failure_evidence(run_id, reason):
    """Failed evidence: never a SUCCEEDED outcome, never a DSN."""
    return {"schema": EVIDENCE_SCHEMA, "runId": run_id, "outcome": "FAILED_ERROR", "reason": _safe(reason),
            "Purpose": PURPOSE, "CanAuthorizePublication": False, "SecurityEvidence": "NOT_RUN"}


def _verified_evaluation(raw, *, train_run_id, index_run_id, prepare_binding, corpus):
    """The evaluation evidence must describe the same training run, index run, prepare run and corpus."""
    evaluation = inputs.parse_json(raw, "evaluation evidence")
    if evaluation.get("schema") != EVALUATION_SCHEMA or evaluation.get("outcome") != "SUCCEEDED":
        raise ValueError("evaluation evidence is not a SUCCEEDED evaluation")
    identity = evaluation.get("identity") or {}
    if identity.get("trainRunId") != train_run_id or identity.get("indexRunId") != index_run_id:
        raise ValueError("evaluation evidence was written for another training or index run")
    if identity.get("prepare") != prepare_binding:
        raise ValueError("evaluation evidence prepare binding differs")
    for key, value in corpus.items():
        if identity.get(key) != value:
            raise ValueError("evaluation evidence identity drift: " + key)
    return evaluation


def _prepare_binding(raw, prepare_run_id):
    """Binding from the admitted prepare-data bytes; the same fields training.bind_prepare_run returns."""
    payload = inputs.parse_json(raw, PREPARE_DATA)
    if payload.get("schema") != prepare.SCHEMA or payload.get("runId") != prepare_run_id:
        raise ValueError("prepare-data schema or run id mismatch")
    training = payload.get("trainingAdmission")
    if payload.get("status") != admission.ADMITTED or not isinstance(training, dict) \
            or training.get("status") != admission.ADMITTED:
        raise ValueError("prepare run is not ADMITTED")
    return {"runId": prepare_run_id, "prepareDataSha256": digest(raw), "prepareDataBytes": len(raw),
            "trainingAdmissionSha256": digest(canonical(training)),
            "admittedTriplets": training["totals"]["admittedTriplets"]}, payload


def _fragment(file_row, index_run_id):
    """Whole-file unit for one validation gallery file; every flag is derived from the reviewed row."""
    if file_row.get("partition") != "validation":
        raise ValueError("gallery file outside the validation partition: " + file_row["fileId"])
    if file_row.get("searchableExecutable") is not True:
        raise ValueError("gallery file is not searchable and executable: " + file_row["fileId"])
    if file_row.get("generated") or file_row.get("vendor"):
        raise ValueError("gallery file is generated or vendored: " + file_row["fileId"])
    return {"fragmentId": file_row["fileId"], "fileId": file_row["fileId"], "sourceSha256": file_row["sha256"],
            "sha256": file_row["sha256"], "startByte": 0, "endByte": file_row["bytes"],
            "language": file_row["language"], "dialect": file_row["language"],
            "canonicalIndexed": True, "searchable": True, "executable": True,
            "provenanceRef": PROVENANCE_PREFIX + ":" + index_run_id + "; source:" + file_row["sha256"]}


def language_payload(snapshot, language, index_manifest_sha256, manifest_model_key, info, gallery, indexed_rows,
                     index_run_id):
    """One DB payload per (model, language): whole-file units and their chunk rows, through prepare_payload.

    ``indexed_rows`` are the chunk rows already verified by ``index_verify.load_index``, in manifest order.
    """
    fragments, files = [], []
    for item in gallery:
        row = item["row"]
        if row["fileId"] != item["fileId"]:
            raise ValueError("gallery entry is an alias of another file: " + item["fileId"])
        files.append(row)
        fragments.append(_fragment(row, index_run_id))
    chunks = [{"fragmentId": row["fragmentId"], "chunkOrdinal": row["chunkOrdinal"],
               "tokenStart": row["tokenStart"], "tokenEnd": row["tokenEnd"],
               "representationSha256": row["representationSha256"], "vector": row["vector"]}
              for row in indexed_rows]
    identity = {"index_key": info["indexKey"], "model_key": manifest_model_key, "gallery_sha256": info["galleryKey"],
                "representation_sha256": index_manifest_sha256, "partition": "validation"}
    return prepare_payload(snapshot, identity, files, fragments, chunks)


def snapshot_identity(loaded, index, report_sha256, prepare_binding):
    sources = inputs.value(loaded, inputs.SOURCE_MANIFEST)["sources"]
    return {"snapshot_id": digest(canonical([SNAPSHOT_DOMAIN, loaded["manifestSha256"],
                                             prepare_binding["runId"], report_sha256])),
            "manifest_sha256": loaded["manifestSha256"],
            "rights_sha256": digest(canonical({row["sourceId"]: row.get("rights", {}) for row in sources})),
            "split_sha256": digest(canonical(index["lineage"]["splitFiles"]))}


def gallery_plan(index, query_manifest):
    """Galleries from the verified training-stage manifest; load_index binds them to the export index keys."""
    files = {row["fileId"]: row for row in index["files"]}
    galleries = {}
    for language in LANGUAGES:
        ids = sorted({file_id for query in query_manifest["queries"] if query["language"] == language
                      for file_id in query["scoredGallery"]})
        missing = [file_id for file_id in ids if file_id not in files]
        if missing:
            raise ValueError("gallery file missing from the reviewed index: " + missing[0])
        galleries[language] = [{"fileId": file_id, "sha256": files[file_id]["sha256"], "row": files[file_id]}
                               for file_id in ids]
        if index_verify.gallery_key(galleries[language]) != query_manifest["galleryKeys"][language]:
            raise ValueError("gallery differs from the training stage gallery: " + language)
    return {"galleries": galleries, "reportSha256": query_manifest["corpus"]["validationReportSha256"]}


def compare_query(db_rows, cpu_scores, *, limit=TOP):
    """Agreement of one query: DB distances and order against the CPU cosine ranking over the same gallery.

    ``db_rows`` is the database order ``[{fileId, distance}]``; ``cpu_scores`` maps every scored file
    to its CPU max-chunk cosine. Order differences whose cosines differ by at most TIE_TOLERANCE are
    near ties (both orders are reported); anything else is a disagreement.
    """
    disagreements, near_ties = [], []
    max_difference = 0.0
    for row in db_rows:
        file_id = row["fileId"]
        if file_id not in cpu_scores:
            disagreements.append({"kind": "FILE_NOT_IN_GALLERY", "fileId": file_id})
            continue
        difference = abs(row["distance"] - (1.0 - cpu_scores[file_id]))
        max_difference = max(max_difference, difference)
        if difference > DISTANCE_TOLERANCE:
            disagreements.append({"kind": "DISTANCE", "fileId": file_id, "difference": difference})
    cpu_order = sorted(cpu_scores, key=lambda item: (-cpu_scores[item], item))[:limit]
    db_order = [row["fileId"] for row in db_rows]
    if len(db_order) != len(cpu_order):
        disagreements.append({"kind": "LENGTH", "database": len(db_order), "cpu": len(cpu_order)})
    for position in range(min(len(db_order), len(cpu_order))):
        if db_order[position] == cpu_order[position]:
            continue
        database_score = cpu_scores.get(db_order[position])
        cpu_score = cpu_scores[cpu_order[position]]
        if database_score is None or abs(database_score - cpu_score) > TIE_TOLERANCE:
            disagreements.append({"kind": "ORDER", "position": position + 1, "database": db_order[position],
                                  "cpu": cpu_order[position]})
        else:
            near_ties.append({"position": position + 1, "database": db_order[position],
                              "cpu": cpu_order[position], "databaseScore": database_score,
                              "cpuScore": cpu_score})
    return {"exact": db_order == cpu_order, "nearTies": near_ties, "disagreements": disagreements,
            "maxAbsScoreDifference": max_difference}


def inventory(connection):
    """Read-only digest of every sandbox index, embedding set, snapshot and fragment set.

    Taken before and after publishing; rows that existed before must still be present and identical.
    """
    with connection.cursor() as cursor:
        cursor.execute("SELECT index_key, snapshot_id, model_key, gallery_sha256, representation_sha256, partition, "
                       "expected_rows, vector_sha256, state FROM code_lab.code_indexes ORDER BY index_key")
        indexes = [list(row) for row in cursor.fetchall()]
        cursor.execute("SELECT index_key, count(*), md5(string_agg(e::text, '|' ORDER BY fragment_id, chunk_ordinal)) "
                       "FROM code_lab.code_embeddings e GROUP BY index_key ORDER BY index_key")
        embeddings = [list(row) for row in cursor.fetchall()]
        cursor.execute("SELECT snapshot_id, manifest_sha256, rights_sha256, split_sha256, state "
                       "FROM code_lab.corpus_snapshots ORDER BY snapshot_id")
        snapshots = [list(row) for row in cursor.fetchall()]
        cursor.execute("SELECT snapshot_id, count(*), md5(string_agg(f::text, '|' ORDER BY fragment_id)) "
                       "FROM code_lab.code_fragments f GROUP BY snapshot_id ORDER BY snapshot_id")
        fragments = [list(row) for row in cursor.fetchall()]
    return {"indexes": indexes, "embeddings": embeddings, "snapshots": snapshots, "fragments": fragments}


SECTIONS = ("indexes", "embeddings", "snapshots", "fragments")


def unchanged_existing(before, after):
    """Every row that existed before must still be present and identical; new rows may only be added."""
    for section in SECTIONS:
        present = {tuple(row) for row in after[section]}
        for row in before[section]:
            if tuple(row) not in present:
                return {"unchanged": False, "section": section, "row": row}
    return {"unchanged": True, "section": None, "row": None}


def _summary(records):
    total = {"queries": 0, "exactMatches": 0, "nearTies": 0, "disagreements": 0, "maxAbsScoreDifference": 0.0}
    for item in records:
        total["queries"] += 1
        total["exactMatches"] += item["exact"]
        total["nearTies"] += len(item["nearTies"])
        total["disagreements"] += len(item["disagreements"])
        total["maxAbsScoreDifference"] = max(total["maxAbsScoreDifference"], item["maxAbsScoreDifference"])
    return total


def _query_files(target, manifest):
    """Read every query-vector file and bind it to the manifest: hash, bytes, whole vectors and window count."""
    if manifest.get("schema") != QUERY_SCHEMA or manifest.get("CanAuthorizePublication") is not False:
        raise ValueError("query-vector manifest schema mismatch")
    windows = sum(query["windows"] for query in manifest["queries"])
    vectors = {}
    for label in MODEL_LABELS:
        raw = inputs.read_bytes(target / QUERY_DIR / (label + ".fp32le"), MAX_BYTES)
        info = manifest["labels"][label]
        if len(raw) != info["bytes"] or digest(raw) != info["sha256"]:
            raise ValueError("query vector bytes drift: " + label)
        if len(raw) % (EMBEDDING_DIMENSIONS * 4):
            raise ValueError("query vector file is not a whole number of vectors: " + label)
        vectors[label] = [raw[offset:offset + EMBEDDING_DIMENSIONS * 4]
                          for offset in range(0, len(raw), EMBEDDING_DIMENSIONS * 4)]
        if len(vectors[label]) != info["vectors"] or len(vectors[label]) != windows:
            raise ValueError("query vector count differs from the manifest windows: " + label)
    return vectors


def require_query_manifest(query_manifest, *, run_id, prepare_binding, corpus, evidence_digests, query_counts):
    """The query manifest must describe this run, prepare run, corpus and evidence, with the evaluated query counts."""
    if query_manifest.get("runId") != run_id:
        raise ValueError("query manifest run id differs from this run")
    if query_manifest.get("prepare") != prepare_binding:
        raise ValueError("query manifest prepare binding differs from prepare-data.json")
    if query_manifest.get("corpus") != corpus:
        raise ValueError("query manifest corpus identity differs from this run's inputs")
    for field, value in evidence_digests.items():
        if query_manifest.get(field) != value:
            raise ValueError("query manifest binding differs: " + field)
    for label in MODEL_LABELS:
        if query_counts.get(label) != len(query_manifest["queries"]):
            raise ValueError("query count differs from the evaluation evidence: " + label)


def _float_vectors(raw_rows):
    return [list(struct.unpack("<%df" % EMBEDDING_DIMENSIONS, raw)) for raw in raw_rows]


# -- stage 1: training interpreter ---------------------------------------------------------------
def query_vectors_stage(*, run_id, inputs_path, config_path, prepare_run_id, train_run_id, index_run_id,
                        evaluation_run_id):
    """Training interpreter only: full binding, export index verification and query-vector files."""
    import torch

    from . import checkpoint, retrieval_eval, retrieval_models, training, validation_plan

    loaded = inputs.load(inputs_path)
    config_loaded = config_module.load(config_path)
    index = prepare.reviewed_index(loaded)
    prepare_binding, _triplets, prepare_payload = training.admitted_binding(loaded, config_loaded, prepare_run_id,
                                                                            index)
    expectations = checkpoint.load_training_evidence(run_dir(train_run_id))
    index_verify.require_training_binding(expectations, loaded=loaded, config_loaded=config_loaded,
                                          prepare_binding=prepare_binding)
    plan = validation_plan.build(loaded, index, prepare_payload["validationQueries"])
    corpus = index_verify.corpus_identity(loaded, config_loaded, plan, prepare_binding)
    export_raw = inputs.read_bytes(run_dir(index_run_id) / index_verify.EXPORT_EVIDENCE, MAX_BYTES)
    export_evidence = inputs.parse_json(export_raw, "export evidence")
    index_verify.require_export_evidence(export_evidence, index_run_id=index_run_id, train_run_id=train_run_id,
                                         expectations=expectations, corpus=corpus)
    evaluation_raw = inputs.read_bytes(run_dir(evaluation_run_id) / EVALUATION_EVIDENCE, MAX_BYTES)
    evaluation = _verified_evaluation(evaluation_raw, train_run_id=train_run_id, index_run_id=index_run_id,
                                      prepare_binding=prepare_binding, corpus=corpus)
    target = run_dir(run_id)
    target.mkdir(parents=True, exist_ok=False)
    flat_queries = [query for language in LANGUAGES for query in plan["queries"][language]]
    try:
        device = checkpoint.cuda_device()
        checkpoint.configure_numerics()
        labels, windows = {}, None
        for label in MODEL_LABELS:
            entry = retrieval_models.frozen_model(device) if label == "E0" else \
                retrieval_models.trained_model(run_dir(train_run_id), TRAINED_EPOCHS[label], expectations, device)
            model_key = index_verify.model_key(entry["identity"])
            manifest, _ = index_verify.load_index(index_run_id, label, plan, corpus, model_key, export_evidence)
            recorded = evaluation["identity"]["indexes"][label]
            if recorded["modelKey"] != model_key or any(
                    recorded["indexKeys"][language] != manifest["languages"][language]["indexKey"]
                    for language in LANGUAGES):
                raise ValueError("evaluation evidence index identity differs from the index manifest: " + label)
            encoded = retrieval_eval.query_vectors_for(entry, plan, loaded["sourcesRoot"], device)
            windows = {query["queryId"]: len(encoded[query["queryId"]]["vectors"]) for query in flat_queries}
            flat = [vector for query in flat_queries for vector in encoded[query["queryId"]]["vectors"]]
            raw = vector_bytes(flat)
            write_once(target / QUERY_DIR / (label + ".fp32le"), raw, RETRIEVAL_ROOT)
            labels[label] = {"modelKey": model_key, "vectors": len(flat), "bytes": len(raw), "sha256": digest(raw)}
            del entry
            torch.cuda.empty_cache()
        queries = [{"queryId": query["queryId"], "language": query["language"], "windows": windows[query["queryId"]],
                    "scoredGallery": sorted(query["scoredGallery"])} for query in flat_queries]
        manifest = {"schema": QUERY_SCHEMA, "runId": run_id, "Purpose": PURPOSE, "CanAuthorizePublication": False,
                    "trainRunId": train_run_id, "indexRunId": index_run_id, "evaluationRunId": evaluation_run_id,
                    "trainingEvidenceSha256": expectations["evidenceSha256"],
                    "exportEvidenceSha256": digest(export_raw),
                    "evaluationEvidenceSha256": digest(evaluation_raw),
                    "prepare": prepare_binding, "corpus": corpus, "dimension": EMBEDDING_DIMENSIONS,
                    "labels": labels, "queries": queries,
                    "galleryKeys": {language: index_verify.gallery_key(plan["galleries"][language])
                                    for language in LANGUAGES},
                    "environment": {"python": sys.executable, "torch": torch.__version__}}
        write_once(target / QUERY_DIR / QUERY_MANIFEST, canonical(manifest), RETRIEVAL_ROOT)
        return {"command": "index-publish-check", "stage": "query-vectors", "runId": run_id, "outcome": "SUCCEEDED",
                "directory": str(target)}
    except Exception as exc:  # a failed stage leaves failure evidence, never a success manifest
        write_once(target / FAILURE, canonical(failure_evidence(run_id, type(exc).__name__ + ": " + str(exc))),
                   RETRIEVAL_ROOT)
        return {"command": "index-publish-check", "stage": "query-vectors", "runId": run_id,
                "outcome": "FAILED_ERROR", "directory": str(target)}


# -- stage 2: index interpreter (psycopg, no torch) ----------------------------------------------
def publish_and_rank_stage(*, run_id, inputs_path, config_path, prepare_run_id, train_run_id, index_run_id,
                           evaluation_run_id, runtime_review):
    """Index interpreter only: rebind from files, publish new keys, rank in the database, compare with CPU."""
    import psycopg

    loaded = inputs.load(inputs_path)
    config_loaded = config_module.load(config_path)
    index = prepare.reviewed_index(loaded)
    target = run_dir(run_id)
    if not target.is_dir():
        raise ValueError("query-vector run directory must exist before the publish stage")
    review = sandbox.runtime_review_root(runtime_review)
    if review is None:
        raise ValueError("publishing requires --runtime-review with root-approval.json, complete-package.json, "
                         "scope-supplement.json and wheels/")
    prepare_binding, prepare_payload = _prepare_binding(
        inputs.read_bytes(run_dir(prepare_run_id) / PREPARE_DATA, MAX_BYTES), prepare_run_id)
    train_raw = inputs.read_bytes(run_dir(train_run_id) / TRAIN_EVIDENCE, MAX_BYTES)
    train_evidence = inputs.parse_json(train_raw, "training evidence")
    if train_evidence.get("outcome") != "SUCCEEDED":
        raise ValueError("training evidence is not SUCCEEDED")
    expectations = {"evidenceSha256": digest(train_raw), "identity": train_evidence["identity"]}
    index_verify.require_training_binding(expectations, loaded=loaded, config_loaded=config_loaded,
                                          prepare_binding=prepare_binding)
    query_manifest = inputs.parse_json(inputs.read_bytes(target / QUERY_DIR / QUERY_MANIFEST, MAX_BYTES),
                                       "query manifest")
    plan = gallery_plan(index, query_manifest)
    corpus = index_verify.corpus_identity(loaded, config_loaded, plan, prepare_binding)
    export_raw = inputs.read_bytes(run_dir(index_run_id) / index_verify.EXPORT_EVIDENCE, MAX_BYTES)
    export_evidence = inputs.parse_json(export_raw, "export evidence")
    index_verify.require_export_evidence(export_evidence, index_run_id=index_run_id, train_run_id=train_run_id,
                                         expectations=expectations, corpus=corpus)
    if export_evidence["identity"]["prepare"] != prepare_binding:
        raise ValueError("export evidence prepare binding differs from prepare-data.json")
    evaluation_raw = inputs.read_bytes(run_dir(evaluation_run_id) / EVALUATION_EVIDENCE, MAX_BYTES)
    evaluation = _verified_evaluation(evaluation_raw, train_run_id=train_run_id, index_run_id=index_run_id,
                                      prepare_binding=prepare_binding, corpus=corpus)
    denominators = evaluation.get("denominators") or {}
    require_query_manifest(
        query_manifest, run_id=run_id, prepare_binding=prepare_binding, corpus=corpus,
        evidence_digests={"trainingEvidenceSha256": expectations["evidenceSha256"],
                          "exportEvidenceSha256": digest(export_raw),
                          "evaluationEvidenceSha256": digest(evaluation_raw)},
        query_counts={label: (denominators.get(label) or {}).get("queries") for label in MODEL_LABELS})
    query_vectors = _query_files(target, query_manifest)
    queries = query_manifest["queries"]
    snapshot = snapshot_identity(loaded, index, plan["reportSha256"], prepare_binding)
    indexes, payloads = {}, {}
    for label in MODEL_LABELS:
        manifest, indexed = index_verify.load_index(index_run_id, label, plan, corpus,
                                                    query_manifest["labels"][label]["modelKey"], export_evidence)
        if manifest["modelKey"] != export_evidence["models"][label]["modelKey"]:
            raise ValueError("export evidence model key differs: " + label)
        indexes[label] = (manifest, indexed)
        directory = run_dir(index_run_id) / index_verify.INDEX_DIR / label
        index_manifest_sha = digest(inputs.read_bytes(directory / index_verify.MANIFEST_NAME, MAX_BYTES))
        for language in LANGUAGES:
            info = manifest["languages"][language]
            gallery = plan["galleries"][language]
            payloads[(label, language)] = language_payload(
                snapshot, language, index_manifest_sha, manifest["modelKey"], info, gallery, indexed[language],
                index_run_id)

    connection = None
    publish, agreement, timings = {}, {}, {}
    try:
        sandbox.ensure_container()
        sandbox.rotate_ephemeral_password()
        connection = sandbox.connect_sandbox()
        initialization = sandbox.initialize_if_empty(connection)
        existing_before = inventory(connection)
        for (label, language), payload in payloads.items():
            publish.setdefault(label, {})[language] = {
                **publish_index(connection, payload, runtime_root=REPO_ROOT, runtime_review=review),
                "indexKey": payload["identity"]["index_key"], "vectorSha256": payload["vectorSha256"],
                "rows": payload["expectedRows"]}
        existing_after = inventory(connection)
        existing_check = unchanged_existing(existing_before, existing_after)
        if not existing_check["unchanged"]:
            raise ValueError("existing sandbox row changed in section {}: {}".format(
                existing_check["section"], existing_check["row"]))
        existing_counts = {"before": {section: len(existing_before[section]) for section in SECTIONS},
                           "after": {section: len(existing_after[section]) for section in SECTIONS}}
        records_by_label = {label: [] for label in MODEL_LABELS}
        for label in MODEL_LABELS:
            manifest, indexed = indexes[label]
            vectors_for_label = query_vectors[label]
            cursor = 0
            for query in queries:
                count = query["windows"]
                query_chunks = _float_vectors(vectors_for_label[cursor:cursor + count])
                cursor += count
                language = query["language"]
                scored = list(query["scoredGallery"])
                scored_set = set(scored)
                started = time.perf_counter()
                db_rows = exact_query(connection, manifest["languages"][language]["indexKey"], query_chunks, scored,
                                      language, partition="validation", limit=TOP)
                timings.setdefault(label, []).append(time.perf_counter() - started)
                rows = [row for row in indexed[language] if row["fileId"] in scored_set]
                cpu = cpu_rank(query_chunks, rows, key="fileId", limit=len(scored_set))
                cpu_scores = {item["fileId"]: item["score"] for item in cpu}
                outcome = compare_query([{"fileId": row["fileId"], "distance": row["distance"]} for row in db_rows],
                                        cpu_scores, limit=TOP)
                records_by_label[label].append({"queryId": query["queryId"], "language": language, **outcome})
        for label in MODEL_LABELS:
            agreement[label] = {language: _summary([row for row in records_by_label[label]
                                                    if row["language"] == language]) for language in LANGUAGES}
            agreement[label]["overall"] = _summary(records_by_label[label])
        latency = {label: {**timed_percentiles(timings[label][WARMUP:]), "warmupExcluded": WARMUP}
                   for label in MODEL_LABELS}
        agreement_raw = canonical({"schema": EVIDENCE_SCHEMA, "runId": run_id, "records": records_by_label})
        write_once(target / AGREEMENT, agreement_raw, RETRIEVAL_ROOT)
        with connection.cursor() as cursor:
            cursor.execute("SELECT current_setting('server_version')")
            server_version = cursor.fetchone()[0]
    finally:
        if connection is not None:
            connection.close()
    disagreements = sum(agreement[label]["overall"]["disagreements"] for label in MODEL_LABELS)
    failure = None
    if disagreements:
        failure = "database ranking disagrees with the CPU ranking beyond tolerance: {} cases".format(disagreements)
    if failure is not None:
        evidence = failure_evidence(run_id, failure)
    else:
        evidence = {"schema": EVIDENCE_SCHEMA, "runId": run_id, "outcome": "SUCCEEDED", "reason": None,
                    "Purpose": PURPOSE, "CanAuthorizePublication": False, "SecurityEvidence": "NOT_RUN",
                    "identity": {**corpus, "prepare": prepare_binding,
                                 "trainingEvidenceSha256": expectations["evidenceSha256"],
                                 "exportEvidenceSha256": digest(export_raw),
                                 "evaluationEvidenceSha256": digest(evaluation_raw),
                                 "queryManifestSha256": digest(inputs.read_bytes(target / QUERY_DIR / QUERY_MANIFEST,
                                                                                 MAX_BYTES)),
                                 "trainRunId": train_run_id, "indexRunId": index_run_id,
                                 "evaluationRunId": evaluation_run_id},
                    "sandbox": {"container": sandbox.CONTAINER, "image": sandbox.IMAGE, "bind": sandbox.BIND,
                                "volume": sandbox.VOLUME, "database": sandbox.DATABASE,
                                "initialization": initialization},
                    "publish": publish, "agreement": agreement, "latency": latency,
                    "existingSandbox": {**existing_counts, "existingRowsUnchanged": True},
                    "agreementDetailsSha256": digest(agreement_raw),
                    "measurementScope": {
                        "dbQueryLatency": "one exact_query call per query and model, timed around the whole call; "
                                          "the timed call includes the vector text build, the service preflight "
                                          "and the index guard queries, plus the ranking query and the loopback "
                                          "round trip",
                        "cpuRanking": "not timed here; CPU ranking is exhaustive pure Python over the same "
                                      "index files and query vectors and is used only for comparison"},
                    "environment": {"python": sys.executable, "psycopg": psycopg.__version__,
                                    "serverVersion": server_version}}
    write_once(target / EVIDENCE, canonical(evidence), RETRIEVAL_ROOT)
    return {"command": "index-publish-check", "stage": "publish-and-rank", "runId": run_id,
            "outcome": evidence["outcome"], "directory": str(target)}


def run_stage(stage, **arguments):
    if stage not in STAGES:
        raise ValueError("unknown stage: " + str(stage))
    if stage == "query-vectors":
        arguments.pop("runtime_review", None)  # publishing needs the gate; query vectors never publish
        return query_vectors_stage(**arguments)
    try:
        return publish_and_rank_stage(**arguments)
    except Exception as exc:  # every failed publish-and-rank run leaves failed evidence
        target = run_dir(arguments["run_id"])
        write_once(target / EVIDENCE, canonical(failure_evidence(arguments["run_id"],
                                                                 type(exc).__name__ + ": " + str(exc))),
                   RETRIEVAL_ROOT)
        return {"command": "index-publish-check", "stage": "publish-and-rank", "runId": arguments["run_id"],
                "outcome": "FAILED_ERROR", "directory": str(target)}
