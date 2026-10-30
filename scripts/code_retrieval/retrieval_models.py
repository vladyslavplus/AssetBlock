"""The compared encoders: frozen upstream (E0) and the three trained epoch checkpoints (E1-E3).

Every model is encoded by one function (checkpoint.reference_vectors: FP32, eval mode, no
autocast, one chunk per forward), so E0 vectors here equal frozen_encoder.encode_chunks
output on the same chunks. Each model has a model key: a digest of its identity (upstream
revision and file hashes for E0; checkpoint manifest and full-state digests for E1-E3;
tokenizer hashes; chunking; pooling; dimension). Frozen and trained keys never collide.

Interpreter: every stage that loads these models runs in the training interpreter
(artifacts/feasibility_pilot/env/ml).
"""

from scripts.code_analysis.representations import CHUNKING

from . import checkpoint
from .index_verify import EMBEDDING_DIMENSIONS, SUPPORT_FILES, WEIGHTS, upstream_identity

FROZEN_LABEL = "E0"
EPOCH_LABELS = {1: "E1", 2: "E2", 3: "E3"}
POOLING = "padding-masked mean over all non-padding tokens including framing, FP32, then L2"
ENCODE_FUNCTION = "checkpoint.reference_vectors: FP32, eval, no autocast, one chunk per forward"


def _tokenizer_hashes(file_hashes):
    return {name: file_hashes[name] for name in SUPPORT_FILES}


def frozen_model(device):
    """E0: the hash-verified pinned upstream encoder."""
    tokenizer, model, info, upstream = checkpoint.load_upstream(device)
    identity = {"kind": "frozen", "label": FROZEN_LABEL, "upstreamRevision": upstream["modelRevision"],
                "checkpointSha256": upstream["fileHashes"][WEIGHTS],
                "fullStateSha256": info["loadedFullStateSha256"],
                "tokenizerHashes": _tokenizer_hashes(upstream["fileHashes"]),
                "chunking": CHUNKING, "pooling": POOLING, "encodeFunction": ENCODE_FUNCTION,
                "dimension": EMBEDDING_DIMENSIONS}
    return {"label": FROZEN_LABEL, "tokenizer": tokenizer, "model": model, "identity": identity}


def trained_model(train_run, epoch, expectations, device):
    """E1-E3: one training epoch, loaded only if its manifest is the one training evidence binds."""
    upstream = upstream_identity()
    expected = expectations["epochs"][str(epoch)]
    epoch_dir = train_run / ("epoch-" + str(epoch))
    tokenizer, model, manifest = checkpoint.load_trained(
        epoch_dir, upstream=upstream, device=device,
        expected_manifest_sha256=expected["checkpointManifestSha256"], expected_identity=expected["identity"])
    identity = {"kind": "trained", "label": EPOCH_LABELS[epoch], "epoch": epoch,
                "checkpointManifestSha256": expected["checkpointManifestSha256"],
                "upstreamRevision": manifest["upstream"]["modelRevision"],
                "fullStateSha256": manifest["fullStateSha256"],
                "tokenizerHashes": {name: manifest["files"][name]["sha256"] for name in SUPPORT_FILES},
                "chunking": CHUNKING, "pooling": POOLING, "encodeFunction": ENCODE_FUNCTION,
                "dimension": EMBEDDING_DIMENSIONS}
    return {"label": EPOCH_LABELS[epoch], "tokenizer": tokenizer, "model": model, "identity": identity}


def encode(model, tokenizer, windows, device):
    """Vectors for token-id windows, one encode function for every model."""
    if not windows:
        raise ValueError("encode needs at least one window")
    return checkpoint.reference_vectors(model, [window["inputIds"] for window in windows],
                                        tokenizer.pad_token_id, device)
