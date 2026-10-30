"""validate-evidence: verify the retrieval evidence chain from bytes and write the evidence package.

Pure file and hash work. The stage imports only torch-free modules, so it runs in the index
interpreter, and its tests need neither torch, CUDA, Docker nor a database. Every named output is
checked by its exact bytes (size first, then SHA256). The stage lists only each checkpoint folder,
never reads another output directory, never calls the inputs loader (which reads the corpus
artifacts), and never opens, reads or hashes the final-test material. Every JSON chain document
it reads is scanned for final-test text, and the scanned names are recorded in the package.

Three statuses stay separate, and each comes only from recorded facts: trainingCompleted,
qualityOutcome, and reviewerAcceptance (a constant, because review is external to the run files).
"""

import hashlib
import os
import re
import sys
from pathlib import Path

from scripts.code_analysis import sandbox
from scripts.code_analysis.contracts import LANGUAGES, canonical, digest
from scripts.code_analysis.corpus import write_once
from scripts.code_analysis.evaluation import freeze_protocol
from scripts.feasibility_pilot.paths import REPO_ROOT, ensure_inside

from . import cli, config as config_module, index_verify, inputs
from .paths import RETRIEVAL_ROOT, run_dir

SCHEMA = "code-retrieval-evidence-package-v1"
PURPOSE = "CODE_RETRIEVAL_EVIDENCE_PACKAGE"
PACKAGE_NAME = "evidence-package.json"
RECONSTRUCTION_NAME = "RECONSTRUCTION.md"
CONFIG_PATH = REPO_ROOT / "scripts" / "code_retrieval" / "config" / "training-config.json"
# Byte identity of the reviewed training configuration. Owner: training-config.json (pinned, not edited here).
REQUIRED_CONFIG_BYTE_SHA256 = "5a7f88c939ce350444aaca4dc945c61dcd942d14afc00a1fe06d3671337ac4e9"
RUNTIME_REVIEW_FILES = ("root-approval.json", "complete-package.json", "scope-supplement.json")
FROZEN_LABEL = "E0"
TRAINED_LABELS = ("E1", "E2", "E3")
# Quality rule, written into every package. A trained model is NO_MEASURABLE_DIFFERENCE_NEAR_CEILING when the
# largest absolute mean paired delta on semantic@20 MRR and Recall is at most NEAR_CEILING_DELTA AND the
# rank-one share of both the frozen and the trained model is at least NEAR_CEILING_SHARE. The delta threshold
# equals one flipped query at the reviewed query count (1/201 is about 0.005), so one changed query is not a
# measurable difference. Smaller losses stay visible in worseOnAnySeed and meanDeltaSign.
NEAR_CEILING_DELTA = 0.005
NEAR_CEILING_SHARE = 0.95
NO_MEASURABLE_DIFFERENCE = "NO_MEASURABLE_DIFFERENCE_NEAR_CEILING"
MEASURABLE_DIFFERENCE = "MEASURABLE_DIFFERENCE"
REVIEWER_ACCEPTANCE = "NOT_RECORDED_IN_EVIDENCE"
FINAL_TEST_TEXT = "final-test"
# The one final-test string a chain document may carry: the prepare run's identity names the membership file it
# was built from, and this stage does not read that file. Owner: the prepare-data identity of the prepare run
# (earlier run evidence, not edited here). Matched by exact document and exact key path, never by substring.
ALLOWED_FINAL_TEST_REFERENCE = ("identity", "inputs", "final-test-membership", "relativePath")
NOT_RECORDED = "NOT RECORDED IN EVIDENCE"
PREPARE_ROLE = ("NOT RECORDED IN EVIDENCE; prepare-data imports no torch, so any interpreter with the repository "
                "dependencies runs it", None)
# (path marker, role name, interpreter path relative to the repository root)
INTERPRETERS = (
    ("feasibility_pilot/env/ml", "pilot CUDA training interpreter (artifacts/feasibility_pilot/env/ml)",
     "artifacts/feasibility_pilot/env/ml/Scripts/python.exe"),
    ("code_analysis/env/ml", "pinned CPU interpreter (artifacts/code_analysis/env/ml)",
     "artifacts/code_analysis/env/ml/Scripts/python.exe"),
    ("code_analysis/env/index", "index interpreter, psycopg without torch (artifacts/code_analysis/env/index)",
     "artifacts/code_analysis/env/index/Scripts/python.exe"),
)
LOCAL_ONLY = ("Checkpoints, query vectors, index vectors and source text are local-only artifacts under artifacts/. "
              "This package carries hashes, byte counts and counts only. Distribution needs a separate rights "
              "decision; nothing is exported out of artifacts.")
STREAM_BLOCK = 1024 * 1024


def _strings(value):
    if isinstance(value, dict):
        for item in value.values():
            yield from _strings(item)
    elif isinstance(value, list):
        for item in value:
            yield from _strings(item)
    elif isinstance(value, str):
        yield value


def interpreter_role(record):
    """Role and interpreter path named by a record; the absolute interpreter path is never copied."""
    for text in _strings(record):
        normalized = text.replace("\\", "/")
        for marker, role, executable in INTERPRETERS:
            if marker in normalized:
                return role, executable
    return NOT_RECORDED, None


def final_test_hits(value, path=()):
    """Exact key paths where final-test text appears: a dict key or a string value. List items show as [index]."""
    if isinstance(value, dict):
        for key, item in value.items():
            if FINAL_TEST_TEXT in key:
                yield path + (key,)
            yield from final_test_hits(item, path + (key,))
    elif isinstance(value, list):
        for index, item in enumerate(value):
            yield from final_test_hits(item, path + ("[%d]" % index,))
    elif isinstance(value, str) and FINAL_TEST_TEXT in value:
        yield path


def _string_at(value, path):
    """The string at an exact key path, or None when the path is absent or holds something else."""
    node = value
    for key in path:
        if not isinstance(node, dict) or key not in node:
            return None
        node = node[key]
    return node if isinstance(node, str) else None


def _expect(condition, message):
    if not condition:
        raise ValueError(message)


def _display(path, base):
    try:
        return path.resolve().relative_to(base.resolve()).as_posix()
    except ValueError:
        return path.name


def _run_file(run_id, name):
    return _display(run_dir(run_id) / name, REPO_ROOT)


def safe_reason(text):
    """Failure reason with no machine path and no sandbox password, as index_publish_check does for the database."""
    root = str(REPO_ROOT)
    for form in (root, root.replace("\\", "/")):
        text = text.replace(form, "<repo>")
    text = re.sub(r"[A-Za-z]:[\\/][^\s'\"]*", "<path>", text)
    text = re.sub(r"(?<![\w.])/(?:[\w.-]+/)+[\w.-]*", "<path>", text)
    dsn = os.environ.get("ASSETBLOCK_CODE_INDEX_DSN") or ""
    if "password=" in dsn:
        parts = dsn.split("password=", 1)[1].split()
        if parts:
            text = text.replace(parts[0], "<redacted>")
    return text


