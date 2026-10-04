import unittest
from pathlib import Path

from scripts.feasibility_pilot.evidence import validate_run
from scripts.feasibility_pilot.extract import expectation_matched
from scripts.feasibility_pilot.inventory import FixtureFile


def _fixture(**kwargs) -> FixtureFile:
    base = dict(
        fixture_id="js-broken",
        relative_path="broken.js",
        language="javascript",
        dialect="javascript",
        sha256="0" * 64,
        origin="test",
        rights="test",
        source_family="js-broken",
        variant="broken",
        expected_outcome="INVALID_PARSE",
        expected_fragment_ids=[],
        expected_diagnostics=[{"kind": "PARSE_ERROR", "startLine": 3}],
        expected_omissions=[],
        required_case=True,
        diagnostic_only=True,
        training_allowed=False,
        vendor_or_generated=False,
        newline="lf",
        notes="",
        path=Path("."),
        size_bytes=1,
    )
    base.update(kwargs)
    return FixtureFile(**base)


class ExpectationTests(unittest.TestCase):
    def test_matched_invalid_stays_invalid(self) -> None:
        fixture = _fixture()
        result = {
            "actualOutcome": "INVALID_PARSE",
            "operationalFailure": False,
            "namedFragments": [],
            "diagnostics": [{"kind": "PARSE_ERROR", "startLine": 3, "startByte": 40}],
        }
        self.assertTrue(expectation_matched(fixture, result))
        self.assertNotEqual(result["actualOutcome"], "VALID_EXTRACTED")

    def test_same_error_on_valid_fixture_fails(self) -> None:
        fixture = _fixture(expected_outcome="VALID_EXTRACTED", expected_fragment_ids=["sumRange"], diagnostic_only=False, training_allowed=True)
        result = {
            "actualOutcome": "INVALID_PARSE",
            "operationalFailure": False,
            "namedFragments": [],
            "diagnostics": [{"kind": "PARSE_ERROR", "startLine": 3, "startByte": 40}],
        }
        self.assertFalse(expectation_matched(fixture, result))

    def test_crash_is_not_matched_by_parse_error(self) -> None:
        fixture = _fixture()
        result = {"actualOutcome": "FAILED", "operationalFailure": True, "namedFragments": [], "diagnostics": []}
        self.assertFalse(expectation_matched(fixture, result))

    def test_declared_parse_error_omission_must_be_present(self) -> None:
        fixture = _fixture(expected_omissions=[{"reason": "PARSE_ERROR"}])
        matched = {
            "actualOutcome": "INVALID_PARSE",
            "operationalFailure": False,
            "namedFragments": [],
            "diagnostics": [{"kind": "PARSE_ERROR", "startLine": 3, "startByte": 40}],
            "omissions": [{"reason": "PARSE_ERROR", "byteCount": 8}, {"reason": "COMMENT", "byteCount": 0}],
        }
        self.assertTrue(expectation_matched(fixture, matched))
        zero_width = dict(matched)
        zero_width["omissions"] = [{"reason": "PARSE_ERROR", "byteCount": 0}]
        self.assertTrue(expectation_matched(fixture, zero_width))
        missing = dict(matched)
        missing["omissions"] = [{"reason": "COMMENT", "byteCount": 0}]
        self.assertFalse(expectation_matched(fixture, missing))


class EvidenceTests(unittest.TestCase):
    def test_missing_model_evidence_is_not_success(self) -> None:
        with self.assertRaises(Exception):
            validate_run("../escape")


if __name__ == "__main__":
    unittest.main()
