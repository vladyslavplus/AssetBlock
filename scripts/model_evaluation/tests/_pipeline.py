"""Synthetic corpus, deterministic stand-in components and a stage driver for end-to-end tests."""

import copy
import json
import re
import tempfile
from pathlib import Path

from scripts.code_analysis.contracts import canonical, digest
from scripts.model_evaluation import candidates, guards, manifest, prepare, score
from scripts.model_evaluation.components import Components, DirectoryContentStore
from scripts.model_evaluation.tests._fixtures import STATE, TOKENIZER_HASHES, manifest_inputs, write_packages

G1 = "def add(a, b):\n    return a + b\n"
G2 = "def mul(a, b):\n    return a * b\n"
G3 = "class Stack:\n    def push(self, x):\n        self.items.append(x)\n"
G4 = "print('hello world')\n"
G5 = "import os\nimport sys\n"
J1 = "class Counter { int next() { return value++; } }\n"
J2 = "interface Shape { double area(); }\n"
NOVEL_1 = "alpha beta gamma delta epsilon zeta eta theta\n"
NOVEL_2 = "kappa lambda sigma omega tau upsilon phi chi\n"


def fid(name):
    return digest(name.encode("utf-8"))


def _shingles(text, size=3):
    words = re.findall(r"\w+|\S", text)
    return {tuple(words[i:i + size]) for i in range(max(len(words) - size + 1, 1))}


class ShingleTokenScorer:
    """Token pair scores as Jaccard similarity of word shingles."""

    identity = {"dolosK": 23, "dolosWindow": 17}

    def pair_scores(self, language, files):
        pairs = []
        shingles = {row["fileId"]: _shingles(row["raw"].decode()) for row in files}
        ids = sorted(shingles)
        for index, left in enumerate(ids):
            for right in ids[index + 1:]:
                union = shingles[left] | shingles[right]
                score = len(shingles[left] & shingles[right]) / len(union) if union else 0.0
                pairs.append({"leftId": left, "rightId": right, "tokenScore": round(score, 12)})
        return pairs


def full_identity(name, threshold=0.9, device="cpu"):
    """The declared identity a component of the given frozen model name reports."""
    base = {"stateSha256": STATE[name], "device": device}
    if name == "verifier":
        return {**base, "kind": "verifier", "threshold": threshold}
    return {**base, "kind": "encoder", "tokenizerHashes": dict(TOKENIZER_HASHES[name]),
            "chunking": manifest.actual_representation()["chunking"]}


class HistogramEncoder:
    """One window per file; the vector is the normalised byte-class histogram."""

    def __init__(self, name, shift=0, device="cpu", **identity):
        self.identity = {**full_identity(name, device=device), "kind": "histogram", **identity}
        self.shift = shift
        self.released = 0

    def windows(self, raw, file):
        ids = [3 + byte for byte in raw]
        chunk = {"inputIds": [0, 5, 2, *ids, 2], "framingTokens": 4, "tokenStart": 0, "tokenEnd": len(ids),
                 "sourceTokenSpans": None}
        return {"originalTokenCount": len(ids), "omittedTokenCount": 0, "chunks": [chunk]}

    def embed(self, chunks):
        vectors = []
        for chunk in chunks:
            counts = [0.0] * 8
            for token in chunk["inputIds"][3:-1]:
                counts[(token + self.shift) % 8] += 1
            norm = sum(value * value for value in counts) ** 0.5
            vectors.append([value / norm for value in counts])
        return vectors

    def release(self):
        self.released += 1


class TrigramVerifier:
    """Positive probability = Jaccard similarity of the id trigrams of the two joint halves."""

    def __init__(self, threshold=0.9, device="cpu"):
        self.identity = {**full_identity("verifier", threshold, device), "kind": "trigram-verifier"}
        self.released = 0

    def probabilities(self, pair_ids):
        result = []
        for ids in pair_ids:
            body = list(ids[3:])
            cut = body.index(2)
            left, right = body[:cut], body[cut + 1:-1]
            grams = lambda values: {tuple(values[i:i + 3]) for i in range(max(len(values) - 2, 1))}
            a, b = grams(left), grams(right)
            result.append(len(a & b) / len(a | b))
        return result

    def release(self):
        self.released += 1


