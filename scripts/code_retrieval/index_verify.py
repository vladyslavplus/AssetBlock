"""Torch-free verification of training binding, export evidence and validation index files.

The training, export and evaluation stages import this module in the CUDA interpreter; the
database check imports it in the index interpreter, which has psycopg but no torch. Keep every
import here torch-free.
"""

import base64

from scripts.code_analysis.contracts import LANGUAGES, canonical, digest, sha
from scripts.code_analysis.evaluation import freeze_protocol
from scripts.code_analysis.representations import read_vectors
from scripts.feasibility_pilot.paths import REPO_ROOT

from . import admission, inputs, prepare
from .paths import run_dir

EMBEDDING_DIMENSIONS = 768
REQUIRED_KEYS = ("kind", "label", "fullStateSha256", "tokenizerHashes", "chunking", "pooling", "dimension")
INDEX_SCHEMA = "code-retrieval-validation-index-v1"
EXPORT_SCHEMA = "code-retrieval-export-index-evidence-v1"
EVALUATION_SCHEMA = "code-retrieval-evaluation-evidence-v1"
TRAINED_EPOCHS = {"E1": 1, "E2": 2, "E3": 3}
PROTOCOL_PATH = REPO_ROOT / "scripts" / "code_analysis" / "config" / "evaluation-protocol.json"
INDEX_DIR = "index"
EXPORT_EVIDENCE = "export-evidence.json"
MANIFEST_NAME = "index-manifest.json"
REFERENCE_PROBE = "reference-probe.json"
LIVE_PROBE = "live-probe.json"
EVALUATION_EVIDENCE = "evaluation-evidence.json"
SELECTION_FILE = "selection.json"
DB_EVIDENCE_NAME = "db-check-evidence.json"
DB_AGREEMENT_NAME = "db-agreement.json"
# Training-side layout and identity records, read by every stage that binds to a training run.
TRAIN_EVIDENCE_SCHEMA = "code-retrieval-train-evidence-v1"
TRAIN_EVIDENCE_NAME = "train-evidence.json"
EXPECTATIONS_SCHEMA = "code-retrieval-reload-expectations-v1"
REFERENCE_SCHEMA = "code-retrieval-eval-reference-v1"
REFERENCE_NAME = "eval-reference.json"
RELOAD_NAME = "reload-check.json"
CHECKPOINT_DIR = "checkpoint"
CHECKPOINT_MANIFEST_NAME = "checkpoint-manifest.json"
SELECTION = "PENDING_VALIDATION"
TRUSTED_IDENTITY = REPO_ROOT / "scripts" / "feasibility_pilot" / "config" / "model-trusted-identity.json"
MAX_JSON_BYTES = 8 * 1024 * 1024
PREPARE_DATA = "prepare-data.json"
CHECKPOINT_SCHEMA = "code-retrieval-trained-checkpoint-v1"
WEIGHTS = "model.safetensors"
SUPPORT_FILES = ("config.json", "vocab.json", "merges.txt", "tokenizer_config.json", "special_tokens_map.json")
MODEL_LABELS = ("E0", "E1", "E2", "E3")
QUERY_SCHEMA = "code-retrieval-query-vectors-v1"
QUERY_DIR = "query-vectors"
QUERY_MANIFEST = "manifest.json"
RELOAD_RECHECK_NAME = "reload-recheck.json"
DB_EVIDENCE_SCHEMA = "code-retrieval-db-check-evidence-v1"


def protocol_sha256(path=PROTOCOL_PATH):
    return freeze_protocol(inputs.parse_json(path.read_bytes(), "evaluation protocol"))["sha256"]


def model_key(identity):
    """Digest of the complete model identity; any changed field changes the key."""
    if not all(key in identity for key in REQUIRED_KEYS) or identity["dimension"] != EMBEDDING_DIMENSIONS:
        raise ValueError("incomplete model identity")
    return digest(canonical(identity))


def gallery_key(gallery):
    return digest(canonical([[row["fileId"], row["sha256"]] for row in gallery]))


def index_key(model_key, gallery, corpus_identity):
    return digest(canonical([model_key, "validation", [[row["fileId"], row["sha256"]] for row in gallery],
                             corpus_identity]))


