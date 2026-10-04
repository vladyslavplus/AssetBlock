import json
import tempfile
import unittest
from pathlib import Path

from scripts.feasibility_pilot.dependency_approval import (
    build_preflight_dependency_manifest,
    evaluate_installed_scanner,
    evaluate_packages,
)


ALLOWED = ["MIT", "Apache-2.0", "BSD-3-Clause", "CC-BY-4.0"]


def _write_graph(tmp: Path) -> tuple[Path, Path]:
    lock = tmp / "scancode-lock.txt"
    inventory = tmp / "inventory.json"
    lock.write_text("requests==2.34.2\ncertifi==2026.7.22\n", encoding="utf-8")
    inventory.write_text(
        json.dumps(
            {
                "packages": [
                    {"name": "requests", "version": "2.34.2", "license": "Apache-2.0", "sha256": ""},
                    {"name": "certifi", "version": "2026.7.22", "license": "MPL-2.0", "sha256": ""},
                ]
            }
        ),
        encoding="utf-8",
    )
    return lock, inventory


EXCEPTIONS = [
    {
        "ecosystem": "pypi",
        "name": "certifi",
        "versions": ["2026.7.22"],
        "license": "MPL-2.0",
        "reason": "Isolated scanner CA bundle under MPL-2.0 for the feasibility prefix only.",
        "reviewedOn": "2026-10-04",
    }
]


class DependencyApprovalTests(unittest.TestCase):
    def test_successful_scanner_with_unapproved_delta_is_blocked(self) -> None:
        result = evaluate_packages(
            [
                {"name": "requests", "version": "2.34.2", "license": "Apache-2.0"},
                {"name": "certifi", "version": "2026.7.22", "license": "MPL-2.0"},
            ],
            allowed_licenses=ALLOWED,
            exceptions=[],
        )
        self.assertFalse(result["approved"])
        self.assertEqual(result["gaps"][0]["name"], "certifi")

    def test_fully_covered_exact_graph_is_ready(self) -> None:
        result = evaluate_packages(
            [
                {"name": "requests", "version": "2.34.2", "license": "Apache-2.0"},
                {"name": "certifi", "version": "2026.7.22", "license": "MPL-2.0"},
            ],
            allowed_licenses=ALLOWED,
            exceptions=EXCEPTIONS,
        )
        self.assertTrue(result["approved"])
        self.assertEqual(result["gaps"], [])

    def test_version_mismatch_and_missing_evidence_are_blocked(self) -> None:
        mismatch = evaluate_packages(
            [{"name": "certifi", "version": "2026.7.22", "license": "MPL-2.0"}],
            allowed_licenses=ALLOWED,
            exceptions=[
                {
                    "ecosystem": "pypi",
                    "name": "certifi",
                    "versions": ["2024.1.1"],
                    "license": "MPL-2.0",
                    "reason": "Wrong version on purpose for the mismatch test case.",
                    "reviewedOn": "2026-10-04",
                }
            ],
        )
        self.assertFalse(mismatch["approved"])

        missing = evaluate_packages(
            [{"name": "mystery", "version": "1.0.0", "license": None}],
            allowed_licenses=ALLOWED,
            exceptions=[],
        )
        self.assertFalse(missing["approved"])
        self.assertIn("unknown", missing["gaps"][0]["reason"])

        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            lock = root / "missing-lock.txt"
            inventory = root / "inventory.json"
            inventory.write_text(json.dumps({"packages": []}), encoding="utf-8")
            result = evaluate_installed_scanner(
                lock_path=lock,
                inventory_path=inventory,
                installed_packages=[{"name": "certifi", "version": "2026.7.22"}],
                exceptions=[],
                policy={"allowedLicenses": ALLOWED},
            )
            self.assertFalse(result["approved"])
            self.assertIn("lock file is missing", result["gaps"][0]["reason"])


