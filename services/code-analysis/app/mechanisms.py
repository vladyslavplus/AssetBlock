"""Narrow adapter over the existing pure analysis mechanisms.

Only the allowlisted pure helpers below may be used. Heavy optional
dependencies (torch, transformers, psycopg, node, scanner binaries) are never
imported here: native tools run through explicit fixed-argv runners, and
missing prerequisites surface as honest stage failures, never import crashes.
"""

from __future__ import annotations

import sys
from pathlib import Path

# strictly below: app/ -> code-analysis/ -> services/ -> repository root.
REPO_ROOT = Path(__file__).resolve().parents[3]
if str(REPO_ROOT) not in sys.path:
    sys.path.insert(0, str(REPO_ROOT))


def _import(name: str):
    try:
        module_name, attr = name.rsplit(".", 1)
    except ValueError:
        raise ImportError(name)
    import importlib
    module = importlib.import_module(module_name)
    return getattr(module, attr)


def extract_source(raw: bytes, file: dict) -> dict:
    return _import("scripts.code_analysis.extraction.extract_source")(raw, file)


def mapped_chunks(raw: bytes, fragment: dict, tokenizer) -> dict:
    return _import("scripts.code_analysis.representations.mapped_chunks")(raw, fragment, tokenizer)


def check_vector_bytes(vectors, dimension: int = 768) -> bytes:
    return _import("scripts.code_analysis.representations.vector_bytes")(vectors, dimension)


def bind_token_pairs(raw_pairs: list, allowlist: list) -> list:
    return _import("scripts.code_analysis.baselines.bind_token_pairs")(raw_pairs, allowlist)


def rank_token_files(pairs: list, query_id: str, gallery_ids: list, limit: int) -> list:
    return _import("scripts.code_analysis.baselines.rank_files")(
        pairs, query_id, gallery_ids, limit=limit)


def union_candidates(token_ids: list, semantic_ids: list, budget: int) -> list:
    return _import("scripts.code_analysis.baselines.union_candidates")(
        token_ids, semantic_ids, budget=budget)


def reciprocal_rank_order(token_ids: list, semantic_ids: list, budget: int) -> list:
    from scripts.code_analysis import baselines
    return baselines.rrf_order(token_ids, semantic_ids, budget=budget)


def generator_header_signals(raw: bytes) -> list:
    return _import("scripts.code_analysis.classification.generator_header")(raw)


def reference_model_key(identity: dict) -> str:
    return _import("scripts.code_analysis.code_index.model_key")(dict(identity))


def actual_representation() -> dict:
    """Representation envelope this code enforces, for identity binding."""
    return {"chunking": _import("scripts.code_analysis.representations.CHUNKING"),
            "framing": "encoder-only",
            "pooling": "padding-masked mean then L2",
            "mask": "padding attention mask",
            "preprocessing": "utf-8 strict, no execution",
            "dimension": 768}


def ensure_inside(path: Path, *roots: Path) -> Path:
    return _import("scripts.feasibility_pilot.paths.ensure_inside")(path, *roots)


def parser_identity() -> dict:
    return _import("scripts.code_analysis.splits.parser_identity")()


def load_frozen_encoder(model_dir, cache_root, expected_files: dict, device: str = "cpu"):
    return _import("scripts.code_analysis.frozen_encoder.load_frozen")(
        model_dir, cache_root, expected_files, device=device)


def encode_frozen_chunks(model, tokenizer, chunks: list, batch_size: int = 8):
    return _import("scripts.code_analysis.frozen_encoder.encode_chunks")(
        model, tokenizer, chunks, batch_size=batch_size)
