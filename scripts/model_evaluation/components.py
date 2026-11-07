"""Injected components: encoders, pair verifier, token scorer and content store.

Stages receive these objects, so unit tests run with tiny deterministic stand-ins and
the real checkpoints are loaded only by the ``load_*`` functions below, in the
interpreter that owns them (the CUDA interpreter for trained encoders).

Interfaces (duck typed):

* retrieval encoder: ``identity`` (dict), ``windows(raw, file)`` (``file`` is the prepared file row) returning the
  ``mapped_chunks`` shape (``originalTokenCount``, ``omittedTokenCount``, ``chunks``
  with ``inputIds``), ``embed(chunks)`` returning L2-normalised vectors, optional ``release()``;
* pair verifier: ``identity``, ``probabilities(pair_ids)`` returning one positive
  probability per joint input, optional ``release()``;
* token scorer: ``pair_scores(language, files)`` with ``files`` of ``{fileId, sha256, raw}``
  returning bound pairs ``{leftId, rightId, tokenScore}``.
"""

from dataclasses import dataclass
from pathlib import Path

from scripts.code_analysis.contracts import digest
from scripts.feasibility_pilot.paths import ensure_inside

from . import guards


@dataclass
class Components:
    token_scorer: object
    e0: object
    e3: object
    verifier: object


def release(component):
    if hasattr(component, "release"):
        component.release()


class DirectoryContentStore:
    """Hash-checked file contents in a materialised directory; never lists or searches it."""

    def __init__(self, root):
        self.root = Path(root)

    def read(self, file_id, sha256):
        path = ensure_inside(self.root / file_id, self.root)
        raw = path.read_bytes()
        if digest(raw) != sha256:
            raise ValueError("stored content differs from its recorded hash: " + file_id)
        return raw


class TorchRetrievalEncoder:
    """Frozen-style mean-pooled encoder over a tokenizer and a loaded model (CPU or CUDA)."""

    def __init__(self, tokenizer, model, identity):
        self.tokenizer, self.model, self.identity = tokenizer, model, dict(identity)

    def windows(self, raw, file):
        """Per-fragment windows exactly as the service maps them: extracted units, one mapping each."""
        from scripts.code_analysis.representations import mapped_chunks
        from scripts.model_evaluation import product_search
        chunks, omitted, original, omitted_fragments = [], 0, 0, 0
        for fragment in product_search.fragments(raw, file):
            try:
                mapped = mapped_chunks(raw, fragment, self.tokenizer)
            except ValueError:
                omitted_fragments += 1
                continue
            chunks.extend(mapped.get("chunks", []))
            omitted += mapped.get("omittedTokenCount", 0)
            original += mapped.get("originalTokenCount", 0)
        return {"originalTokenCount": original, "omittedTokenCount": omitted,
                "omittedFragmentCount": omitted_fragments, "chunks": chunks}

    def embed(self, chunks):
        from scripts.code_analysis.frozen_encoder import encode_chunks
        vectors = []
        for start in range(0, len(chunks), 8):
            vectors.extend(encode_chunks(self.model, self.tokenizer, chunks[start:start + 8])[0])
        return vectors

    def release(self):
        self.model = None


class TorchPairVerifier:
    """Joint-input verifier; positive-class softmax probability per layout."""

    def __init__(self, model, pad_id, device, identity):
        self.model, self.pad_id, self.device, self.identity = model, pad_id, device, dict(identity)

    def probabilities(self, pair_ids):
        import math
        from scripts.pair_verification import training
        logits = training._forward_logits(self.model, [list(ids) for ids in pair_ids], self.device,
                                          pad_id=self.pad_id, batch_size=8)
        result = []
        for row in logits:
            top = max(row)
            exps = [math.exp(value - top) for value in row]
            result.append(exps[1] / sum(exps))
        return result

    def release(self):
        self.model = None


class DolosTokenScorer:
    """Token pair scores from the external Dolos tool through the shared token stage."""

    def __init__(self, work_root, *, k, window, timeout_s=3600):
        self.work_root, self.k, self.window, self.timeout_s = Path(work_root), k, window, timeout_s

    def pair_scores(self, language, files):
        from scripts.code_analysis import baselines
        from scripts.code_analysis.corpus import ARTIFACT_ROOT
        staged = []
        source_dir = ensure_inside(self.work_root / "staged" / language, ARTIFACT_ROOT)
        source_dir.mkdir(parents=True, exist_ok=True)
        for row in files:
            path = source_dir / row["fileId"]
            path.write_bytes(row["raw"])
            staged.append({"fileId": row["fileId"], "sha256": row["sha256"], "absolutePath": str(path)})
        result = baselines.token_stage(source_dir, language, staged, self.work_root / "dolos" / language,
                                       k=self.k, window=self.window, timeout_s=self.timeout_s)
        if result.get("status") != "SUCCEEDED":
            raise ValueError("token stage failed for " + language)
        return result["boundPairs"]


