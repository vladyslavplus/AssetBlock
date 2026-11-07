"""Product-faithful semantic search: per-fragment windows and the service's own file aggregation.

The deployed service pools every window of a query file, asks the index for the nearest gallery
windows in batches of 80 (SQL: per gallery file the minimum cosine distance, ordered by distance
then file identity, limited to the semantic depth) and merges batches by minimum distance. This
module imports the service's aggregation function unchanged and replaces only the database call
with an in-memory exact search that follows the same SQL semantics.

Residual divergence from the deployed path (reported in the README and result notes): the index
embeds windows produced at ingestion time, here they are produced at evaluation time with the same
extraction and chunk mapping; the in-memory search returns the same fields except span positions,
which no ranking uses. A database-backed run remains the parity reference.
"""

import sys
from contextlib import contextmanager
from types import SimpleNamespace
from unittest import mock

from scripts.feasibility_pilot.paths import REPO_ROOT

QUERY_BATCH = 80
MAX_GALLERY = 1000
MAX_LIMIT = 40
UNIT_TOLERANCE = 1e-6


def _unit(vectors):
    """float32 matrix of vectors that must already be unit length (the encoders L2-normalise)."""
    import numpy
    matrix = numpy.asarray(vectors, dtype=numpy.float32)
    if matrix.ndim != 2 or not numpy.all(numpy.abs(numpy.linalg.norm(matrix.astype(numpy.float64), axis=1) - 1.0)
                                         <= UNIT_TOLERANCE):
        raise ValueError("vectors must be unit length")
    return matrix


def _service():
    root = str(REPO_ROOT / "services" / "code-analysis")
    if root not in sys.path:
        sys.path.insert(0, root)
    from app import contracts, stages
    from scripts.code_analysis import code_index
    return contracts, stages, code_index


def fragments(raw, file):
    """Extracted executable units of one file, as the service maps them (no whole-file fallback)."""
    from scripts.code_analysis.extraction import extract_source
    record = {"fileId": file["fileId"], "sha256": file["sha256"], "language": file["language"]}
    if file.get("dialect") is not None:
        record["dialect"] = file["dialect"]
    result = extract_source(raw, record)
    return list(result.get("fragments") or []) if result.get("searchableExecutable") else []


class InMemoryIndex:
    """Gallery window vectors by file; stands in for the database connection."""

    def __init__(self, vectors_by_file):
        self.vectors_by_file = {file_id: [list(v) for v in vectors]
                                for file_id, vectors in vectors_by_file.items() if vectors}


def exact_query(connection, index, vectors, gallery_ids, language, *, partition, limit=40, runtime_target=None):
    """The SQL's semantics: per file the minimum cosine distance over query x gallery windows."""
    import numpy
    del index, language, partition, runtime_target
    if not 0 < len(vectors) <= QUERY_BATCH or not 0 < len(gallery_ids) <= MAX_GALLERY             or len(set(gallery_ids)) != len(gallery_ids):
        raise ValueError("bounded unique full gallery/query required")
    if type(limit) is not int or not 1 <= limit <= MAX_LIMIT:
        raise ValueError("file candidate budget invalid")
    missing = [file_id for file_id in gallery_ids if not connection.vectors_by_file.get(file_id)]
    if missing:
        raise ValueError("full gallery missing indexed eligible source")
    queries = _unit(vectors)
    best = {}
    for file_id in gallery_ids:
        distances = 1.0 - numpy.asarray(queries @ _unit(connection.vectors_by_file[file_id]).T, dtype=numpy.float32)
        best[file_id] = float(distances.min())
    ordered = sorted(best, key=lambda file_id: (best[file_id], file_id))[:limit]
    return [{"fileId": file_id, "distance": best[file_id]} for file_id in ordered]


@contextmanager
def _patched(code_index):
    with mock.patch.object(code_index, "exact_query", exact_query):
        yield


def semantic_rank(query_vectors, gallery_vectors, *, language_group="python", limit=None):
    """Ranked ``[{fileId, score, rank}]`` for one query file through the service's aggregation function."""
    contracts, stages, code_index = _service()
    gallery = {file_id: vectors for file_id, vectors in gallery_vectors.items() if vectors}
    if not query_vectors or not gallery:
        return []
    snapshot = SimpleNamespace(
        files=[SimpleNamespace(language_group=language_group, file_id=file_id) for file_id in sorted(gallery)],
        index_key="0" * 64, partition="validation")
    windows = [{"queryFileId": "query", "queryGroup": language_group} for _ in query_vectors]
    with _patched(code_index):
        ranked = stages._exact_retrieval(InMemoryIndex(gallery), snapshot, windows, list(query_vectors))
    rows = ranked.get("query", [])
    return rows if limit is None else rows[:limit]
