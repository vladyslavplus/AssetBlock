"""Contained artifact roots for retrieval runs; reuses the corpus path policy."""

import re
from pathlib import Path

from scripts.code_analysis.corpus import ARTIFACT_ROOT
from scripts.feasibility_pilot.paths import REPO_ROOT, ensure_inside

CORPUS_ARTIFACT_ROOT = ARTIFACT_ROOT
RETRIEVAL_ROOT = REPO_ROOT / "artifacts" / "code_retrieval"
INPUT_ROOT = RETRIEVAL_ROOT / "inputs"

RUN_ID = re.compile(r"[A-Za-z0-9][A-Za-z0-9_-]{0,79}")


def run_dir(run_id):
    if not isinstance(run_id, str) or not RUN_ID.fullmatch(run_id):
        raise ValueError("invalid run id")
    return ensure_inside(RETRIEVAL_ROOT / "runs" / run_id, RETRIEVAL_ROOT)


def derived_dir(run_id):
    return ensure_inside(run_dir(run_id) / "derived", RETRIEVAL_ROOT)


def input_path(name):
    if not isinstance(name, str) or not RUN_ID.fullmatch(name):
        raise ValueError("invalid input artifact name")
    return ensure_inside(INPUT_ROOT / (name + ".json"), RETRIEVAL_ROOT)


def contained(path, *roots):
    return ensure_inside(Path(path), *(roots or (RETRIEVAL_ROOT, CORPUS_ARTIFACT_ROOT)))
