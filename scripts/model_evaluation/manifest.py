"""Freeze manifest: the pre-registered evaluation contract and its canonical hash.

The manifest binds preprocessing, model identities (recorded evidence-package
identity strings only), operating points, pooling, metrics, bootstrap, claim gates,
minimum counts, resource protocol, rerun rule and the corpus identity. After the
hash is recorded nothing in it may change; a changed manifest is a new manifest.
"""

import copy
import math
from pathlib import Path

from scripts.code_analysis.contracts import canonical, digest, sha
from scripts.code_retrieval import inputs
from scripts.feasibility_pilot.paths import REPO_ROOT, ensure_inside
from scripts.pair_verification import evidence

from . import guards

SCHEMA = "model-evaluation-freeze-manifest-v1"
MANIFEST_NAME = "freeze-manifest.json"
MAX_INPUT_BYTES = 1024 * 1024

CORPUS_PENDING = "PENDING_OWNER_DECISION"
CORPUS_BOUND = "BOUND"

# Owner: the independent review's proposed acceptance (claim gates). They are
# pre-registered and a manifest that differs from them is rejected.
CLAIM_GATES = {
    "macroPrecisionMin": 0.8,
    "macroF1Min": 0.8,
    "replacementF1Floor": 0.85,
    "replacementF1Margin": 0.02,
    "replacementPrecisionMin": 0.8,
    "replacementRecallMin": 0.75,
    "replacementMaxLanguageF1Loss": 0.03,
    "productLowerBoundMin": 0.8,
    "falseFindingsPerPositiveQueryMax": 0.25,
    "spuriousAlertRateMax": 0.05,
    "replacementDeltaLowerBoundAbove": 0,
    "confidenceLevel": 0.95,
}
METRIC_NAMES = ("precision", "recall", "f1", "falseFindingsPerPositiveQuery", "spuriousAlertRate",
                "abstentionCoverage", "multiMatchRecall")
AGGREGATION = {"caseWithinSeed": "mean", "seedWithinLanguage": "mean", "languages": "equal-macro",
               "micro": "reported"}
RERUN_RULE = {"allowedReasons": list(guards.RERUN_REASONS), "maxFreeScoringRuns": 1,
              "firstRunRetained": True, "rerunOnUnfavorableResult": False}
REPLACEMENT_BASELINES = ["PRODUCT", "TOKEN_RANK_CUT"]
BOOTSTRAP_PROCEDURE = "FAMILY_CLUSTERED_PERCENTILE_WITHIN_LANGUAGE_AND_KIND"
CONSTANT_SECTIONS = {"claimGates": CLAIM_GATES, "replacementBaselines": REPLACEMENT_BASELINES,
                     "metrics": {"names": list(METRIC_NAMES), "judgments": ["RELEVANT", "NOT_RELEVANT", "UNKNOWN"]},
                     "aggregation": AGGREGATION, "rerunRule": RERUN_RULE}
SECTIONS = ("preprocessing", "models", "execution", "operatingPoints", "candidateBudget", "pooling", "metrics",
            "aggregation", "bootstrap", "claimGates", "replacementBaselines", "minimumCounts",
            "resourceProtocol", "rerunRule", "cpuGpuParity", "excludedSources", "corpus")
INPUT_SECTIONS = tuple(name for name in SECTIONS if name not in CONSTANT_SECTIONS)


def _text(value):
    if not isinstance(value, str) or not value.strip():
        raise ValueError("manifest text field is empty")
    return value


def _count(value, minimum=0):
    if type(value) is not int or value < minimum:
        raise ValueError("manifest count is invalid")
    return value


def _number(value):
    if type(value) not in (int, float) or not math.isfinite(value):
        raise ValueError("manifest number is invalid")
    return value


def _exact(value, keys, name):
    if not isinstance(value, dict):
        raise ValueError(name + " must be an object")
    if set(value) != set(keys):
        raise ValueError(name + " keys differ from the contract")
    return value


