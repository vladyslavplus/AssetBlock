"""Bounded GPU smoke updates, checkpointing, and reload checks."""

from __future__ import annotations

import hashlib
import json
import os
import time
from pathlib import Path
from typing import Any

import torch
import torch.nn.functional as F
from torch.amp import GradScaler, autocast
from transformers import RobertaModel

from .models import (
    MeanPoolEncoder,
    PairVerifier,
    cuda_memory_snapshot,
    freeze_except_last_blocks,
    gpu_probe,
    l2_delta,
    load_encoder,
    load_tokenizer,
    pack_ids,
    reset_stage_peaks,
    tensor_fingerprint,
)
from .pair_layout import encode_fragment_ids, encode_pair_ids, pad_to_length
from .paths import ARTIFACT_ROOT, ensure_inside
from .subprocess_util import run_bounded


TEMPERATURE = 0.07
ATOL = 1e-5
RTOL = 1e-4


def _device() -> torch.device:
    if not torch.cuda.is_available():
        raise RuntimeError("CUDA is required for training evidence")
    return torch.device("cuda")


def _seed(seed: int) -> None:
    torch.manual_seed(seed)
    torch.cuda.manual_seed_all(seed)


def _named_param(model: torch.nn.Module, needle: str) -> torch.nn.Parameter:
    for name, param in model.named_parameters():
        if needle in name:
            return param
    raise KeyError(needle)


def _track_optimizer_steps(optimizer: torch.optim.Optimizer) -> dict[str, int]:
    state = {"count": 0}
    original = optimizer.step

    def tracked(*args, **kwargs):
        state["count"] += 1
        return original(*args, **kwargs)

    optimizer.step = tracked  # type: ignore[method-assign]
    return state


def _trainable_gradients_finite(module: torch.nn.Module) -> tuple[bool, float]:
    total_norm = 0.0
    for param in module.parameters():
        if not param.requires_grad:
            continue
        grad = param.grad
        if grad is None:
            continue
        if not torch.isfinite(grad).all():
            return False, total_norm
        total_norm += float(grad.detach().float().norm().cpu())
    return True, total_norm


def _inject_named_overflow(module: torch.nn.Module, needles: list[str]) -> None:
    for needle in needles:
        param = _named_param(module, needle)
        if param.grad is None:
            param.grad = torch.full_like(param, float("inf"))
        else:
            param.grad.detach().fill_(float("inf"))


def _amp_maybe_step(
    *,
    scaler: GradScaler,
    optimizer: torch.optim.Optimizer,
    module: torch.nn.Module,
    loss: torch.Tensor,
    step_state: dict[str, int],
) -> tuple[bool, float]:
    scaler.unscale_(optimizer)
    grads_ok, total_norm = _trainable_gradients_finite(module)
    loss_ok = bool(torch.isfinite(loss).all().item())
    if not grads_ok or not loss_ok:
        scaler.update()
        return False, total_norm
    before = step_state["count"]
    scaler.step(optimizer)
    scaler.update()
    return step_state["count"] > before, total_norm


def tensors_close(actual: torch.Tensor, stored: torch.Tensor) -> dict[str, Any]:
    if tuple(actual.shape) != tuple(stored.shape):
        return {
            "matched": False,
            "finite": bool(torch.isfinite(actual).all() and torch.isfinite(stored).all()),
            "reason": "dimension mismatch",
            "actualShape": list(actual.shape),
            "storedShape": list(stored.shape),
        }
    finite = bool(torch.isfinite(actual).all() and torch.isfinite(stored).all())
    if not finite:
        return {"matched": False, "finite": False, "reason": "nonfinite values"}
    close = bool(torch.allclose(actual, stored, atol=ATOL, rtol=RTOL))
    max_abs = float((actual - stored).abs().max().cpu())
    rel = float(((actual - stored).abs() / stored.abs().clamp(min=1e-8)).max().cpu())
    return {
        "matched": close,
        "finite": True,
        "maxAbs": max_abs,
        "maxRel": rel,
        "reason": None if close else "value mismatch",
    }


