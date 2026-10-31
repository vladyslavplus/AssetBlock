import unittest
import tempfile
from pathlib import Path

from scripts.code_analysis.contracts import digest
from scripts.pair_verification import pair_rules


def endpoint(fragment_id, body, *, file_id=None, family="family", partition="train", parent_fragment=None,
             language="python"):
    file_id = file_id or fragment_id
    fragment = {"fragmentId": fragment_id, "fileId": file_id, "startByte": 0,
                "endByte": len(body), "sha256": digest(body), "language": language}
    if parent_fragment:
        fragment["parentFragmentId"] = parent_fragment
        fragment["derivation"] = {"transformation": "COMMENT_FORMAT"}
    parent = {"fileId": file_id, "sourceId": "source", "path": file_id + ".py",
              "sha256": digest(b"file:" + body), "familyId": family, "partition": partition,
              "language": language, "normalizedSha256": digest(b"normalized:" + body)}
    return {"fragment": fragment, "parent": parent, "body": body}


class PairRuleTests(unittest.TestCase):
 def test_partition_gate_refuses_validation_and_final_test(self):
    for partition in ("validation", "final-test"):
        with self.assertRaisesRegex(ValueError, "train partition"):
            pair_rules.endpoint_identity(endpoint("a", b"alpha", partition=partition), files={}, aliases={})


 def test_fragment_digest_drift_is_refused(self):
    row = endpoint("a", b"alpha")
    row["body"] = b"changed"
    with self.assertRaisesRegex(ValueError, "digest"):
        pair_rules.endpoint_identity(row, files={}, aliases={})

 def test_source_span_byte_drift_is_refused(self):
    row = endpoint("a", b"x = 1")
    row["fragment"]["sourceSha256"] = row["parent"]["sha256"]
    row["parent"]["bytes"] = len(b"x = 1")
    with tempfile.TemporaryDirectory() as temp:
        source = Path(temp) / "source" / "a.py"
        source.parent.mkdir()
        source.write_bytes(b"x = 2")
        with self.assertRaisesRegex(ValueError, "source byte drift"):
            pair_rules.endpoint_identity(row, files={}, aliases={}, sources_root=temp)

 def test_invalid_fragment_span_is_refused(self):
    row = endpoint("a", b"alpha")
    row["fragment"]["startByte"] = 4
    row["fragment"]["endByte"] = 2
    with self.assertRaisesRegex(ValueError, "span"):
        pair_rules.endpoint_identity(row, files={}, aliases={})


 def test_family_is_not_a_negative_bar_and_same_parent_is(self):
    left = pair_rules.endpoint_identity(endpoint("a", b"x = 1", file_id="file-a"), files={}, aliases={})
    right = pair_rules.endpoint_identity(endpoint("b", b"if x: print(x)", file_id="file-b"), files={}, aliases={})
    self.assertEqual(left["familyId"], right["familyId"])
    pair_rules._validate_negative(left, right)
    derivative = pair_rules.endpoint_identity(
        endpoint("c", b"x = 1", file_id="file-c", parent_fragment="parent"), files={}, aliases={})
    sibling = pair_rules.endpoint_identity(
        endpoint("d", b"if x: print(x)", file_id="file-d", parent_fragment="parent"), files={}, aliases={})
    with self.assertRaisesRegex(ValueError, "same canonical fragment"):
        pair_rules._validate_negative(derivative, sibling)

 def test_normalized_identical_fragments_are_not_negatives(self):
    left = pair_rules.endpoint_identity(endpoint("a", b"x=1", file_id="file-a"), files={}, aliases={})
    right = pair_rules.endpoint_identity(endpoint("b", b"x = 1", file_id="file-b"), files={}, aliases={})
    with self.assertRaisesRegex(ValueError, "normalized-identical"):
        pair_rules._validate_negative(left, right)

 def test_unparsed_negative_is_recorded_with_exact_parse_reason(self):
    anchor = endpoint("a", b"function f() { return 1; }", file_id="file-a", language="typescript")
    positive = endpoint("b", b"function g() { return 2; }", file_id="file-b", language="typescript")
    invalid = endpoint("c", b"function {", file_id="file-c", language="typescript")
    valid_negative = endpoint("d", b"class C {}", file_id="file-d", language="typescript")
    records, exclusions = pair_rules.reviewed_pair_records(
        [{"queryFragmentId": "a", "endpoints": [anchor, positive, invalid, valid_negative]}],
        files=[], aliases={}, sources_root=None, split_files={})
    self.assertEqual(len(records), 2)
    excluded = exclusions["excludedNegativePairs"]
    self.assertEqual(len(excluded), 1)
    self.assertEqual(excluded[0]["language"], "typescript")
    self.assertEqual(excluded[0]["normalizationFailures"][0]["reason"], "PARSE_FAILURE")


 def test_held_out_file_family_and_normalized_content_are_refused(self):
    for kind in ("file", "family", "normalized"):
        anchor = endpoint("anchor", b"x = 1", file_id="anchor", family="anchor-family")
        positive = endpoint("positive", b"x = 2", file_id="positive", family="positive-family")
        negative = endpoint("negative", b"if x: print(x)", file_id="negative", family="negative-family")
        for row, role in ((anchor, "anchor"), (positive, "positive"), (negative, "negative")):
            row["label"] = role
        held = {"fileId": "other", "familyId": "other-family", "partition": "validation",
                "language": "python", "normalizedSha256": "f" * 64}
        expected = {"file": "HELD_OUT_FILE_ID", "family": "HELD_OUT_FAMILY",
                    "normalized": "HELD_OUT_NORMALIZED_FILE"}[kind]
        if kind == "file":
            held["fileId"] = negative["parent"]["fileId"]
        elif kind == "family":
            held["familyId"] = negative["parent"]["familyId"]
        else:
            held["language"] = negative["parent"]["language"]
            held["normalizedSha256"] = negative["parent"]["normalizedSha256"]
        _, exclusions = pair_rules.reviewed_pair_records(
            [{"queryFragmentId": "anchor", "endpoints": [anchor, positive, negative]}],
            files=[], aliases={}, sources_root=None, split_files={"held": held})
        self.assertEqual(exclusions[expected], 1)


 def test_duplicate_pair_reviews_collapse_and_conflicting_labels_fail(self):
    a, b, c = endpoint("a", b"x = 1"), endpoint("b", b"if x: print(x)"), endpoint("c", b"class C: pass")
    triplets = [{"queryFragmentId": "a", "endpoints": [a, b, c]},
                {"queryFragmentId": "a", "endpoints": [a, b, c]}]
    records, exclusions = pair_rules.reviewed_pair_records(
        triplets, files=[], aliases={}, sources_root=None, split_files={})
    self.assertEqual(len(records), 2)
    self.assertEqual(exclusions["DUPLICATE_REVIEW_COLLAPSED"], 2)
    self.assertTrue(all(row["duplicateReviewCount"] == 1 for row in records))
    conflict = [{"queryFragmentId": "a", "endpoints": [a, b, c]},
                {"queryFragmentId": "a", "endpoints": [a, c, b]}]
    with self.assertRaisesRegex(ValueError, "CONFLICTING_PAIR_LABEL"):
        pair_rules.reviewed_pair_records(conflict, files=[], aliases={}, sources_root=None, split_files={})


 def test_identical_positive_cap_drops_deterministically(self):
    rows = []
    for index in range(20):
        rows.append({"pairId": f"{index:064x}", "label": "SIMILAR",
                     "left": {"fragmentSha256": f"{index:064x}"},
                     "right": {"fragmentSha256": f"{index:064x}"}})
    for index in range(10):
        rows.append({"pairId": f"{100+index:064x}", "label": "SIMILAR",
                     "left": {"fragmentSha256": f"{100+index:064x}"},
                     "right": {"fragmentSha256": f"{200+index:064x}"}})
    first, stats = pair_rules.cap_identical_positives(rows)
    second, _ = pair_rules.cap_identical_positives(list(reversed(rows)))
    self.assertEqual([row["pairId"] for row in first], [row["pairId"] for row in second])
    self.assertEqual(stats["byteIdenticalAfter"], 1)
    self.assertLessEqual(stats["byteIdenticalShareAfter"], 0.1)


 def test_unknown_cannot_enter_pair_labels(self):
    anchor = endpoint("a", b"x = 1")
    positive = endpoint("b", b"x = 1", file_id="file-b")
    unknown = endpoint("c", b"class C: pass")
    anchor["label"], positive["label"], unknown["label"] = "anchor", "positive", "unknown"
    records, exclusions = pair_rules.reviewed_pair_records(
        [{"queryFragmentId": "a", "endpoints": [anchor, positive, unknown]}],
        files=[], aliases={}, sources_root=None, split_files={})
    self.assertEqual([row["label"] for row in records], ["SIMILAR"])
    self.assertEqual(exclusions["UNKNOWN_LABEL"], 1)
