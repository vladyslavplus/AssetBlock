"""Authored fixture builders for code_retrieval tests. No real corpus data."""

import hashlib
import json
from pathlib import Path

from scripts.code_analysis.contracts import LANGUAGES, canonical, digest

COMMIT = "a" * 40
LICENSE = b"Fixture license text\n"
RUBRIC = "implementation-similarity-v1"


def sha(value):
    return hashlib.sha256(value).hexdigest()


def canon(value):
    return canonical(value)


def identity(*parts):
    return sha(canon(list(parts)))


GRANT_DEFAULTS = {"searchIndexAllowed": True, "trainingAllowed": True, "evaluationAllowed": True,
                  "rawRedistributionAllowed": False, "derivedArtifactRedistributionAllowed": False}


def rights(paths, grants=None):
    merged = dict(GRANT_DEFAULTS)
    merged.update(grants or {})
    return {"status": "REVIEWED", "reviewer": "Fixture rights reviewer", "reviewedOn": "2026-10-01",
            "rationale": "Authored fixture grant.", "terms": "Fixture-1.0", "commit": COMMIT,
            "noticeHashes": {"LICENSE": sha(LICENSE)}, "fileHashes": dict(paths), "grants": merged}


def source(source_id, paths, grants=None):
    return {"sourceId": source_id, "commit": COMMIT, "repository": "fixture/" + source_id,
            "repositoryUrl": "https://github.com/fixture/" + source_id,
            "lineage": [source_id + " at fixture"], "lineageEvidence": "authored",
            "files": [{"path": path, "sha256": value, "language": language}
                      for path, value, language in paths],
            "notices": [{"path": "LICENSE", "sha256": sha(LICENSE)}],
            "partition": "train", "partitionReviewed": True, "rights": rights([(path, value)
                                                                               for path, value, _ in paths], grants)}


def python_body(name):
    return "def {name}(value):\n    result = value + 1\n    return result\n".format(name=name).encode("utf-8")


_POLYGLOT_BODY = {
    "python": "def {name}(value):\n    result = value + 1\n    return result\n",
    "csharp": "class F{name}\n{{\n    public int F{name}(int value)\n    {{\n"
              "        int result = value + 1;\n        return result;\n    }}\n}}\n",
    "java": "public class F{name}\n{{\n    public int f{name}(int value)\n    {{\n"
            "        int result = value + 1;\n        return result;\n    }}\n}}\n",
    "javascript": "function f{name}(value)\n{{\n  const result = value + 1;\n  return result;\n}}\n",
    "typescript": "function f{name}(value: number): number\n{{\n  const result = value + 1;\n"
                  "  return result;\n}}\n",
}


def polyglot_body(language, name):
    """A minimal body that parses under the named language (fixture normalization)."""
    text = _POLYGLOT_BODY.get(language)
    if text is None:
        raise ValueError("no fixture body for language: " + language)
    return text.format(name=name).encode("utf-8")


def node(source_id, path, language, partition, family, body, **overrides):
    row = {"fileId": overrides.get("file_id") or identity(source_id, path, sha(body)),
           "sourceId": source_id, "path": path, "sha256": sha(body), "bytes": len(body),
           "language": language, "dialect": overrides.get("dialect", language), "partition": partition,
           "familyId": family, "original": True, "generated": False, "vendor": False,
           "normalizationExcludedReason": None,
           "normalizedSha256": overrides.get("normalized") or sha(b"norm:" + body),
           "extraction": "VALID_EXTRACTED", "searchableExecutable": True, "assignmentReviewed": True,
           "test": False, "permissions": {"trainingAllowed": True, "evaluationAllowed": True}}
    return {"row": row, "body": body}


def fragment(node, start=0, end=None, **overrides):
    body = node["body"]
    end = len(body) if end is None else end
    piece = body[start:end]
    row = {"fragmentId": overrides.get("fragment_id") or identity(node["row"]["fileId"], sha(piece), start, end),
           "fileId": node["row"]["fileId"], "sha256": sha(piece), "sourceSha256": node["row"]["sha256"],
           "language": node["row"]["language"], "dialect": node["row"]["dialect"],
           "startByte": start, "endByte": end, "searchable": True, "executable": True,
           "partialMLCoverage": False, "name": "f", "kind": "function_definition", "canonicalIndexed": True}
    row.update(overrides.get("patch") or {})
    return row


def pair(label, left, right):
    return {"label": label, "leftFragmentId": left["fragmentId"], "leftSha256": left["sha256"],
            "rightFragmentId": right["fragmentId"], "rightSha256": right["sha256"]}


def triplet(language, query, positive, negatives, *, reviewer="Fixture reviewer",
            rationale="Fixture relation reviewed.", status="REVIEWED", partition="train",
            reviewed_on="2026-10-01"):
    return {"language": language, "partition": partition, "rubric": RUBRIC,
            "queryFragmentId": query["fragmentId"], "querySha256": query["sha256"],
            "positive": pair("SIMILAR", query, positive),
            "negatives": [pair("DISSIMILAR", query, row) for row in negatives],
            "status": status, "reviewer": reviewer, "rationale": rationale, "reviewedOn": reviewed_on}


