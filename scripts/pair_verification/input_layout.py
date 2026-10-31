"""Joint-input token layout shared by pair preparation and its unit tests."""

from dataclasses import dataclass


@dataclass(frozen=True)
class PairLayout:
    input_ids: tuple[int, ...]
    a_original_tokens: int
    b_original_tokens: int
    a_retained_tokens: int
    b_retained_tokens: int
    max_tokens: int
    content_budget: int
    a_budget: int
    b_budget: int
    framing_tokens: int
    truncated: bool


def layout_pair_ids(a_ids, b_ids, *, cls_id, mode_id, sep_id, max_tokens=512):
    if max_tokens != 512:
        raise ValueError("pair maximum length is frozen at 512 tokens")
    if not all(type(value) is int and value >= 0 for value in (cls_id, mode_id, sep_id)):
        raise ValueError("pair framing token ids must be nonnegative integers")
    if not isinstance(a_ids, (list, tuple)) or not isinstance(b_ids, (list, tuple)):
        raise ValueError("pair content ids must be sequences")
    if any(type(value) is not int or value < 0 for value in (*a_ids, *b_ids)):
        raise ValueError("pair content token id is invalid")
    framing = 5
    content_budget = max_tokens - framing
    a_budget = (content_budget + 1) // 2
    b_budget = content_budget - a_budget
    a_kept, b_kept = tuple(a_ids[:a_budget]), tuple(b_ids[:b_budget])
    ids = (cls_id, mode_id, sep_id, *a_kept, sep_id, *b_kept, sep_id)
    if (len(ids) > max_tokens or ids[:3] != (cls_id, mode_id, sep_id)
            or ids[3 + len(a_kept)] != sep_id or ids[-1] != sep_id):
        raise ValueError("joint-input layout framing mismatch")
    return PairLayout(tuple(ids), len(a_ids), len(b_ids), len(a_kept), len(b_kept), max_tokens,
                      content_budget, a_budget, b_budget, framing,
                      len(a_kept) != len(a_ids) or len(b_kept) != len(b_ids))


def window_content(window):
    ids = window.get("inputIds")
    if not isinstance(ids, list) or len(ids) < 5 or window.get("framingTokens") != 4:
        raise ValueError("frozen window framing mismatch")
    return ids[3:-1]
