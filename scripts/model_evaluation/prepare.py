"""Prepare stage: bind an evaluation corpus manifest into a hashed prepared corpus.

Only identities, labels and counts are handled; file contents are never read here.
A successful run writes the prepared corpus and a NEW freeze manifest version whose
corpus section is bound and which references the previous manifest by hash.
"""

from pathlib import Path

from scripts.code_analysis.contracts import LANGUAGES, canonical, digest, sha
from scripts.code_retrieval import inputs
from scripts.feasibility_pilot.paths import REPO_ROOT, ensure_inside
from scripts.pair_verification import evidence

from . import guards, manifest as manifest_module, metrics

CORPUS_SCHEMA = "model-evaluation-corpus-v1"
PREPARED_SCHEMA = "model-evaluation-prepared-corpus-v1"
PREPARED_NAME = "prepared-corpus.json"
MAX_CORPUS_BYTES = 64 * 1024 * 1024
KINDS = ("gallery", "query")
POOL_SOURCES = ("TOKEN", "E0", "E3", "RANDOM_SAMPLE", "EXHAUSTIVE")
FILE_KEYS = {"fileId", "sha256", "language", "family", "partition", "kind", "source"}
OPTIONAL_FILE_KEYS = {"dialect"}
QUERY_KEYS = {"queryId", "queryFileId", "language", "family", "seedGroup", "stratum", "judgments", "dialect"}


def _text(value, name):
    if not isinstance(value, str) or not value:
        raise ValueError("corpus field is empty: " + name)
    return value


def source_excluded(source, excluded_sources):
    """True when a declared source is on the excluded list, compared as written and in normalised form."""
    forms = guards.compact_forms(source)
    return any(source == item or forms & guards.compact_forms(item) for item in excluded_sources)


def _validate_files(files, excluded_sources):
    if not isinstance(files, list) or not files:
        raise ValueError("corpus needs files")
    by_id = {}
    for row in files:
        if not isinstance(row, dict) or not FILE_KEYS <= set(row) <= FILE_KEYS | OPTIONAL_FILE_KEYS:
            raise ValueError("corpus file keys differ from the contract")
        _text(row["source"], "source")
        if source_excluded(row["source"], excluded_sources):
            raise guards.SealedInputRefused("corpus file comes from an excluded source")
        sha(row["fileId"])
        sha(row["sha256"])
        if row["fileId"] in by_id:
            raise ValueError("corpus file identity is duplicated")
        if row["language"] not in LANGUAGES or row["kind"] not in KINDS:
            raise ValueError("corpus file language or kind is unsupported")
        if row["partition"] != guards.CORPUS_ROLE:
            raise ValueError("corpus file partition is not the evaluation partition: " + row["partition"])
        _text(row["family"], "family")
        by_id[row["fileId"]] = row
    return by_id


def _validate_queries(queries, files):
    if not isinstance(queries, list) or not queries:
        raise ValueError("corpus needs queries")
    gallery = {language: sorted(fid for fid, row in files.items()
                                if row["kind"] == "gallery" and row["language"] == language)
               for language in LANGUAGES}
    prepared, seen, filled = [], set(), 0
    for query in queries:
        if not isinstance(query, dict) or not QUERY_KEYS - {"dialect"} <= set(query) <= QUERY_KEYS:
            raise ValueError("corpus query keys differ from the contract")
        for key in ("queryId", "family", "seedGroup", "stratum"):
            _text(query[key], key)
        if query["queryId"] in seen:
            raise ValueError("corpus query identity is duplicated")
        seen.add(query["queryId"])
        file = files.get(query["queryFileId"])
        if (file is None or file["kind"] != "query" or file["language"] != query["language"]
                or file["family"] != query["family"]):
            raise ValueError("query file is missing or disagrees with the query")
        guards.require_corpus_role(file["partition"])
        judgments = query["judgments"]
        if not isinstance(judgments, dict):
            raise ValueError("query judgments must be an object")
        scope = set(gallery[query["language"]])
        if not set(judgments) <= scope:
            raise ValueError("judgments must cover gallery files of the query language only")
        full = {}
        for file_id in gallery[query["language"]]:
            entry = judgments.get(file_id)
            if entry is None:
                filled += 1
                full[file_id] = {"label": metrics.UNKNOWN, "provenance": []}
                continue
            if (not isinstance(entry, dict) or set(entry) != {"label", "provenance"}
                    or entry["label"] not in metrics.JUDGMENTS or not isinstance(entry["provenance"], list)
                    or any(source not in POOL_SOURCES for source in entry["provenance"])
                    or len(set(entry["provenance"])) != len(entry["provenance"])):
                raise ValueError("judgment entry is invalid")
            if entry["label"] != metrics.UNKNOWN and not entry["provenance"]:
                raise ValueError("a judged file needs pooling provenance")
            if entry["label"] == metrics.RELEVANT and files[file_id]["family"] != query["family"]:
                raise ValueError("relevant gallery file family differs from the query family")
            full[file_id] = {"label": entry["label"], "provenance": sorted(entry["provenance"])}
        record = {key: query[key] for key in ("queryId", "queryFileId", "language", "family", "seedGroup",
                                              "stratum")}
        record["dialect"] = query.get("dialect")
        record["judgments"] = full
        prepared.append(record)
    return prepared, filled


