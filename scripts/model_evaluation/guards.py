"""Fail-closed input, output and run-order guards for model evaluation.

Withheld-partition material is refused unconditionally: a marker in any path, id,
role, manifest section or command-line argument raises, whatever flag accompanies it.
No scoring of such material exists in this package. Nothing here opens a referenced file.
"""

import re
import unicodedata
from pathlib import Path

from scripts.code_analysis.contracts import canonical, digest, sha
from scripts.code_analysis.corpus import write_once
from scripts.code_retrieval import inputs
from scripts.code_retrieval.paths import RUN_ID
from scripts.feasibility_pilot.paths import REPO_ROOT, ensure_inside

EVALUATION_ROOT = REPO_ROOT / "artifacts" / "model_evaluation"
RUN_ROOT = EVALUATION_ROOT / "runs"
REGISTRY_ROOT = EVALUATION_ROOT / "scoring-registry"

# Owner: the independent review's proposed acceptance; a rerun is never a reaction to a result.
RERUN_REASONS = ("IMPLEMENTATION_DEFECT", "INFRASTRUCTURE_FAULT")
MAX_FREE_SCORING_RUNS = 1

CORPUS_ROLE = "evaluation"
# Markers match a whole token or the start of a token of an identifier.
MARKERS = ("seal", "finaltest", "heldout", "holdout", "withheld")
# Adjacent token pairs that spell a marker: the first token exactly, the second as a token prefix.
MARKER_PAIRS = (("final", "test"), ("held", "out"), ("hold", "out"), ("with", "held"))
# Partition aliases refused only when they are a whole role.
ROLE_ALIASES = ("test", "testing")
# Fields written by the code itself (hashes, source paths, environment) are never scanned.
GENERATED_KEYS = frozenset(("codeIdentity", "sourceClosure", "environment", "dirtyPathsSha256"))
_HASH_TEXT = re.compile(r"[0-9a-f]{32,64}")

_ZERO_WIDTH = dict.fromkeys(map(ord, "​‌‍‎‏⁠⁡⁢⁣⁤﻿­"))
_HOMOGLYPHS = str.maketrans({
    "а": "a", "в": "b", "е": "e", "ё": "e", "з": "3", "і": "i", "ї": "i", "ј": "j", "к": "k", "м": "m",
    "н": "h", "о": "o", "р": "p", "с": "c", "т": "t", "у": "y", "х": "x", "ѕ": "s", "ь": "b", "һ": "h",
    "ԁ": "d", "ԛ": "q", "ԝ": "w", "ѡ": "w", "օ": "o", "ս": "s", "ɑ": "a", "ꜱ": "s", "ʟ": "l", "Ꮪ": "s",
    "α": "a", "β": "b", "ε": "e", "ι": "i", "κ": "k", "ν": "v", "ο": "o", "ρ": "p", "τ": "t", "υ": "u",
    "χ": "x", "ω": "w", "ς": "s", "ɡ": "g", "ı": "i", "ӏ": "l"})
_SYMBOLS = str.maketrans({"$": "s", "@": "a", "|": "l"})
_DIGITS = {"0": "o", "3": "e", "4": "a", "5": "s", "7": "t"}
_CAMEL = re.compile(r"(?<=[a-z0-9])(?=[A-Z][a-z])")
_WORDS = re.compile(r"[0-9A-Za-z]+")


class SealedInputRefused(ValueError):
    """A withheld-partition input was supplied."""


class ScoringRunRefused(ValueError):
    """A scoring run violates the single-run rule."""


class StageFailed(ValueError):
    """A stage raised after its inputs were accepted; failed-run evidence was written."""


def _clean(value):
    text = unicodedata.normalize("NFKC", str(value)).translate(_ZERO_WIDTH)
    text = "".join(ch for ch in unicodedata.normalize("NFD", text) if unicodedata.category(ch) not in ("Mn", "Me"))
    return text.translate(_HOMOGLYPHS).translate(_SYMBOLS)


_HEX_WORD = re.compile(r"[0-9a-fA-F]{32,64}")
_LEET = {"1": ("i", "l"), "0": "o", "3": "e", "4": "a", "5": "s", "7": "t"}
# Markers long enough to be matched as a substring of a separator-delimited word.
LONG_MARKERS = ("finaltest", "heldout", "holdout", "withheld")
SEAL = "seal"
# A proper prefix token followed by a token that completes the marker ("se_al", "hel_dout"). The remainder
# must be matched exactly when it is at most two characters, so that "sea level" or "se alias" pass.
_SHORT_REMAINDER = 2


def _readings():
    """The digit readings: leet 1 as i, leet 1 as l, digits dropped, digits as separators."""
    one = [str.maketrans({**{k: v for k, v in _LEET.items() if k != "1"}, "1": "i"}),
           str.maketrans({**{k: v for k, v in _LEET.items() if k != "1"}, "1": "l"})]
    return [lambda word, m=m: [word.translate(m)] for m in one] + [
        lambda word: [re.sub(r"[0-9]", "", word)], lambda word: re.split(r"[0-9]+", word)]