class Inventory:
    """Every file the package names: a display path, the exact byte count and the SHA256 of the bytes.
    JSON documents are scanned for final-test text; each scan result is kept for the package."""

    def __init__(self):
        self.files = {}
        self.scanned = {}
        self.allowed = []

    def allow(self, display, record):
        """Records the one allowed final-test reference found in a document (see ALLOWED_FINAL_TEST_REFERENCE)."""
        self.allowed.append({"document": display, **record})

    def _record(self, display, sha_value, size):
        self.files[display] = {"path": display, "sha256": sha_value, "bytes": size}

    def read(self, display, path, *, expected_sha=None, expected_bytes=None):
        try:
            raw = inputs.read_bytes(path, inputs.MAX_ARTIFACT_BYTES)
        except OSError:
            raise ValueError("missing or unreadable evidence file: " + display) from None
        _expect(raw, "empty evidence file: " + display)
        _expect(expected_bytes is None or len(raw) == expected_bytes, "size mismatch: " + display)
        _expect(expected_sha is None or digest(raw) == expected_sha, "hash mismatch: " + display)
        self._record(display, digest(raw), len(raw))
        return raw

    def scan(self, display, value, allowed=()):
        """Records whether a parsed document names the final-test partition outside the exact allowed key paths."""
        found = any(path not in allowed for path in final_test_hits(value))
        self.scanned[display] = found
        _expect(not found, "final-test appears in " + display)

    def json(self, display, path, *, scan=True):
        raw = self.read(display, path)
        parsed = inputs.parse_json(raw, display)
        _expect(canonical(parsed) == raw, "evidence file is not canonical: " + display)
        if scan:
            self.scan(display, parsed)
        return parsed, raw

    def stream(self, display, path, *, expected_sha, expected_bytes):
        """Large named files (weights): size first, then SHA256, read in blocks."""
        try:
            hasher = hashlib.sha256()
            size = 0
            with path.open("rb") as stream:
                for block in iter(lambda: stream.read(STREAM_BLOCK), b""):
                    hasher.update(block)
                    size += len(block)
        except OSError:
            raise ValueError("missing or unreadable evidence file: " + display) from None
        _expect(size, "empty evidence file: " + display)
        _expect(size == expected_bytes, "size mismatch: " + display)
        _expect(hasher.hexdigest() == expected_sha, "hash mismatch: " + display)
        self._record(display, hasher.hexdigest(), size)

    def record(self, display, raw):
        """Bytes already read and checked by a shared guard."""
        self._record(display, digest(raw), len(raw))

    def listing(self):
        return [self.files[key] for key in sorted(self.files)]


def _verify_sources(inv, *, inputs_path, config_path, protocol_path, trusted_path, base, required_config_sha):
    inputs_raw = inv.read(_display(inputs_path, base), inputs_path)
    inputs_sha = digest(canonical(inputs.parse_json(inputs_raw, "inputs manifest")))
    inv.read(_display(config_path, base), config_path)
    config = config_module.load(config_path)
    _expect(config["byteSha256"] == required_config_sha,
            "configuration byte identity differs from the reviewed training configuration")
    protocol_raw = inv.read(_display(protocol_path, base), protocol_path)
    inv.read(_display(trusted_path, base), trusted_path)
    protocol = inputs.parse_json(protocol_raw, "evaluation protocol")
    return {"inputsSha256": inputs_sha, "config": config, "protocol": protocol,
            "protocolSha256": freeze_protocol(protocol)["sha256"], "protocolFileSha256": digest(protocol_raw),
            "trusted": index_verify.upstream_identity(trusted_path), "inputsPath": _display(inputs_path, base),
            "configPath": _display(config_path, base)}


def _verify_runtime(inv, runtime_review, base):
    """The publish gate's reviewed package: approval files are read and hashed; wheels are not re-hashed here."""
    root = sandbox.runtime_review_root(runtime_review)
    _expect(root is not None, "runtime review directory lacks its approval files or wheels")
    for name in RUNTIME_REVIEW_FILES:
        inv.read(_display(root / name, base), root / name)
    return {"path": _display(root, base), "approvalFiles": list(RUNTIME_REVIEW_FILES)}


def _verify_training(inv, train_id, sources, binding):
    train_dir = run_dir(train_id)
    train_display = train_id + "/" + index_verify.TRAIN_EVIDENCE_NAME
    inv.read(train_display, train_dir / index_verify.TRAIN_EVIDENCE_NAME)
    expectations = index_verify.load_training_evidence(train_dir)
    train, train_raw = inv.json(train_display, train_dir / index_verify.TRAIN_EVIDENCE_NAME)
    config = sources["config"]
    index_verify.require_training_binding(expectations, loaded={"manifestSha256": sources["inputsSha256"]},
                                          config_loaded=config, prepare_binding=binding)
    identity = train["identity"]
    _expect(identity.get("protocolSha256") == config["protocolSha256"], "training admission protocol differs")
    _expect(identity["upstream"]["modelRevision"] == sources["trusted"]["modelRevision"],
            "training upstream revision differs from the trusted identity")
    _expect(identity["upstream"]["fileHashes"] == sources["trusted"]["fileHashes"],
            "training upstream file hashes differ from the trusted identity")
    _expect(identity["upstreamLoadedFullStateSha256"] == identity["upstreamBaselineFullStateSha256"],
            "upstream loaded state differs from its baseline")
    epochs = config["config"]["training"]["epochs"]
    _expect(len(train["epochs"]) == epochs, "training epoch count differs from the configuration")
    epoch_facts, manifests = [], {}
    for number in range(1, epochs + 1):
        row = train["epochs"][number - 1]
        folder = "epoch-" + str(number)
        epoch_dir = train_dir / folder
        manifest, manifest_raw = inv.json(train_id + "/" + folder + "/" + index_verify.CHECKPOINT_MANIFEST_NAME,
                                          epoch_dir / index_verify.CHECKPOINT_MANIFEST_NAME)
        _expect(digest(manifest_raw) == row["checkpointManifestSha256"],
                "checkpoint manifest hash differs from training evidence: " + folder)
        _expect(manifest.get("schema") == index_verify.CHECKPOINT_SCHEMA and manifest.get("epoch") == number,
                "checkpoint manifest schema or epoch mismatch: " + folder)
        _expect(manifest.get("selectedForUse") is False and manifest.get("selection") == index_verify.SELECTION,
                "checkpoint manifest must not be selected for use: " + folder)
        _expect(manifest.get("runId") == train_id and manifest.get("configSha256") == config["configSha256"]
                and manifest.get("inputsManifestSha256") == sources["inputsSha256"]
                and manifest.get("prepare") == identity["prepare"],
                "checkpoint manifest identity differs from training evidence: " + folder)
        _expect(manifest.get("upstream", {}).get("modelRevision") == sources["trusted"]["modelRevision"]
                and manifest["upstream"].get("supportFileHashes") == {
                    name: sources["trusted"]["fileHashes"][name] for name in index_verify.SUPPORT_FILES},
                "checkpoint upstream identity differs from the trusted identity: " + folder)
        files = manifest.get("files")
        _expect(isinstance(files, dict) and set(files) == {index_verify.WEIGHTS, *index_verify.SUPPORT_FILES},
                "checkpoint manifest file set mismatch: " + folder)
        checkpoint_dir = epoch_dir / index_verify.CHECKPOINT_DIR
        _expect({entry.name for entry in checkpoint_dir.iterdir()} == set(files),
                "checkpoint folder holds an extra or missing file: " + folder)
        for name, entry in files.items():
            inv.stream(train_id + "/" + folder + "/" + index_verify.CHECKPOINT_DIR + "/" + name,
                       checkpoint_dir / name, expected_sha=entry["sha256"], expected_bytes=entry["bytes"])
        reference_display = train_id + "/" + folder + "/" + index_verify.REFERENCE_NAME
        reference_raw = inv.read(reference_display, epoch_dir / index_verify.REFERENCE_NAME)
        reference = inputs.parse_json(reference_raw, "eval reference")
        inv.scan(reference_display, reference)
        _, packed = index_verify.reference_record(reference)
        _expect(digest(packed) == row["referenceSha256"],
                "eval reference vectors differ from training evidence: " + folder)
        reloads = {}
        for name in (index_verify.RELOAD_NAME, index_verify.RELOAD_RECHECK_NAME):
            result, _ = inv.json(train_id + "/" + folder + "/" + name, epoch_dir / name)
            _expect(result.get("passed") is True and result.get("epochDirectory") == folder,
                    "reload check failed or names another epoch: " + folder + "/" + name)
            reloads[name] = result["passed"]
        manifests[str(number)] = digest(manifest_raw)
        epoch_facts.append({"epoch": number, "frozenUnchanged": row["frozenUnchanged"],
                            "encoderChanged": row["encoderChanged"], "plannedSteps": row["plannedSteps"],
                            "successfulSteps": row["successfulSteps"], "skippedSteps": row["skippedSteps"],
                            "nonFiniteStepLosses": row["nonFiniteStepLosses"],
                            "elapsedSeconds": row["elapsedSeconds"], "reloadChecksPassed": all(reloads.values()),
                            "checkpointManifestSha256": row["checkpointManifestSha256"],
                            "referenceSha256": row["referenceSha256"]})
    for name, field in ((index_verify.REFERENCE_PROBE, "referenceProbeSha256"),
                        (index_verify.LIVE_PROBE, "liveProbeSha256")):
        display = train_id + "/" + name
        probe_raw = inv.read(display, train_dir / name)
        inv.scan(display, inputs.parse_json(probe_raw, display))
        _expect(digest(probe_raw) == train["referenceProbe"][field],
                "probe bytes differ from training evidence: " + name)
    _expect(train["referenceProbe"]["comparison"]["result"] == "MATCH", "reference probe does not match")
    log_path = ensure_inside(train_dir / train["reload"]["log"], train_dir)
    inv.read(train_id + "/" + train["reload"]["log"], log_path)
    return {"expectations": expectations, "train": train, "trainRaw": train_raw, "epochs": epoch_facts,
            "manifests": manifests, "identity": identity, "epochCount": epochs}


