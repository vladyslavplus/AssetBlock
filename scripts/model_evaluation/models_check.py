"""Read-only readiness check of the manifest model identities (no model is loaded).

Evidence packages are hash-only documents. A path is refused before it is opened if
its name carries a withheld-partition marker; a package that does not hash to the
recorded identity is a mismatch, and a model without a supplied path is reported as
not verified rather than as passing.
"""

from pathlib import Path

from scripts.code_analysis.contracts import canonical, digest, sha
from scripts.code_retrieval import inputs
from scripts.feasibility_pilot.paths import REPO_ROOT, ensure_inside
from scripts.pair_verification import evidence

from . import guards, manifest as manifest_module

MAX_PACKAGE_BYTES = 64 * 1024 * 1024
VERIFIED, NOT_VERIFIED = "VERIFIED", "NOT_VERIFIED"


def check(manifest, package_paths=None, *, root=REPO_ROOT, state_hashes=None, threshold=None):
    manifest_module.validate_manifest(manifest)
    package_paths = package_paths or {}
    unknown = set(package_paths) - set(manifest["models"])
    if unknown:
        raise ValueError("package paths for unknown models: " + ", ".join(sorted(unknown)))
    state_hashes = state_hashes or {}
    unknown = set(state_hashes) - set(manifest["models"])
    if unknown:
        raise ValueError("state hashes for unknown models: " + ", ".join(sorted(unknown)))
    guards.refuse_sealed(paths=list(package_paths.values()))
    report, package_thresholds = {}, {}
    for name, identity in sorted(manifest["models"].items()):
        sha(identity["evidencePackageSha256"])
        entry = {"evidencePackageSha256": identity["evidencePackageSha256"], "status": NOT_VERIFIED,
                 "reason": "no evidence package path supplied"}
        if name in package_paths:
            path = ensure_inside(Path(package_paths[name]), Path(root))
            raw = inputs.read_bytes(path, MAX_PACKAGE_BYTES)
            if digest(raw) != identity["evidencePackageSha256"]:
                raise ValueError("evidence package for %s does not hash to the recorded identity" % name)
            document = inputs.parse_json(raw, "evidence package")
            evidence.validate(document)
            values = manifest_module.package_values(name, document)
            threshold_in_package = values.pop("threshold", None)
            if values != identity["packageBinding"]:
                raise ValueError("selected checkpoint of %s differs from its evidence package" % name)
            if threshold_in_package is not None:
                package_thresholds[name] = threshold_in_package
            entry.update({"status": VERIFIED, "reason": None, "bytes": len(raw),
                          "canonical": canonical(document) == raw})
        if name in state_hashes:
            if state_hashes[name] != identity["stateSha256"]:
                raise ValueError("checkpoint state of %s differs from the frozen state hash" % name)
            entry["stateStatus"] = VERIFIED
        else:
            entry["stateStatus"] = NOT_VERIFIED
            if entry["status"] == VERIFIED:
                entry.update({"status": NOT_VERIFIED, "reason": "no checkpoint state hash supplied"})
        report[name] = entry
    expected = manifest["operatingPoints"]["validationThresholdRuleResult"]["threshold"]
    if any(value != expected for value in package_thresholds.values()):
        raise ValueError("validation threshold in the evidence package differs from the frozen threshold")
    if threshold is not None and threshold != expected:
        raise ValueError("verifier threshold differs from the frozen threshold")
    thresholds = VERIFIED if threshold is not None else NOT_VERIFIED
    return {"models": report, "threshold": thresholds,
            "allVerified": thresholds == VERIFIED and all(row["status"] == VERIFIED for row in report.values())}