def _variants(value):
    """Per digit reading: the separator-delimited words and the camelCase tokens of an identifier.

    32 to 64 character pure-hex words (file ids, digests) are hashes written by the code, not names, and are
    dropped; shorter hex-looking words are still read.
    """
    words = [word for word in _WORDS.findall(_clean(value)) if not _HEX_WORD.fullmatch(word)]
    tokens = [piece for word in words for piece in _CAMEL.split(word)]
    result = []
    for reading in _readings():
        def fold(items):
            folded = (word.casefold().translate(_HOMOGLYPHS).translate(_SYMBOLS) for word in items)
            return [part for word in folded for part in reading(word) if part]
        result.append((fold(words), fold(tokens)))
    return result


def _token_variants(value):
    return [tokens for _, tokens in _variants(value)]


def _prefix_joined(tokens):
    """A token that is a proper prefix of a marker, completed by the next token."""
    for left, right in zip(tokens, tokens[1:]):
        for marker in (SEAL, *LONG_MARKERS):
            if 0 < len(left) < len(marker) and marker.startswith(left):
                rest = marker[len(left):]
                if right == rest or (len(rest) > _SHORT_REMAINDER and right.startswith(rest)):
                    return True
    return False


def _tokens_hit(tokens):
    if any(token.startswith(marker) for token in tokens for marker in MARKERS):
        return True
    if _prefix_joined(tokens):
        return True
    if any(left == first and right.startswith(second)
           for left, right in zip(tokens, tokens[1:]) for first, second in MARKER_PAIRS):
        return True
    run = ""
    for token in [*tokens, ""]:
        if len(token) == 1:
            run += token
            continue
        if any(run[start:].startswith(marker) for start in range(len(run)) for marker in MARKERS):
            return True
        run = ""
    return False


def _words_hit(words):
    return any(marker in word for word in words for marker in LONG_MARKERS) or any(
        word.startswith(SEAL) for word in words)


def has_sealed_marker(value):
    return any(_words_hit(words) or _tokens_hit(tokens) for words, tokens in _variants(value))


def is_role_alias(value):
    return any("".join(tokens) in ROLE_ALIASES for tokens in _token_variants(value))


def compact_forms(value):
    """Separator-free comparison forms of an identifier, one per reading of its digits."""
    return {"".join(tokens) for tokens in _token_variants(value)}


def is_generated(path, text=None):
    """True for fields the code writes itself: hash values and the code-identity subtrees."""
    if any(part in GENERATED_KEYS for part in path):
        return True
    return isinstance(text, str) and bool(_HASH_TEXT.fullmatch(text))


def _walk_strings(value, path=()):
    if isinstance(value, dict):
        for key, nested in value.items():
            if str(key) in GENERATED_KEYS:
                continue
            yield str(key)
            yield from _walk_strings(nested, path + (str(key),))
    elif isinstance(value, (list, tuple)):
        for nested in value:
            yield from _walk_strings(nested, path)
    elif isinstance(value, (str, Path)):
        text = str(value)
        if not is_generated(path, text):
            yield text


def sealed_hits(*, paths=(), ids=(), roles=(), manifest=None, cli_args=()):
    hits = []
    for kind, items in (("path", paths), ("id", ids), ("role", roles), ("argument", cli_args)):
        hits.extend((kind, str(item)) for item in items if has_sealed_marker(item))
    hits.extend(("role", str(role)) for role in roles if is_role_alias(role))
    if manifest is not None:
        hits.extend(("manifest", text) for text in _walk_strings(manifest) if has_sealed_marker(text))
    return hits


def refuse_sealed(*, paths=(), ids=(), roles=(), manifest=None, cli_args=()):
    """Raise when any input names withheld material; there is no exception path."""
    if sealed_hits(paths=paths, ids=ids, roles=roles, manifest=manifest, cli_args=cli_args):
        raise SealedInputRefused("withheld-partition input refused")


def require_corpus_role(role):
    """The closed allowlist of corpus roles: ``evaluation`` only."""
    refuse_sealed(roles=[role])
    if role != CORPUS_ROLE:
        raise ValueError("corpus role is not allowed: " + str(role))
    return role


def validate_run_id(run_id):
    if not isinstance(run_id, str) or not RUN_ID.fullmatch(run_id):
        raise ValueError("invalid run id")
    refuse_sealed(ids=[run_id])
    return run_id


def run_dir(run_id, root=RUN_ROOT):
    validate_run_id(run_id)
    return ensure_inside(Path(root) / run_id, Path(root))


def write_output(path, data, root):
    """Write-once output; an existing file is a collision and raises FileExistsError."""
    write_once(Path(path), data, Path(root))


