"""Trained encoder checkpoints: write-once epoch snapshots, a verified loader and the reload proof.

Checkpoints are safetensors-only snapshots of the full encoder. The loader here is
separate from the frozen loader in code_analysis: it accepts only a checkpoint whose
manifest digest and identity equal the values bound by a verified training evidence
file, refuses any file that is missing, extra or drifted, and never reads the pinned
upstream identity it was trained from except to check it.

Interpreter note: every stage that loads trained checkpoints (reload, export-index,
evaluate) runs in the training interpreter, artifacts/feasibility_pilot/env/ml. The
frozen upstream load used for the reference probe stays in the pinned CPU interpreter.
"""

import base64
import math

import torch
import torch.nn.functional as F
from safetensors.torch import save
from transformers import RobertaModel, RobertaTokenizerFast

from scripts.code_analysis.contracts import canonical, digest, sha
from scripts.code_analysis.corpus import write_once
from scripts.code_analysis.frozen_encoder import file_hash, load_frozen, state_hash
from scripts.code_analysis.representations import read_vectors, vector_bytes
from scripts.feasibility_pilot.paths import REPO_ROOT, ensure_inside

from . import inputs
from .index_verify import (CHECKPOINT_DIR, CHECKPOINT_MANIFEST_NAME, CHECKPOINT_SCHEMA, EMBEDDING_DIMENSIONS,
                           EXPECTATIONS_SCHEMA, MAX_JSON_BYTES, REFERENCE_NAME, REFERENCE_SCHEMA, RELOAD_NAME,
                           RELOAD_RECHECK_NAME, SELECTION, SUPPORT_FILES, WEIGHTS, reference_record,
                           upstream_identity)

RELOAD_SCHEMA = "code-retrieval-reload-check-v1"
EXPECTATIONS_NAME = "reload-expectations.json"
MODEL_ROOT = REPO_ROOT / "artifacts" / "feasibility_pilot" / "cache" / "models"
UPSTREAM_DIR = MODEL_ROOT / "unixcoder-base-nine"
RELOAD_TOLERANCE = 1e-5
IDENTITY_KEYS = ("runId", "configSha256", "inputsManifestSha256", "prepare")
MAX_REFERENCE_BYTES = 4 * 1024 * 1024


def configure_numerics():
    """FP32 means FP32: no TF32 and deterministic cuDNN for training, reference and reload alike."""
    torch.backends.cuda.matmul.allow_tf32 = False
    torch.backends.cudnn.allow_tf32 = False
    torch.backends.cudnn.deterministic = True
    torch.backends.cudnn.benchmark = False


def cuda_device():
    if not torch.cuda.is_available():
        raise ValueError("CUDA is required for trained encoder work; no CPU fallback")
    return torch.device("cuda")


def load_upstream(device):
    """Hash-verified frozen load of the pinned upstream encoder (training starting point)."""
    upstream = upstream_identity()
    tokenizer, model, info = load_frozen(UPSTREAM_DIR, MODEL_ROOT, upstream["fileHashes"], device=device)
    return tokenizer, model, info, upstream


def encode_rows(model, rows, pad_id, device, *, amp):
    """Pooled, L2-normalized vectors: padding-masked mean over all tokens, framing included, FP32."""
    length = max(len(row) for row in rows)
    ids = torch.tensor([row + [pad_id] * (length - len(row)) for row in rows], dtype=torch.long, device=device)
    mask = torch.tensor([[1] * len(row) + [0] * (length - len(row)) for row in rows], dtype=torch.long, device=device)
    with torch.autocast(device_type=device.type, dtype=torch.float16, enabled=amp and device.type == "cuda"):
        hidden = model(input_ids=ids, attention_mask=mask).last_hidden_state
    hidden = hidden.float()
    pooled = (hidden * mask.unsqueeze(-1)).sum(1) / mask.sum(1).unsqueeze(-1)
    return F.normalize(pooled, p=2, dim=-1)


def reference_vectors(model, chunk_ids, pad_id, device):
    """Eval-mode FP32 vectors, one row per forward, no autocast: the reload-comparable form."""
    was_training = model.training
    model.eval()
    try:
        with torch.inference_mode():
            return [encode_rows(model, [ids], pad_id, device, amp=False)[0].cpu().tolist() for ids in chunk_ids]
    finally:
        model.train(was_training)


