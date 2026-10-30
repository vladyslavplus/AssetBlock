import base64
import json
import os
import tempfile
import unittest
from pathlib import Path
from unittest import mock

try:
    from scripts.code_analysis.contracts import LANGUAGES, canonical, digest
    from scripts.code_retrieval import (admission, cli, config as config_module, evidence_package as ep, index_verify,
                                        prepare)
    TEST_IMPORT_ERROR = None
except ImportError as exc:  # keeps the module import failure visible in the report
    TEST_IMPORT_ERROR = exc

DIM = 768
LABELS = ("E0", "E1", "E2", "E3")
TRAINED = ("E1", "E2", "E3")
CHAIN = {"prepare": "prepare-fixture", "train": "train-fixture", "index": "index-fixture",
         "evaluation": "evaluation-fixture", "database": "database-fixture"}
STAGE = "evidence-fixture"
GALLERY_PER_LANGUAGE = 2
QUERIES_PER_LANGUAGE = 2
QUERY_DENOMINATORS = {label: {"queries": 201} for label in LABELS}


def h(name):
    return digest(name.encode("utf-8"))


def identity_for(label):
    return {"kind": "frozen" if label == "E0" else "trained", "label": label, "fullStateSha256": h("state-" + label),
            "tokenizerHashes": {"vocab": h("vocab")}, "chunking": "chunk-fixture", "pooling": "mean-fixture",
            "dimension": DIM}