def corpus_identity(loaded, config_loaded, plan, prepare_binding):
    return {"inputsManifestSha256": loaded["manifestSha256"], "prepareRunId": prepare_binding["runId"],
            "validationReportSha256": plan["reportSha256"], "protocolSha256": protocol_sha256(),
            "configSha256": config_loaded["configSha256"]}


def require_training_binding(expectations, *, loaded, config_loaded, prepare_binding):
    """Refuse trained models whose training identity differs from the inputs, config and prepare run now bound."""
    identity = expectations["identity"]
    prepare = identity.get("prepare") or {}
    compared = [("inputsManifestSha256", identity.get("inputsManifestSha256"), loaded["manifestSha256"]),
                ("configSha256", identity.get("configSha256"), config_loaded["configSha256"])]
    if "configByteSha256" in identity:
        compared.append(("configByteSha256", identity["configByteSha256"], config_loaded["byteSha256"]))
    for key in ("runId", "prepareDataSha256", "trainingAdmissionSha256", "admittedTriplets"):
        compared.append(("prepare." + key, prepare.get(key), prepare_binding[key]))
    for name, found, current in compared:
        if found != current:
            raise ValueError("training evidence binding drift: " + name)


def require_export_evidence(export_evidence, *, index_run_id, train_run_id, expectations, corpus):
    """Refuse indexes whose export evidence does not describe this index run, training run and corpus."""
    if export_evidence.get("schema") != EXPORT_SCHEMA or export_evidence.get("runId") != index_run_id:
        raise ValueError("export evidence schema or run id mismatch")
    if export_evidence.get("outcome") != "SUCCEEDED":
        raise ValueError("index export did not succeed; evaluation refuses its indexes")
    identity = export_evidence.get("identity") or {}
    if (identity.get("trainRunId") != train_run_id
            or identity.get("trainingEvidenceSha256") != expectations["evidenceSha256"]):
        raise ValueError("export evidence was written for another training run")
    for key, value in corpus.items():
        if identity.get(key) != value:
            raise ValueError("export evidence identity drift: " + key)


def load_index(index_run, label, plan, corpus, model_key_value, export_evidence):
    """Verify one index: export evidence, model identity, index and gallery keys, file sizes and digests."""
    directory = run_dir(index_run) / INDEX_DIR / label
    manifest = inputs.parse_json(inputs.read_bytes(directory / MANIFEST_NAME, inputs.MAX_ARTIFACT_BYTES),
                                 "index manifest")
    if manifest.get("schema") != INDEX_SCHEMA or manifest.get("label") != label:
        raise ValueError("index manifest schema or label mismatch: " + label)
    if manifest.get("partition") != "validation":
        raise ValueError("index is not a validation-partition index")
    exported = export_evidence["models"].get(label) or {}
    if manifest["modelKey"] != exported.get("modelKey") or manifest["languages"] != exported.get("languages"):
        raise ValueError("index manifest differs from the export evidence: " + label)
    if model_key(manifest["identity"]) != manifest["modelKey"]:
        raise ValueError("index model key does not match its identity: " + label)
    if manifest["modelKey"] != model_key_value:
        raise ValueError("mixed identity: loaded model differs from the index model: " + label)
    if manifest["corpus"] != corpus:
        raise ValueError("index corpus identity differs from this evaluation: " + label)
    indexes = {}
    for language in LANGUAGES:
        info = manifest["languages"][language]
        gallery = plan["galleries"][language]
        if gallery_key(gallery) != info["galleryKey"]:
            raise ValueError("index gallery differs from the admitted gallery: " + label + "/" + language)
        if index_key(manifest["modelKey"], gallery, corpus) != info["indexKey"]:
            raise ValueError("index key does not recompute: " + label + "/" + language)
        raw = inputs.read_bytes(directory / (language + ".fp32le"), inputs.MAX_ARTIFACT_BYTES)
        if len(raw) != info["vectorBytes"] or digest(raw) != info["vectorSha256"]:
            raise ValueError("index vector bytes drift: " + label + "/" + language)
        chunks_raw = inputs.read_bytes(directory / (language + ".chunks.json"), inputs.MAX_ARTIFACT_BYTES)
        if len(chunks_raw) != info["chunkManifestBytes"] or digest(chunks_raw) != info["chunkManifestSha256"]:
            raise ValueError("index chunk manifest drift: " + label + "/" + language)
        chunk_rows = inputs.parse_json(chunks_raw, "chunk manifest")
        if len(chunk_rows) != info["rows"]:
            raise ValueError("index row count drift: " + label + "/" + language)
        vectors = read_vectors(raw, info["rows"])
        indexes[language] = [{**row, "vector": vector} for row, vector in zip(chunk_rows, vectors)]
    return manifest, indexes