def verify_manifest_bytes(raw, frozen_sha256):
    """Scoring gate: the manifest on disk must be canonical and hash to the supplied value."""
    sha(frozen_sha256)
    value = inputs.parse_json(raw, "freeze manifest")
    if canonical(value) != raw:
        raise ValueError("freeze manifest bytes are not canonical")
    if digest(raw) != frozen_sha256:
        raise ValueError("freeze manifest differs from its frozen hash")
    return value


def read_attempts(directory):
    directory = Path(directory)
    return [inputs.parse_json(entry.read_bytes(), "scoring attempt") for entry in sorted(directory.glob("attempt-*.json"))]


MODEL_ORDER = ("e0", "e3", "verifier")


def registered_run_ids(root):
    """Run ids of every attempt in the registry, whatever the models."""
    root = Path(root)
    if not root.is_dir():
        return set()
    return {attempt["runId"] for directory in root.glob("*") if directory.is_dir()
            for attempt in read_attempts(directory)}


def model_triples(models):
    """The two identities of what is evaluated: the three evidence-package hashes and the three state hashes."""
    return {"packageTripleSha256": digest(canonical([sha(models[name]["evidencePackageSha256"])
                                                     for name in MODEL_ORDER])),
            "stateTripleSha256": digest(canonical([sha(models[name]["stateSha256"]) for name in MODEL_ORDER]))}


def model_key(models):
    """Registry key: all three evidence-package hashes and all three state hashes."""
    return digest(canonical({"packages": [sha(models[name]["evidencePackageSha256"]) for name in MODEL_ORDER],
                             "states": [sha(models[name]["stateSha256"]) for name in MODEL_ORDER]}))


def matching_attempts(root, package_triple_sha256, state_triple_sha256):
    """Every recorded attempt over the same evidence packages OR the same checkpoint states.

    Swapping one checkpoint inside the same evidence packages, or re-packaging the same checkpoints, is
    therefore a further scoring run of the same models and needs a reason.
    """
    found = []
    for directory in sorted(Path(root).glob("*")):
        if directory.is_dir():
            for attempt in read_attempts(directory):
                if (attempt.get("packageTripleSha256") == package_triple_sha256
                        or attempt.get("stateTripleSha256") == state_triple_sha256):
                    found.append(attempt)
    return sorted(found, key=lambda attempt: (attempt["sequence"], attempt["runId"]))


def register_scoring_run(*, manifest_sha256, key_sha256, package_triple_sha256, state_triple_sha256,
                         corpus_identity_sha256, label_sha256, run_id,
                         root=REGISTRY_ROOT, rerun_of=None, reason=None, allowed_reasons=RERUN_REASONS,
                         max_free_runs=MAX_FREE_SCORING_RUNS):
    """Record a scoring attempt.

    A scoring over the same three evidence packages or the same three checkpoint states as an earlier
    attempt is a rerun, under any manifest, corpus or labels. The corpus identity and the label hash are
    recorded per attempt so that a change between attempts is visible.
    """
    validate_run_id(run_id)
    for value in (manifest_sha256, key_sha256, package_triple_sha256, state_triple_sha256, corpus_identity_sha256,
                  label_sha256):
        sha(value)
    directory = ensure_inside(Path(root) / key_sha256, Path(root))
    Path(root).mkdir(parents=True, exist_ok=True)
    existing = matching_attempts(root, package_triple_sha256, state_triple_sha256)
    recorded_ids = [attempt["runId"] for attempt in existing]
    if run_id in recorded_ids:
        raise ScoringRunRefused("run id already used for these models")
    if len(existing) >= max_free_runs:
        if reason not in allowed_reasons:
            raise ScoringRunRefused("a further scoring run of these models needs a reason in "
                                    + ", ".join(allowed_reasons))
        if rerun_of not in recorded_ids:
            raise ScoringRunRefused("a rerun must reference a recorded earlier run")
    elif rerun_of is not None or reason is not None:
        raise ScoringRunRefused("the first scoring run cannot declare a rerun")
    record = {"modelKeySha256": key_sha256, "packageTripleSha256": package_triple_sha256,
              "stateTripleSha256": state_triple_sha256, "corpusIdentitySha256": corpus_identity_sha256,
              "labelSha256": label_sha256, "manifestSha256": manifest_sha256, "runId": run_id,
              "sequence": len(existing) + 1, "rerunOf": rerun_of, "reason": reason,
              "priorRunsRetained": recorded_ids}
    directory.mkdir(parents=True, exist_ok=True)
    count = len(list(directory.glob("attempt-*.json")))
    write_output(directory / ("attempt-%04d.json" % (count + 1)), canonical(record), Path(root))
    return record


def record_failure(directory, root, stage, error):
    """Write-once failed-run evidence; a failed run never leaves a success marker."""
    record = {"outcome": "FAILED", "stage": stage, "errorType": type(error).__name__, "error": str(error)}
    write_output(Path(directory) / "failed-run.json", canonical(record), root)
    return StageFailed("%s failed: %s" % (stage, error))