def train_retrieval(
    *,
    model_dir: Path,
    output_dir: Path,
    triplets: list[dict[str, str]],
    max_length: int,
    last_blocks: int,
    max_updates: int = 5,
    max_attempts: int = 20,
    timeout_s: float = 600,
    inject_overflow_on_attempts: dict[int, list[str]] | None = None,
) -> dict[str, Any]:
    wall_started = time.perf_counter()
    device = _device()
    _seed(42)
    t_probe = time.perf_counter()
    probe = gpu_probe(device)
    probe_s = time.perf_counter() - t_probe
    reset_stage_peaks(device)
    t_init = time.perf_counter()
    tokenizer = load_tokenizer(model_dir)
    encoder = load_encoder(model_dir, trainable=True)
    trainable = freeze_except_last_blocks(encoder, last_blocks)
    wrapper = MeanPoolEncoder(encoder).to(device)
    optimizer = torch.optim.AdamW((p for p in wrapper.parameters() if p.requires_grad), lr=1e-5)
    scaler = GradScaler("cuda", enabled=True)
    step_state = _track_optimizer_steps(optimizer)
    init_mem = cuda_memory_snapshot(device, "initialization")
    init_s = time.perf_counter() - t_init

    trainable_before = _named_param(encoder, "encoder.layer.11.attention.self.query.weight").detach().cpu().clone()
    frozen_before = _named_param(encoder, "encoder.layer.0.attention.self.query.weight").detach().cpu().clone()

    updates = 0
    attempts = 0
    skipped_amp = 0
    successful_attempts: list[dict[str, Any]] = []
    skipped_attempts: list[dict[str, Any]] = []
    idx = 0
    reset_stage_peaks(device)
    t_train = time.perf_counter()
    while updates < max_updates and attempts < max_attempts:
        if time.perf_counter() - wall_started > timeout_s:
            break
        attempts += 1
        item = triplets[idx % len(triplets)]
        idx += 1
        texts = [item["query"], item["positive"], item["negativeA"], item["negativeB"]]
        encoded = [encode_fragment_ids(tokenizer, text, max_length) for text in texts]
        ids, mask = pack_ids(tokenizer, [row[0] for row in encoded], max_length)
        ids = ids.to(device)
        mask = mask.to(device)
        optimizer.zero_grad(set_to_none=True)
        with autocast("cuda", dtype=torch.float16):
            vecs = wrapper(ids, mask)
            query, pos, neg_a, neg_b = vecs[0], vecs[1], vecs[2], vecs[3]
            logits = torch.stack([(query * pos).sum(), (query * neg_a).sum(), (query * neg_b).sum()]) / TEMPERATURE
            loss = F.cross_entropy(logits.unsqueeze(0), torch.zeros(1, dtype=torch.long, device=device))
        scaler.scale(loss).backward()
        needles = (inject_overflow_on_attempts or {}).get(attempts) or []
        if needles:
            _inject_named_overflow(wrapper, needles)
        stepped, total_norm = _amp_maybe_step(
            scaler=scaler, optimizer=optimizer, module=wrapper, loss=loss, step_state=step_state
        )
        row = {
            "attempt": attempts,
            "skipped": not stepped,
            "loss": None if not stepped else float(loss.detach().float().cpu()),
            "gradNorm": total_norm,
            "optimizerStepped": stepped,
        }
        if stepped:
            updates += 1
            successful_attempts.append(row)
        else:
            skipped_amp += 1
            skipped_attempts.append(row)

    train_mem = cuda_memory_snapshot(device, "training")
    train_s = time.perf_counter() - t_train
    trainable_after = _named_param(encoder, "encoder.layer.11.attention.self.query.weight").detach().cpu().clone()
    frozen_after = _named_param(encoder, "encoder.layer.0.attention.self.query.weight").detach().cpu().clone()
    encoder_delta = l2_delta(trainable_before, trainable_after)
    frozen_delta = l2_delta(frozen_before, frozen_after)
    reset_stage_peaks(device)
    t_save = time.perf_counter()
    output_dir.mkdir(parents=True, exist_ok=True)
    save_dir = output_dir / "checkpoint"
    encoder.save_pretrained(save_dir)
    tokenizer.save_pretrained(save_dir)
    eval_payload = _eval_vectors(wrapper, tokenizer, triplets[0], max_length, device)
    (output_dir / "eval-reference.json").write_text(json.dumps(eval_payload), encoding="utf-8")
    ckpt_hash = _hash_tree(save_dir)
    save_mem = cuda_memory_snapshot(device, "saveAndEval")
    save_s = time.perf_counter() - t_save
    del wrapper, encoder, optimizer, scaler
    torch.cuda.empty_cache()
    return {
        "architecture": "retrieval-encoder",
        "updates": updates,
        "attempts": attempts,
        "skippedAmp": skipped_amp,
        "successfulAttempts": successful_attempts,
        "skippedAttempts": skipped_attempts,
        "history": successful_attempts + skipped_attempts,
        "trainableNames": trainable[:20],
        "encoderDeltaL2": encoder_delta,
        "frozenDeltaL2": frozen_delta,
        "encoderChanged": encoder_delta > 1e-8,
        "frozenInvariant": frozen_delta < 1e-6,
        "checkpointDir": str(save_dir),
        "checkpointHash": ckpt_hash,
        "gpu": {
            "prerequisiteProbe": probe,
            "initialization": init_mem,
            "training": train_mem,
            "saveAndEval": save_mem,
            "note": "training.maxAllocatedBytes is the training-stage peak; prerequisiteProbe is not a training peak",
        },
        "timings": {
            "probeSeconds": probe_s,
            "initializationSeconds": init_s,
            "trainingSeconds": train_s,
            "saveAndEvalSeconds": save_s,
            "wallSeconds": time.perf_counter() - wall_started,
        },
        "durationSeconds": train_s,
        "evalReference": eval_payload,
        "outcome": "SUCCEEDED" if updates >= 5 and encoder_delta > 1e-8 and frozen_delta < 1e-6 else "FAILED",
    }


