"""Isolated CPU smoke for transformers 5.17.0 with hash-pinned local unixcoder weights. Not a product entry point."""

from __future__ import annotations

import hashlib
import json
import sys
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]
if str(REPO_ROOT) not in sys.path:
    sys.path.insert(0, str(REPO_ROOT))
MODEL_DIR = REPO_ROOT / "artifacts" / "feasibility_pilot" / "cache" / "models" / "unixcoder-base-nine"


def run_smoke(model_dir: Path = MODEL_DIR) -> dict[str, object]:
    from scripts.feasibility_pilot.env_setup import verify_ml_smoke_package_pins, verify_pinned_model_identity

    identity = verify_pinned_model_identity(model_dir)

    import torch
    import transformers
    from transformers import RobertaModel, RobertaTokenizerFast

    package_pins = verify_ml_smoke_package_pins(torch_version=torch.__version__)

    use_safetensors = bool(identity["useSafetensors"])
    tokenizer = RobertaTokenizerFast.from_pretrained(
        str(model_dir),
        local_files_only=True,
        trust_remote_code=False,
    )
    model = RobertaModel.from_pretrained(
        str(model_dir),
        local_files_only=True,
        trust_remote_code=False,
        use_safetensors=use_safetensors,
    ).eval()
    for param in model.parameters():
        param.requires_grad_(False)

    text = "def add(a, b): return a + b"
    encoded = tokenizer(text, return_tensors="pt", truncation=True, max_length=64)
    if encoded["input_ids"].numel() == 0:
        raise RuntimeError("tokenization produced an empty input")
    if encoded["attention_mask"].shape != encoded["input_ids"].shape:
        raise RuntimeError("attention mask shape drift")

    with torch.inference_mode():
        hidden = model(**encoded).last_hidden_state.float()
        mask = encoded["attention_mask"].unsqueeze(-1)
        pooled = (hidden * mask).sum(1) / mask.sum(1).unsqueeze(-1)
        vector = torch.nn.functional.normalize(pooled, p=2, dim=-1).squeeze(0)

    if vector.numel() != 768 or not torch.isfinite(vector).all():
        raise RuntimeError("embedding dimension or finiteness check failed")

    digest = hashlib.sha256(vector.cpu().numpy().tobytes()).hexdigest()
    model2 = RobertaModel.from_pretrained(
        str(model_dir),
        local_files_only=True,
        trust_remote_code=False,
        use_safetensors=use_safetensors,
    ).eval()
    with torch.inference_mode():
        hidden2 = model2(**encoded).last_hidden_state.float()
        pooled2 = (hidden2 * mask).sum(1) / mask.sum(1).unsqueeze(-1)
        vector2 = torch.nn.functional.normalize(pooled2, p=2, dim=-1).squeeze(0)
    if not torch.allclose(vector, vector2, atol=1e-5, rtol=1e-5):
        raise RuntimeError("reload repeatability check failed")

    return {
        "outcome": "PASSED",
        "transformers": transformers.__version__,
        "torch": torch.__version__,
        "packagePins": package_pins,
        "modelRevision": identity["modelRevision"],
        "selectedWeights": identity["selectedWeights"],
        "fileHashes": identity["fileHashes"],
        "embeddingDim": int(vector.numel()),
        "embeddingSha256": digest,
        "tokenizerClass": type(tokenizer).__name__,
        "modelClass": type(model).__name__,
    }


def main() -> int:
    try:
        payload = run_smoke(MODEL_DIR)
    except FileNotFoundError as exc:
        print(json.dumps({"outcome": "FAILED", "reason": str(exc), "modelDir": str(MODEL_DIR)}, sort_keys=True))
        return 1
    except (ValueError, RuntimeError) as exc:
        print(
            json.dumps(
                {"outcome": "FAILED", "reason": str(exc), "modelDir": str(MODEL_DIR)},
                sort_keys=True,
            )
        )
        return 1
    print(json.dumps(payload, sort_keys=True))
    return 0


if __name__ == "__main__":
    sys.exit(main())