def load_retrieval_encoder(kind, spec, device):
    """Load the frozen base (``kind='base'``) or a trained checkpoint (``kind='trained'``)."""
    from scripts.code_analysis import representations
    if kind == "base":
        from scripts.code_analysis.frozen_encoder import load_frozen
        tokenizer, model, facts = load_frozen(spec["modelDir"], spec["cacheRoot"], spec["expectedFiles"],
                                              device=device)
        return TorchRetrievalEncoder(tokenizer, model, {
            "kind": kind, "stateSha256": facts["loadedFullStateSha256"], "device": str(device),
            "tokenizerHashes": dict(facts["tokenizerHashes"]), "chunking": representations.CHUNKING})
    if kind == "trained":
        from scripts.code_retrieval import checkpoint
        tokenizer, model, manifest = checkpoint.load_trained(
            Path(spec["epochDir"]), upstream=spec["upstream"], device=device,
            expected_manifest_sha256=spec["expectedManifestSha256"], expected_identity=spec["expectedIdentity"])
        return TorchRetrievalEncoder(tokenizer, model, {
            "kind": kind, "stateSha256": manifest["fullStateSha256"], "device": str(device),
            "tokenizerHashes": {name: value for name, value in manifest["files"].items() if name != checkpoint.WEIGHTS},
            "chunking": representations.CHUNKING})
    raise ValueError("unknown encoder kind")


def load_pair_verifier(spec, device):
    from transformers import RobertaTokenizerFast
    from scripts.pair_verification import checkpoints
    path = Path(spec["checkpointPath"])
    model, manifest = checkpoints.load_epoch_checkpoint(
        path, root=Path(spec["root"]), expected_manifest_sha256=spec["expectedManifestSha256"],
        expected_training_evidence=spec["expectedTrainingEvidence"], upstream=spec["upstream"], device=device)
    tokenizer = RobertaTokenizerFast.from_pretrained(str(path.parent.parent / "support"), local_files_only=True)
    return TorchPairVerifier(model, tokenizer.pad_token_id, device,
                             {"kind": "verifier", "stateSha256": manifest["modelStateSha256"],
                              "device": str(device)})


# Facts the loader reports about what it actually loaded; they must equal the declaration.
CHECKED_ON_LOAD = ("stateSha256", "device", "tokenizerHashes", "chunking")


class LazyComponent:
    """A model that loads on first use. ``identity`` is the declared, cheap identity and never loads it.

    The first load verifies that the loaded model reports the declared state and device.
    """

    def __init__(self, identity, loader):
        self._identity, self._loader, self._component, self.loads = dict(identity), loader, None, 0

    @property
    def identity(self):
        return dict(self._identity)

    def _get(self):
        if self._component is None:
            self._component = self._loader()
            self.loads += 1
            loaded = self._component.identity
            if any(loaded.get(key) != self._identity.get(key) for key in CHECKED_ON_LOAD):
                self._component = None
                raise ValueError("loaded model differs from the declared identity")
        return self._component

    def release(self):
        component, self._component = self._component, None
        if component is not None:
            release(component)

    def __getattr__(self, name):
        if name.startswith("_"):
            raise AttributeError(name)
        return getattr(self._get(), name)


def declared_identity(config, name):
    identity = config[name]["identity"]
    required = {"stateSha256", "device"}
    required |= {"threshold"} if name == "verifier" else {"tokenizerHashes", "chunking"}
    if not isinstance(identity, dict) or not required <= set(identity):
        raise ValueError("components description needs a full identity for " + name)
    return identity


def load_components(config_path):
    """Build the real components from a JSON description; models load lazily and one at a time."""
    from scripts.code_retrieval import inputs
    guards.refuse_sealed(paths=[config_path])
    config = inputs.parse_json(inputs.read_bytes(Path(config_path), 1024 * 1024), "components description")
    guards.refuse_sealed(manifest=config)
    device = config["device"]
    token_identity = {"dolosK": config["dolos"]["k"], "dolosWindow": config["dolos"]["window"]}
    scorer = DolosTokenScorer(config["dolos"]["workRoot"], k=config["dolos"]["k"], window=config["dolos"]["window"])
    scorer.identity = token_identity
    return Components(
        token_scorer=scorer,
        e0=LazyComponent(declared_identity(config, "e0"), lambda: load_retrieval_encoder("base", config["e0"], device)),
        e3=LazyComponent(declared_identity(config, "e3"),
                         lambda: load_retrieval_encoder("trained", config["e3"], device)),
        verifier=LazyComponent(declared_identity(config, "verifier"),
                               lambda: load_pair_verifier(config["verifier"], device)))