def _verify_index(inv, index_id, train_id, sources, binding, training):
    index_dir = run_dir(index_id)
    export, export_raw = inv.json(index_id + "/" + index_verify.EXPORT_EVIDENCE,
                                  index_dir / index_verify.EXPORT_EVIDENCE)
    corpus = {"inputsManifestSha256": sources["inputsSha256"], "prepareRunId": binding["runId"],
              "validationReportSha256": export["identity"]["validationReportSha256"],
              "protocolSha256": sources["protocolSha256"], "configSha256": sources["config"]["configSha256"]}
    index_verify.require_export_evidence(export, index_run_id=index_id, train_run_id=train_id,
                                         expectations=training["expectations"], corpus=corpus)
    _expect(export["identity"]["prepare"] == binding, "export evidence prepare binding differs")
    _expect(export["identity"]["checkpointManifestSha256"]
            == {label: training["manifests"][label[1:]] for label in TRAINED_LABELS},
            "export evidence checkpoint manifests differ from training evidence")
    _expect(export["likeForLike"]["result"] == "MATCH", "like-for-like check did not match")
    probe_display = index_id + "/" + index_verify.REFERENCE_PROBE
    probe_raw = inv.read(probe_display, index_dir / index_verify.REFERENCE_PROBE)
    inv.scan(probe_display, inputs.parse_json(probe_raw, probe_display))
    _expect(digest(probe_raw) == export["referenceProbe"]["referenceSha256"],
            "export reference probe bytes differ from the export evidence")
    live_display = index_id + "/" + index_verify.LIVE_PROBE
    inv.scan(live_display, inputs.parse_json(inv.read(live_display, index_dir / index_verify.LIVE_PROBE), live_display))
    _expect(set(export["galleryFiles"]) == set(LANGUAGES) and set(export["queryCounts"]) == set(LANGUAGES),
            "export languages are not the five reviewed languages")
    _expect(set(export["models"]) == set(index_verify.MODEL_LABELS), "export model set is not E0..E3")
    models = {}
    for label in index_verify.MODEL_LABELS:
        entry = export["models"][label]
        directory = index_dir / index_verify.INDEX_DIR / label
        folder = index_id + "/" + index_verify.INDEX_DIR + "/" + label
        manifest, manifest_raw = inv.json(folder + "/" + index_verify.MANIFEST_NAME,
                                          directory / index_verify.MANIFEST_NAME)
        _expect(manifest.get("schema") == index_verify.INDEX_SCHEMA and manifest.get("label") == label
                and manifest.get("partition") == "validation",
                "index manifest schema, label or partition mismatch: " + label)
        _expect(manifest["modelKey"] == entry["modelKey"] and manifest["languages"] == entry["languages"],
                "index manifest differs from the export evidence: " + label)
        _expect(index_verify.model_key(manifest["identity"]) == manifest["modelKey"],
                "index model key does not match its identity: " + label)
        _expect(manifest["corpus"] == corpus and manifest["dimension"] == index_verify.EMBEDDING_DIMENSIONS,
                "index corpus or dimension differs: " + label)
        _expect(set(entry["languages"]) == set(LANGUAGES),
                "index languages are not the five reviewed languages: " + label)
        languages = {}
        for language in LANGUAGES:
            info = entry["languages"][language]
            _expect(info["vectorBytes"] == info["rows"] * index_verify.EMBEDDING_DIMENSIONS * 4,
                    "index vector bytes do not match its rows: " + label + "/" + language)
            _expect(info["galleryFiles"] == export["galleryFiles"][language],
                    "index gallery size differs from the export evidence: " + label + "/" + language)
            inv.stream(folder + "/" + language + ".fp32le", directory / (language + ".fp32le"),
                       expected_sha=info["vectorSha256"], expected_bytes=info["vectorBytes"])
            chunks_display = folder + "/" + language + ".chunks.json"
            chunks_raw = inv.read(chunks_display, directory / (language + ".chunks.json"),
                                  expected_sha=info["chunkManifestSha256"], expected_bytes=info["chunkManifestBytes"])
            chunks = inputs.parse_json(chunks_raw, "chunk manifest")
            inv.scan(chunks_display, chunks)
            _expect(len(chunks) == info["rows"], "chunk manifest row count differs: " + label + "/" + language)
            languages[language] = {key: info[key] for key in (
                "galleryFiles", "galleryKey", "indexKey", "rows", "vectorBytes", "vectorSha256", "chunkManifestBytes",
                "partialMLCoverageFiles", "omittedTokenCount")}
        models[label] = {"modelKey": entry["modelKey"], "manifestSha256": digest(manifest_raw), "languages": languages,
                         "peakAllocatedVRAM": entry["peakAllocatedVRAM"]}
    return {"export": export, "exportRaw": export_raw, "models": models, "corpus": corpus}