def train_pair(
    *,
    model_dir: Path,
    output_dir: Path,
    pairs: list[dict[str, Any]],
    max_length: int,
    last_blocks: int,
    max_updates: int = 5,
    max_attempts: int = 20,
    timeout_s: float = 600,
    inject_overflow_on_attempts: dict[int, list[str]] | None = None,
) -> dict[str, Any]:
    wall_started = time.perf_counter()
    device = _device()
    _seed(42)
    t_probe = time.perf_counter()
    probe = gpu_probe(device)
    probe_s = time.perf_counter() - t_probe
    reset_stage_peaks(device)
    t_init = time.perf_counter()
    tokenizer = load_tokenizer(model_dir)
    encoder = load_encoder(model_dir, trainable=True)
    model = PairVerifier(encoder, encoder.config.hidden_size).to(device)
    freeze_except_last_blocks(model.encoder, last_blocks)
    for param in model.head.parameters():
        param.requires_grad_(True)
    optimizer = torch.optim.AdamW((p for p in model.parameters() if p.requires_grad), lr=1e-5)
    scaler = GradScaler("cuda", enabled=True)
    step_state = _track_optimizer_steps(optimizer)
    init_mem = cuda_memory_snapshot(device, "initialization")
    init_s = time.perf_counter() - t_init

    trainable_before = _named_param(model.encoder, "encoder.layer.11.attention.self.query.weight").detach().cpu().clone()
    frozen_before = _named_param(model.encoder, "encoder.layer.0.attention.self.query.weight").detach().cpu().clone()
    head_before = model.head.weight.detach().cpu().clone()

    interaction = _pair_interaction_checks(model, tokenizer, pairs[0], max_length, device)

    updates = 0
    attempts = 0
    skipped_amp = 0
    successful_attempts: list[dict[str, Any]] = []
    skipped_attempts: list[dict[str, Any]] = []
    idx = 0
    reset_stage_peaks(device)
    t_train = time.perf_counter()
    while updates < max_updates and attempts < max_attempts:
        if time.perf_counter() - wall_started > timeout_s:
            break
        attempts += 1
        item = pairs[idx % len(pairs)]
        idx += 1
        encoded = encode_pair_ids(tokenizer, item["left"], item["right"], max_length)
        ids, mask = pack_ids(tokenizer, [encoded.input_ids], max_length)
        ids = ids.to(device)
        mask = mask.to(device)
        label = torch.tensor([int(item["label"])], device=device)
        optimizer.zero_grad(set_to_none=True)
        with autocast("cuda", dtype=torch.float16):
            logits, _ = model(ids, mask)
            loss = F.cross_entropy(logits, label)
        scaler.scale(loss).backward()
        needles = (inject_overflow_on_attempts or {}).get(attempts) or []
        if needles:
            _inject_named_overflow(model, needles)
        stepped, total_norm = _amp_maybe_step(
            scaler=scaler, optimizer=optimizer, module=model, loss=loss, step_state=step_state
        )
        row = {
            "attempt": attempts,
            "skipped": not stepped,
            "loss": None if not stepped else float(loss.detach().float().cpu()),
            "gradNorm": total_norm,
            "optimizerStepped": stepped,
        }
        if stepped:
            updates += 1
            successful_attempts.append(row)
        else:
            skipped_amp += 1
            skipped_attempts.append(row)

    train_mem = cuda_memory_snapshot(device, "training")
    train_s = time.perf_counter() - t_train
    trainable_after = _named_param(model.encoder, "encoder.layer.11.attention.self.query.weight").detach().cpu().clone()
    frozen_after = _named_param(model.encoder, "encoder.layer.0.attention.self.query.weight").detach().cpu().clone()
    head_after = model.head.weight.detach().cpu().clone()
    encoder_delta = l2_delta(trainable_before, trainable_after)
    frozen_delta = l2_delta(frozen_before, frozen_after)
    head_delta = l2_delta(head_before, head_after)
    reset_stage_peaks(device)
    t_save = time.perf_counter()
    output_dir.mkdir(parents=True, exist_ok=True)
    save_dir = output_dir / "checkpoint"
    model.encoder.save_pretrained(save_dir)
    tokenizer.save_pretrained(save_dir)
    torch.save(model.head.state_dict(), save_dir / "pair_head.pt")
    eval_payload = _eval_pair(model, tokenizer, pairs[0], max_length, device)
    (output_dir / "eval-reference.json").write_text(json.dumps(eval_payload), encoding="utf-8")
    (output_dir / "pair-layout.json").write_text(
        json.dumps(
            {
                "framingOverhead": encode_pair_ids(tokenizer, pairs[0]["left"], pairs[0]["right"], max_length).framing_overhead,
                "layout": "[CLS] <encoder-only> [SEP] A [SEP] B [SEP]",
            },
            indent=2,
        ),
        encoding="utf-8",
    )
    save_mem = cuda_memory_snapshot(device, "saveAndEval")
    save_s = time.perf_counter() - t_save
    result = {
        "architecture": "joint-input-pair-verifier",
        "updates": updates,
        "attempts": attempts,
        "skippedAmp": skipped_amp,
        "successfulAttempts": successful_attempts,
        "skippedAttempts": skipped_attempts,
        "history": successful_attempts + skipped_attempts,
        "encoderDeltaL2": encoder_delta,
        "frozenDeltaL2": frozen_delta,
        "headDeltaL2": head_delta,
        "encoderChanged": encoder_delta > 1e-8,
        "headChanged": head_delta > 1e-8,
        "frozenInvariant": frozen_delta < 1e-6,
        "interaction": interaction,
        "checkpointDir": str(save_dir),
        "checkpointHash": _hash_tree(save_dir),
        "gpu": {
            "prerequisiteProbe": probe,
            "initialization": init_mem,
            "training": train_mem,
            "saveAndEval": save_mem,
            "note": "training.maxAllocatedBytes is the training-stage peak; prerequisiteProbe is not a training peak",
        },
        "timings": {
            "probeSeconds": probe_s,
            "initializationSeconds": init_s,
            "trainingSeconds": train_s,
            "saveAndEvalSeconds": save_s,
            "wallSeconds": time.perf_counter() - wall_started,
        },
        "durationSeconds": train_s,
        "evalReference": eval_payload,
        "fallbackUsed": False,
        "outcome": "SUCCEEDED"
        if updates >= 5 and encoder_delta > 1e-8 and head_delta > 1e-8 and interaction["crossFragmentChanges"]
        else "FAILED",
    }
    del model, encoder, optimizer, scaler
    torch.cuda.empty_cache()
    return result


