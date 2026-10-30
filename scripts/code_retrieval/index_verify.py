"""Torch-free verification of training binding, export evidence and validation index files.

The training, export and evaluation stages import this module in the CUDA interpreter; the
database check imports it in the index interpreter, which has psycopg but no torch. Keep every
import here torch-free.
"""

from scripts.code_analysis.contracts import LANGUAGES, canonical, digest
from scripts.code_analysis.evaluation import freeze_protocol
from scripts.code_analysis.representations import read_vectors
from scripts.feasibility_pilot.paths import REPO_ROOT

from . import inputs
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


def protocol_sha256():
    return freeze_protocol(inputs.parse_json(PROTOCOL_PATH.read_bytes(), "evaluation protocol"))["sha256"]


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