def fake_identity(dirty=False):
    closure = [{"path": "scripts/model_evaluation/score.py", "sha256": "ab" * 32}]
    return {"commit": "c" * 40, "dirty": dirty, "dirtyPathCount": 1 if dirty else 0,
            "dirtyPathsSha256": "cd" * 32, "sourceClosure": closure,
            "sourceClosureSha256": digest(canonical(closure)),
            "environment": {"python": "3.12.0", "implementation": "CPython", "platform": "test",
                            "packageCount": 1, "packageListSha256": "ef" * 32}}


def fake_components(threshold=0.9, device="cpu"):
    return Components(token_scorer=ShingleTokenScorer(), e0=HistogramEncoder("e0", device=device),
                      e3=HistogramEncoder("e3", shift=1, device=device), verifier=TrigramVerifier(threshold, device))


def corpus_document(*, swap=False, extras=False):
    """Handcrafted corpus. ``swap`` mislabels the copies so the product precision drops to zero.

    ``extras`` adds one gallery file per query family (the relevant files of the swapped labels); ``swap``
    implies them. Without them the gallery is the plain one used by the candidate-stage tests.
    """
    # A RELEVANT gallery file shares the family of its query; the other gallery files are negatives.
    gallery = [("g1", G1, "python", "fa"), ("g1b", G1, "python", "fa"), ("g2", G2, "python", "gb"),
               ("g3", G3, "python", "fb"), ("g4", G4, "python", "fc"), ("g5", G5, "python", "ge"),
               ("j1", J1, "java", "fj"), ("j2", J2, "java", "gj2")]
    if swap or extras:
        gallery += [("g2a", G2, "python", "fa"), ("g2b", G2, "python", "fb"), ("g2c", G2, "python", "fc")]
    queries = [("qa", G1, "python", "fa", "exact copy"), ("qb", G3, "python", "fb", "exact copy"),
               ("qc", G4, "python", "fc", "exact copy"), ("qn1", NOVEL_1, "python", "fn1", "hard negative"),
               ("qn2", NOVEL_2, "python", "fn2", "hard negative"), ("qj", J1, "java", "fj", "exact copy")]
    files = [{"fileId": fid(name), "sha256": digest(text.encode()), "language": language, "family": family,
              "partition": "evaluation", "kind": "gallery", "source": "synthetic-source-1"}
             for name, text, language, family in gallery]
    files += [{"fileId": fid("file-" + name), "sha256": digest(text.encode()), "language": language,
               "family": family, "partition": "evaluation", "kind": "query",
               "source": "synthetic-source-2"}
              for name, text, language, family, _ in queries]
    relevant = {"qa": {"g1", "g1b"}, "qb": {"g3"}, "qc": {"g4"}, "qn1": set(), "qn2": set(), "qj": {"j1"}}
    unknown = {"qa": {"g5"}, "qc": {"g5"}, "qn2": {"g5"}}
    if swap:
        relevant = {"qa": {"g2a"}, "qb": {"g2b"}, "qc": {"g2c"}, "qn1": set(), "qn2": set(), "qj": {"j1"}}
    documents = []
    for name, _, language, family, stratum in queries:
        judgments = {}
        for gname, _, glanguage, _ in gallery:
            if glanguage != language:
                continue
            label = ("UNKNOWN" if gname in unknown.get(name, ())
                     else "RELEVANT" if gname in relevant[name] else "NOT_RELEVANT")
            judgments[fid(gname)] = {"label": label, "provenance": ["E0", "E3", "RANDOM_SAMPLE", "TOKEN"]}
        documents.append({"queryId": name, "queryFileId": fid("file-" + name), "language": language,
                          "family": family, "seedGroup": "seed-" + family, "stratum": stratum,
                          "judgments": judgments})
    return {"schema": prepare.CORPUS_SCHEMA, "files": files, "queries": documents}


