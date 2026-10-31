import unittest
from types import SimpleNamespace

from scripts.code_analysis.contracts import canonical, digest
from scripts.code_analysis.splits import normalized_hash
from scripts.pair_verification import frozen_data, pair_rules, prepare, receipt


def package():
    return {"schema": "pair-verification-mined-candidate-package-v1", "candidates": [
        {"candidateId": "c1", "left": {"fragmentSha256": "a" * 64},
         "right": {"fragmentSha256": "b" * 64}}]}


def raw_receipt(candidate_package, *, label="DISSIMILAR", left=None):
    return canonical({"schema": receipt.SCHEMA, "candidatePackageSha256": digest(canonical(candidate_package)),
                      "labels": [{"candidateId": "c1", "leftFragmentSha256": left or "a" * 64,
                                  "rightFragmentSha256": "b" * 64, "label": label, "rationale": "reviewed",
                                  "reviewerSessionId": "review-session", "reviewedOn": "2026-10-30"}]})


class ReceiptTests(unittest.TestCase):
 def test_regenerated_package_mismatch_fails_closed(self):
    with self.assertRaisesRegex(ValueError, "regenerated candidate package"):
        prepare._require_regenerated_package({"candidates": []}, canonical({"candidates": [{"id": "changed"}]}))

 def test_receipt_package_hash_and_fragment_binding(self):
    p = package()
    with self.assertRaisesRegex(ValueError, "package hash"):
        receipt.load_receipt(raw_receipt(p), candidate_package_raw=b"different", candidate_package=p)
    with self.assertRaisesRegex(ValueError, "fragment hash"):
        receipt.load_receipt(raw_receipt(p, left="c" * 64), candidate_package_raw=canonical(p), candidate_package=p)


 def test_wrong_label_fails_and_ambiguous_is_excluded(self):
    p = package()
    with self.assertRaisesRegex(ValueError, "label"):
        receipt.load_receipt(raw_receipt(p, label="UNKNOWN"), candidate_package_raw=canonical(p), candidate_package=p)
    loaded = receipt.load_receipt(raw_receipt(p, label="AMBIGUOUS"), candidate_package_raw=canonical(p), candidate_package=p)
    candidate = {"candidateId": "c1", "anchorFragmentId": "anchor", "rank": 1}
    admitted, exclusions = receipt.admit_dissimilar([candidate], loaded)
    self.assertEqual(admitted, [])
    self.assertEqual(exclusions["ambiguous"], 1)


 def test_missing_receipt_admits_zero_mined_negatives(self):
    candidates = [{"candidateId": "a", "anchorFragmentId": "x", "rank": 1},
                  {"candidateId": "b", "anchorFragmentId": "x", "rank": 2}]
    admitted, exclusions = receipt.admit_dissimilar(candidates, None)
    self.assertEqual(admitted, [])
    self.assertEqual(exclusions["admitted"], 0)
    self.assertEqual(exclusions["noReceipt"], 2)

 def test_package_receipt_admission_mined_pair_and_training_layout(self):
    bodies = {"anchor": b"left_value = 1", "candidate": b"if right_value: print(right_value)"}
    catalog = {}
    for fragment_id, body in bodies.items():
        fragment = {"fragmentId": fragment_id, "fileId": "file-" + fragment_id,
                    "sha256": digest(body), "language": "python", "startByte": 0, "endByte": len(body)}
        parent = {"fileId": fragment["fileId"], "sourceId": "source-" + fragment_id,
                  "path": fragment_id + ".py", "sha256": digest(b"source:" + body),
                  "familyId": "family-" + fragment_id, "partition": "train", "language": "python"}
        catalog[fragment_id] = {"fragment": fragment, "parent": parent, "body": body,
                                "chunks": [{"inputIds": [0, 1, 2, 7, 8, 2], "framingTokens": 4}],
                                "originalTokenCount": 2,
                                "normalizedFragmentSha256": normalized_hash(body, "python")}
    selected, _ = frozen_data.nearest_candidates(["anchor"], catalog,
                                                  {key: [[1.0, 0.0]] for key in catalog}, [], maximum=1)
    package_value = prepare._candidate_package(selected, catalog)
    package_raw = canonical(package_value)
    row = package_value["candidates"][0]
    receipt_raw = canonical({"schema": receipt.SCHEMA,
                             "candidatePackageSha256": digest(package_raw),
                             "labels": [{"candidateId": row["candidateId"],
                                         "leftFragmentSha256": row["left"]["fragmentSha256"],
                                         "rightFragmentSha256": row["right"]["fragmentSha256"],
                                         "label": "DISSIMILAR", "rationale": "independently reviewed",
                                         "reviewerSessionId": "review-session",
                                         "reviewedOn": "2026-10-30"}]})
    binding = receipt.load_receipt(receipt_raw, candidate_package_raw=package_raw,
                                   candidate_package=package_value)
    admitted, stats = receipt.admit_dissimilar(selected, binding)
    self.assertEqual(stats["admitted"], 1)
    pair = prepare._mined_pair(admitted[0], catalog, binding["labels"][row["candidateId"]],
                               sources_root=None, aliases={})
    training_records, _ = pair_rules.cap_identical_positives([pair])
    tokenizer = SimpleNamespace(cls_token_id=0, sep_token_id=2,
                                convert_tokens_to_ids=lambda token: 1)
    encoded = {"fragments": catalog, "vectors": {key: [[1.0, 0.0]] for key in catalog}}
    rows, stats = prepare._make_training_rows(training_records, encoded, tokenizer)
    self.assertEqual(len(rows), 1)
    self.assertEqual(rows[0]["label"], "DISSIMILAR")
    self.assertEqual(rows[0]["review"]["reviewerSessionId"], "review-session")
    self.assertEqual(stats["pairCount"], 1)