def _check_canonical_numbers(value):
    """Reject values whose canonical form would be ambiguous (non-finite, integral float, bool)."""
    if isinstance(value, dict):
        for nested in value.values():
            _check_canonical_numbers(nested)
    elif isinstance(value, list):
        for nested in value:
            _check_canonical_numbers(nested)
    elif isinstance(value, float):
        if not math.isfinite(value) or value == int(value):
            raise ValueError("manifest number is not canonical")


def actual_representation():
    """The representation envelope the code enforces, read from the service and the chunker (not copied)."""
    service_contracts()
    from app import mechanisms
    return mechanisms.actual_representation()


def _preprocessing(value):
    _exact(value, ("framing", "chunking"), "preprocessing")
    actual = actual_representation()
    if value["framing"] != actual["framing"] or value["chunking"] != actual["chunking"]:
        raise ValueError("preprocessing differs from the representation the code enforces")


MODEL_NAMES = ("e0", "e3", "verifier")
ENCODERS = ("e0", "e3")


UNBOUND_STATE_REASON = ("no evidence package carries the raw loaded-state hash; it lives inside the checkpoint "
                        "manifest, which the package binds only by hash; protected by the registry key instead")

# Fixed, documented key paths of the selected checkpoint and threshold in the real evidence packages.
# Retrieval package (scripts/code_retrieval/evidence_package.py, e0 and e3):
#   identities.trustedModelFileHashes             base model file hashes (e0)
#   identities.checkpointManifestSha256.E3        selected trained checkpoint manifest hash (e3)
# Pair-verification package (scripts/pair_verification/validation_evidence.py, verifier):
#   selectedEpoch, identities.checkpointHashes[selectedEpoch - 1] and
#   metrics.pipelines.E3.threshold.threshold      the validation threshold of the product pipeline
PACKAGE_PATHS = {
    "e0": {"trustedModelFileHashes": ("identities", "trustedModelFileHashes")},
    "e3": {"checkpointManifestSha256": ("identities", "checkpointManifestSha256", "E3")},
    "verifier": {"selectedEpoch": ("selectedEpoch",),
                 "checkpointSha256": ("identities", "checkpointHashes", None)},
}
VERIFIER_THRESHOLD_PATH = ("metrics", "pipelines", "E3", "threshold", "threshold")


def package_values(name, document):
    """Selected checkpoint facts and (verifier) threshold read from a parsed evidence package; fail closed."""
    def walk(path, label):
        node = document
        for step in path:
            if step is None:
                epoch = document.get("selectedEpoch") if isinstance(document, dict) else None
                if (type(epoch) is not int or not isinstance(node, list) or not 1 <= epoch <= len(node)):
                    raise ValueError("evidence package for %s has no checkpoint for its selected epoch" % name)
                node = node[epoch - 1]
            elif isinstance(node, dict) and step in node:
                node = node[step]
            else:
                raise ValueError("evidence package for %s lacks %s" % (name, label))
        return node
    values = {field: walk(path, ".".join(str(step) for step in path))
              for field, path in PACKAGE_PATHS[name].items()}
    if name == "verifier":
        values["threshold"] = walk(VERIFIER_THRESHOLD_PATH, ".".join(VERIFIER_THRESHOLD_PATH))
    return values


def _binding(name, value):
    expected = tuple(PACKAGE_PATHS[name])
    _exact(value, expected, "package binding of " + name)
    if name == "e0":
        hashes = value["trustedModelFileHashes"]
        if not isinstance(hashes, dict) or not hashes:
            raise ValueError("trusted model file hashes are required for e0")
        for file_name, file_hash in hashes.items():
            _text(file_name)
            sha(file_hash)
    elif name == "e3":
        sha(value["checkpointManifestSha256"])
    else:
        if type(value["selectedEpoch"]) is not int or value["selectedEpoch"] < 1:
            raise ValueError("selectedEpoch must be a positive integer")
        sha(value["checkpointSha256"])


