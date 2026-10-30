"""Random-weight RobertaModel and tokenizer built in code. No upstream files, no network."""

import hashlib
import json

import torch
from transformers import RobertaConfig, RobertaModel

SPECIAL_TOKENS = ["<s>", "<pad>", "</s>", "<unk>", "<mask>", "<encoder-only>"]
HIDDEN = 768
LAYERS = 3
MODEL_REVISION = "tiny-fixture-revision"
SUPPORT_NAMES = ("config.json", "vocab.json", "merges.txt", "tokenizer_config.json", "special_tokens_map.json")


def _byte_characters():
    """GPT-2 byte-to-character table, so every byte has a vocabulary entry."""
    printable = list(range(ord("!"), ord("~") + 1)) + list(range(ord("¡"), ord("¬") + 1)) + list(
        range(ord("®"), ord("ÿ") + 1))
    codes = list(printable)
    extra = 0
    for byte in range(256):
        if byte not in printable:
            printable.append(byte)
            codes.append(256 + extra)
            extra += 1
    return [chr(code) for code in codes]


def write_upstream(directory):
    """Support files as the trained checkpoints expect them; returns the upstream identity."""
    directory.mkdir(parents=True, exist_ok=True)
    vocab_tokens = SPECIAL_TOKENS + _byte_characters()
    vocab = {token: index for index, token in enumerate(vocab_tokens)}
    config = RobertaConfig(vocab_size=len(vocab), hidden_size=HIDDEN, num_hidden_layers=LAYERS,
                           num_attention_heads=12, intermediate_size=128, max_position_embeddings=514,
                           pad_token_id=1, bos_token_id=0, eos_token_id=2, type_vocab_size=1)
    config.save_pretrained(str(directory))
    (directory / "vocab.json").write_text(json.dumps(vocab), encoding="utf-8")
    (directory / "merges.txt").write_text("#version: 0.2\n", encoding="utf-8")
    (directory / "tokenizer_config.json").write_text("{}", encoding="utf-8")
    (directory / "special_tokens_map.json").write_text("{}", encoding="utf-8")
    file_hashes = {name: hashlib.sha256((directory / name).read_bytes()).hexdigest() for name in SUPPORT_NAMES}
    return {"modelRevision": MODEL_REVISION, "fileHashes": file_hashes}


def build_model(directory, seed=0):
    """Random encoder whose config matches the support files in directory."""
    config = RobertaConfig.from_pretrained(str(directory))
    torch.manual_seed(seed)
    model = RobertaModel(config)
    model.eval()
    return model