def _verify_evaluation(inv, evaluation_id, index_id, train_id, binding, training, index):
    evaluation_dir = run_dir(evaluation_id)
    evaluation, evaluation_raw = inv.json(evaluation_id + "/" + index_verify.EVALUATION_EVIDENCE,
                                          evaluation_dir / index_verify.EVALUATION_EVIDENCE)
    index_verify.verified_evaluation(evaluation_raw, train_run_id=train_id, index_run_id=index_id,
                                     prepare_binding=binding, corpus=index["corpus"])
    identity = evaluation["identity"]
    _expect(identity["trainingEvidenceSha256"] == training["expectations"]["evidenceSha256"],
            "evaluation evidence binds another training evidence file")
    _expect(identity["indexExportOutcome"] == "SUCCEEDED", "evaluation consumed an export that did not succeed")
    for label in index_verify.MODEL_LABELS:
        _expect(identity["indexes"][label]["modelKey"] == index["models"][label]["modelKey"]
                and identity["indexes"][label]["indexKeys"] == {
                    language: index["models"][label]["languages"][language]["indexKey"] for language in LANGUAGES},
                "evaluation index identity differs from the export manifests: " + label)
    selection, selection_raw = inv.json(evaluation_id + "/" + index_verify.SELECTION_FILE,
                                        evaluation_dir / index_verify.SELECTION_FILE)
    _expect(selection == evaluation["selection"], "selection file differs from the evaluation evidence")
    _expect("approvedForUse" in selection, "selection does not record approvedForUse")
    _expect(selection["approvedForUse"] is False, "selection is approved for use; this stage refuses it")
    # selectedForUse is the older field name; the real records never wrote it, so only a present true is refused.
    _expect(selection.get("selectedForUse", False) is False, "selection is selected for use; this stage refuses it")
    _expect(selection.get("status") == "CHOSEN_BY_RULE", "selection status is not CHOSEN_BY_RULE")
    _expect(selection.get("choice") in TRAINED_LABELS, "selection choice is not a trained epoch")
    _expect(selection["checkpointManifestSha256"]
            == {label: training["manifests"][label[1:]] for label in TRAINED_LABELS},
            "selection checkpoint manifests differ from training evidence")
    return {"evaluation": evaluation, "evaluationRaw": evaluation_raw, "selectionRaw": selection_raw}


def _verify_queries(inv, database_id, chain, binding, training, index, evaluation):
    database_dir = run_dir(database_id)
    query_manifest_name = database_id + "/" + index_verify.QUERY_DIR + "/" + index_verify.QUERY_MANIFEST
    query, query_raw = inv.json(query_manifest_name,
                                database_dir / index_verify.QUERY_DIR / index_verify.QUERY_MANIFEST)
    _expect(query.get("trainRunId") == chain["train"] and query.get("indexRunId") == chain["index"]
            and query.get("evaluationRunId") == chain["evaluation"],
            "query manifest names another training, index or evaluation run")
    counts = {label: evaluation["evaluation"]["denominators"][label]["queries"] for label in index_verify.MODEL_LABELS}
    index_verify.require_query_manifest(
        query, run_id=database_id, prepare_binding=binding, corpus=index["corpus"],
        evidence_digests={"trainingEvidenceSha256": training["expectations"]["evidenceSha256"],
                          "exportEvidenceSha256": digest(index["exportRaw"]),
                          "evaluationEvidenceSha256": digest(evaluation["evaluationRaw"])},
        query_counts=counts)
    queries = query["queries"]
    _expect(queries, "zero queries in the query manifest")
    per_language = {language: sum(1 for item in queries if item["language"] == language) for language in LANGUAGES}
    _expect(sum(per_language.values()) == len(queries) and per_language == index["export"]["queryCounts"],
            "query counts per language disagree with the export evidence")
    sizes = [len(item["scoredGallery"]) for item in queries]
    denominators = evaluation["evaluation"]["denominators"]
    for label in index_verify.MODEL_LABELS:
        _expect(denominators[label]["galleryCounts"]["min"] == min(sizes)
                and denominators[label]["galleryCounts"]["max"] == max(sizes),
                "gallery sizes disagree between the query manifest and the evaluation: " + label)
    for label in index_verify.MODEL_LABELS:
        _expect(query["labels"][label]["modelKey"] == index["models"][label]["modelKey"],
                "query manifest model key differs from the export: " + label)
    for language in LANGUAGES:
        keys = {index["models"][label]["languages"][language]["galleryKey"] for label in index_verify.MODEL_LABELS}
        _expect(keys == {query["galleryKeys"][language]}, "gallery key differs across the chain: " + language)
    raws = index_verify.query_vector_files(database_dir / index_verify.QUERY_DIR, query, inputs.MAX_ARTIFACT_BYTES)
    for label, raw in raws.items():
        inv.record(database_id + "/" + index_verify.QUERY_DIR + "/" + label + ".fp32le", raw)
    return {"query": query, "queryRaw": query_raw, "sizes": sizes, "perLanguage": per_language}


def _verify_database(inv, database_id, chain, binding, training, index, evaluation, queries, sources):
    database_dir = run_dir(database_id)
    database, database_raw = inv.json(database_id + "/" + index_verify.DB_EVIDENCE_NAME,
                                      database_dir / index_verify.DB_EVIDENCE_NAME)
    _expect(database.get("schema") == index_verify.DB_EVIDENCE_SCHEMA and database.get("runId") == database_id
            and database.get("outcome") == "SUCCEEDED", "database evidence is not a SUCCEEDED database check")
    identity = database["identity"]
    _expect(identity["prepare"] == binding and identity["trainRunId"] == chain["train"]
            and identity["indexRunId"] == chain["index"] and identity["evaluationRunId"] == chain["evaluation"],
            "database evidence binds another run")
    expected = {"configSha256": sources["config"]["configSha256"], "inputsManifestSha256": sources["inputsSha256"],
                "protocolSha256": sources["protocolSha256"], "validationReportSha256":
                index["corpus"]["validationReportSha256"], "prepareRunId": binding["runId"],
                "trainingEvidenceSha256": training["expectations"]["evidenceSha256"],
                "exportEvidenceSha256": digest(index["exportRaw"]),
                "evaluationEvidenceSha256": digest(evaluation["evaluationRaw"]),
                "queryManifestSha256": digest(queries["queryRaw"])}
    for key, value in expected.items():
        _expect(identity.get(key) == value, "database evidence binds another input: " + key)
    agreement, agreement_raw = inv.json(database_id + "/" + index_verify.DB_AGREEMENT_NAME,
                                        database_dir / index_verify.DB_AGREEMENT_NAME)
    _expect(database["agreementDetailsSha256"] == digest(agreement_raw),
            "agreement file differs from the database evidence")
    publish = database["publish"]
    _expect(set(publish) == set(index_verify.MODEL_LABELS), "database publish labels are not E0..E3")
    added = 0
    for label in index_verify.MODEL_LABELS:
        _expect(set(publish[label]) == set(LANGUAGES), "database publish languages are not the five reviewed languages")
        for language in LANGUAGES:
            row = publish[label][language]
            export_info = index["models"][label]["languages"][language]
            _expect(row["status"] == "PUBLISHED_COMPLETE" and row["indexKey"] == export_info["indexKey"]
                    and row["vectorSha256"] == export_info["vectorSha256"] and row["rows"] == export_info["rows"],
                    "published index differs from the export evidence: " + label + "/" + language)
            added += 1
    _expect(database["existingSandbox"]["existingRowsUnchanged"] is True, "existing sandbox rows changed")
    records = agreement["records"]
    for label in index_verify.MODEL_LABELS:
        _expect(len(records[label]) == len(queries["query"]["queries"]), "agreement query count differs: " + label)
        _expect(all(item["disagreements"] == [] for item in records[label]),
                "database ranking disagrees with the CPU ranking: " + label)
    return {"database": database, "databaseRaw": database_raw, "agreementRaw": agreement_raw, "indexesPublished": added}