def upstream_identity(path=TRUSTED_IDENTITY):
    """Pinned upstream revision and file hashes from the trusted-identity record."""
    trusted = inputs.parse_json(inputs.read_bytes(path, MAX_JSON_BYTES), "model trusted identity")
    return {"modelRevision": trusted["modelRevision"], "fileHashes": trusted["fileHashes"]}


def prepare_binding_from_bytes(raw, prepare_run_id):
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


def verified_evaluation(raw, *, train_run_id, index_run_id, prepare_binding, corpus):
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


def load_training_evidence(run):
    """Verified training evidence: the per-epoch expectations every trained-checkpoint stage binds to."""
    raw = inputs.read_bytes(run / TRAIN_EVIDENCE_NAME, MAX_JSON_BYTES)
    evidence = inputs.parse_json(raw, TRAIN_EVIDENCE_NAME)
    if evidence.get("schema") != TRAIN_EVIDENCE_SCHEMA or evidence.get("runId") != run.name:
        raise ValueError("training evidence schema or run id mismatch")
    if evidence.get("outcome") != "SUCCEEDED" or evidence.get("CanAuthorizePublication") is not False:
        raise ValueError("training evidence must be SUCCEEDED and non-publishing")
    if evidence.get("selection") != {"selectedForUse": False, "selection": SELECTION}:
        raise ValueError("training evidence selection must stay PENDING_VALIDATION")
    identity = evidence["identity"]
    epochs = {}
    for position, row in enumerate(evidence["epochs"], start=1):
        if row.get("epoch") != position:
            raise ValueError("training evidence epochs are out of order")
        epochs[str(position)] = {"checkpointManifestSha256": row["checkpointManifestSha256"],
                                 "identity": {"runId": run.name, "configSha256": identity["configSha256"],
                                              "inputsManifestSha256": identity["inputsManifestSha256"],
                                              "prepare": identity["prepare"]}}
    return {"schema": EXPECTATIONS_SCHEMA, "runId": run.name, "evidenceSha256": digest(raw),
            "identity": identity, "epochs": epochs}


def reference_record(reference):
    """Validated reference: token ids, FP32 bytes and the stored digest of both."""
    if reference.get("schema") != REFERENCE_SCHEMA or reference.get("dimension") != EMBEDDING_DIMENSIONS:
        raise ValueError("eval reference schema or dimension mismatch")
    chunk_ids = reference["inputIds"]
    if digest(canonical(chunk_ids)) != sha(reference["inputIdsSha256"]):
        raise ValueError("eval reference token ids drift")
    packed = base64.b64decode(reference["vectorsBase64"], validate=True)
    if len(packed) != reference["vectorBytes"] or digest(packed) != sha(reference["vectorSha256"]):
        raise ValueError("eval reference vector bytes drift")
    return chunk_ids, packed


def query_vector_files(directory, manifest, cap):
    """Every query-vector file named by the manifest: exact bytes, whole vectors and matching window counts."""
    if manifest.get("schema") != QUERY_SCHEMA or manifest.get("CanAuthorizePublication") is not False:
        raise ValueError("query-vector manifest schema mismatch")
    windows = sum(query["windows"] for query in manifest["queries"])
    raw_by_label = {}
    for label in MODEL_LABELS:
        raw = inputs.read_bytes(directory / (label + ".fp32le"), cap)
        info = manifest["labels"][label]
        if len(raw) != info["bytes"] or digest(raw) != info["sha256"]:
            raise ValueError("query vector bytes drift: " + label)
        if len(raw) % (EMBEDDING_DIMENSIONS * 4):
            raise ValueError("query vector file is not a whole number of vectors: " + label)
        count = len(raw) // (EMBEDDING_DIMENSIONS * 4)
        if count != info["vectors"] or count != windows:
            raise ValueError("query vector count differs from the manifest windows: " + label)
        raw_by_label[label] = raw
    return raw_by_label


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