def _models(value):
    _exact(value, MODEL_NAMES, "models")
    for name, entry in value.items():
        keys = (("evidencePackageSha256", "stateSha256", "packageBinding", "unboundByPackage")
                + (("tokenizerHashes",) if name in ENCODERS else ()))
        _exact(entry, keys, "model identity")
        sha(entry["evidencePackageSha256"])
        sha(entry["stateSha256"])
        _binding(name, entry["packageBinding"])
        if entry["unboundByPackage"] != {"stateSha256": UNBOUND_STATE_REASON}:
            raise ValueError("unboundByPackage must record the state hash with its documented reason")
        if name in ENCODERS:
            hashes = entry["tokenizerHashes"]
            if not isinstance(hashes, dict) or not hashes:
                raise ValueError("tokenizer file hashes are required for " + name)
            for file_name, file_hash in hashes.items():
                _text(file_name)
                sha(file_hash)


def service_contracts():
    """The deployed service's own contract constants (token depth, semantic depth, union budget, Dolos shape)."""
    import sys
    from scripts.feasibility_pilot.paths import REPO_ROOT as root
    service = str(root / "services" / "code-analysis")
    if service not in sys.path:
        sys.path.insert(0, service)
    from app import contracts
    return contracts


DEVICES = ("cpu", "cuda")


def _execution(value):
    _exact(value, ("dolosK", "dolosWindow"), "execution")
    contracts = service_contracts()
    if value["dolosK"] != contracts.TOKEN_K or value["dolosWindow"] != contracts.TOKEN_WINDOW:
        raise ValueError("Dolos shape differs from the service contract")


def _excluded_sources(value):
    if not isinstance(value, list) or not value:
        raise ValueError("excluded sources must be a non-empty list")
    for item in value:
        _text(item)
    if len(set(value)) != len(value):
        raise ValueError("excluded sources contain duplicates")


def _operating_points(value):
    _exact(value, ("productMaterialRule", "tokenRankCut", "semanticRankCut", "validationThresholdRuleResult"),
           "operatingPoints")
    _text(value["productMaterialRule"])
    _exact(value["tokenRankCut"], ("name", "rank"), "tokenRankCut")
    _text(value["tokenRankCut"]["name"])
    _count(value["tokenRankCut"]["rank"], 1)
    _exact(value["semanticRankCut"], ("name", "rank"), "semanticRankCut")
    _text(value["semanticRankCut"]["name"])
    _count(value["semanticRankCut"]["rank"], 1)
    _exact(value["validationThresholdRuleResult"], ("name", "threshold"), "threshold rule result")
    _text(value["validationThresholdRuleResult"]["name"])
    _number(value["validationThresholdRuleResult"]["threshold"])


def _candidate_budget(value):
    _exact(value, ("tokenTop", "semanticTop", "maxCandidates", "verifierWindowPairs"), "candidateBudget")
    for key in ("tokenTop", "semanticTop", "maxCandidates", "verifierWindowPairs"):
        _count(value[key], 1)
    contracts = service_contracts()
    if (value["tokenTop"], value["semanticTop"], value["maxCandidates"]) != (
            contracts.TOKEN_TOP, contracts.SEMANTIC_TOP, contracts.UNION_BUDGET):
        raise ValueError("candidate budget differs from the service contract")


POOLING_RULES = ("UNION_TOKEN_E0_E3_PLUS_RANDOM_SAMPLE", "EXHAUSTIVE")


def _pooling(value):
    _exact(value, ("rule", "depthK", "randomRecallSampleSize", "randomSeed", "unjudgedLabel"), "pooling")
    if value["rule"] not in POOLING_RULES:
        raise ValueError("pooling rule is not recognised")
    _count(value["depthK"], 1)
    _count(value["randomRecallSampleSize"])
    if value["rule"] == "EXHAUSTIVE" and value["randomRecallSampleSize"] != 0:
        raise ValueError("an exhaustive pooling rule judges every file; the random recall sample size must be 0")
    if type(value["randomSeed"]) is not int:
        raise ValueError("pooling seed must be an integer")
    if value["unjudgedLabel"] != "UNKNOWN":
        raise ValueError("unjudged files must be UNKNOWN, never negatives")