class Fixture:
    """One consistent synthetic chain written under a runs root. ``overrides`` edits one named object
    just before it is written, so a test breaks exactly one fact and every hash stays self-consistent."""

    def __init__(self, root, overrides=None):
        self.root = root
        self.overrides = overrides or {}
        self.raw = {}

    def emit(self, name, value, path):
        """Write one canonical JSON object; an override edits the object in place before serialization."""
        if name in self.overrides:
            self.overrides[name](value)
        raw = canonical(value)
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(raw)
        return raw

    def write(self, path, data):
        path = Path(path)
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(data)

    def build(self):
        root = self.root
        cfg = config_module.load(ep.CONFIG_PATH)
        trusted = index_verify.upstream_identity()
        inputs_raw = self.emit("inputs", {"schema": "fixture-inputs", "artifacts": []}, root / "inputs.json")
        inputs_sha = digest(inputs_raw)
        prep_dir = root / CHAIN["prepare"]
        prep_obj = {"schema": prepare.SCHEMA, "runId": CHAIN["prepare"], "status": admission.ADMITTED,
                    "trainingAdmission": {"status": admission.ADMITTED, "totals": {"admittedTriplets": 3}},
                    "finalTestMembership": {"partition": "final-test", "scored": False, "tuning": False,
                                            "ranksInspected": False}}
        prep_raw = self.emit("prepare", prep_obj, prep_dir / index_verify.PREPARE_DATA)
        binding = index_verify.prepare_binding_from_bytes(prep_raw, CHAIN["prepare"])[0]
        corpus = {"inputsManifestSha256": inputs_sha, "prepareRunId": CHAIN["prepare"],
                  "validationReportSha256": h("report"), "protocolSha256": index_verify.protocol_sha256(),
                  "configSha256": cfg["configSha256"]}
        self._train(cfg, trusted, inputs_sha, binding)
        self._index(corpus, binding)
        self._evaluation(corpus, binding)
        self._database(corpus, binding)
        return self

    def _train(self, cfg, trusted, inputs_sha, binding):
        train_dir = self.root / CHAIN["train"]
        packed = bytes(range(8))
        epochs, manifests = [], {}
        for number in (1, 2, 3):
            folder = train_dir / ("epoch-" + str(number))
            files = {name: (name + "-" + str(number)).encode("utf-8") * 3
                     for name in (index_verify.WEIGHTS,) + index_verify.SUPPORT_FILES}
            for name, data in files.items():
                self.write(folder / index_verify.CHECKPOINT_DIR / name, data)
            manifest = {"schema": index_verify.CHECKPOINT_SCHEMA, "epoch": number, "runId": CHAIN["train"],
                        "configSha256": cfg["configSha256"], "inputsManifestSha256": inputs_sha, "prepare": binding,
                        "selectedForUse": False, "selection": index_verify.SELECTION,
                        "upstream": {"modelRevision": trusted["modelRevision"],
                                     "supportFileHashes": {name: trusted["fileHashes"][name]
                                                           for name in index_verify.SUPPORT_FILES}},
                        "files": {name: {"sha256": digest(data), "bytes": len(data)} for name, data in files.items()}}
            manifest_raw = self.emit("epoch-manifest-%d" % number, manifest,
                                     folder / index_verify.CHECKPOINT_MANIFEST_NAME)
            reference = {"schema": index_verify.REFERENCE_SCHEMA, "dimension": DIM, "rows": 1, "padTokenId": 1,
                         "inputIds": [[0, 5, 2]], "inputIdsSha256": digest(canonical([[0, 5, 2]])),
                         "vectorBytes": len(packed), "vectorSha256": digest(packed),
                         "vectorsBase64": base64.b64encode(packed).decode("ascii")}
            self.emit("eval-reference", reference, folder / index_verify.REFERENCE_NAME)
            for name in (index_verify.RELOAD_NAME, index_verify.RELOAD_RECHECK_NAME):
                self.emit("reload-check-%d" % number, {"passed": True, "epochDirectory": "epoch-" + str(number),
                                                       "schema": "code-retrieval-reload-check-v1"},
                          folder / name)
            manifests[str(number)] = digest(manifest_raw)
            epochs.append({"epoch": number, "checkpointManifestSha256": digest(manifest_raw),
                           "referenceSha256": digest(packed), "frozenUnchanged": True, "encoderChanged": True,
                           "plannedSteps": 2, "successfulSteps": 2, "skippedSteps": 0, "nonFiniteStepLosses": 0,
                           "elapsedSeconds": 1.0})
        ref_probe = self.emit("reference-probe", {"schema": "probe-fixture", "side": "reference"},
                              train_dir / index_verify.REFERENCE_PROBE)
        live_probe = self.emit("live-probe", {"schema": "probe-fixture", "side": "live"},
                               train_dir / index_verify.LIVE_PROBE)
        pinned = {"python": "artifacts/code_analysis/env/ml/python.exe"}
        self.write(train_dir / "reload-process.log", b"reload ok\n")
        identity = {"configByteSha256": cfg["byteSha256"], "configSha256": cfg["configSha256"],
                    "inputsManifestSha256": inputs_sha, "prepare": binding, "protocolSha256": cfg["protocolSha256"],
                    "upstream": {"modelRevision": trusted["modelRevision"], "fileHashes": trusted["fileHashes"]},
                    "upstreamBaselineFullStateSha256": h("full"), "upstreamLoadedFullStateSha256": h("full")}
        train = {"schema": index_verify.TRAIN_EVIDENCE_SCHEMA, "runId": CHAIN["train"], "outcome": "SUCCEEDED",
                 "reason": None, "CanAuthorizePublication": False,
                 "selection": {"selectedForUse": False, "selection": index_verify.SELECTION},
                 "identity": identity, "epochs": epochs,
                 "steps": {"planned": 6, "successful": 6, "skipped": 0, "nonFiniteLosses": 0},
                 "reload": {"passed": True, "exitCode": 0, "log": "reload-process.log"},
                 "referenceProbe": {"referenceProbeSha256": digest(ref_probe), "liveProbeSha256": digest(live_probe),
                                    "comparison": {"result": "MATCH"},
                                    "reference": {"interpreter": pinned}},
                 "elapsedSeconds": 3.0, "environment": {"python": "artifacts/feasibility_pilot/env/ml/python.exe"}}
        self.train_raw = self.emit("train", train, train_dir / index_verify.TRAIN_EVIDENCE_NAME)
        self.manifests = manifests

    def _index(self, corpus, binding):
        index_dir = self.root / CHAIN["index"]
        models, galleries = {}, {}
        for language in LANGUAGES:
            ids = [h("file-%s-%d" % (language, i)) for i in range(GALLERY_PER_LANGUAGE)]
            galleries[language] = [{"fileId": file_id, "sha256": h("sha-" + file_id)} for file_id in ids]
        for label in LABELS:
            languages = {}
            directory = index_dir / index_verify.INDEX_DIR / label
            model_key = index_verify.model_key(identity_for(label))
            for language in LANGUAGES:
                gallery = galleries[language]
                chunks = [{"fileId": row["fileId"], "fragmentId": row["fileId"], "chunkOrdinal": 0, "tokenStart": 0,
                           "tokenEnd": 1, "totalInputTokens": 1, "representationSha256": h("rep-" + row["fileId"])}
                          for row in gallery]
                chunks_raw = canonical(chunks)
                vectors = b"\x01" * (len(gallery) * DIM * 4)
                self.write(directory / (language + ".fp32le"), vectors)
                self.write(directory / (language + ".chunks.json"), chunks_raw)
                languages[language] = {
                    "galleryFiles": len(gallery), "galleryKey": index_verify.gallery_key(gallery),
                    "indexKey": index_verify.index_key(model_key, gallery, corpus), "rows": len(chunks),
                    "vectorBytes": len(vectors), "vectorSha256": digest(vectors),
                    "chunkManifestBytes": len(chunks_raw), "chunkManifestSha256": digest(chunks_raw),
                    "partialMLCoverageFiles": 1 if label == "E1" else 0, "omittedTokenCount": 0, "embedSeconds": 0.1}
            manifest = {"schema": index_verify.INDEX_SCHEMA, "label": label, "kind": identity_for(label)["kind"],
                        "partition": "validation", "modelKey": model_key, "identity": identity_for(label),
                        "dimension": DIM, "chunking": "chunk-fixture", "languages": languages, "corpus": corpus}
            self.emit("index-manifest-" + label, manifest, directory / index_verify.MANIFEST_NAME)
            models[label] = {"modelKey": model_key, "languages": languages, "peakAllocatedVRAM": 1,
                             "peakReservedVRAM": 1, "workingSet": {}}
        self.galleries = galleries
        self.models = models
        ref_probe = self.emit("index-reference-probe", {"schema": "probe-fixture", "side": "index-reference"},
                              index_dir / index_verify.REFERENCE_PROBE)
        self.emit("index-live-probe", {"schema": "probe-fixture", "side": "index-live"},
                  index_dir / index_verify.LIVE_PROBE)
        export = {"schema": index_verify.EXPORT_SCHEMA, "runId": CHAIN["index"], "outcome": "SUCCEEDED",
                  "reason": None, "CanAuthorizePublication": False,
                  "identity": {**corpus, "prepare": binding, "prepareRunId": CHAIN["prepare"],
                               "trainRunId": CHAIN["train"], "trainingEvidenceSha256": digest(self.train_raw),
                               "checkpointManifestSha256": {label: self.manifests[label[1:]] for label in TRAINED}},
                  "models": models,
                  "galleryFiles": {language: GALLERY_PER_LANGUAGE for language in LANGUAGES},
                  "queryCounts": {language: QUERIES_PER_LANGUAGE for language in LANGUAGES},
                  "likeForLike": {"result": "MATCH", "sampleChunks": GALLERY_PER_LANGUAGE, "maxAbsDifference": 0.0,
                                  "tolerance": 1e-6},
                  "referenceProbe": {"referenceSha256": digest(ref_probe), "comparison": {"result": "MATCH"}},
                  "environment": {"python": "artifacts/feasibility_pilot/env/ml/python.exe"}, "elapsedSeconds": 1.0}
        self.export_raw = self.emit("export", export, index_dir / index_verify.EXPORT_EVIDENCE)

    def _evaluation(self, corpus, binding):
        queries = []
        for language in LANGUAGES:
            scored = sorted(row["fileId"] for row in self.galleries[language])
            for number in range(QUERIES_PER_LANGUAGE):
                queries.append({"queryId": h("query-%s-%d" % (language, number)), "language": language,
                                "windows": 1, "scoredGallery": scored})
        total = len(queries)
        denominators = {label: {"queries": total, "galleryCounts": {"min": GALLERY_PER_LANGUAGE, "mean": 2.0,
                                                                    "max": GALLERY_PER_LANGUAGE},
                                "relevantCounts": {"min": 1, "mean": 1.0, "max": 1}, "unknownExcluded": 0}
                        for label in LABELS}
        shares = {"E0": 0.985, "E1": 0.985, "E2": 0.98, "E3": 0.985}
        saturation = {label: {"overall": {"firstRelevantAtRankOneShare": shares[label], "queries": total},
                              "perLanguage": {}} for label in LABELS}
        deltas = {"E1": (0.0, 0.0), "E2": (-0.0025, 0.0033), "E3": (0.0, 0.0033)}
        paired = {}
        for label in TRAINED:
            mrr, recall = deltas[label]
            paired[label] = {"semantic@20.mrr": {"meanDifference": mrr, "improved": 0,
                                                 "worse": 1 if mrr < 0 else 0, "equal": 99 if mrr < 0 else 100,
                                                 "seeds": 100},
                             "semantic@20.recall": {"meanDifference": recall, "improved": 2 if recall else 0,
                                                    "worse": 0, "equal": 98 if recall else 100, "seeds": 100},
                             "union@40.recall": {"meanDifference": 0.0, "improved": 0, "worse": 0, "equal": 100,
                                                 "seeds": 100}}
        selection = {"schema": "code-retrieval-selection-v1", "runId": CHAIN["evaluation"], "choice": "E3",
                     "status": "CHOSEN_BY_RULE", "approvedForUse": False,
                     "checkpointManifestSha256": {label: self.manifests[label[1:]] for label in TRAINED},
                     "scores": {"E1": {"mrr": 0.99, "recall": 0.98}, "E2": {"mrr": 0.98, "recall": 0.97},
                                "E3": {"mrr": 0.99, "recall": 0.98}},
                     "scope": "trained epochs only", "rule": "semantic-only seed-macro MRR@20",
                     "comparedAgainstFrozen": {"model": "E3", "mrrDifference": 0.0, "recallDifference": 0.0033},
                     "trainingManifestsEdited": False}
        if "selection" in self.overrides:
            self.overrides["selection"](selection)
        identity = {**corpus, "prepare": binding, "prepareRunId": CHAIN["prepare"], "trainRunId": CHAIN["train"],
                    "trainingEvidenceSha256": digest(self.train_raw), "indexRunId": CHAIN["index"],
                    "indexExportOutcome": "SUCCEEDED",
                    "indexes": {label: {"modelKey": self.models[label]["modelKey"],
                                        "indexKeys": {language: self.models[label]["languages"][language]["indexKey"]
                                                      for language in LANGUAGES}} for label in LABELS}}
        evaluation = {"schema": index_verify.EVALUATION_SCHEMA, "runId": CHAIN["evaluation"], "outcome": "SUCCEEDED",
                      "reason": None, "CanAuthorizePublication": False, "identity": identity,
                      "denominators": denominators, "saturation": saturation, "pairedDeltas": paired,
                      "selection": selection,
                      "measurements": {label: {"queryEmbedding": {"count": 7, "p50": 0.01, "p95": 0.02},
                                               "queryRanking": {"count": 7, "p50": 0.001, "p95": 0.002}}
                                       for label in LABELS},
                      "measurementScope": {"queryEmbedding": "per query", "queryRanking": "per query on CPU"},
                      "environment": {"python": "artifacts/feasibility_pilot/env/ml/python.exe"},
                      "elapsedSeconds": 1.0, "token": {"status": "SUCCEEDED"}, "tables": {}}
        evaluation_dir = self.root / CHAIN["evaluation"]
        self.emit("selection", selection, evaluation_dir / index_verify.SELECTION_FILE)
        self.evaluation_raw = self.emit("evaluation", evaluation, evaluation_dir / index_verify.EVALUATION_EVIDENCE)
        self.queries = queries

    def _database(self, corpus, binding):
        db_dir = self.root / CHAIN["database"]
        vectors = b"\x00" * (len(self.queries) * DIM * 4)
        labels = {label: {"modelKey": self.models[label]["modelKey"], "vectors": len(self.queries),
                          "bytes": len(vectors), "sha256": digest(vectors)} for label in LABELS}
        for label in LABELS:
            self.write(db_dir / index_verify.QUERY_DIR / (label + ".fp32le"), vectors)
        query = {"schema": index_verify.QUERY_SCHEMA, "runId": CHAIN["database"],
                 "Purpose": "CODE_RETRIEVAL_DB_INDEX_CHECK", "CanAuthorizePublication": False,
                 "trainRunId": CHAIN["train"], "indexRunId": CHAIN["index"], "evaluationRunId": CHAIN["evaluation"],
                 "trainingEvidenceSha256": digest(self.train_raw), "exportEvidenceSha256": digest(self.export_raw),
                 "evaluationEvidenceSha256": digest(self.evaluation_raw), "prepare": binding, "corpus": corpus,
                 "dimension": DIM, "labels": labels, "queries": self.queries,
                 "galleryKeys": {language: index_verify.gallery_key(self.galleries[language])
                                 for language in LANGUAGES},
                 "environment": {"python": "artifacts/code_analysis/env/index/python.exe"}}
        query_raw = self.emit("query", query, db_dir / index_verify.QUERY_DIR / index_verify.QUERY_MANIFEST)
        publish = {label: {language: {"indexKey": self.models[label]["languages"][language]["indexKey"],
                                      "rows": self.models[label]["languages"][language]["rows"],
                                      "status": "PUBLISHED_COMPLETE",
                                      "vectorSha256": self.models[label]["languages"][language]["vectorSha256"]}
                           for language in LANGUAGES} for label in LABELS}
        records = {label: [{"queryId": row["queryId"], "language": row["language"], "exact": True, "nearTies": [],
                            "disagreements": [], "maxAbsScoreDifference": 0.0} for row in self.queries]
                   for label in LABELS}
        agreement = {"schema": index_verify.DB_EVIDENCE_SCHEMA, "runId": CHAIN["database"], "records": records}
        agreement_raw = self.emit("agreement", agreement, db_dir / index_verify.DB_AGREEMENT_NAME)
        database = {"schema": index_verify.DB_EVIDENCE_SCHEMA, "runId": CHAIN["database"], "outcome": "SUCCEEDED",
                    "reason": None, "CanAuthorizePublication": False,
                    "identity": {**corpus, "prepare": binding, "prepareRunId": CHAIN["prepare"],
                                 "trainRunId": CHAIN["train"], "indexRunId": CHAIN["index"],
                                 "evaluationRunId": CHAIN["evaluation"],
                                 "trainingEvidenceSha256": digest(self.train_raw),
                                 "exportEvidenceSha256": digest(self.export_raw),
                                 "evaluationEvidenceSha256": digest(self.evaluation_raw),
                                 "queryManifestSha256": digest(query_raw)},
                    "publish": publish, "agreementDetailsSha256": digest(agreement_raw),
                    "latency": {label: {"count": len(self.queries), "p50": 0.01, "p95": 0.02, "warmupExcluded": 3}
                                for label in LABELS},
                    "existingSandbox": {"before": {"indexes": 4, "embeddings": 4, "snapshots": 2, "fragments": 2},
                                        "after": {"indexes": 24, "embeddings": 24, "snapshots": 3, "fragments": 3},
                                        "existingRowsUnchanged": True},
                    "sandbox": {"container": "fixture-container", "image": "fixture-image",
                                "bind": "127.0.0.1:55433:5432", "volume": "fixture-volume",
                                "database": "fixture-database", "initialization": {"ddl": "EXISTING_PRESERVED"}},
                    "environment": {"python": "artifacts/code_analysis/env/index/python.exe", "psycopg": "3.2.10",
                                    "serverVersion": "16"}}
        self.emit("database", database, db_dir / index_verify.DB_EVIDENCE_NAME)