class LiveGraphGateTests(unittest.TestCase):
    def test_covered_lock_with_extra_installed_package_is_blocked(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            lock, inventory = _write_graph(Path(tmp))
            result = evaluate_installed_scanner(
                lock_path=lock,
                inventory_path=inventory,
                installed_packages=[
                    {"name": "requests", "version": "2.34.2"},
                    {"name": "certifi", "version": "2026.7.22"},
                    {"name": "unexpected", "version": "1.0.0"},
                ],
                exceptions=EXCEPTIONS,
                policy={"allowedLicenses": ALLOWED},
            )
            self.assertFalse(result["approved"])
            self.assertFalse(result["environmentReady"])
            self.assertTrue(any("not in the reviewed lock" in item["reason"] for item in result["gaps"]))
            self.assertTrue(result["evidenceHashes"]["lockSha256IsNotInstalledFileHash"])

    def test_covered_lock_with_different_installed_version_is_blocked(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            lock, inventory = _write_graph(Path(tmp))
            result = evaluate_installed_scanner(
                lock_path=lock,
                inventory_path=inventory,
                installed_packages=[
                    {"name": "requests", "version": "2.34.2"},
                    {"name": "certifi", "version": "2024.1.1"},
                ],
                exceptions=EXCEPTIONS,
                policy={"allowedLicenses": ALLOWED},
            )
            self.assertFalse(result["approved"])
            self.assertTrue(any("does not match the reviewed lock" in item["reason"] for item in result["gaps"]))

    def test_missing_scanner_interpreter_is_blocked(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            lock, inventory = _write_graph(Path(tmp))
            result = evaluate_installed_scanner(
                lock_path=lock,
                inventory_path=inventory,
                interpreter_path=Path(tmp) / "missing-python.exe",
                exceptions=EXCEPTIONS,
                policy={"allowedLicenses": ALLOWED},
            )
            self.assertFalse(result["approved"])
            self.assertIn("scanner interpreter is missing", result["gaps"][0]["reason"])

    def test_matching_covered_graph_is_approved(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            lock, inventory = _write_graph(Path(tmp))
            result = evaluate_installed_scanner(
                lock_path=lock,
                inventory_path=inventory,
                installed_packages=[
                    {"name": "requests", "version": "2.34.2"},
                    {"name": "certifi", "version": "2026.7.22"},
                ],
                exceptions=EXCEPTIONS,
                policy={"allowedLicenses": ALLOWED},
            )
            self.assertTrue(result["approved"])
            self.assertTrue(result["environmentReady"])
            self.assertEqual(result["gaps"], [])
            self.assertIsNone(result["environment"].get("installedFileHash"))
            self.assertTrue(result["evidenceHashes"]["lockSha256IsNotInstalledFileHash"])


class PreflightDependencyManifestTests(unittest.TestCase):
    def _proposed(self, tmp: Path, status: str) -> Path:
        path = tmp / "proposed-dependency-exceptions.json"
        path.write_text(json.dumps({"status": status}), encoding="utf-8")
        return path

    def test_approved_installed_graph_is_not_pending_exception(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            lock, inventory = _write_graph(root)
            (root / "scancode.exe").write_bytes(b"not-used")
            approval = evaluate_installed_scanner(
                lock_path=lock,
                inventory_path=inventory,
                installed_packages=[
                    {"name": "requests", "version": "2.34.2"},
                    {"name": "certifi", "version": "2026.7.22"},
                ],
                exceptions=EXCEPTIONS,
                policy={"allowedLicenses": ALLOWED},
            )
            manifest = build_preflight_dependency_manifest(
                {"scancode": {"package": "scancode-toolkit", "version": "32.5.0"}},
                approval=approval,
                proposed_exception_path=self._proposed(root, "RECORDED_IN_ROOT_EXCEPTIONS"),
            )
            self.assertEqual(manifest["scancodeStatus"], "APPROVED_INSTALLED")
            self.assertTrue(manifest["environmentGraphApproved"])
            self.assertEqual(manifest["proposedExceptionStatus"], "RECORDED_IN_ROOT_EXCEPTIONS")
            self.assertNotEqual(manifest["proposedExceptionStatus"], "REVIEW_READY_NOT_APPROVED")
            self.assertIsNone(manifest["policyCompliant"])
            self.assertTrue(manifest["executablePresenceIsNotPolicyCompliance"])
            self.assertNotEqual(manifest["scancodeStatus"], "BLOCKED_PENDING_EXCEPTION")

    def test_unapproved_observed_graph_is_blocked_despite_executable(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            lock, inventory = _write_graph(root)
            (root / "scancode.exe").write_bytes(b"not-used")
            approval = evaluate_installed_scanner(
                lock_path=lock,
                inventory_path=inventory,
                installed_packages=[
                    {"name": "requests", "version": "2.34.2"},
                    {"name": "certifi", "version": "2026.7.22"},
                    {"name": "unexpected", "version": "1.0.0"},
                ],
                exceptions=EXCEPTIONS,
                policy={"allowedLicenses": ALLOWED},
            )
            manifest = build_preflight_dependency_manifest(
                {"scancode": {"package": "scancode-toolkit", "version": "32.5.0"}},
                approval=approval,
                proposed_exception_path=self._proposed(root, "RECORDED_IN_ROOT_EXCEPTIONS"),
            )
            self.assertEqual(manifest["scancodeStatus"], "BLOCKED")
            self.assertFalse(manifest["environmentGraphApproved"])
            self.assertIsNone(manifest["policyCompliant"])
            self.assertTrue(any("not in the reviewed lock" in str(item.get("reason")) for item in manifest["gaps"]))

    def test_missing_scanner_is_not_yet_observed(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            lock, inventory = _write_graph(root)
            approval = evaluate_installed_scanner(
                lock_path=lock,
                inventory_path=inventory,
                interpreter_path=root / "missing-python.exe",
                exceptions=EXCEPTIONS,
                policy={"allowedLicenses": ALLOWED},
            )
            manifest = build_preflight_dependency_manifest(
                {"scancode": {"package": "scancode-toolkit", "version": "32.5.0"}},
                approval=approval,
                proposed_exception_path=self._proposed(root, "REVIEW_READY_NOT_APPROVED"),
            )
            self.assertEqual(manifest["scancodeStatus"], "NOT_YET_OBSERVED")
            self.assertFalse(manifest["environmentGraphApproved"])
            self.assertFalse(manifest["observed"])
            self.assertIsNone(manifest["policyCompliant"])
            self.assertEqual(manifest["proposedExceptionStatus"], "REVIEW_READY_NOT_APPROVED")


class DeltaPackageTests(unittest.TestCase):
    def test_delta_is_recorded_not_self_approved_as_review(self) -> None:
        path = Path("scripts/feasibility_pilot/config/scancode-dependency-delta.json")
        payload = json.loads(path.read_text(encoding="utf-8"))
        self.assertEqual(payload["status"], "RECORDED_IN_ROOT_EXCEPTIONS")
        self.assertEqual(payload["reviewedOn"], "2026-10-04")
        for item in payload["proposedExceptions"]:
            self.assertEqual(item["reviewedOn"], "2026-10-04")
            self.assertEqual(item["status"], "RECORDED")


if __name__ == "__main__":
    unittest.main()