def verify_chain(chain, *, inputs_path, runtime_review, config_path=CONFIG_PATH,
                 protocol_path=index_verify.PROTOCOL_PATH,
                 trusted_path=index_verify.TRUSTED_IDENTITY, required_config_byte_sha256=REQUIRED_CONFIG_BYTE_SHA256,
                 base=REPO_ROOT):
    """Every check of the chain, in dependency order. Raises on the first refused fact."""
    inv = Inventory()
    sources = _verify_sources(inv, inputs_path=inputs_path, config_path=config_path, protocol_path=protocol_path,
                              trusted_path=trusted_path, base=base, required_config_sha=required_config_byte_sha256)
    runtime = _verify_runtime(inv, Path(runtime_review), base)
    prepare_display = chain["prepare"] + "/" + index_verify.PREPARE_DATA
    prepare, prepare_raw = inv.json(prepare_display, run_dir(chain["prepare"]) / index_verify.PREPARE_DATA, scan=False)
    # The membership record names the final-test partition by design and is not scanned. The one allowed reference is
    # the entry's key and its relativePath, at the exact path; every other key and value of the document is scanned.
    reference = _string_at(prepare, ALLOWED_FINAL_TEST_REFERENCE)
    allowed = (ALLOWED_FINAL_TEST_REFERENCE[:-1], ALLOWED_FINAL_TEST_REFERENCE) if reference is not None else ()
    if reference is not None:
        inv.allow(prepare_display, {"keyPath": ".".join(ALLOWED_FINAL_TEST_REFERENCE), "value": reference})
    inv.scan(prepare_display, {key: value for key, value in prepare.items() if key != "finalTestMembership"},
             allowed=allowed)
    binding, payload = index_verify.prepare_binding_from_bytes(prepare_raw, chain["prepare"])
    membership = payload.get("finalTestMembership") or {}
    _expect(membership.get("partition") == "final-test" and membership.get("scored") is False
            and membership.get("tuning") is False and membership.get("ranksInspected") is False,
            "prepare membership records final-test use")
    training = _verify_training(inv, chain["train"], sources, binding)
    index = _verify_index(inv, chain["index"], chain["train"], sources, binding, training)
    evaluation = _verify_evaluation(inv, chain["evaluation"], chain["index"], chain["train"], binding, training, index)
    queries = _verify_queries(inv, chain["database"], chain, binding, training, index, evaluation)
    database = _verify_database(inv, chain["database"], chain, binding, training, index, evaluation, queries, sources)
    return {"inventory": inv, "sources": sources, "runtime": runtime, "binding": binding, "membership": membership,
            "training": training, "index": index, "evaluation": evaluation, "queries": queries, "database": database}


def _quality(evaluation):
    """Paired deltas against the frozen model and rank-one shares, classified by the stated numeric rule."""
    paired = evaluation["pairedDeltas"]
    saturation = evaluation["saturation"]
    frozen_share = saturation[FROZEN_LABEL]["overall"]["firstRelevantAtRankOneShare"]
    query_count = evaluation["denominators"][FROZEN_LABEL]["queries"]
    per_model = {}
    for label in TRAINED_LABELS:
        mrr = paired[label]["semantic@20.mrr"]
        recall = paired[label]["semantic@20.recall"]
        share = saturation[label]["overall"]["firstRelevantAtRankOneShare"]
        deltas = (mrr["meanDifference"], recall["meanDifference"])
        largest = max(abs(delta) for delta in deltas)
        signed = max(deltas, key=abs)
        near = largest <= NEAR_CEILING_DELTA and min(frozen_share, share) >= NEAR_CEILING_SHARE
        per_model[label] = {
            "semanticMrrAt20": {key: mrr[key] for key in ("meanDifference", "improved", "worse", "equal", "seeds")},
            "semanticRecallAt20": {key: recall[key]
                                   for key in ("meanDifference", "improved", "worse", "equal", "seeds")},
            "largestAbsoluteMeanDifference": largest,
            "meanDeltaSign": "positive" if signed > 0 else "negative" if signed < 0 else "zero",
            "worseOnAnySeed": mrr["worse"] > 0 or recall["worse"] > 0,
            "rankOneShareFrozen": frozen_share,
            "rankOneShareModel": share,
            "classification": NO_MEASURABLE_DIFFERENCE if near else MEASURABLE_DIFFERENCE}
    status = NO_MEASURABLE_DIFFERENCE if all(row["classification"] == NO_MEASURABLE_DIFFERENCE
                                             for row in per_model.values()) else MEASURABLE_DIFFERENCE
    return {"status": status,
            "rule": {"largestAbsoluteMeanDifferenceAtMost": NEAR_CEILING_DELTA,
                     "rankOneShareAtLeastFrozenAndModel": NEAR_CEILING_SHARE,
                     "metrics": "semantic@20 MRR and Recall, seed-paired mean difference against the frozen model",
                     "reason": "At {} queries one flipped query moves Recall@20 by 1/{} = {:.4f}; the threshold {} "
                               "equals one such query, so one changed query is not a measurable difference. "
                               "worseOnAnySeed and meanDeltaSign show smaller losses.".format(
                                   query_count, query_count, 1 / query_count, NEAR_CEILING_DELTA),
                     "text": NO_MEASURABLE_DIFFERENCE + " when both conditions hold for a trained epoch; "
                             + MEASURABLE_DIFFERENCE + " otherwise, with the numbers in perModel"},
            "perModel": per_model}