def _metric_queries(prepared):
    return metrics.normalize_queries([
        {**{key: query[key] for key in ("queryId", "language", "family", "seedGroup", "stratum")},
         **({"dialect": query["dialect"]} if query["dialect"] is not None else {}),
         "judgments": {fid: entry["label"] for fid, entry in query["judgments"].items()}, "findings": []}
        for query in prepared])


def below_minimum(summary, minimum_counts):
    def short(core, minimum):
        counts = core["counts"]
        return {"positiveQueries": counts["positiveQueries"] < minimum["positiveQueries"],
                "families": counts["positiveFamilies"] < minimum["families"],
                "noMatchQueries": counts["noMatchFullyReviewed"] < minimum["noMatchQueries"]}
    languages = {language: short(core, minimum_counts["perLanguage"])
                 for language, core in summary["perLanguage"].items()}
    tsx = summary["perDialect"].get("tsx")
    strata = {label: (summary["perStratum"][label]["counts"]["positiveQueries"]
                      + summary["perStratum"][label]["counts"]["noMatchQueries"]) < minimum
              if label in summary["perStratum"] else True
              for label, minimum in minimum_counts["perStratum"].items()}
    return {"languages": languages,
            "tsxDialect": short(tsx, minimum_counts["tsxDialect"]) if tsx else
            {"positiveQueries": True, "families": True, "noMatchQueries": True},
            "strata": strata,
            "missingLanguages": sorted(set(LANGUAGES) - set(summary["perLanguage"]))}


def corpus_identity_sha256(prepared):
    """Label-free identity of what is evaluated: query ids, query files and gallery files."""
    return digest(canonical({
        "queries": sorted([query["queryId"], query["queryFileId"]] for query in prepared["queries"]),
        "gallery": sorted(row["fileId"] for row in prepared["files"] if row["kind"] == "gallery")}))


def label_sha256(prepared):
    """Identity of the judgments (labels and pooling provenance) of every query."""
    return digest(canonical(sorted([query["queryId"], query["judgments"]] for query in prepared["queries"])))


def build_prepared(corpus, *, minimum_counts, freeze_manifest_sha256, source_sha256, excluded_sources):
    if not isinstance(corpus, dict) or set(corpus) != {"schema", "files", "queries"} \
            or corpus["schema"] != CORPUS_SCHEMA:
        raise ValueError("corpus manifest keys or schema differ from the contract")
    guards.refuse_sealed(manifest=corpus)
    files = _validate_files(corpus["files"], excluded_sources)
    prepared, filled = _validate_queries(corpus["queries"], files)
    summary = metrics.summarize(_metric_queries(prepared))
    artifact = {
        "schema": PREPARED_SCHEMA, "freezeManifestSha256": freeze_manifest_sha256,
        "sourceCorpusSha256": source_sha256,
        "files": sorted(corpus["files"], key=lambda row: row["fileId"]),
        "queries": sorted(prepared, key=lambda row: row["queryId"]),
        "unjudgedFilledAsUnknown": filled,
        "counts": {"queries": len(prepared), "files": len(files),
                   "perLanguage": {language: core["counts"] for language, core in summary["perLanguage"].items()},
                   "perStratum": {label: core["counts"] for label, core in summary["perStratum"].items()},
                   "perDialect": {label: core["counts"] for label, core in summary["perDialect"].items()}},
        "belowMinimum": below_minimum(summary, minimum_counts)}
    evidence.validate(artifact)
    return artifact


def read_manifest(directory, expected_sha256):
    """The manifest on disk, verified against a hash supplied from outside the directory."""
    raw = (Path(directory) / manifest_module.MANIFEST_NAME).read_bytes()
    value = guards.verify_manifest_bytes(raw, expected_sha256)
    manifest_module.validate_manifest(value)
    return value, digest(raw)


def run(*, run_id, manifest_run_id, manifest_sha256, corpus_path, run_root=guards.RUN_ROOT, inputs_root=REPO_ROOT):
    guards.refuse_sealed(ids=[run_id, manifest_run_id], paths=[corpus_path])
    target = guards.run_dir(run_id, run_root)
    previous, previous_sha = read_manifest(guards.run_dir(manifest_run_id, run_root), manifest_sha256)
    source = ensure_inside(Path(corpus_path), Path(inputs_root))
    raw = inputs.read_bytes(source, MAX_CORPUS_BYTES)
    artifact = build_prepared(inputs.parse_json(raw, "corpus manifest"),
                              minimum_counts=previous["minimumCounts"],
                              freeze_manifest_sha256=previous_sha, source_sha256=digest(raw),
                              excluded_sources=previous["excludedSources"])
    prepared_bytes = canonical(artifact)
    bound, bound_sha = manifest_module.bind_corpus(previous, prepared_corpus_sha256=digest(prepared_bytes),
                                                   source_corpus_sha256=digest(raw))
    guards.write_output(target / PREPARED_NAME, prepared_bytes, run_root)
    guards.write_output(target / manifest_module.MANIFEST_NAME, canonical(bound), run_root)
    return {"outcome": "SUCCEEDED", "runId": run_id, "directory": target,
            "preparedCorpusSha256": digest(prepared_bytes), "manifestSha256": bound_sha,
            "previousManifestSha256": previous_sha}