def write_sources(root, nodes):
    root = Path(root)
    for item in nodes:
        target = root / item["row"]["sourceId"] / item["row"]["path"]
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes(item["body"])


def corpus(nodes, fragments, *, sources=None, families=None, extraction="extraction-fixture"):
    """Assemble a manifest/split/extract triple consistent with the authored nodes."""
    rows = [item["row"] for item in nodes]
    sources = sources or []
    family_map = families or {row["familyId"]: {"partition": row["partition"],
                                                "sources": [row["sourceId"]], "assignmentReviewed": True}
                              for row in rows}
    manifest = {"schema": "code-corpus-v1", "sources": sources}
    split = {"schema": "code-corpus-v1", "files": rows, "families": family_map,
             "rightsSha256": digest(canonical({row["sourceId"]: row["rights"]
                                               for row in sources})),
             "normalization": {"algorithm": "fixture"}}
    extract = {"schema": "code-corpus-v1", "files": rows, "fragments": fragments,
               "extractionSha256": extraction}
    return manifest, split, extract


def index(manifest, split, extract, triplets, pair_reviews=(), extra_files=(), extra_fragments=()):
    merged = {"pairReviews": list(pair_reviews), "extraFiles": list(extra_files),
              "extraFragments": list(extra_fragments), "trainTriplets": list(triplets)}
    from scripts.code_retrieval.corpus_index import build
    return build(manifest, split, extract, [merged])


def retrieval_config(**overrides):
    config = {"schema": "code-retrieval-config-v1", "languages": list(LANGUAGES), "seed": 42,
              "protocolRevision": "code-retrieval-protocol-v1",
              "admission": {"minAnchorsPerLanguage": 2, "maxAnchorsPerLanguage": 60,
                            "minNegativesPerAnchor": 2, "minNonIdenticalPositiveAnchorsPerLanguage": 1,
                            "positiveTransformations": ["AST_BOUND_RENAME", "COMMENT_FORMAT"]},
              "proposal": {"anchorsPerLanguage": 2, "negativesPerAnchor": 2},
              "training": {"upstreamModelRevision": "5f0dc256eb0904af496d427451da5d7f90261676",
                           "optimizer": "AdamW", "maxTokens": 256, "framingTokens": 4,
                           "embeddingDimensions": 768, "trainableEncoderBlocks": 2,
                           "microbatchTriplets": 1, "gradientAccumulation": 4, "epochs": 3,
                           "maxAdmittedTriplets": 300, "maxAnchorsPerLanguage": 60,
                           "learningRate": 1e-5, "temperature": 0.07, "gradientClip": 1.0,
                           "deadlineMinutes": 90},
              "selection": {"metric": "seedMacroMRR@20", "tieBreak": ["Recall@20", "earlierEpoch"]},
              "finalTest": {"sealed": True, "scored": False, "tuning": False}}
    for section, patch in overrides.items():
        if isinstance(patch, dict) and isinstance(config.get(section), dict):
            config[section] = {**config[section], **patch}
        else:
            config[section] = patch
    return config


def derived_positive(parent_node, parent_fragment, *, transformation="COMMENT_FORMAT", version="comment-format-v1",
                     output=None, review=True, reviewer="Fixture reviewer", reviewed_on="2026-10-01",
                     language=None, body=None):
    """A minimally valid derived unit for admission fixtures (no shared transform invoked)."""
    from scripts.code_retrieval.candidates import derived_fragment_id
    import base64
    if output is None:
        comment = b"# fixture\n" if (language or parent_node["row"]["language"]) == "python" else b"// fixture\n"
        output = parent_node["body"] + comment
    output_sha = sha(output)
    fragment_id = derived_fragment_id(parent_fragment["fragmentId"], transformation, version, output_sha)
    row = {"fragmentId": fragment_id, "derived": True, "fileId": parent_node["row"]["fileId"],
           "parentFragmentId": parent_fragment["fragmentId"], "parentFileId": parent_node["row"]["fileId"],
           "sourceSha256": parent_node["row"]["sha256"], "sha256": output_sha, "bytes": len(output),
           "bodyBase64": base64.b64encode(output).decode("ascii"),
           "materialization": {"encoding": "base64", "sha256": output_sha},
           "language": language or parent_node["row"]["language"],
           "dialect": parent_node["row"]["dialect"], "name": "f", "kind": "function_definition",
           "searchable": True, "executable": True, "partialMLCoverage": False,
           "derivation": {"transformation": transformation, "version": version, "changedRanges": [],
                          "rationale": "fixture", "outputSha256": output_sha},
           "reviewStatus": "REVIEWED" if review else "CANDIDATE_UNREVIEWED",
           "reviewer": reviewer if review else None, "reviewedOn": reviewed_on if review else None}
    return row