def _verify_files(checkpoint_dir, expected):
    """Exact file set and per-file size and SHA256 of a checkpoint directory."""
    entries = list(checkpoint_dir.iterdir())
    if any(entry.is_dir() for entry in entries) or {entry.name for entry in entries} != set(expected):
        raise ValueError("checkpoint directory holds an extra or missing file")
    for name, entry in expected.items():
        path = ensure_inside(checkpoint_dir / name, checkpoint_dir)
        if path.stat().st_size != entry.get("bytes") or file_hash(path) != sha(entry.get("sha256")):
            raise ValueError("checkpoint file drift: " + name)


def write_epoch_checkpoint(epoch_dir, *, root, model, upstream, fields, support_dir=None):
    """Write-once epoch snapshot: safetensors weights, verified support files, bound manifest."""
    if epoch_dir.exists():
        raise FileExistsError("immutable checkpoint collision: " + str(epoch_dir))
    support = UPSTREAM_DIR if support_dir is None else support_dir
    state = {name: tensor.detach().cpu().contiguous() for name, tensor in model.state_dict().items()}
    files = {WEIGHTS: save(state, metadata={"format": "pt"})}
    for name in SUPPORT_FILES:
        data = (support / name).read_bytes()
        if digest(data) != upstream["fileHashes"][name]:
            raise ValueError("upstream support file drift: " + name)
        files[name] = data
    for name, data in files.items():
        write_once(epoch_dir / CHECKPOINT_DIR / name, data, root)
    manifest = {"schema": CHECKPOINT_SCHEMA,
                "upstream": {"modelRevision": upstream["modelRevision"],
                             "supportFileHashes": {name: upstream["fileHashes"][name] for name in SUPPORT_FILES}},
                "files": {name: {"sha256": digest(data), "bytes": len(data)} for name, data in files.items()},
                "fullStateSha256": state_hash(model),
                "selectedForUse": False, "selection": SELECTION, **fields}
    write_once(epoch_dir / CHECKPOINT_MANIFEST_NAME, canonical(manifest), root)
    return manifest


def _check_encoder(model, full_state_sha256):
    if model.config.hidden_size != EMBEDDING_DIMENSIONS:
        raise ValueError("trained encoder dimension drift")
    if model.config.is_decoder:
        raise ValueError("bidirectional encoder configuration required")
    if state_hash(model) != sha(full_state_sha256):
        raise ValueError("trained encoder full state differs from its checkpoint manifest")


def _epoch_number(epoch_dir):
    name = epoch_dir.name
    if not name.startswith("epoch-") or not name[len("epoch-"):].isdigit():
        raise ValueError("checkpoint folder is not an epoch folder: " + name)
    return int(name[len("epoch-"):])


def load_trained(epoch_dir, *, upstream, device, expected_manifest_sha256, expected_identity):
    """Load one checkpoint only if its manifest is the one the training evidence binds."""
    number = _epoch_number(epoch_dir)
    raw = inputs.read_bytes(epoch_dir / CHECKPOINT_MANIFEST_NAME, MAX_JSON_BYTES)
    if digest(raw) != sha(expected_manifest_sha256):
        raise ValueError("checkpoint manifest is not the one training evidence binds")
    manifest = inputs.parse_json(raw, "checkpoint manifest")
    if manifest.get("schema") != CHECKPOINT_SCHEMA:
        raise ValueError("unexpected checkpoint manifest schema")
    if manifest.get("epoch") != number:
        raise ValueError("checkpoint epoch differs from its folder")
    if manifest.get("selectedForUse") is not False or manifest.get("selection") != SELECTION:
        raise ValueError("checkpoint selection state must stay PENDING_VALIDATION")
    for key in IDENTITY_KEYS:
        if manifest.get(key) != expected_identity.get(key):
            raise ValueError("checkpoint identity differs from training evidence: " + key)
    upstream_block = manifest.get("upstream") or {}
    if upstream_block.get("modelRevision") != upstream["modelRevision"]:
        raise ValueError("checkpoint upstream revision drift")
    if upstream_block.get("supportFileHashes") != {name: upstream["fileHashes"][name] for name in SUPPORT_FILES}:
        raise ValueError("checkpoint upstream file identity drift")
    checkpoint = epoch_dir / CHECKPOINT_DIR
    expected = manifest.get("files")
    if not isinstance(expected, dict) or set(expected) != {WEIGHTS, *SUPPORT_FILES}:
        raise ValueError("checkpoint manifest file set mismatch")
    _verify_files(checkpoint, expected)
    tokenizer = RobertaTokenizerFast.from_pretrained(str(checkpoint), local_files_only=True, trust_remote_code=False)
    model = RobertaModel.from_pretrained(str(checkpoint), local_files_only=True, trust_remote_code=False,
                                         use_safetensors=True)
    _verify_files(checkpoint, expected)  # a swap between the first check and the loads is detected here
    _check_encoder(model, manifest["fullStateSha256"])
    model = model.to(device).eval()
    for param in model.parameters():
        param.requires_grad_(False)
    return tokenizer, model, manifest


