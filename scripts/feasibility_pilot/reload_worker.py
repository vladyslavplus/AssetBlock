"""Fresh-process reload comparison worker."""

from __future__ import annotations

import argparse
import json
from pathlib import Path

import torch

from scripts.feasibility_pilot.models import MeanPoolEncoder, PairVerifier, load_encoder, load_tokenizer, pack_ids
from scripts.feasibility_pilot.pair_layout import encode_fragment_ids, encode_pair_ids
from scripts.feasibility_pilot.train import tensors_close


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--kind", choices=["retrieval", "pair"], required=True)
    parser.add_argument("--checkpoint", required=True)
    parser.add_argument("--reference", required=True)
    parser.add_argument("--output", required=True)
    parser.add_argument("--max-length", type=int, required=True)
    args = parser.parse_args()
    checkpoint = Path(args.checkpoint)
    output = Path(args.output)
    if not Path(args.reference).exists():
        output.write_text(json.dumps({"kind": args.kind, "matched": False, "finite": False, "reason": "missing reference"}), encoding="utf-8")
        return 1
    reference = json.loads(Path(args.reference).read_text(encoding="utf-8"))
    device = torch.device("cuda" if torch.cuda.is_available() else "cpu")
    tokenizer = load_tokenizer(checkpoint)
    encoder = load_encoder(checkpoint, trainable=False).to(device)
    encoder.eval()
    if args.kind == "retrieval":
        payload = _reload_retrieval(encoder, tokenizer, reference, args.max_length, device)
    else:
        payload = _reload_pair(encoder, tokenizer, checkpoint, reference, args.max_length, device)
    output.write_text(json.dumps(payload, indent=2), encoding="utf-8")
    return 0 if payload.get("matched") and payload.get("finite") else 1


def _reload_retrieval(encoder, tokenizer, reference: dict, max_length: int, device) -> dict:
    query = reference.get("queryText")
    stored_ids = reference.get("inputIds")
    stored_mask = reference.get("attentionMask")
    stored_vec = reference.get("vector")
    stored_dim = reference.get("dim")
    if not query or stored_vec is None or stored_dim is None:
        return {
            "kind": "retrieval",
            "matched": False,
            "finite": False,
            "reason": "missing query/reference",
            "fallbackUsed": False,
        }
    wrapper = MeanPoolEncoder(encoder).to(device)
    wrapper.eval()
    if stored_ids and stored_mask:
        ids = torch.tensor([stored_ids], dtype=torch.long)
        mask = torch.tensor([stored_mask], dtype=torch.long)
    else:
        ids, mask = pack_ids(tokenizer, [encode_fragment_ids(tokenizer, query, max_length)[0]], max_length)
    with torch.no_grad():
        vec = wrapper(ids.to(device), mask.to(device)).float().cpu()[0]
    stored = torch.tensor(stored_vec, dtype=torch.float32)
    if int(stored.numel()) != int(stored_dim) or int(vec.numel()) != int(stored_dim):
        return {
            "kind": "retrieval",
            "matched": False,
            "finite": bool(torch.isfinite(vec).all()),
            "reason": "dimension mismatch",
            "actualDim": int(vec.numel()),
            "storedDim": int(stored_dim),
            "fallbackUsed": False,
        }
    compared = tensors_close(vec, stored)
    compared.update({"kind": "retrieval", "device": str(device), "fallbackUsed": False, "actualDim": int(vec.numel())})
    return compared


def _reload_pair(encoder, tokenizer, checkpoint: Path, reference: dict, max_length: int, device) -> dict:
    left = reference.get("leftText")
    right = reference.get("rightText")
    stored_logits = reference.get("logits")
    if not left or not right or stored_logits is None:
        return {
            "kind": "pair",
            "matched": False,
            "finite": False,
            "reason": "missing query/reference",
            "fallbackUsed": False,
        }
    model = PairVerifier(encoder, encoder.config.hidden_size).to(device)
    head_path = checkpoint / "pair_head.pt"
    if not head_path.exists():
        return {"kind": "pair", "matched": False, "finite": False, "reason": "missing pair head", "fallbackUsed": False}
    model.head.load_state_dict(torch.load(head_path, map_location=device))
    model.eval()
    stored_ids = reference.get("inputIds")
    stored_mask = reference.get("attentionMask")
    if stored_ids and stored_mask:
        ids = torch.tensor([stored_ids], dtype=torch.long)
        mask = torch.tensor([stored_mask], dtype=torch.long)
    else:
        encoded = encode_pair_ids(tokenizer, left, right, max_length)
        ids, mask = pack_ids(tokenizer, [encoded.input_ids], max_length)
    with torch.no_grad():
        logits = model(ids.to(device), mask.to(device))[0].float().cpu()[0]
    stored = torch.tensor(stored_logits, dtype=torch.float32)
    compared = tensors_close(logits, stored)
    compared.update({"kind": "pair", "device": str(device), "fallbackUsed": False, "actualDim": int(logits.numel())})
    return compared


if __name__ == "__main__":
    raise SystemExit(main())
