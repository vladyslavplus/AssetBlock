"""Retrieval training-data admission, encoder training and provenance binding.

This package consumes only explicitly identified, hash-bound local artifacts. The
admission stage never loads an ML model. The training stage loads the pinned encoder
only after binding to an admitted prepare run. Nothing downloads source, writes a
database, scores the sealed final test, or authorizes publication.
"""

SCHEMA = "code-retrieval-v1"

PURPOSE = "CODE_RETRIEVAL_DATA_ADMISSION"


def report(payload):
    """Attach the non-authorizing report envelope shared by corpus tooling."""
    return {**payload, "schema": SCHEMA, "Purpose": PURPOSE,
            "CanAuthorizePublication": False, "SecurityEvidence": "NOT_RUN"}