def _pair_interaction_checks(model: PairVerifier, tokenizer, sample: dict[str, Any], max_length: int, device) -> dict[str, Any]:
    model.eval()
    with torch.no_grad():
        left = sample["left"]
        right = sample["right"]
        alt = sample.get("altRight") or (sample["right"] + "\nreturn 0;\n")
        first = encode_pair_ids(tokenizer, left, right, max_length)
        second = encode_pair_ids(tokenizer, left, alt, max_length)
        pad_len = max(len(first.input_ids), len(second.input_ids), 64)
        ids1, mask1 = pad_to_length(first.input_ids, tokenizer.pad_token_id, pad_len)
        ids2, mask2 = pad_to_length(second.input_ids, tokenizer.pad_token_id, pad_len)
        hidden1 = model(
            torch.tensor([ids1], device=device),
            torch.tensor([mask1], device=device),
        )[1]
        hidden2 = model(
            torch.tensor([ids2], device=device),
            torch.tensor([mask2], device=device),
        )[1]
        a_start, a_end = first.a_span
        a_delta = float((hidden1[0, a_start:a_end] - hidden2[0, a_start:a_end]).abs().max().cpu())
        padded = ids1 + [tokenizer.pad_token_id]
        # Padding-only control: compare last nonpad token vs a longer pad copy.
        ids_pad, mask_pad = pad_to_length(first.input_ids, tokenizer.pad_token_id, pad_len + 8)
        hidden_pad = model(
            torch.tensor([ids_pad], device=device),
            torch.tensor([mask_pad], device=device),
        )[1]
        pad_delta = float((hidden1[0, : len(first.input_ids)] - hidden_pad[0, : len(first.input_ids)]).abs().max().cpu())
        swapped = encode_pair_ids(tokenizer, right, left, max_length)
    model.train()
    return {
        "crossFragmentChanges": a_delta > 1e-6,
        "aRepresentationAbsMaxDelta": a_delta,
        "paddingAbsMaxDelta": pad_delta,
        "paddingDoesNotInteract": pad_delta < 1e-4,
        "orderSwappedLength": len(swapped.input_ids),
        "truncated": first.truncated or second.truncated,
    }


