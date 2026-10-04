"""Pair token layout helper used by the joint-input model and unit tests."""

from __future__ import annotations

from dataclasses import dataclass


ENCODER_ONLY = "<encoder-only>"


@dataclass(frozen=True)
class PairEncoding:
    input_ids: list[int]
    attention_mask: list[int]
    framing_overhead: int
    a_original: int
    b_original: int
    a_retained: int
    b_retained: int
    truncated: bool
    a_span: tuple[int, int]
    b_span: tuple[int, int]
    separator_positions: list[int]


def single_token_id(tokenizer, token: str) -> int:
    token_id = tokenizer.convert_tokens_to_ids(token)
    unk = tokenizer.unk_token_id
    if token_id is None or token_id == unk:
        raise ValueError(f"required special token missing from tokenizer: {token}")
    return int(token_id)


def encoder_framing_ids(tokenizer) -> list[int]:
    cls_id = single_token_id(tokenizer, tokenizer.cls_token)
    mode_id = single_token_id(tokenizer, ENCODER_ONLY)
    sep_id = single_token_id(tokenizer, tokenizer.sep_token)
    ids = [cls_id, mode_id, sep_id]
    if len(ids) != 3:
        raise ValueError("encoder framing must be three token ids")
    return ids


def encode_fragment_ids(tokenizer, text: str, max_length: int) -> tuple[list[int], bool, int]:
    prefix = encoder_framing_ids(tokenizer)
    sep_id = single_token_id(tokenizer, tokenizer.sep_token)
    content = list(tokenizer.encode(text, add_special_tokens=False))
    overhead = len(prefix) + 1
    budget = max_length - overhead
    if budget < 0:
        raise ValueError("max_length smaller than framing overhead")
    truncated = len(content) > budget
    retained = content[:budget]
    ids = prefix + retained + [sep_id]
    if len(ids) > max_length:
        raise ValueError("fragment encoding exceeded max_length")
    return ids, truncated, len(content)


def encode_pair_ids(tokenizer, text_a: str, text_b: str, max_length: int) -> PairEncoding:
    prefix = encoder_framing_ids(tokenizer)
    sep_id = single_token_id(tokenizer, tokenizer.sep_token)
    a_ids = list(tokenizer.encode(text_a, add_special_tokens=False))
    b_ids = list(tokenizer.encode(text_b, add_special_tokens=False))
    # Layout: [CLS] <encoder-only> [SEP] A [SEP] B [SEP]
    framing = prefix + [sep_id, sep_id]
    overhead = len(framing)
    if overhead != 5:
        raise ValueError(f"expected five framing tokens, got {overhead}")
    content_budget = max_length - overhead
    if content_budget < 2:
        raise ValueError("insufficient content budget for pair")
    a_budget = (content_budget + 1) // 2
    b_budget = content_budget - a_budget
    truncated = len(a_ids) > a_budget or len(b_ids) > b_budget
    a_ret = a_ids[:a_budget]
    b_ret = b_ids[:b_budget]
    ids = prefix + a_ret + [sep_id] + b_ret + [sep_id]
    if len(ids) > max_length:
        raise ValueError("pair encoding exceeded configured budget")
    sep_positions = [i for i, token_id in enumerate(ids) if token_id == sep_id]
    if len(sep_positions) != 3:
        raise ValueError("pair layout must contain exactly three separators")
    a_start = len(prefix)
    a_end = a_start + len(a_ret)
    b_start = a_end + 1
    b_end = b_start + len(b_ret)
    mask = [1] * len(ids)
    return PairEncoding(
        input_ids=ids,
        attention_mask=mask,
        framing_overhead=overhead,
        a_original=len(a_ids),
        b_original=len(b_ids),
        a_retained=len(a_ret),
        b_retained=len(b_ret),
        truncated=truncated,
        a_span=(a_start, a_end),
        b_span=(b_start, b_end),
        separator_positions=sep_positions,
    )


def pad_to_length(ids: list[int], pad_id: int, length: int) -> tuple[list[int], list[int]]:
    if len(ids) > length:
        raise ValueError("cannot pad a sequence longer than the target length")
    mask = [1] * len(ids) + [0] * (length - len(ids))
    padded = ids + [pad_id] * (length - len(ids))
    return padded, mask
