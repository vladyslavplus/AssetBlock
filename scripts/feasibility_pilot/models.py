"""Frozen encoder, retrieval smoke training, and joint-input pair model."""

from __future__ import annotations

import json
from pathlib import Path
from typing import Any

import torch
import torch.nn.functional as F
from torch import nn
from transformers import RobertaModel, RobertaTokenizerFast

from .pair_layout import encode_fragment_ids, encode_pair_ids, pad_to_length


class MeanPoolEncoder(nn.Module):
    def __init__(self, encoder: RobertaModel):
        super().__init__()
        self.encoder = encoder

    def forward(self, input_ids: torch.Tensor, attention_mask: torch.Tensor) -> torch.Tensor:
        hidden = self.encoder(input_ids=input_ids, attention_mask=attention_mask).last_hidden_state
        mask = attention_mask.unsqueeze(-1).to(hidden.dtype)
        pooled = (hidden * mask).sum(dim=1) / mask.sum(dim=1).clamp(min=1e-6)
        return F.normalize(pooled, p=2, dim=-1)


class PairVerifier(nn.Module):
    def __init__(self, encoder: RobertaModel, hidden_size: int):
        super().__init__()
        self.encoder = encoder
        self.head = nn.Linear(hidden_size, 2)

    def forward(self, input_ids: torch.Tensor, attention_mask: torch.Tensor) -> tuple[torch.Tensor, torch.Tensor]:
        outputs = self.encoder(input_ids=input_ids, attention_mask=attention_mask)
        hidden = outputs.last_hidden_state
        cls = hidden[:, 0]
        logits = self.head(cls)
        return logits, hidden


def load_tokenizer(model_dir: Path):
    return RobertaTokenizerFast.from_pretrained(
        str(model_dir),
        local_files_only=True,
        trust_remote_code=False,
    )


def load_encoder(model_dir: Path, *, trainable: bool) -> RobertaModel:
    # Prefer local safetensors if present. The published card only ships
    # pytorch_model.bin; that file is loaded only with local_files_only and
    # without trust_remote_code, after hashing against the pinned revision.
    model = RobertaModel.from_pretrained(
        str(model_dir),
        local_files_only=True,
        trust_remote_code=False,
        use_safetensors=(model_dir / "model.safetensors").exists(),
    )
    if not trainable:
        model.eval()
        for param in model.parameters():
            param.requires_grad_(False)
    return model


def freeze_except_last_blocks(model: RobertaModel, n_blocks: int) -> list[str]:
    for param in model.parameters():
        param.requires_grad_(False)
    names: list[str] = []
    layers = model.encoder.layer
    for layer in layers[-n_blocks:]:
        for name, param in layer.named_parameters():
            param.requires_grad_(True)
            names.append(name)
    return names


def tensor_fingerprint(tensor: torch.Tensor) -> dict[str, float]:
    detached = tensor.detach().float().cpu()
    return {
        "mean": float(detached.mean()),
        "std": float(detached.std(unbiased=False)),
        "absMax": float(detached.abs().max()),
        "norm": float(torch.linalg.vector_norm(detached)),
    }


def l2_delta(before: torch.Tensor, after: torch.Tensor) -> float:
    return float(torch.linalg.vector_norm((after - before).float()).cpu())


def cuda_synchronize(device: torch.device) -> None:
    torch.cuda.synchronize(device)


def reset_stage_peaks(device: torch.device) -> None:
    cuda_synchronize(device)
    torch.cuda.reset_peak_memory_stats(device)


def cuda_memory_snapshot(device: torch.device, label: str) -> dict[str, Any]:
    cuda_synchronize(device)
    return {
        "label": label,
        "isTrainingPeak": label == "training",
        "allocatedBytes": int(torch.cuda.memory_allocated(device)),
        "reservedBytes": int(torch.cuda.memory_reserved(device)),
        "maxAllocatedBytes": int(torch.cuda.max_memory_allocated(device)),
        "maxReservedBytes": int(torch.cuda.max_memory_reserved(device)),
    }


def gpu_probe(device: torch.device) -> dict[str, Any]:
    """CUDA prerequisite probe. This is not a training-stage peak."""
    reset_stage_peaks(device)
    x = torch.randn(1024, 1024, device=device)
    y = torch.randn(1024, 1024, device=device)
    z = x @ y
    snap = cuda_memory_snapshot(device, "prerequisiteProbe")
    ok = bool(torch.isfinite(z).all().item())
    snap["matmulFinite"] = ok
    snap["isTrainingPeak"] = False
    snap["note"] = "prerequisite CUDA matmul probe; do not report as training peak"
    return snap


def pack_ids(tokenizer, rows: list[list[int]], max_length: int) -> tuple[torch.Tensor, torch.Tensor]:
    padded = []
    masks = []
    pad_id = tokenizer.pad_token_id
    for ids in rows:
        ids_p, mask = pad_to_length(ids, pad_id, max_length)
        padded.append(ids_p)
        masks.append(mask)
    return torch.tensor(padded, dtype=torch.long), torch.tensor(masks, dtype=torch.long)


def save_json(path: Path, payload: dict[str, Any]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(payload, indent=2), encoding="utf-8")
