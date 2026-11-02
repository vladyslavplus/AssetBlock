"""Encoder identity binding and REPORT stage evidence."""

import pytest

from app import contracts, mechanisms
from app.stages import (_dialect_extraction_counts, _full_matrix, _token_stage_counts, _validate_report_body, _verify_encoder_identity,
                        Pipeline, StageRecord, _reconcile_declaration, _pack_semantic_vectors, _unpack_semantic_vectors, _reuse_input, _reuse_binding, _reuse_tool_identity)


def _snapshot(model_identity):
    from types import SimpleNamespace
    return SimpleNamespace(model_identity=model_identity)


def _identity(**overrides):
    actual = mechanisms.actual_representation()
    base = {"checkpointSha256": "a" * 64,
            "loadedStateSha256": "d" * 64,
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
    if stage == "VERIFIER":
        from app.policy import EVALUATION_EVIDENCE_SHA256
        return StageRecord(stage=stage, applicability="MANDATORY", outcome="NOT_APPLICABLE",
                           error_code="POLICY_DISABLED", duration_seconds=0.0,
                           tool_identity={"mode": "DISABLED_BY_EVALUATION", "evidenceSha256": EVALUATION_EVIDENCE_SHA256})
    return StageRecord(stage=stage, applicability="MANDATORY", outcome=outcome,
                       duration_seconds=0.0)


def test_token_stage_counts_project_group_lists_to_counts():
    assert _token_stage_counts({"queryFiles": 2, "batches": 1, "rankedFiles": 3,
                                "comparedGroups": ["python"],
                                "uncomparedGroups": ["csharp"]}) == {
        "queryFiles": 2, "batches": 1, "rankedFiles": 3,
        "comparedGroups": 1, "uncomparedGroups": 1}


def test_dialect_extraction_counts_project_observed_extracted_units():
    assert _dialect_extraction_counts({
        "python": {"attempted": 2, "extracted": 1, "omitted": 1},
        "unknown": {"attempted": 0, "extracted": 0, "omitted": 2},
    }) == {"python": 1, "unknown": 0}


def test_record_binds_each_stage_input_to_run_and_prior_stage_outputs():
    from types import SimpleNamespace
    metadata = SimpleNamespace(input_digest="a" * 64)
    pipe = Pipeline(context=SimpleNamespace(metadata=metadata))
    inventory = _record("INVENTORY")
    inventory.output_sha256 = "b" * 64
    pipe.record(inventory)
    extraction = _record("EXTRACTION")
    pipe.record(extraction)

    assert inventory.input_sha256 == contracts.sha256_hex(contracts.canonical({
        "stage": "INVENTORY", "runInputDigest": "a" * 64, "upstream": []}))
    assert extraction.input_sha256 == contracts.sha256_hex(contracts.canonical({
        "stage": "EXTRACTION", "runInputDigest": "a" * 64,
        "upstream": [{"stage": "INVENTORY", "outcome": "SUCCEEDED",
                      "outputSha256": "b" * 64, "errorCode": None}]}))
    assert contracts.is_sha256(inventory.input_sha256)
    assert contracts.is_sha256(extraction.input_sha256)
    assert inventory.input_sha256 != extraction.input_sha256


def test_full_matrix_requires_all_eight():
    pipe = Pipeline(context=None)
    for stage in ("INVENTORY", "EXTRACTION", "TOKEN", "SEMANTIC", "LICENSE",
                  "UNION", "VERIFIER"):
        pipe.record(_record(stage))
    assert _full_matrix(pipe) is False
    pipe.record(_record("REPORT"))
    assert _full_matrix(pipe) is True


def test_report_validation_rejects_fabricated_review_eligibility():
    from types import SimpleNamespace
    metadata = SimpleNamespace(run_id="r", input_digest="d")
    pipe = Pipeline(context=SimpleNamespace(metadata=metadata))
    body = {"schema": "code-analysis-v1", "runId": "r", "inputDigest": "d",
            "admittedBy": None, "policy": {},
            "sellerSafe": {"reportId": "x"}, "reportId": "x",
            "findings": [], "coverage": {"omissions": []},
            "retrievalSummary": {"tokenTop": 20, "semanticTop": 20, "unionBudget": 40, "queries": []}}
    _validate_report_body(pipe, body)
    bad = dict(body, policy={"reviewEligible": True})
    with pytest.raises(contracts.ContractError):
        _validate_report_body(pipe, bad)
    bad2 = dict(body, admittedBy="someone")
    with pytest.raises(contracts.ContractError):
        _validate_report_body(pipe, bad2)


def test_unmatched_declaration_finding_preserves_scope_without_file_attribution():
    from types import SimpleNamespace
    entry = SimpleNamespace(scope_path="missing/scope", spdx="MIT")
    file = SimpleNamespace(canonical_path="src/main.py", sha256="a" * 64)
    pipe = Pipeline(
        context=SimpleNamespace(metadata=SimpleNamespace(declared_licenses=[entry])),
        inventory=SimpleNamespace(files=[file]),
    )
    _reconcile_declaration(pipe)
    finding = pipe.findings[0]
    assert finding["path"] == entry.scope_path
    assert finding["fileId"] is None
    assert finding["startByte"] is None and finding["endByte"] is None
    assert finding["startLine"] is None and finding["endLine"] is None
    assert "src/main.py" not in finding["path"]


def test_unknown_license_finding_includes_stable_file_identity():
    from types import SimpleNamespace
    entry = SimpleNamespace(scope_path="src", spdx="MIT")
    file = SimpleNamespace(canonical_path="src/main.py", sha256="b" * 64)
    pipe = Pipeline(
        context=SimpleNamespace(metadata=SimpleNamespace(declared_licenses=[entry])),
        inventory=SimpleNamespace(files=[file]),
        license_rows=[{"path": file.canonical_path, "expression": "UNKNOWN"}],
    )
    _reconcile_declaration(pipe)
    finding = pipe.findings[0]
    assert finding["path"] == file.canonical_path
    assert finding["fileId"] == contracts.sha256_hex(
        contracts.canonical([file.sha256, file.canonical_path]))


def test_semantic_reuse_vectors_pack_to_bounded_float32_and_round_trip():
    import struct
    vectors = [[float(i) / 1000 for i in range(768)]]
    encoded = _pack_semantic_vectors(vectors)
    restored = _unpack_semantic_vectors(encoded, 1)
    expected = list(struct.unpack("<768f", struct.pack("<768f", *vectors[0])))
    assert restored == [expected]
    assert len(encoded) == 4096


def test_semantic_reuse_vector_payload_rejects_wrong_dimensions():
    with pytest.raises(contracts.ContractError):
        _unpack_semantic_vectors("AAAA", 1)


def test_reuse_adapter_preserves_normalized_parent_run_binding():
    from types import SimpleNamespace
    parent_run = "11111111-1111-4111-8111-111111111111"
    reuse = {
        "sourceRunId": parent_run,
        "sourceInputDigest": "a" * 64,
        "sourceDeclarationDigest": "b" * 64,
        "sourceMaterialDigest": "c" * 64,
        "artifacts": [],
    }
    security = SimpleNamespace(
        security_generation=3,
        archive_evidence_id="22222222-2222-4222-8222-222222222222",
        malware_evidence_id="33333333-3333-4333-8333-333333333333",
    )
    metadata = SimpleNamespace(
        run_id="44444444-4444-4444-8444-444444444444",
        input_digest="d" * 64, content_sha256="e" * 64,
        material_digest="c" * 64, declaration_digest="f" * 64,
        security_evidence=security, reuse=reuse,
    )
    snapshot = SimpleNamespace(
        snapshot_id="snapshot", revision="revision", model_key="model",
        index_key="index", model_identity={"identity": "test"},
    )
    pipe = Pipeline(context=SimpleNamespace(
        metadata=metadata, snapshot=snapshot, policy={"policyDigest": "9" * 64}))
    binding = _reuse_binding(pipe, "SEMANTIC")
    binding["sourceRunId"] = reuse["sourceRunId"]
    binding["sourceInputDigest"] = reuse["sourceInputDigest"]
    binding["declarationDigest"] = reuse["sourceDeclarationDigest"]
    artifact = {"binding": binding, "data": {"toolIdentity": {"encoder": "test"}}}
    item = {"stage": "SEMANTIC", "sourceReportId": "55555555-5555-4555-8555-555555555555",
            "sourceStageId": "66666666-6666-4666-8666-666666666666",
            "artifactDigest": "7" * 64, "artifact": artifact}
    reuse["artifacts"] = [item]
    data, _, parent = _reuse_input(pipe, "SEMANTIC")
    identity = _reuse_tool_identity(data, parent)
    assert identity["reuseParentRunId"] == parent_run
    assert identity["reuseParentReportId"] == item["sourceReportId"]
    assert identity["reuseParentStageId"] == item["sourceStageId"]
    assert identity["reused"] is True
