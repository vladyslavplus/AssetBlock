"""Capacity registry: one active run, digest-bound conflicts, no attempts."""

from app.service import _enter_run, _exit_run


def _state():
    return {"busy": False, "active": {}}


def test_first_claim_wins_and_release_frees():
    state = _state()
    acquired, conflict = _enter_run(state, "run-1", "a" * 64)
    assert acquired and conflict is None
    acquired, conflict = _enter_run(state, "run-2", "b" * 64)
    assert not acquired and conflict.code == "SERVICE_BUSY"
    _exit_run(state, "run-1")
    acquired, conflict = _enter_run(state, "run-2", "b" * 64)
    assert acquired and conflict is None


def test_same_run_different_digest_conflicts():
    state = _state()
    _enter_run(state, "run-1", "a" * 64)
    acquired, conflict = _enter_run(state, "run-1", "b" * 64)
    assert not acquired and conflict.code == "STALE_RUN_CONFLICT"


def test_same_run_same_digest_is_busy_not_new_claim():
    state = _state()
    _enter_run(state, "run-1", "a" * 64)
    acquired, conflict = _enter_run(state, "run-1", "a" * 64)
    assert not acquired and conflict.code == "SERVICE_BUSY"
    assert state["active"] == {"run-1": "a" * 64}
