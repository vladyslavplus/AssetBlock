"""Encoder identity binding and REPORT stage evidence."""

import pytest

from app import contracts, mechanisms
from app.stages import (_full_matrix, _validate_report_body, _verify_encoder_identity,
                        Pipeline, StageRecord)


def _snapshot(model_identity):
    from types import SimpleNamespace
    return SimpleNamespace(model_identity=model_identity)


def _identity(**overrides):
    actual = mechanisms.actual_representation()
    base = {"checkpointSha256": "a" * 64,
            "tokenizerHashes": {"vocab.json": "b" * 64},
            "framing": actual["framing"],
            "pooling": actual["pooling"],
            "mask": actual["mask"],
            "preprocessing": actual["preprocessing"],
            "chunking": actual["chunking"],
            "dimension": 768,
            "selectionSha256": "c" * 64}
    base.update(overrides)
    return base


def _loaded(identity):
    return {"checkpointSha256": identity["checkpointSha256"],
            "tokenizerHashes": dict(identity["tokenizerHashes"]),
            "loadedFullStateSha256": "d" * 64,
            "dimension": 768,
            "device": "cpu",
            "safetensorsOnly": True,
            "trainableParameters": 0,
            "framing": identity["framing"],
            "pooling": identity["pooling"]}


def test_bound_encoder_passes():
    identity = _identity()
    _verify_encoder_identity(_loaded(identity), _snapshot(identity))


def test_other_checkpoint_rejected():
    identity = _identity()
    loaded = _loaded(identity)
    loaded["checkpointSha256"] = "f" * 64
    with pytest.raises(contracts.ContractError) as exc_info:
        _verify_encoder_identity(loaded, _snapshot(identity))
    assert exc_info.value.code == "MODEL_UNAVAILABLE"


def test_other_tokenizer_rejected():
    identity = _identity()
    loaded = _loaded(identity)
    loaded["tokenizerHashes"] = {"vocab.json": "e" * 64}
    with pytest.raises(contracts.ContractError) as exc_info:
        _verify_encoder_identity(loaded, _snapshot(identity))
    assert exc_info.value.code == "MODEL_UNAVAILABLE"


def test_framing_change_rejected():
    identity = _identity(framing="other-framing")
    with pytest.raises(contracts.ContractError) as exc_info:
        _verify_encoder_identity(_loaded(_identity()), _snapshot(identity))
    assert exc_info.value.code == "MODEL_UNAVAILABLE"


def test_missing_model_identity_rejected():
    with pytest.raises(contracts.ContractError) as exc_info:
        _verify_encoder_identity(_loaded(_identity()), _snapshot(None))
    assert exc_info.value.code == "REFERENCE_MANIFEST_INVALID"


def _record(stage, outcome="SUCCEEDED"):
    return StageRecord(stage=stage, applicability="MANDATORY", outcome=outcome,
                       duration_seconds=0.0)


def test_full_matrix_requires_all_eight():
    pipe = Pipeline(context=None)
    for stage in ("INVENTORY", "EXTRACTION", "TOKEN", "SEMANTIC", "LICENSE",
                  "UNION", "VERIFIER"):
        pipe.record(_record(stage))
    assert _full_matrix(pipe) is False
    pipe.record(_record("REPORT"))
    assert _full_matrix(pipe) is True


def test_report_validation_rejects_authority_flip():
    from types import SimpleNamespace
    metadata = SimpleNamespace(run_id="r", input_digest="d")
    pipe = Pipeline(context=SimpleNamespace(metadata=metadata))
    body = {"schema": "code-analysis-v1", "runId": "r", "inputDigest": "d",
            "admittedBy": None, "policy": {"canAuthorizePublication": False},
            "sellerSafe": {"reportId": "x"}, "reportId": "x",
            "findings": [], "coverage": {"omissions": []}}
    _validate_report_body(pipe, body)
    bad = dict(body, policy={"canAuthorizePublication": True})
    with pytest.raises(contracts.ContractError):
        _validate_report_body(pipe, bad)
    bad2 = dict(body, admittedBy="someone")
    with pytest.raises(contracts.ContractError):
        _validate_report_body(pipe, bad2)