def _training_status(train, epochs):
    steps = train["steps"]
    facts = {"stepsPlanned": steps["planned"], "stepsSuccessful": steps["successful"],
             "stepsSkipped": steps["skipped"], "nonFiniteStepLosses": steps["nonFiniteLosses"],
             "frozenStateUnchangedEveryEpoch": all(row["frozenUnchanged"] is True for row in epochs),
             "encoderChangedEveryEpoch": all(row["encoderChanged"] is True for row in epochs),
             "reloadPassed": train["reload"]["passed"] is True,
             "reloadChecksPassedEveryEpoch": all(row["reloadChecksPassed"] for row in epochs)}
    completed = (steps["planned"] == steps["successful"] and steps["skipped"] == 0 and steps["nonFiniteLosses"] == 0
                 and facts["frozenStateUnchangedEveryEpoch"] and facts["encoderChangedEveryEpoch"]
                 and facts["reloadPassed"] and facts["reloadChecksPassedEveryEpoch"])
    return {"status": "COMPLETED" if completed else "NOT_COMPLETED", "facts": facts}


def command_argv(stage, values):
    """Argument list for one stage. Flag names and required arguments come from the real parser."""
    parser = cli.stage_parsers()[stage]
    dests = {action.dest for action in parser._actions}
    unknown = set(values) - dests
    _expect(not unknown, "ledger gives arguments {} that {} does not take".format(sorted(unknown), stage))
    argv = [stage]
    for action in parser._actions:
        if not action.option_strings or action.dest == "help":
            continue
        if action.dest not in values:
            _expect(not action.required, "ledger omits the required {} of {}".format(action.option_strings[0], stage))
            continue
        value = values[action.dest]
        for item in (value if isinstance(value, list) else [value]):
            argv += [action.option_strings[0], str(item)]
    return argv


def _ledger(run_id, chain, sources, verified, superseded_run_ids):
    train = verified["training"]["train"]
    index = verified["index"]["export"]
    evaluation = verified["evaluation"]["evaluation"]
    query = verified["queries"]["query"]
    database = verified["database"]["database"]
    shared = {"inputs": sources["inputsPath"], "config": sources["configPath"]}
    chained = {"prepare_run_id": chain["prepare"], "train_run_id": chain["train"], "index_run_id": chain["index"],
               "evaluation_run_id": chain["evaluation"]}
    probe_train = _run_file(chain["train"], index_verify.REFERENCE_PROBE)
    probe_index = _run_file(chain["index"], index_verify.REFERENCE_PROBE)
    probe_evaluation = _run_file(chain["evaluation"], index_verify.REFERENCE_PROBE)
    runtime = verified["runtime"]["path"]
    train_role = interpreter_role(train["environment"])
    entries = [
        ("prepare-data", "prepare-data", PREPARE_ROLE, {"run_id": chain["prepare"], **shared}),
        ("environment-probe (reference)", "environment-probe", interpreter_role(train["referenceProbe"]["reference"]),
         {**shared, "out": probe_train}),
        ("train", "train", train_role,
         {"run_id": chain["train"], **shared, "prepare_run_id": chain["prepare"], "reference_probe": probe_train}),
        ("train-reload-check", "train-reload-check", train_role,
         {"run_id": chain["train"], "config": sources["configPath"], "check_name": "reload-check.json"}),
        ("train-reload-check", "train-reload-check", train_role,
         {"run_id": chain["train"], "config": sources["configPath"], "check_name": "reload-recheck.json"}),
        ("export-index", "export-index", interpreter_role(index["environment"]),
         {"run_id": chain["index"], **shared, "prepare_run_id": chain["prepare"], "train_run_id": chain["train"],
          "reference_probe": probe_index}),
        ("evaluate", "evaluate", interpreter_role(evaluation["environment"]),
         {"run_id": chain["evaluation"], **shared, "prepare_run_id": chain["prepare"],
          "train_run_id": chain["train"], "index_run_id": chain["index"], "reference_probe": probe_evaluation}),
        ("index-publish-check query-vectors", "index-publish-check", interpreter_role(query["environment"]),
         {"stage": "query-vectors", "run_id": chain["database"], **shared, **chained}),
        ("index-publish-check publish-and-rank", "index-publish-check", interpreter_role(database["environment"]),
         {"stage": "publish-and-rank", "run_id": chain["database"], **shared, **chained, "runtime_review": runtime}),
        ("validate-evidence", "validate-evidence", interpreter_role({"python": sys.executable}),
         {"run_id": run_id, **shared, **chained, "database_run_id": chain["database"], "runtime_review": runtime,
          "superseded_run_id": list(superseded_run_ids)}),
    ]
    return [{"order": order, "stage": label, "interpreterRole": role_and_path[0],
             "interpreterPath": role_and_path[1], "command": command_argv(parser_stage, values)}
            for order, (label, parser_stage, role_and_path, values) in enumerate(entries, start=1)]


def _measurements(verified):
    train = verified["training"]["train"]
    models = verified["index"]["export"]["models"]
    evaluation = verified["evaluation"]["evaluation"]
    database = verified["database"]["database"]
    return {
        "training": {"elapsedSeconds": train["elapsedSeconds"],
                     "epochElapsedSeconds": [row["elapsedSeconds"] for row in verified["training"]["epochs"]],
                     "stepsPlanned": train["steps"]["planned"], "stepsSuccessful": train["steps"]["successful"],
                     "reloadPassed": train["reload"]["passed"]},
        "index": {"exportElapsedSeconds": verified["index"]["export"]["elapsedSeconds"],
                  "vectorBytesByLabel": {label: sum(info["vectorBytes"] for info in models[label]["languages"].values())
                                         for label in index_verify.MODEL_LABELS},
                  "chunkManifestBytesByLabel": {label: sum(info["chunkManifestBytes"]
                                                           for info in models[label]["languages"].values())
                                                for label in index_verify.MODEL_LABELS},
                  "rowsByLabel": {label: sum(info["rows"] for info in models[label]["languages"].values())
                                  for label in index_verify.MODEL_LABELS},
                  "peakAllocatedVRAMByLabel": {label: models[label]["peakAllocatedVRAM"]
                                               for label in index_verify.MODEL_LABELS}},
        "cpuAndCudaEvaluation": {"elapsedSeconds": evaluation["elapsedSeconds"],
                                 "measurementScope": evaluation["measurementScope"],
                                 "perLabel": {label: {"queryEmbeddingCudaP50Seconds":
                                                      evaluation["measurements"][label]["queryEmbedding"]["p50"],
                                                      "queryEmbeddingCudaP95Seconds":
                                                      evaluation["measurements"][label]["queryEmbedding"]["p95"],
                                                      "queryRankingCpuP50Seconds":
                                                      evaluation["measurements"][label]["queryRanking"]["p50"],
                                                      "queryRankingCpuP95Seconds":
                                                      evaluation["measurements"][label]["queryRanking"]["p95"]}
                                              for label in index_verify.MODEL_LABELS}},
        "databaseLoopback": {"note": "one exact_query call per query and model over the loopback sandbox; "
                                     "not comparable with the CPU ranking numbers",
                             "perLabel": {label: {"timedQueries": database["latency"][label]["count"],
                                                  "p50Seconds": database["latency"][label]["p50"],
                                                  "p95Seconds": database["latency"][label]["p95"]}
                                          for label in index_verify.MODEL_LABELS}}}