@unittest.skipIf(TEST_IMPORT_ERROR is not None, "evidence package imports unavailable")
class EvidenceChainTests(unittest.TestCase):
    def setUp(self):
        temp = tempfile.TemporaryDirectory()
        self.addCleanup(temp.cleanup)
        self.root = Path(temp.name)
        self.review = self.root / "review"
        self.review.mkdir()
        for name in ep.RUNTIME_REVIEW_FILES:
            (self.review / name).write_bytes(b"approval " + name.encode("utf-8"))
        patches = [mock.patch.object(ep, "run_dir", lambda run_id: self.root / run_id),
                   mock.patch.object(ep, "RETRIEVAL_ROOT", self.root),
                   mock.patch.object(ep.sandbox, "runtime_review_root", lambda path=None: self.review)]
        for patch in patches:
            patch.start()
            self.addCleanup(patch.stop)

    def verify(self, **kwargs):
        return ep.verify_chain(CHAIN, inputs_path=self.root / "inputs.json", runtime_review=str(self.review), **kwargs)

    def chain(self, **overrides):
        Fixture(self.root, overrides).build()
        return self.verify()

    def assert_refused(self, message, **overrides):
        with self.assertRaisesRegex(ValueError, message):
            self.chain(**overrides)

    def rewrite(self, relative, mutate):
        """Edits one written JSON file after the chain is built; the stage must read the edited bytes."""
        path = self.root / relative
        document = json.loads(path.read_bytes())
        mutate(document)
        path.write_bytes(canonical(document))

    # -- passing chain and the three statuses ------------------------------------------------
    def test_consistent_chain_passes_and_statuses_stay_separate(self):
        verified = self.chain()
        package = ep.build_package(STAGE, CHAIN, verified)
        statuses = package["statuses"]
        self.assertEqual(statuses["trainingCompleted"]["status"], "COMPLETED")
        self.assertEqual(statuses["qualityOutcome"]["status"], ep.NO_MEASURABLE_DIFFERENCE)
        self.assertEqual(statuses["reviewerAcceptance"], "NOT_RECORDED_IN_EVIDENCE")
        self.assertEqual(set(statuses), {"trainingCompleted", "qualityOutcome", "reviewerAcceptance"})
        self.assertEqual(package["finalTest"]["opened"], False)
        self.assertEqual(package["finalTest"]["scored"], False)
        self.assertEqual(package["identities"]["configByteSha256"], ep.REQUIRED_CONFIG_BYTE_SHA256)
        self.assertEqual(package["identities"]["evaluationProtocolSha256"], index_verify.protocol_sha256())
        self.assertNotEqual(package["identities"]["admissionProtocolSha256"],
                            package["identities"]["evaluationProtocolSha256"])
        inventory = package["inventory"]["files"]
        self.assertEqual(package["inventory"]["fileCount"], len(inventory))
        for entry in inventory:
            self.assertFalse(Path(entry["path"]).is_absolute() or ":" in entry["path"], entry["path"])
        self.assertEqual(len(package["commandLedger"]), 10)
        self.assertEqual(package["commandLedger"][-1]["stage"], "validate-evidence")

    def test_run_writes_a_succeeded_package_and_a_reconstruction(self):
        Fixture(self.root).build()
        result = ep.run(run_id=STAGE, inputs_path=self.root / "inputs.json", config_path=ep.CONFIG_PATH,
                        prepare_run_id=CHAIN["prepare"], train_run_id=CHAIN["train"], index_run_id=CHAIN["index"],
                        evaluation_run_id=CHAIN["evaluation"], database_run_id=CHAIN["database"],
                        runtime_review=str(self.review))
        self.assertEqual(result["outcome"], "SUCCEEDED")
        package = json.loads((self.root / STAGE / ep.PACKAGE_NAME).read_bytes())
        self.assertEqual(package["outcome"], "SUCCEEDED")
        self.assertTrue((self.root / STAGE / ep.RECONSTRUCTION_NAME).is_file())
        markdown = (self.root / STAGE / ep.RECONSTRUCTION_NAME).read_text(encoding="utf-8")
        self.assertIn("NO_MEASURABLE_DIFFERENCE_NEAR_CEILING", markdown)
        self.assertIn("NOT_RECORDED_IN_EVIDENCE", markdown)

    def test_failed_stage_writes_failed_evidence_and_no_success_marker(self):
        Fixture(self.root).build()
        (self.root / CHAIN["index"] / index_verify.EXPORT_EVIDENCE).unlink()
        result = ep.run(run_id=STAGE, inputs_path=self.root / "inputs.json", config_path=ep.CONFIG_PATH,
                        prepare_run_id=CHAIN["prepare"], train_run_id=CHAIN["train"], index_run_id=CHAIN["index"],
                        evaluation_run_id=CHAIN["evaluation"], database_run_id=CHAIN["database"],
                        runtime_review=str(self.review))
        self.assertEqual(result["outcome"], "FAILED_ERROR")
        text = (self.root / STAGE / ep.PACKAGE_NAME).read_text(encoding="utf-8")
        self.assertIn('"outcome":"FAILED_ERROR"', text)
        self.assertNotIn("SUCCEEDED", text)
        self.assertFalse((self.root / STAGE / ep.RECONSTRUCTION_NAME).exists())

    def test_failure_reason_has_no_machine_path(self):
        Fixture(self.root).build()
        result = ep.run(run_id=STAGE, inputs_path=self.root / "inputs.json",
                        config_path=self.root / "missing-config.json",
                        prepare_run_id=CHAIN["prepare"], train_run_id=CHAIN["train"], index_run_id=CHAIN["index"],
                        evaluation_run_id=CHAIN["evaluation"], database_run_id=CHAIN["database"],
                        runtime_review=str(self.review))
        self.assertEqual(result["outcome"], "FAILED_ERROR")
        package = json.loads((self.root / STAGE / ep.PACKAGE_NAME).read_bytes())
        self.assertEqual(package["reason"], "ValueError: missing or unreadable evidence file: missing-config.json")
        self.assertNotIn(str(self.root), json.dumps(package))

    def test_safe_reason_removes_drive_and_unix_paths(self):
        text = ep.safe_reason("open failed: C:\\Users\\someone\\run\\file.json and /tmp/run/x.json")
        self.assertNotIn("C:", text)
        self.assertNotIn("/tmp", text)
        self.assertIn("<path>", text)

    # -- rejections: each one breaks one fact ------------------------------------------------
    def test_missing_evidence_file_is_refused(self):
        Fixture(self.root).build()
        (self.root / CHAIN["index"] / index_verify.EXPORT_EVIDENCE).unlink()
        with self.assertRaisesRegex(ValueError, "missing or unreadable evidence file"):
            self.verify()

    def test_failed_outcome_is_refused(self):
        self.assert_refused("index export did not succeed",
                            export=lambda e: e.update(outcome="FAILED_ERROR", reason="fixture"))

    def test_same_size_byte_change_is_a_hash_mismatch(self):
        Fixture(self.root).build()
        path = self.root / CHAIN["train"] / "epoch-1" / index_verify.CHECKPOINT_DIR / "vocab.json"
        data = bytearray(path.read_bytes())
        data[0] ^= 0x01
        path.write_bytes(bytes(data))
        with self.assertRaisesRegex(ValueError, "hash mismatch: train-fixture/epoch-1/checkpoint/vocab.json"):
            self.verify()

    def test_appended_byte_is_a_size_mismatch(self):
        Fixture(self.root).build()
        path = self.root / CHAIN["index"] / index_verify.INDEX_DIR / "E1" / "python.fp32le"
        path.write_bytes(path.read_bytes() + b"\x00")
        with self.assertRaisesRegex(ValueError, "size mismatch: index-fixture/index/E1/python.fp32le"):
            self.verify()

    def test_database_bound_to_another_export_is_refused(self):
        self.assert_refused("database evidence binds another input: exportEvidenceSha256",
                            database=lambda d: d["identity"].update(exportEvidenceSha256=h("other-export")))

    def test_missing_language_is_refused(self):
        self.assert_refused("export languages are not the five reviewed languages",
                            export=lambda e: e["galleryFiles"].pop("typescript"))

    def test_extra_language_is_refused(self):
        self.assert_refused("export languages are not the five reviewed languages",
                            export=lambda e: e["galleryFiles"].update(klingon=2))

    def test_extra_model_is_refused(self):
        self.assert_refused("export model set is not E0..E3",
                            export=lambda e: e["models"].update({"E4": e["models"]["E3"]}))

    def test_missing_model_is_refused(self):
        self.assert_refused("export model set is not E0..E3",
                            export=lambda e: e.update(models={k: v for k, v in e["models"].items() if k != "E2"}))

    def test_counts_that_disagree_are_refused(self):
        self.assert_refused("query counts per language disagree with the export evidence",
                            export=lambda e: e["queryCounts"].update(python=QUERIES_PER_LANGUAGE + 1))

    def test_zero_queries_are_refused(self):
        self.assert_refused("query count differs from the evaluation evidence: E0",
                            query=lambda q: q.update(queries=[]))

    def test_selected_for_use_checkpoint_is_refused(self):
        self.assert_refused("checkpoint manifest must not be selected for use: epoch-1",
                            **{"epoch-manifest-1": lambda m: m.update(selectedForUse=True)})

    def test_approved_for_use_selection_is_refused(self):
        self.assert_refused("selection is approved for use",
                            selection=lambda s: s.update(approvedForUse=True))

    def test_selected_for_use_true_on_the_selection_is_refused(self):
        self.assert_refused("selection is selected for use",
                            selection=lambda s: s.update(selectedForUse=True))

    def test_missing_approved_for_use_is_refused(self):
        self.assert_refused("selection does not record approvedForUse",
                            selection=lambda s: s.pop("approvedForUse", None))

    def test_wrong_selection_status_is_refused(self):
        self.assert_refused("selection status is not CHOSEN_BY_RULE",
                            selection=lambda s: s.update(status="PENDING"))

    def test_selection_without_selected_for_use_is_accepted(self):
        self.assertIsNotNone(self.chain())
        selection = json.loads((self.root / CHAIN["evaluation"] / index_verify.SELECTION_FILE).read_bytes())
        self.assertNotIn("selectedForUse", selection)

    def test_final_test_in_a_query_is_refused(self):
        self.assert_refused("final-test appears in database-fixture/query-vectors/manifest.json",
                            query=lambda q: q.update(Purpose="final-test"))

    def test_final_test_text_in_an_index_manifest_is_refused(self):
        Fixture(self.root).build()
        self.rewrite(CHAIN["index"] + "/" + index_verify.INDEX_DIR + "/E1/" + index_verify.MANIFEST_NAME,
                     lambda m: m.update(note="final-test"))
        with self.assertRaisesRegex(ValueError, "final-test appears in index-fixture/index/E1/index-manifest.json"):
            self.verify()

    def test_final_test_text_in_the_agreement_is_refused(self):
        Fixture(self.root).build()
        self.rewrite(CHAIN["database"] + "/" + index_verify.DB_AGREEMENT_NAME, lambda a: a.update(note="final-test"))
        with self.assertRaisesRegex(ValueError, "final-test appears in database-fixture/db-agreement.json"):
            self.verify()

    def test_final_test_text_in_the_selection_is_refused(self):
        Fixture(self.root).build()
        self.rewrite(CHAIN["evaluation"] + "/" + index_verify.SELECTION_FILE, lambda s: s.update(note="final-test"))
        with self.assertRaisesRegex(ValueError, "final-test appears in evaluation-fixture/selection.json"):
            self.verify()

    def test_chain_documents_scanned_are_listed_in_the_package(self):
        package = ep.build_package(STAGE, CHAIN, self.chain())
        scanned = package["finalTest"]["chainDocumentsScanned"]
        for name in ("prepare-fixture/prepare-data.json", "evaluation-fixture/selection.json",
                     "database-fixture/db-agreement.json", "index-fixture/index/E1/index-manifest.json"):
            self.assertIn(name, scanned)
        self.assertFalse(package["finalTest"]["finalTestTextFound"])

    # -- the one allowed final-test reference, by exact document and key path --------------------
    ALLOWED_IDENTITY = {"inputs": {"final-test-membership": {
        "byteSha256": "a" * 64, "bytes": 1, "canonicalSha256": "b" * 64, "relativePath": "final-test-membership.json"}}}

    def test_final_test_text_beside_the_allowed_reference_is_refused(self):
        identity = json.loads(canonical(self.ALLOWED_IDENTITY))
        identity["inputs"]["final-test-membership"]["note"] = "final-test"
        self.assert_refused("final-test appears in prepare-fixture/prepare-data.json",
                            prepare=lambda p: p.update(identity=identity))

    def test_allowed_reference_at_its_exact_key_path_passes_and_is_recorded(self):
        verified = self.chain(prepare=lambda p: p.update(identity=self.ALLOWED_IDENTITY))
        package = ep.build_package(STAGE, CHAIN, verified)
        self.assertEqual(package["finalTest"]["allowedReferences"],
                         [{"document": "prepare-fixture/prepare-data.json",
                           "keyPath": "identity.inputs.final-test-membership.relativePath",
                           "value": "final-test-membership.json"}])
        self.assertFalse(package["finalTest"]["finalTestTextFound"])

    def test_same_reference_at_another_key_path_is_refused(self):
        self.assert_refused("final-test appears in prepare-fixture/prepare-data.json",
                            prepare=lambda p: p.update(identity={"inputs": {"other": {
                                "relativePath": "final-test-membership.json"}}}))

    def test_same_reference_in_another_document_is_refused(self):
        self.assert_refused("final-test appears in train-fixture/train-evidence.json",
                            prepare=lambda p: p.update(identity=self.ALLOWED_IDENTITY),
                            train=lambda t: t.update(reference={"relativePath": "final-test-membership.json"}))

    def test_other_final_test_text_in_the_prepare_document_is_refused(self):
        self.assert_refused("final-test appears in prepare-fixture/prepare-data.json",
                            prepare=lambda p: (p.update(identity=self.ALLOWED_IDENTITY), p.update(note="final-test")))

    def test_non_canonical_evidence_is_refused(self):
        Fixture(self.root).build()
        path = self.root / CHAIN["index"] / index_verify.EXPORT_EVIDENCE
        path.write_bytes(json.dumps(json.loads(path.read_bytes()), indent=2).encode("utf-8"))
        with self.assertRaisesRegex(ValueError, "not canonical: index-fixture/export-evidence.json"):
            self.verify()

    def test_empty_evidence_file_is_refused(self):
        Fixture(self.root).build()
        (self.root / CHAIN["database"] / index_verify.DB_AGREEMENT_NAME).write_bytes(b"")
        with self.assertRaisesRegex(ValueError, "empty evidence file: database-fixture/db-agreement.json"):
            self.verify()

    def test_checkpoint_manifest_hash_must_match_training_evidence(self):
        self.assert_refused("checkpoint manifest hash differs from training evidence: epoch-1",
                            train=lambda t: t["epochs"][0].update(checkpointManifestSha256=h("other")))

    def test_prepare_membership_that_scored_final_test_is_refused(self):
        self.assert_refused("prepare membership records final-test use",
                            prepare=lambda p: p["finalTestMembership"].update(scored=True))

    def test_failed_reload_check_is_refused(self):
        self.assert_refused("reload check failed or names another epoch: epoch-1",
                            **{"reload-check-1": lambda r: r.update(passed=False)})

    def test_selection_file_must_equal_the_evaluation_record(self):
        self.assert_refused("selection file differs from the evaluation evidence",
                            evaluation=lambda e: e["selection"].update(choice="E1"))

    def test_configuration_byte_identity_is_checked(self):
        Fixture(self.root).build()
        with self.assertRaisesRegex(ValueError, "configuration byte identity differs"):
            self.verify(required_config_byte_sha256=h("other"))

    def test_reload_log_outside_its_run_folder_is_refused(self):
        self.assert_refused("traversal rejected", train=lambda t: t["reload"].update(log="../outside.log"))

    def test_trusted_identity_drift_is_refused(self):
        Fixture(self.root).build()
        trusted = json.loads(Path(index_verify.TRUSTED_IDENTITY).read_bytes())
        name = next(iter(trusted["fileHashes"]))
        trusted["fileHashes"][name] = h("drift")
        path = self.root / "trusted.json"
        path.write_bytes(canonical(trusted))
        with self.assertRaisesRegex(ValueError, "training upstream file hashes differ from the trusted identity"):
            self.verify(trusted_path=path)

    def test_protocol_drift_is_refused(self):
        Fixture(self.root).build()
        protocol = json.loads(Path(index_verify.PROTOCOL_PATH).read_bytes())
        protocol["frozen"]["inputLength"] = 999
        path = self.root / "protocol.json"
        path.write_bytes(canonical(protocol))
        with self.assertRaises(ValueError):
            self.verify(protocol_path=path)

    def test_extra_file_in_the_checkpoint_folder_is_refused(self):
        Fixture(self.root).build()
        (self.root / CHAIN["train"] / "epoch-1" / index_verify.CHECKPOINT_DIR / "extra.bin").write_bytes(b"x")
        with self.assertRaisesRegex(ValueError, "checkpoint folder holds an extra or missing file: epoch-1"):
            self.verify()

    def test_database_disagreement_is_refused(self):
        self.assert_refused("database ranking disagrees with the CPU ranking: E1",
                            agreement=lambda a: a["records"]["E1"][0].update(disagreements=["row"]))

    def test_swapped_run_ids_are_refused_in_each_stage(self):
        swaps = {
            "train": lambda t: t.update(runId=CHAIN["index"]),
            "export": lambda e: e["identity"].update(trainRunId=CHAIN["index"]),
            "evaluation": lambda e: e["identity"].update(indexRunId=CHAIN["train"]),
            "database": lambda d: d["identity"].update(indexRunId=CHAIN["evaluation"]),
            "query": lambda q: q.update(trainRunId=CHAIN["index"]),
        }
        for name, swap in swaps.items():
            with self.subTest(stage=name):
                with self.assertRaises(ValueError):
                    self.chain(**{name: swap})

    def test_missing_runtime_review_is_refused(self):
        Fixture(self.root).build()
        with mock.patch.object(ep.sandbox, "runtime_review_root", return_value=None):
            with self.assertRaisesRegex(ValueError, "runtime review directory lacks"):
                self.verify()

    def test_unfinished_training_is_reported_not_hidden(self):
        verified = self.chain(train=lambda t: t["epochs"][1].update(frozenUnchanged=False))
        status = ep.build_package(STAGE, CHAIN, verified)["statuses"]["trainingCompleted"]
        self.assertEqual(status["status"], "NOT_COMPLETED")
        self.assertFalse(status["facts"]["frozenStateUnchangedEveryEpoch"])

    # -- quality classification, both ways and at the boundaries ----------------------------------
    def quality_evaluation(self, deltas, shares=None, worse=0):
        """Minimal evaluation record for the quality rule; deltas maps each trained label to (MRR, Recall) means."""
        shares = shares or {}
        paired = {}
        saturation = {label: {"overall": {"firstRelevantAtRankOneShare": shares.get(label, 0.99)}} for label in LABELS}
        for label in TRAINED:
            mrr, recall = deltas[label]
            paired[label] = {"semantic@20.mrr": {"meanDifference": mrr, "improved": 0, "worse": worse,
                                                 "equal": 100 - worse, "seeds": 100},
                             "semantic@20.recall": {"meanDifference": recall, "improved": 0, "worse": 0,
                                                    "equal": 100, "seeds": 100}}
        return {"pairedDeltas": paired, "saturation": saturation, "denominators": QUERY_DENOMINATORS}

    def test_quality_near_ceiling_is_no_measurable_difference(self):
        quality = ep._quality(self.quality_evaluation({"E1": (0.001, 0.002), "E2": (0.0, 0.0), "E3": (0.0, 0.0)}))
        self.assertEqual(quality["status"], ep.NO_MEASURABLE_DIFFERENCE)
        rows = quality["perModel"].values()
        self.assertTrue(all(row["classification"] == ep.NO_MEASURABLE_DIFFERENCE for row in rows))

    def test_quality_clear_difference_is_measurable_with_numbers(self):
        evaluation = {"pairedDeltas": {label: {"semantic@20.mrr": {"meanDifference": 0.05, "improved": 30,
                                                                   "worse": 2, "equal": 68, "seeds": 100},
                                               "semantic@20.recall": {"meanDifference": 0.04, "improved": 28,
                                                                      "worse": 1, "equal": 71, "seeds": 100}}
                                      for label in TRAINED},
                      "saturation": {"E0": {"overall": {"firstRelevantAtRankOneShare": 0.6}},
                                     "E1": {"overall": {"firstRelevantAtRankOneShare": 0.62}},
                                     "E2": {"overall": {"firstRelevantAtRankOneShare": 0.61}},
                                     "E3": {"overall": {"firstRelevantAtRankOneShare": 0.63}}},
                      "denominators": QUERY_DENOMINATORS}
        quality = ep._quality(evaluation)
        self.assertEqual(quality["status"], ep.MEASURABLE_DIFFERENCE)
        row = quality["perModel"]["E3"]
        self.assertEqual(row["classification"], ep.MEASURABLE_DIFFERENCE)
        self.assertEqual(row["largestAbsoluteMeanDifference"], 0.05)
        self.assertEqual(row["semanticMrrAt20"]["improved"], 30)
        self.assertEqual(row["meanDeltaSign"], "positive")
        self.assertNotIn("improvement", json.dumps(quality).lower())

    def test_delta_exactly_at_the_threshold_is_no_measurable_difference(self):
        quality = ep._quality(self.quality_evaluation({"E1": (ep.NEAR_CEILING_DELTA, 0.0), "E2": (0.0, 0.0),
                                                       "E3": (0.0, 0.0)}))
        self.assertEqual(quality["perModel"]["E1"]["classification"], ep.NO_MEASURABLE_DIFFERENCE)

    def test_negative_delta_of_minus_point_zero_one_is_measurable(self):
        evaluation = self.quality_evaluation({"E1": (-0.01, 0.0), "E2": (0.0, 0.0), "E3": (0.0, 0.0)}, worse=1)
        quality = ep._quality(evaluation)
        self.assertEqual(quality["status"], ep.MEASURABLE_DIFFERENCE)
        self.assertEqual(quality["perModel"]["E1"]["meanDeltaSign"], "negative")
        self.assertTrue(quality["perModel"]["E1"]["worseOnAnySeed"])

    def test_rank_one_share_below_the_stated_share_is_measurable(self):
        quality = ep._quality(self.quality_evaluation({"E1": (0.0, 0.0), "E2": (0.0, 0.0), "E3": (0.0, 0.0)},
                                                      shares={"E3": 0.94}))
        self.assertEqual(quality["perModel"]["E3"]["classification"], ep.MEASURABLE_DIFFERENCE)
        self.assertEqual(quality["status"], ep.MEASURABLE_DIFFERENCE)

    def test_small_loss_is_visible_but_not_measurable(self):
        quality = ep._quality(self.quality_evaluation({"E1": (-0.0025, 0.0), "E2": (0.0, 0.0), "E3": (0.0, 0.0)},
                                                      worse=1))
        row = quality["perModel"]["E1"]
        self.assertEqual(row["classification"], ep.NO_MEASURABLE_DIFFERENCE)
        self.assertTrue(row["worseOnAnySeed"])
        self.assertEqual(row["meanDeltaSign"], "negative")
        self.assertIn("one flipped query", quality["rule"]["reason"])

    def test_first_limitation_follows_the_quality_status(self):
        near = ep.build_package(STAGE, CHAIN, self.chain())
        self.assertIn("near the ceiling", near["limitations"]["verifiedByStage"][0])
        measurable = ep.build_package(STAGE, CHAIN, self.chain(
            evaluation=lambda e: e["pairedDeltas"]["E3"]["semantic@20.mrr"].update(meanDifference=0.05)))
        self.assertEqual(measurable["statuses"]["qualityOutcome"]["status"], ep.MEASURABLE_DIFFERENCE)
        first = measurable["limitations"]["verifiedByStage"][0]
        self.assertIn("measurable differences", first)
        self.assertNotIn("near the ceiling", first)

    # -- ledger, labels and recorded statements ---------------------------------------------------
    def test_ledger_flags_parse_with_the_real_parser(self):
        package = ep.build_package(STAGE, CHAIN, self.chain())
        ledger = package["commandLedger"]
        for entry in ledger:
            with self.subTest(stage=entry["stage"]):
                cli.build_parser().parse_args(entry["command"])
        self.assertTrue(ledger[0]["interpreterRole"].startswith("NOT RECORDED IN EVIDENCE"))
        export = next(entry for entry in ledger if entry["stage"] == "export-index")
        self.assertIn("--reference-probe", export["command"])
        reload_entries = [entry for entry in ledger if entry["stage"] == "train-reload-check"]
        self.assertEqual(len(reload_entries), 2)
        self.assertIn("reload-recheck.json", reload_entries[1]["command"])
        self.assertNotIn("--inputs", reload_entries[0]["command"])

    def test_publish_ledger_entry_carries_the_runtime_review(self):
        package = ep.build_package(STAGE, CHAIN, self.chain())
        publish = next(entry for entry in package["commandLedger"] if entry["stage"].endswith("publish-and-rank"))
        self.assertIn("--runtime-review", publish["command"])
        self.assertEqual(package["runtimeReview"]["approvalFiles"], list(ep.RUNTIME_REVIEW_FILES))

    def test_superseded_runs_are_recorded_and_named_in_the_ledger(self):
        package = ep.build_package(STAGE, CHAIN, self.chain(),
                                   superseded_run_ids=["retrieval-evidence-old-02", "retrieval-evidence-old-01"])
        self.assertEqual(package["supersededRunIds"], ["retrieval-evidence-old-01", "retrieval-evidence-old-02"])
        self.assertIn("retrieval-evidence-old-01, retrieval-evidence-old-02",
                      " ".join(package["limitations"]["recordedByOperator"]))
        validate = package["commandLedger"][-1]
        self.assertEqual(validate["command"].count("--superseded-run-id"), 2)

    def test_database_limitation_uses_recorded_fields_only(self):
        first = " ".join(ep.build_package(STAGE, CHAIN, self.chain())["limitations"]["verifiedByStage"])
        self.assertIn("added 20 new index keys", first)
        self.assertIn("indexes before 4, after 24", first)
        self.assertNotIn("volume", first.lower())

    def test_recorded_group_is_labelled_as_not_verified(self):
        limitations = ep.build_package(STAGE, CHAIN, self.chain())["limitations"]
        self.assertTrue(limitations["recordedByOperator"])
        self.assertIn("The baseline used per-fragment chunking.", limitations["recordedByOperator"])
        self.assertIn("16-window cap", " ".join(limitations["verifiedByStage"]))

    # -- no secret or DSN in any output ----------------------------------------------------------
    def test_secrets_and_dsn_never_reach_the_package(self):
        secret = "pw-" + "x" * 20
        Fixture(self.root).build()
        with mock.patch.dict(os.environ, {"ASSETBLOCK_CODE_INDEX_DSN":
                                          "host=127.0.0.1 port=55433 dbname=d user=u password=" + secret}):
            verified = self.verify()
            package = ep.build_package(STAGE, CHAIN, verified)
            text = json.dumps(package) + ep.reconstruction_markdown(package)
        self.assertNotIn(secret, text)
        self.assertNotIn("password", text.lower())
        self.assertNotIn("postgresql://", text.lower())
        self.assertNotIn("dsn", text.lower())


if __name__ == "__main__":
    unittest.main()
