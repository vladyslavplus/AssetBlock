"""Frozen encoder retrieval over the reviewed fixture fragments."""

from __future__ import annotations

import math
from typing import Any

import torch

from .models import MeanPoolEncoder, load_encoder, load_tokenizer, pack_ids
from .pair_layout import encode_fragment_ids


def cosine(a: torch.Tensor, b: torch.Tensor) -> float:
    return float((a * b).sum().cpu())


def run_frozen(
    *,
    model_dir,
    fragments: list[dict[str, str]],
    max_length: int,
    device: torch.device,
) -> dict[str, Any]:
    tokenizer = load_tokenizer(model_dir)
    encoder = load_encoder(model_dir, trainable=True)
    before = next(encoder.parameters()).detach().cpu().clone()
    wrapper = MeanPoolEncoder(encoder).to(device)
    wrapper.eval()
    vectors = []
    with torch.no_grad():
        for item in fragments:
            ids, truncated, original = encode_fragment_ids(tokenizer, item["text"], max_length)
            tensor_ids, mask = pack_ids(tokenizer, [ids], max_length)
            vec = wrapper(tensor_ids.to(device), mask.to(device)).float().cpu()[0]
            vectors.append(
                {
                    "fixtureId": item["fixtureId"],
                    "fragmentId": item["fragmentId"],
                    "language": item["language"],
                    "truncated": truncated,
                    "originalTokens": original,
                    "retainedTokens": len(ids),
                    "dim": int(vec.numel()),
                    "finite": bool(torch.isfinite(vec).all()),
                    "norm": float(torch.linalg.vector_norm(vec)),
                    "vector": vec,
                }
            )
    after = next(encoder.parameters()).detach().cpu().clone()
    weight_delta = float(torch.linalg.vector_norm(after - before))
    rankings = []
    for left in vectors:
        scored = []
        for right in vectors:
            if left["fragmentId"] == right["fragmentId"]:
                continue
            scored.append(
                {
                    "id": right["fragmentId"],
                    "fixtureId": right["fixtureId"],
                    "cosine": cosine(left["vector"], right["vector"]),
                }
            )
        scored.sort(key=lambda row: row["cosine"], reverse=True)
        rankings.append({"query": left["fragmentId"], "neighbors": scored[:10]})
    serializable = []
    for item in vectors:
        serializable.append({k: v for k, v in item.items() if k != "vector"} | {"vectorHead": item["vector"][:8].tolist()})
    repeats = []
    probe = fragments[0]
    ids, _, _ = encode_fragment_ids(tokenizer, probe["text"], max_length)
    tensor_ids, mask = pack_ids(tokenizer, [ids], max_length)
    with torch.no_grad():
        first = wrapper(tensor_ids.to(device), mask.to(device)).float()
        for _ in range(5):
            again = wrapper(tensor_ids.to(device), mask.to(device)).float()
            repeats.append(float((first - again).abs().max().cpu()))
    del wrapper, encoder
    torch.cuda.empty_cache()
    return {
        "outputDim": serializable[0]["dim"] if serializable else None,
        "weightDelta": weight_delta,
        "frozenInvariant": weight_delta < 1e-8,
        "repeatMaxAbs": max(repeats) if repeats else None,
        "vectors": serializable,
        "rankings": rankings,
        "finite": all(item["finite"] and item["norm"] > 0 for item in serializable),
    }
