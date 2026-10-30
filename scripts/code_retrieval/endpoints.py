"""Tokenized windows for admitted triplet endpoints, shared by training and the environment probe."""

from scripts.code_analysis.representations import mapped_chunks

from . import admission, corpus_index


def endpoint_windows(endpoint, sources_root, tokenizer):
    """Mapped windows for one endpoint; derived endpoints tokenize their own blob."""
    fragment = endpoint["fragment"]
    if admission.is_derived(fragment):
        raw = endpoint["body"]
        token_fragment = {"fragmentId": fragment["fragmentId"], "startByte": 0, "endByte": len(raw),
                          "sha256": fragment["sha256"]}
    else:
        raw = corpus_index.source_bytes(sources_root, endpoint["parent"])
        token_fragment = fragment
    return mapped_chunks(raw, token_fragment, tokenizer)["chunks"]