def _per_label(values, labels):
    return ", ".join("{} {}".format(label, values[label]) for label in labels)


def _limitations(verified, quality, superseded_run_ids):
    """Two groups: facts this stage measured, and statements the operator recorded that it cannot verify."""
    index = verified["index"]["export"]
    models = verified["index"]["models"]
    partial = {label: sum(info["partialMLCoverageFiles"] for info in models[label]["languages"].values())
               for label in index_verify.MODEL_LABELS}
    omitted = {label: sum(info["omittedTokenCount"] for info in models[label]["languages"].values())
               for label in index_verify.MODEL_LABELS}
    files = sum(index["galleryFiles"].values())
    queries = sum(verified["queries"]["perLanguage"].values())
    shares = {label: round(quality["perModel"][label]["rankOneShareModel"], 4) for label in TRAINED_LABELS}
    frozen_share = round(quality["perModel"]["E1"]["rankOneShareFrozen"], 4)
    cap = verified["sources"]["protocol"]["frozen"]["maxWindowsPerFragment"]
    database = verified["database"]["database"]
    before = database["existingSandbox"]["before"]
    after = database["existingSandbox"]["after"]
    seeds = quality["perModel"]["E1"]["semanticMrrAt20"]["seeds"]
    if quality["status"] == NO_MEASURABLE_DIFFERENCE:
        first = ("Validation is near the ceiling: the frozen model's first-relevant-at-rank-one share is "
                 "{:.4f} over {} "
                 "queries and the trained epochs measure {}. The validation set cannot separate the models; the result "
                 "is {}, not an improvement claim.".format(frozen_share, queries, _per_label(shares, TRAINED_LABELS),
                                                          quality["status"]))
    else:
        deltas = {label: "{:.6g}".format(quality["perModel"][label]["largestAbsoluteMeanDifference"])
                  for label in TRAINED_LABELS}
        first = ("Validation shows measurable differences from the frozen model: the largest absolute mean paired "
                 "delta on semantic@20 per trained model is {}, the frozen first-relevant-at-rank-one share is {:.4f} "
                 "and the trained shares are {}. The result is {}; the numbers are in the statuses section.".format(
                     _per_label(deltas, TRAINED_LABELS), frozen_share, _per_label(shares, TRAINED_LABELS),
                     quality["status"]))
    verified_facts = [
        first,
        "Gallery files are indexed whole with a {}-window cap per fragment (maxWindowsPerFragment in the evaluation "
        "protocol): truncated files are {} (of {} gallery files) and omitted tokens are {}.".format(
            cap, _per_label(partial, index_verify.MODEL_LABELS), files,
            _per_label(omitted, index_verify.MODEL_LABELS)),
        "The like-for-like check samples {} chunks of one language (maximum absolute difference {}). The overall "
        "paired deltas are pooled over {} seeds per model.".format(
            index["likeForLike"]["sampleChunks"], index["likeForLike"]["maxAbsDifference"], seeds),
        "The database check added {} new index keys; existing sandbox rows are unchanged (indexes before {}, after "
        "{}). Database and CPU timings are not comparable.".format(
            verified["database"]["indexesPublished"], before["indexes"], after["indexes"]),
        "Selection is CHOSEN_BY_RULE with approvedForUse false; a selectedForUse key, when present, must be false. "
        "The selection file equals the evaluation record.",
        "The validation report digest is cross-checked between runs; this stage does not recompute it.",
        "The 297 gallery files, 201 queries and 20 published indexes are consistent across the chain; they are not "
        "checked against an independent source.",
        "Earlier evidence has two protocol identities under the same key name protocolSha256: the admission protocol "
        "(training) and the evaluation protocol (export, evaluation, database). This package records them separately.",
        "Independence of the earlier stages from the final test is not proven by this stage. "
        "The stage records that the "
        "chain documents name no final-test partition and that prepare membership records the final test as not "
        "scored, not used for tuning and not rank-inspected.",
        "The publish gate's runtime review directory is recorded by path, with its approval files hashed; its wheels "
        "are not re-hashed by this stage.",
        "Reviewer acceptance is not recorded in the run files and this package does not claim it.",
    ]
    recorded = [
        "Training pairs depend on synthetic transformations; the corpus is finite; the TypeScript and TSX transfer is "
        "experimental.",
        "The current split has one training repository family per language; queries are fragments of their own "
        "relevant files.",
        "Upstream pretraining may overlap the corpora; the upstream training data was not inspected.",
        "The baseline used per-fragment chunking.",
        "No normalized token bound-pair digest is recorded: the raw token-comparison output differs between runs while "
        "the normalized bound pairs are identical.",
        "The evaluation corrective-rerun budget is spent: one corrective rerun was used.",
        "There is no product authorization or configuration change in this chain.",
    ]
    if superseded_run_ids:
        recorded.append("This evidence stage was rerun after review findings. "
                        "Superseded evidence is kept and not used: "
                        + ", ".join(superseded_run_ids) + ".")
    return {"verifiedByStage": verified_facts, "recordedByOperator": recorded}


CHECKS = (
    "inputs manifest canonical digest matches every run that names it",
    "configuration byte SHA256 equals the reviewed training configuration",
    "every named file is read by exact size and SHA256; every JSON document is canonical and non-empty",
    "prepare-data binding equals the training identity (run id, bytes, admission digest, admitted triplets)",
    "training identity equals the configuration, trusted upstream identity and admission protocol",
    "every epoch: checkpoint manifest hash, selection state, identity, exact weight and support files, the checkpoint "
    "folder file set, eval reference, reload checks passed",
    "export evidence: outcome, training binding, checkpoint manifests, like-for-like match, five languages, E0..E3",
    "every index manifest and vector/chunk file: exact size and SHA256, rows, model key, corpus identity, partition",
    "evaluation evidence: outcome, index keys and model keys, denominators, selection file equals the record, "
    "selection records approvedForUse false and status CHOSEN_BY_RULE; a present selectedForUse is not true",
    "query manifest: run id, bindings, query counts per label and per language, gallery sizes, vector files",
    "database evidence: outcome, every binding, every label and language published with the export key, agreement "
    "digest, zero disagreements, existing rows unchanged",
    "final-test: prepare membership records scored false, tuning false, ranks not inspected; every JSON chain document "
    "listed in finalTest.chainDocumentsScanned names no final-test text, except the one reference in "
    "finalTest.allowedReferences",
    "runtime review: the publish gate's review directory holds the approval files, which are hashed",
    "the evidence ledger's arguments are the parser's flags, with every required argument present",
)


