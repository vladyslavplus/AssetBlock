import unittest

from scripts.pair_verification import evidence
from scripts.pair_verification.experiment import build
from scripts.pair_verification.input_layout import layout_pair_ids


class LayoutAndEvidenceTests(unittest.TestCase):
 def test_layout_uses_512_five_framing_tokens_and_asymmetric_budget(self):
    layout = layout_pair_ids(list(range(300)), list(range(300)), cls_id=10, mode_id=11, sep_id=12)
    self.assertEqual(len(layout.input_ids), 512)
    self.assertEqual((layout.content_budget, layout.a_budget, layout.b_budget), (507, 254, 253))
    self.assertEqual(layout.framing_tokens, 5)
    self.assertTrue(layout.truncated)


 def test_evidence_rejects_secret_and_source_text_fields(self):
    self.assertEqual(len(evidence.validate({"outcome": "SUCCEEDED", "counts": {"pairs": 3}})), 64)
    with self.assertRaisesRegex(ValueError, "forbidden field"):
        evidence.validate({"sourceText": "private code"})
    with self.assertRaisesRegex(ValueError, "forbidden field"):
        evidence.validate({"environment": {"password": "hidden"}})


 def test_frozen_configuration_hash_is_stable(self):
    kwargs = {"identity": {"prepare": "a" * 64}, "validation_identity": {"queryCount": 201},
              "token_evidence": {"status": "SUCCEEDED"}}
    first = build(**kwargs)
    second = build(**kwargs)
    self.assertEqual(first[1], second[1])
    self.assertEqual(first[2], second[2])

 def test_candidate_contract_requires_token_union(self):
    value, _, _ = build(identity={}, validation_identity={}, token_evidence={"status": "SUCCEEDED"})
    self.assertEqual(value["candidateContract"]["mode"], "TOKEN_UNION")
    self.assertEqual(value["candidateContract"]["maximumUniqueFiles"], 40)
    with self.assertRaisesRegex(ValueError, "successful token branch"):
        build(identity={}, validation_identity={}, token_evidence={"status": "BLOCKED"})