def _bootstrap(value):
    _exact(value, ("procedure", "resamples", "seed", "unit", "confidence"), "bootstrap")
    if value["procedure"] != BOOTSTRAP_PROCEDURE or value["unit"] != "family":
        raise ValueError("bootstrap must resample families")
    _count(value["resamples"], 1)
    if type(value["seed"]) is not int:
        raise ValueError("bootstrap seed must be an integer")
    if value["confidence"] != CLAIM_GATES["confidenceLevel"]:
        raise ValueError("bootstrap confidence differs from the claim gates")


def _group_minimum(value, name):
    _exact(value, ("positiveQueries", "families", "noMatchQueries"), name)
    for key in value:
        _count(value[key], 1)


def _minimum_counts(value):
    _exact(value, ("perLanguage", "tsxDialect", "perStratum"), "minimumCounts")
    _group_minimum(value["perLanguage"], "perLanguage minimum")
    _group_minimum(value["tsxDialect"], "tsxDialect minimum")
    if not isinstance(value["perStratum"], dict) or not value["perStratum"]:
        raise ValueError("per-stratum minimum counts are required")
    for label, minimum in value["perStratum"].items():
        _text(label)
        _count(minimum, 1)


def _resource_protocol(value):
    _exact(value, ("components", "warmupQueries", "timedQueries", "percentiles", "includeWorstCase"),
           "resourceProtocol")
    if not isinstance(value["components"], list) or not value["components"]:
        raise ValueError("resource components are required")
    for component in value["components"]:
        _text(component)
    _count(value["warmupQueries"])
    _count(value["timedQueries"], 1)
    if value["percentiles"] != [50, 95] or value["includeWorstCase"] is not True:
        raise ValueError("resource protocol must report p50, p95 and the worst case")


def _cpu_gpu_parity(value):
    _exact(value, ("model", "sampleSize", "seed", "topK", "pipeline", "compares", "onDisagreement",
                    "devices"), "cpuGpuParity")
    if value["devices"] != list(DEVICES):
        raise ValueError("parity devices must be the frozen cpu and cuda pair")
    if value["model"] != "e0" or value["compares"] != ["topK", "tieOrder"]:
        raise ValueError("parity must compare frozen E0 top-K and tie order")
    _count(value["sampleSize"], 1)
    _count(value["topK"], 1)
    if type(value["seed"]) is not int:
        raise ValueError("parity seed must be an integer")
    if value["pipeline"] != "PRODUCT":
        raise ValueError("parity compares the PRODUCT pipeline only")
    if value["onDisagreement"] != "REPORT_LIMITATION":
        raise ValueError("a parity disagreement is reported, never retuned")


def _corpus(value):
    _exact(value, ("status", "identity"), "corpus")
    if value["status"] == CORPUS_PENDING:
        if value["identity"] is not None:
            raise ValueError("a pending corpus cannot carry an identity")
    elif value["status"] == CORPUS_BOUND:
        if not isinstance(value["identity"], dict) or "corpusManifestSha256" not in value["identity"]:
            raise ValueError("a bound corpus needs an identity")
        for key in value["identity"]:
            sha(value["identity"][key])
    else:
        raise ValueError("corpus status is not recognised")


def _constant(name):
    def check(value):
        if value != CONSTANT_SECTIONS[name]:
            raise ValueError(name + " differs from the pre-registered value")
    return check


VALIDATORS = {"preprocessing": _preprocessing, "models": _models, "execution": _execution,
              "excludedSources": _excluded_sources, "operatingPoints": _operating_points,
              "candidateBudget": _candidate_budget, "pooling": _pooling, "bootstrap": _bootstrap,
              "minimumCounts": _minimum_counts, "resourceProtocol": _resource_protocol,
              "cpuGpuParity": _cpu_gpu_parity, "corpus": _corpus,
              **{name: _constant(name) for name in CONSTANT_SECTIONS}}


