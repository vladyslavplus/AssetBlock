"""Tiny synthetic fixtures; no real corpus, label or partition material is used."""

import copy

from scripts.model_evaluation import manifest

R, N, U = "RELEVANT", "NOT_RELEVANT", "UNKNOWN"
HEX = "ab" * 32
TOKENIZER_HASHES = {"e0": {"vocab.json": "a1" * 32, "merges.txt": "a2" * 32},
                    "e3": {"vocab.json": "a1" * 32, "merges.txt": "a2" * 32}}
STATE = {"e0": "e0" * 32, "e3": "e3" * 32, "verifier": "fe" * 32}
SELECTED_EPOCH = 3
CHECKPOINT_HASHES = ["c%d" % epoch * 32 for epoch in range(1, 6)]
E3_MANIFEST_HASH = "d3" * 32
BASE_FILE_HASHES = {"vocab.json": "a1" * 32, "merges.txt": "a2" * 32, "model.safetensors": "a3" * 32}
THRESHOLD = 0.10154
PACKAGE_BINDING = {"e0": {"trustedModelFileHashes": dict(BASE_FILE_HASHES)},
                   "e3": {"checkpointManifestSha256": E3_MANIFEST_HASH},
                   "verifier": {"selectedEpoch": SELECTED_EPOCH, "checkpointSha256": CHECKPOINT_HASHES[2]}}


def package_documents(threshold=THRESHOLD, **changes):
    """Evidence packages carrying the real key paths: one retrieval package (e0, e3) and one pair package."""
    retrieval = {"identities": {"trustedModelFileHashes": dict(BASE_FILE_HASHES),
                                "checkpointManifestSha256": {"E1": "d1" * 32, "E2": "d2" * 32, "E3": E3_MANIFEST_HASH}}}
    pair = {"selectedEpoch": SELECTED_EPOCH, "identities": {"checkpointHashes": list(CHECKPOINT_HASHES)},
            "metrics": {"pipelines": {"E0": {"threshold": {"threshold": 0.5}},
                                      "E3": {"threshold": {"threshold": threshold}}}}}
    documents = {"e0": retrieval, "e3": retrieval, "verifier": pair}
    for key, value in changes.items():
        name, field = key.split("__")
        documents[name] = value if field == "all" else {**documents[name], field: value}
    return documents


def write_packages(directory, inputs, **changes):
    """Write the package documents next to the inputs, bind their hashes, return ``{model: path}``."""
    from pathlib import Path
    from scripts.code_analysis.contracts import canonical, digest
    paths = {}
    threshold = inputs["operatingPoints"]["validationThresholdRuleResult"]["threshold"]
    for name, document in package_documents(threshold, **changes).items():
        path = Path(directory) / ("package-%s.json" % ("retrieval" if name != "verifier" else "pair"))
        raw = canonical(document)
        path.write_bytes(raw)
        inputs["models"][name]["evidencePackageSha256"] = digest(raw)
        paths[name] = path
    return paths


EXCLUDED_SOURCES = ["synthetic-excluded-a", "synthetic-excluded-b"]


def query(query_id, *, judgments, findings=(), language="python", family=None, seed=None,
          stratum="exact copy", dialect=None):
    record = {"queryId": query_id, "language": language, "family": family or "fam-" + query_id,
              "seedGroup": seed or "seed-" + query_id, "stratum": stratum,
              "judgments": dict(judgments), "findings": list(findings)}
    if dialect is not None:
        record["dialect"] = dialect
    return record


def positive_set(language, count, *, prefix, false_finding=False, dialect=None):
    """``count`` independent-family positive queries; optionally each also emits one negative."""
    rows = []
    for index in range(count):
        name = "%s-%s-%02d" % (prefix, language, index)
        findings = ["r", "n"] if false_finding else ["r"]
        rows.append(query(name, language=language, judgments={"r": R, "n": N}, findings=findings,
                          dialect=dialect))
    return rows


def no_match_set(language, count, *, prefix, alarms=0, dialect=None):
    rows = []
    for index in range(count):
        name = "%s-%s-nm%02d" % (prefix, language, index)
        rows.append(query(name, language=language, judgments={"a": N, "b": N},
                          findings=["a"] if index < alarms else [], dialect=dialect, stratum="hard negative"))
    return rows


def manifest_inputs():
    return {
        "preprocessing": {"framing": manifest.actual_representation()["framing"],
                          "chunking": manifest.actual_representation()["chunking"]},
        "models": {name: {"evidencePackageSha256": HEX, "stateSha256": STATE[name],
                          "packageBinding": copy.deepcopy(PACKAGE_BINDING[name]),
                          "unboundByPackage": {"stateSha256": manifest.UNBOUND_STATE_REASON},
                          **({"tokenizerHashes": dict(TOKENIZER_HASHES[name])} if name != "verifier" else {})}
                   for name in ("e0", "e3", "verifier")},
        "execution": {"dolosK": 23, "dolosWindow": 17},
        "excludedSources": list(EXCLUDED_SOURCES),
        "operatingPoints": {"productMaterialRule": "all top positive token-score ties",
                            "tokenRankCut": {"name": "token-rank-cut", "rank": 1},
                            "semanticRankCut": {"name": "semantic-rank-cut", "rank": 1},
                            "validationThresholdRuleResult": {"name": "validation-threshold-rule",
                                                              "threshold": 0.10154}},
        "candidateBudget": {"tokenTop": 20, "semanticTop": 20, "maxCandidates": 40, "verifierWindowPairs": 3},
        "pooling": {"rule": "UNION_TOKEN_E0_E3_PLUS_RANDOM_SAMPLE", "depthK": 20,
                    "randomRecallSampleSize": 25, "randomSeed": 7, "unjudgedLabel": "UNKNOWN"},
        "bootstrap": {"procedure": "FAMILY_CLUSTERED_PERCENTILE_WITHIN_LANGUAGE", "resamples": 2000, "seed": 20261103,
                      "unit": "family", "confidence": 0.95},
        "minimumCounts": {"perLanguage": {"positiveQueries": 10, "families": 10, "noMatchQueries": 5},
                          "tsxDialect": {"positiveQueries": 10, "families": 10, "noMatchQueries": 5},
                          "perStratum": {"exact copy": 5, "hard negative": 5}},
        "resourceProtocol": {"components": ["tokenization", "window-alignment", "inference", "queue"],
                             "warmupQueries": 3, "timedQueries": 30, "percentiles": [50, 95],
                             "includeWorstCase": True},
        "cpuGpuParity": {"model": "e0", "sampleSize": 20, "seed": 11, "topK": 20, "pipeline": "PRODUCT",
                         "compares": ["topK", "tieOrder"],
                         "onDisagreement": "REPORT_LIMITATION", "devices": ["cpu", "cuda"]},
        "corpus": {"status": manifest.CORPUS_PENDING, "identity": None},
    }


def minimum_counts():
    return {"perLanguage": {"positiveQueries": 10, "families": 10, "noMatchQueries": 5},
            "tsxDialect": {"positiveQueries": 10, "families": 10, "noMatchQueries": 5},
            "perStratum": {"exact copy": 5, "hard negative": 5}}