def _eval_vectors(wrapper: MeanPoolEncoder, tokenizer, sample: dict[str, str], max_length: int, device) -> dict[str, Any]:
    wrapper.eval()
    with torch.no_grad():
        ids, mask = pack_ids(
            tokenizer,
            [encode_fragment_ids(tokenizer, sample["query"], max_length)[0]],
            max_length,
        )
        ids = ids.to(device)
        mask = mask.to(device)
        with autocast("cuda", enabled=False):
            vec = wrapper(ids, mask).float().cpu()[0]
    wrapper.train()
    ids_list = ids[0].detach().cpu().tolist()
    mask_list = mask[0].detach().cpu().tolist()
    vector = [float(x) for x in vec.tolist()]
    return {
        "queryText": sample["query"],
        "inputIds": ids_list,
        "attentionMask": mask_list,
        "maxLength": max_length,
        "vector": vector,
        "dim": int(vec.numel()),
        "finite": bool(torch.isfinite(vec).all()),
    }


def _eval_pair(model: PairVerifier, tokenizer, sample: dict[str, Any], max_length: int, device) -> dict[str, Any]:
    model.eval()
    with torch.no_grad():
        encoded = encode_pair_ids(tokenizer, sample["left"], sample["right"], max_length)
        ids, mask = pack_ids(tokenizer, [encoded.input_ids], max_length)
        logits = model(ids.to(device), mask.to(device))[0].float().cpu()[0]
    model.train()
    return {
        "leftText": sample["left"],
        "rightText": sample["right"],
        "inputIds": ids[0].detach().cpu().tolist(),
        "attentionMask": mask[0].detach().cpu().tolist(),
        "maxLength": max_length,
        "logits": [float(logits[0]), float(logits[1])],
        "dim": 2,
        "finite": bool(torch.isfinite(logits).all()),
    }


def _hash_tree(root: Path) -> str:
    digest = hashlib.sha256()
    for path in sorted(root.rglob("*")):
        if path.is_file():
            digest.update(path.relative_to(root).as_posix().encode("utf-8"))
            digest.update(path.read_bytes())
    return digest.hexdigest()


def reload_in_fresh_process(*, kind: str, checkpoint: Path, reference: Path, model_dir: Path, max_length: int, python_exe: Path) -> dict[str, Any]:
    worker = Path(__file__).with_name("reload_worker.py")
    out_path = checkpoint.parent / "reload-output.json"
    result = run_bounded(
        [
            str(python_exe),
            str(worker),
            "--kind",
            kind,
            "--checkpoint",
            str(checkpoint),
            "--reference",
            str(reference),
            "--output",
            str(out_path),
            "--max-length",
            str(max_length),
        ],
        cwd=ARTIFACT_ROOT,
        allowed_roots=[ARTIFACT_ROOT, Path(__file__).resolve().parent, model_dir],
        timeout_s=300,
        extra_env_allow=("HF_HOME", "TRANSFORMERS_CACHE", "HF_HUB_OFFLINE", "TRANSFORMERS_OFFLINE"),
        env={
            "HF_HUB_OFFLINE": "1",
            "TRANSFORMERS_OFFLINE": "1",
            "HF_HOME": str(ARTIFACT_ROOT / "cache" / "hf"),
            "PYTHONPATH": str(Path(__file__).resolve().parents[2]),
        },
    )
    payload = json.loads(out_path.read_text(encoding="utf-8")) if out_path.exists() else {}
    payload["process"] = {
        "returncode": result.returncode,
        "timedOut": result.timed_out,
        "stderr": result.stderr[-4000:],
    }
    return payload