def validate_manifest(manifest):
    if not isinstance(manifest, dict) or set(manifest) != {"schema", *SECTIONS}:
        raise ValueError("manifest sections differ from the contract")
    if manifest["schema"] != SCHEMA:
        raise ValueError("unexpected manifest schema")
    _check_canonical_numbers(manifest)
    for section in SECTIONS:
        VALIDATORS[section](manifest[section])
    guards.refuse_sealed(manifest=manifest)
    evidence.validate(manifest)
    budget, points = manifest["candidateBudget"], manifest["operatingPoints"]
    if points["tokenRankCut"]["rank"] > budget["tokenTop"] or points["semanticRankCut"]["rank"] > budget["semanticTop"]:
        raise ValueError("a rank cut exceeds the retained channel depth")
    if manifest["pooling"]["depthK"] > min(budget["tokenTop"], budget["semanticTop"]):
        raise ValueError("pool depth exceeds the retained channel depth")
    return manifest


def freeze_manifest(supplied):
    """Return ``(manifest, sha256)`` for the supplied input sections; pre-registered rules are added."""
    if not isinstance(supplied, dict):
        raise ValueError("manifest inputs must be an object")
    unknown = set(supplied) - set(INPUT_SECTIONS)
    if unknown:
        raise ValueError("unknown manifest input sections: " + ", ".join(sorted(unknown)))
    missing = set(INPUT_SECTIONS) - set(supplied)
    if missing:
        raise ValueError("missing manifest input sections: " + ", ".join(sorted(missing)))
    manifest = {"schema": SCHEMA, **supplied, **{name: value for name, value in CONSTANT_SECTIONS.items()}}
    validate_manifest(manifest)
    return manifest, digest(canonical(manifest))


def bind_corpus(manifest, *, prepared_corpus_sha256, source_corpus_sha256):
    """A new freeze version whose corpus section is bound; the previous manifest is referenced by hash."""
    previous = manifest_sha256(manifest)
    if manifest["corpus"]["status"] != CORPUS_PENDING:
        raise ValueError("only a pending corpus can be bound")
    bound = {**copy.deepcopy(manifest),
             "corpus": {"status": CORPUS_BOUND,
                        "identity": {"corpusManifestSha256": sha(prepared_corpus_sha256),
                                     "sourceCorpusSha256": sha(source_corpus_sha256),
                                     "previousManifestSha256": previous}}}
    validate_manifest(bound)
    return bound, digest(canonical(bound))


def manifest_sha256(manifest):
    return digest(canonical(validate_manifest(manifest)))


def freeze(*, run_id, inputs_path, evidence_paths, state_hashes, threshold, run_root=guards.RUN_ROOT,
           inputs_root=REPO_ROOT):
    """Read a JSON inputs document, write the frozen manifest once, return its identity.

    The models-check pre-flight is mandatory: an evidence package path and a checkpoint state hash for
    e0, e3 and the verifier, and the verifier threshold, must all be supplied and must verify; otherwise
    nothing is written.
    """
    from . import models_check
    guards.refuse_sealed(ids=[run_id], paths=[inputs_path, *(evidence_paths or {}).values()])
    if set(evidence_paths or ()) != set(MODEL_NAMES) or set(state_hashes or ()) != set(MODEL_NAMES)             or threshold is None:
        raise ValueError("freeze requires evidence packages and state hashes for e0, e3 and verifier "
                         "and the verifier threshold")
    target = guards.run_dir(run_id, run_root) / MANIFEST_NAME
    source = ensure_inside(Path(inputs_path), Path(inputs_root))
    supplied = inputs.parse_json(inputs.read_bytes(source, MAX_INPUT_BYTES), "manifest inputs")
    manifest, manifest_hash = freeze_manifest(supplied)
    report = models_check.check(manifest, evidence_paths, root=inputs_root, state_hashes=state_hashes,
                                threshold=threshold)
    if not report["allVerified"]:
        raise ValueError("models-check did not verify every model; the freeze is refused")
    guards.write_output(target, canonical(manifest), run_root)
    return {"outcome": "SUCCEEDED", "runId": run_id, "directory": target.parent,
            "manifestSha256": manifest_hash}
