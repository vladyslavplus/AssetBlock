"""Retrieval training-data admission and provenance binding.

This package consumes only explicitly identified, hash-bound local artifacts. It
never downloads source, loads an ML model, instantiates an optimizer, writes a
database, scores the sealed final test, or authorizes publication.
"""

SCHEMA = "code-retrieval-v1"

PURPOSE = "CODE_RETRIEVAL_DATA_ADMISSION"


def report(payload):
    """Attach the non-authorizing report envelope shared by corpus tooling."""
    return {**payload, "schema": SCHEMA, "Purpose": PURPOSE,
            "CanAuthorizePublication": False, "SecurityEvidence": "NOT_RUN"}
