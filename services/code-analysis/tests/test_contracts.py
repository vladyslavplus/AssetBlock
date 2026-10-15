"""Contract, policy, and identity tests: versioning, enums, binding."""

from app import contracts, policy as policy_module


def test_policy_is_immutable_and_has_no_publication_authority():
    first = policy_module.current_policy()
    assert first["policyVersion"] == contracts.POLICY_ID
    assert first["canAuthorizePublication"] is False
    assert first["canSubmit"] is False
    assert first["verifier"]["mode"] == "PENDING_RESEARCH_EVALUATION"
    assert first["verifier"]["outputs"] == []
    assert first["policyDigest"] == policy_module.POLICY_DIGEST
    # Same content, same digest across builds.
    assert policy_module.build_policy()["policyDigest"] == policy_module.POLICY_DIGEST


def test_exposed_enums_use_upper_snake_case():
    for enum in (contracts.STAGE, contracts.APPLICABILITY, contracts.STAGE_OUTCOME,
                 contracts.RUN_OUTCOME, contracts.COVERAGE, contracts.VERIFIER_MODE,
                 contracts.SENSITIVITY, contracts.FINDING_TYPE):
        for member in enum:
            assert member.name == member.value
            assert member.value == member.value.upper()
    for code in contracts.ERROR_CODES:
        assert code == code.upper()
    for reason in contracts.OMISSION_REASONS:
        assert reason == reason.upper()


def test_metadata_rejects_unknown_fields(make_metadata_archive):
    metadata, _archive = make_metadata_archive
    metadata["unexpectedField"] = True
    try:
        contracts.validate_metadata(metadata)
    except contracts.ContractError as exc:
        assert exc.code == "UNKNOWN_METADATA_FIELD"
    else:
        raise AssertionError("unknown field accepted")


def test_metadata_rejects_policy_mismatch(make_metadata_archive):
    metadata, _archive = make_metadata_archive
    metadata["policyVersion"] = "other-policy"
    try:
        contracts.validate_metadata(metadata)
    except contracts.ContractError as exc:
        assert exc.code == "POLICY_MISMATCH"
    else:
        raise AssertionError("policy mismatch accepted")


def test_input_digest_binds_exact_tuple(make_metadata_archive):
    metadata_dict, _archive = make_metadata_archive
    validated = contracts.validate_metadata(metadata_dict)
    recomputed = contracts.compute_analysis_digest(
        validated, policy_module.POLICY_DIGEST, None)
    assert recomputed == metadata_dict["inputDigest"]
    tampered = dict(metadata_dict)
    tampered["materialDigest"] = "f" * 64
    validated_tampered = contracts.validate_metadata(tampered)
    assert contracts.compute_analysis_digest(
        validated_tampered, policy_module.POLICY_DIGEST, None) != metadata_dict["inputDigest"]


def test_digest_covers_declaration_and_evidence_not_reservation(make_metadata_archive):
    metadata_dict, _archive = make_metadata_archive
    validated = contracts.validate_metadata(metadata_dict)
    base = contracts.compute_analysis_digest(validated, policy_module.POLICY_DIGEST, None)
    changed_licenses = dict(metadata_dict)
    changed_licenses["declaredLicenses"] = [{"scopePath": "calc", "spdx": "MIT"}]
    revalidated = contracts.validate_metadata(changed_licenses)
    assert contracts.compute_analysis_digest(
        revalidated, policy_module.POLICY_DIGEST, None) != base
    changed_evidence = dict(metadata_dict)
    evidence = dict(changed_evidence["securityEvidence"])
    evidence["malwareEvidenceId"] = "other_evidence_9"
    changed_evidence["securityEvidence"] = evidence
    revalidated_evidence = contracts.validate_metadata(changed_evidence)
    assert contracts.compute_analysis_digest(
        revalidated_evidence, policy_module.POLICY_DIGEST, None) != base
    changed_lease = dict(metadata_dict)
    reservation = dict(changed_lease["reservation"])
    reservation["generation"] = 7
    reservation["leaseToken"] = "e" * 64
    changed_lease["reservation"] = reservation
    revalidated_lease = contracts.validate_metadata(changed_lease)
    assert contracts.compute_analysis_digest(
        revalidated_lease, policy_module.POLICY_DIGEST, None) == base


def _reuse_metadata(metadata):
    from uuid import uuid4

    source_run = str(uuid4())
    source_report = str(uuid4())
    source_declaration = "c" * 64 if metadata["declarationDigest"] != "c" * 64 else "d" * 64
    artifacts = []
    for stage in ("EXTRACTION", "TOKEN", "SEMANTIC"):
        artifact = {"binding": {
            "sourceRunId": source_run,
            "sourceInputDigest": "b" * 64,
            "contentSha256": metadata["contentSha256"],
            "materialDigest": metadata["materialDigest"],
            "declarationDigest": source_declaration,
            "stage": stage,
        }, "data": {"fixture": stage}}
        encoded = contracts.canonical(artifact).decode().rstrip("\n")
        artifacts.append({"stage": stage, "sourceReportId": source_report,
                          "sourceStageId": str(uuid4()),
                          "artifactDigest": contracts.sha256_hex(contracts.canonical(artifact)),
                          "artifactJson": encoded})
    metadata["reuse"] = {"sourceRunId": source_run,
                         "sourceInputDigest": "b" * 64,
                         "sourceDeclarationDigest": source_declaration,
                         "sourceMaterialDigest": metadata["materialDigest"],
                         "artifacts": artifacts}
    return metadata


def test_declaration_only_reuse_accepts_three_bound_immutable_artifacts(make_metadata_archive):
    metadata, _archive = make_metadata_archive
    validated = contracts.validate_metadata(_reuse_metadata(dict(metadata)))
    assert validated.reuse is not None
    assert {item["stage"] for item in validated.reuse["artifacts"]} == {
        "EXTRACTION", "TOKEN", "SEMANTIC"}


def test_declaration_only_reuse_rejects_tampered_artifact_digest(make_metadata_archive):
    metadata, _archive = make_metadata_archive
    tampered = _reuse_metadata(dict(metadata))
    artifact = tampered["reuse"]["artifacts"][0]
    artifact["artifactJson"] = artifact["artifactJson"].replace(
        '"fixture":"EXTRACTION"', '"fixture":"ALTERED"')
    try:
        contracts.validate_metadata(tampered)
    except contracts.ContractError as exc:
        assert exc.code == "REFERENCE_MANIFEST_INVALID"
    else:
        raise AssertionError("tampered reuse artifact accepted")