def load_reload_expectations(path, run_id):
    """Expectations written by the training run before its fresh-process reload."""
    expectations = inputs.parse_json(inputs.read_bytes(path, MAX_JSON_BYTES), "reload expectations")
    if expectations.get("schema") != EXPECTATIONS_SCHEMA or expectations.get("runId") != run_id:
        raise ValueError("reload expectations schema or run id mismatch")
    return expectations


def write_reference(epoch_dir, *, root, model, chunk_ids, pad_id, device):
    """Fixed reference vectors for one epoch: eval mode, FP32, one chunk per forward."""
    vectors = reference_vectors(model, chunk_ids, pad_id, device)
    packed = vector_bytes(vectors)
    record = {"schema": REFERENCE_SCHEMA, "dimension": EMBEDDING_DIMENSIONS, "rows": len(chunk_ids),
              "padTokenId": pad_id, "inputIds": chunk_ids, "inputIdsSha256": digest(canonical(chunk_ids)),
              "vectorBytes": len(packed), "vectorSha256": digest(packed),
              "vectorsBase64": base64.b64encode(packed).decode("ascii")}
    write_once(epoch_dir / REFERENCE_NAME, canonical(record), root)
    return record


def reload_epoch(epoch_dir, *, root, upstream, device, expected, check_name=RELOAD_NAME):
    """Load one checkpoint in this process, re-encode its reference, compare to the stored vectors."""
    reference = inputs.parse_json(inputs.read_bytes(epoch_dir / REFERENCE_NAME, MAX_REFERENCE_BYTES),
                                  "eval reference")
    chunk_ids, packed = reference_record(reference)
    tokenizer, model, manifest = load_trained(epoch_dir, upstream=upstream, device=device,
                                              expected_manifest_sha256=expected["checkpointManifestSha256"],
                                              expected_identity=expected["identity"])
    if tokenizer.pad_token_id != reference["padTokenId"]:
        raise ValueError("reload pad token differs from the reference record")
    rows = reference_vectors(model, chunk_ids, tokenizer.pad_token_id, device)
    stored = read_vectors(packed, len(chunk_ids))
    finite = all(math.isfinite(value) for row in rows for value in row)
    norm_deviation = max(abs(math.sqrt(sum(value * value for value in row)) - 1.0) for row in rows)
    max_difference = max(abs(a - b) for left, right in zip(rows, stored) for a, b in zip(left, right))
    shape = [len(rows), len(rows[0])]
    full_state_matches = state_hash(model) == manifest["fullStateSha256"]
    passed = (shape == [len(chunk_ids), EMBEDDING_DIMENSIONS] and finite
              and norm_deviation <= RELOAD_TOLERANCE and max_difference <= RELOAD_TOLERANCE and full_state_matches)
    result = {"schema": RELOAD_SCHEMA, "epochDirectory": epoch_dir.name, "shape": shape, "allFinite": finite,
              "maxNormDeviation": norm_deviation, "maxAbsDifference": max_difference,
              "tolerance": RELOAD_TOLERANCE, "fullStateMatches": full_state_matches, "passed": passed}
    write_once(epoch_dir / check_name, canonical(result), root)
    return result


def reload_run(run, *, expectations, root, device, epochs, check_name=RELOAD_NAME):
    """Fresh-process proof: every epoch checkpoint reloads and reproduces its stored vectors."""
    upstream = upstream_identity()
    configure_numerics()
    results = [reload_epoch(run / ("epoch-" + str(number)), root=root, upstream=upstream, device=device,
                            expected=expectations["epochs"][str(number)], check_name=check_name)
               for number in range(1, epochs + 1)]
    return {"epochs": results, "passed": all(row["passed"] for row in results)}