def build_package(run_id, chain, verified, *, superseded_run_ids=()):
    sources = verified["sources"]
    training = verified["training"]
    index = verified["index"]
    evaluation = verified["evaluation"]
    queries = verified["queries"]
    database = verified["database"]
    inv = verified["inventory"]
    quality = _quality(evaluation["evaluation"])
    inventory = inv.listing()
    trained = {label: training["manifests"][label[1:]] for label in TRAINED_LABELS}
    membership = verified["membership"]
    superseded = sorted(set(superseded_run_ids))
    return {
        "schema": SCHEMA, "runId": run_id, "outcome": "SUCCEEDED", "reason": None, "Purpose": PURPOSE,
        "CanAuthorizePublication": False, "SecurityEvidence": "NOT_RUN", "chain": chain,
        "identities": {
            "configByteSha256": sources["config"]["byteSha256"], "configSha256": sources["config"]["configSha256"],
            "admissionProtocolSha256": sources["config"]["protocolSha256"],
            "evaluationProtocolSha256": sources["protocolSha256"],
            "evaluationProtocolFileSha256": sources["protocolFileSha256"],
            "inputsManifestSha256": sources["inputsSha256"],
            "trustedModelRevision": sources["trusted"]["modelRevision"],
            "trustedModelFileHashes": sources["trusted"]["fileHashes"],
            "prepare": verified["binding"], "trainingEvidenceSha256": training["expectations"]["evidenceSha256"],
            "checkpointManifestSha256": trained,
            "modelKeys": {label: index["models"][label]["modelKey"] for label in index_verify.MODEL_LABELS},
            "indexKeys": {label: {language: index["models"][label]["languages"][language]["indexKey"]
                                  for language in LANGUAGES} for label in index_verify.MODEL_LABELS},
            "validationReportSha256": index["corpus"]["validationReportSha256"],
            "exportEvidenceSha256": digest(index["exportRaw"]),
            "evaluationEvidenceSha256": digest(evaluation["evaluationRaw"]),
            "queryManifestSha256": digest(queries["queryRaw"]),
            "databaseEvidenceSha256": digest(database["databaseRaw"]),
            "agreementDetailsSha256": digest(database["agreementRaw"])},
        "inventory": {"fileCount": len(inventory), "totalBytes": sum(item["bytes"] for item in inventory),
                      "files": inventory},
        "runtimeReview": verified["runtime"],
        "commandLedger": _ledger(run_id, chain, sources, verified, superseded),
        "supersededRunIds": superseded,
        "measurements": _measurements(verified),
        "statuses": {"trainingCompleted": _training_status(training["train"], training["epochs"]),
                     "qualityOutcome": quality, "reviewerAcceptance": REVIEWER_ACCEPTANCE},
        "finalTest": {
            "opened": False, "scored": False, "sealRead": False,
            "basis": "the stage has no code path that opens, scores or reads the final test or its seal",
            "chainDocumentsScanned": sorted(inv.scanned),
            "allowedReferences": inv.allowed,
            "finalTestTextFound": any(inv.scanned.values()),
            "prepareMembership": {"partition": membership.get("partition"), "scored": membership.get("scored"),
                                  "tuning": membership.get("tuning"),
                                  "ranksInspected": membership.get("ranksInspected")},
            "statement": "This stage does not open the final test. The chain documents listed were scanned and name no "
                         "final-test partition. Prepare membership records the final test as not scored, not used for "
                         "tuning and not rank-inspected. Independence of earlier stages is not proven."},
        "limitations": _limitations(verified, quality, superseded),
        "checks": list(CHECKS),
        "torchImported": "torch" in sys.modules,
        "localOnly": LOCAL_ONLY,
    }


def failure_package(run_id, reason):
    return {"schema": SCHEMA, "runId": run_id, "outcome": "FAILED_ERROR", "reason": reason, "Purpose": PURPOSE,
            "CanAuthorizePublication": False, "SecurityEvidence": "NOT_RUN"}


def reconstruction_markdown(package):
    """Human-readable reconstruction from the package data; English, no secrets, no DSN, no machine paths."""
    identities = package["identities"]
    lines = ["# Retrieval evidence reconstruction", "",
             "Generated by the validate-evidence stage from the evidence package. Hashes and counts only; "
             "no source text, vectors or checkpoint content.", "", "## Chain", ""]
    for name, value in package["chain"].items():
        lines.append("- {}: `{}`".format(name, value))
    lines += ["", "## Statuses", "",
              "- trainingCompleted: {}".format(package["statuses"]["trainingCompleted"]["status"]),
              "- qualityOutcome: {}".format(package["statuses"]["qualityOutcome"]["status"]),
              "- reviewerAcceptance: {}".format(package["statuses"]["reviewerAcceptance"]), "",
              "## Command ledger", "",
              "Each command runs from the repository root. The interpreter path is shown where the evidence records "
              "it; `python` marks a stage whose interpreter is not recorded.", ""]
    for entry in package["commandLedger"]:
        executable = entry["interpreterPath"] or "python"
        lines.append("{}. {}, interpreter: {}".format(entry["order"], entry["stage"], entry["interpreterRole"]))
        lines.append("   `{} -B -m scripts.code_retrieval {}`".format(executable, " ".join(entry["command"])))
    lines += ["", "## Identities", "",
              "- configByteSha256: `{}`".format(identities["configByteSha256"]),
              "- evaluationProtocolSha256: `{}`".format(identities["evaluationProtocolSha256"]),
              "- admissionProtocolSha256: `{}`".format(identities["admissionProtocolSha256"]),
              "- inputsManifestSha256: `{}`".format(identities["inputsManifestSha256"]),
              "- validationReportSha256: `{}`".format(identities["validationReportSha256"]), "",
              "## Inventory", "",
              "- files: {}; total bytes: {}".format(package["inventory"]["fileCount"],
                                                    package["inventory"]["totalBytes"]),
              "", "## Limitations", "", "### Verified by this stage", ""]
    lines += ["- " + text for text in package["limitations"]["verifiedByStage"]]
    lines += ["", "### Recorded by the operator, not verified by this stage", ""]
    lines += ["- " + text for text in package["limitations"]["recordedByOperator"]]
    lines += ["", "## Local-only artifacts", "", package["localOnly"], ""]
    return "\n".join(lines)


def run(*, run_id, inputs_path, config_path, prepare_run_id, train_run_id, index_run_id, evaluation_run_id,
        database_run_id, runtime_review, superseded_run_ids=()):
    chain = {"prepare": prepare_run_id, "train": train_run_id, "index": index_run_id,
             "evaluation": evaluation_run_id, "database": database_run_id}
    target = run_dir(run_id)
    target.mkdir(parents=True, exist_ok=False)
    try:
        verified = verify_chain(chain, inputs_path=Path(inputs_path), config_path=Path(config_path),
                                runtime_review=runtime_review)
        package = build_package(run_id, chain, verified, superseded_run_ids=superseded_run_ids)
        write_once(target / PACKAGE_NAME, canonical(package), RETRIEVAL_ROOT)
        write_once(target / RECONSTRUCTION_NAME, reconstruction_markdown(package).encode("utf-8"), RETRIEVAL_ROOT)
        outcome = "SUCCEEDED"
    except Exception as exc:  # the package records every refused run; it never writes a success marker
        reason = safe_reason(type(exc).__name__ + ": " + str(exc))
        write_once(target / PACKAGE_NAME, canonical(failure_package(run_id, reason)), RETRIEVAL_ROOT)
        outcome = "FAILED_ERROR"
    return {"command": "validate-evidence", "runId": run_id, "outcome": outcome, "directory": str(target)}