def small_manifest_inputs(*, threshold=0.9, resamples=30):
    inputs = copy.deepcopy(manifest_inputs())
    inputs["operatingPoints"]["validationThresholdRuleResult"]["threshold"] = threshold
    inputs["bootstrap"]["resamples"] = resamples
    inputs["minimumCounts"] = {"perLanguage": {"positiveQueries": 3, "families": 3, "noMatchQueries": 1},
                               "tsxDialect": {"positiveQueries": 3, "families": 3, "noMatchQueries": 1},
                               "perStratum": {"exact copy": 1, "hard negative": 1}}
    inputs["resourceProtocol"]["warmupQueries"] = 1
    inputs["resourceProtocol"]["timedQueries"] = 1
    inputs["cpuGpuParity"]["sampleSize"] = 3
    return inputs


class Workspace:
    """Temporary run root, input root and registry with every stage as a method."""

    def __init__(self, test_case):
        self.temp = tempfile.TemporaryDirectory()
        test_case.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.run_root = self.root / "runs"
        self.registry = self.root / "registry"
        self.content = self.root / "content"
        self.content.mkdir()
        self.store = DirectoryContentStore(self.content)

    def write_json(self, name, value):
        path = self.root / name
        path.write_text(json.dumps(value), encoding="utf-8")
        return path

    def materialise(self, corpus_texts):
        for file_id, text in corpus_texts.items():
            (self.content / file_id).write_bytes(text.encode())

    def freeze(self, run_id="freeze-01", inputs=None):
        inputs = copy.deepcopy(inputs or small_manifest_inputs())
        paths = write_packages(self.root, inputs)
        path = self.write_json("freeze-inputs.json", inputs)
        result = manifest.freeze(
            run_id=run_id, inputs_path=path, run_root=self.run_root, inputs_root=self.root,
            evidence_paths=paths, state_hashes=dict(STATE),
            threshold=inputs["operatingPoints"]["validationThresholdRuleResult"]["threshold"])
        self.frozen_sha256 = result["manifestSha256"]
        return result

    def prepare(self, run_id="prepare-01", manifest_run_id="freeze-01", corpus=None, name="corpus.json",
                manifest_sha256=None):
        path = self.write_json(name, corpus or corpus_document())
        result = prepare.run(run_id=run_id, manifest_run_id=manifest_run_id,
                             manifest_sha256=manifest_sha256 or self.frozen_sha256, corpus_path=path,
                             run_root=self.run_root, inputs_root=self.root)
        self.bound_sha256 = result["manifestSha256"]
        return result

    def candidates(self, run_id="cand-01", prepare_run_id="prepare-01", components=None, **kwargs):
        kwargs.setdefault("capture", fake_identity)
        return candidates.run(run_id=run_id, prepare_run_id=prepare_run_id,
                              manifest_sha256=self.bound_sha256,
                              components=components or fake_components(), store=self.store,
                              run_root=self.run_root, **kwargs)

    def score(self, prepared, cands, run_id="score-01", **kwargs):
        kwargs.setdefault("capture", fake_identity)
        return score.run(run_id=run_id, prepare_run_id="prepare-01", candidates_run_id="cand-01",
                         manifest_sha256=prepared["manifestSha256"], candidates_sha256=cands["candidatesSha256"],
                         run_root=self.run_root, registry_root=self.registry, **kwargs)

    def prepared_dir(self, run_id="prepare-01"):
        return guards.run_dir(run_id, self.run_root)

    def full_run(self, corpus=None, components=None):
        texts = {}
        document = corpus or corpus_document()
        sources = {fid(n): t for n, t in [("g1", G1), ("g1b", G1), ("g2", G2), ("g3", G3), ("g4", G4),
                                          ("g5", G5), ("j1", J1), ("j2", J2), ("file-qa", G1),
                                          ("file-qb", G3), ("file-qc", G4), ("file-qn1", NOVEL_1),
                                          ("file-qn2", NOVEL_2), ("file-qj", J1), ("g2a", G2), ("g2b", G2),
                                          ("g2c", G2)]}
        texts.update(sources)
        self.materialise(texts)
        self.freeze()
        prepared = self.prepare(corpus=document)
        cands = self.candidates(components=components)
        return prepared, cands

    def result(self, run_id="score-01"):
        return json.loads((guards.run_dir(run_id, self.run_root) / score.RESULT_NAME).read_bytes())


def canonical_bytes(value):
    return canonical(value)
