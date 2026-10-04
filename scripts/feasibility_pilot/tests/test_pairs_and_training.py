import unittest

from scripts.feasibility_pilot.inventory import FixtureFile, PairCase, training_allowlist
from scripts.feasibility_pilot.pair_layout import encode_pair_ids


class FakeTokenizer:
    cls_token = "<s>"
    sep_token = "</s>"
    unk_token_id = 3

    def convert_tokens_to_ids(self, token: str) -> int:
        return {"<s>": 0, "<encoder-only>": 4, "</s>": 2}.get(token, 3)

    def encode(self, text: str, add_special_tokens: bool = False) -> list[int]:
        del add_special_tokens
        return list(range(10, 10 + max(1, len(text.split()))))


class PairLayoutTests(unittest.TestCase):
    def test_five_framing_tokens_single_separator_between(self) -> None:
        encoded = encode_pair_ids(FakeTokenizer(), "one two three", "four five", 32)
        self.assertEqual(encoded.framing_overhead, 5)
        self.assertEqual(encoded.input_ids[0], 0)
        self.assertEqual(encoded.input_ids[1], 4)
        self.assertEqual(encoded.input_ids[2], 2)
        self.assertEqual(encoded.separator_positions, [2, 2 + encoded.a_retained + 1, len(encoded.input_ids) - 1])
        self.assertEqual(encoded.input_ids[encoded.a_span[1]], 2)
        self.assertEqual(len(encoded.input_ids), encoded.framing_overhead + encoded.a_retained + encoded.b_retained)
        self.assertEqual(encoded.attention_mask, [1] * len(encoded.input_ids))

    def test_truncation_at_budget(self) -> None:
        encoded = encode_pair_ids(FakeTokenizer(), "a " * 200, "b " * 200, 20)
        self.assertTrue(encoded.truncated)
        self.assertLessEqual(len(encoded.input_ids), 20)


class TrainingExclusionTests(unittest.TestCase):
    def _file(self, fid: str, allowed: bool, diagnostic: bool, outcome: str = "VALID_EXTRACTED") -> FixtureFile:
        return FixtureFile(
            fixture_id=fid,
            relative_path=f"{fid}.js",
            language="javascript",
            dialect="javascript",
            sha256="0" * 64,
            origin="test",
            rights="test",
            source_family="fam",
            variant="base",
            expected_outcome=outcome,
            expected_fragment_ids=["fn"],
            expected_diagnostics=[],
            expected_omissions=[],
            required_case=True,
            diagnostic_only=diagnostic,
            training_allowed=allowed,
            vendor_or_generated=False,
            newline="lf",
            notes="",
            path=__import__("pathlib").Path("."),
            size_bytes=1,
        )

    def test_invalid_and_diagnostic_excluded(self) -> None:
        files = [
            self._file("ok", True, False),
            self._file("broken", False, True, "INVALID_PARSE"),
            self._file("diag", False, True),
        ]
        pairs = [
            PairCase("p1", "javascript", "ok", "ok", "positive", False, True, "fam", ""),
            PairCase("p2", "javascript", "broken", "ok", "positive", True, False, "fam", ""),
        ]
        allowed = training_allowlist(files, pairs)
        self.assertEqual(allowed, {"ok"})

    def test_training_allowed_parse_failure_is_excluded(self) -> None:
        files = [
            self._file("ok", True, False),
            self._file("failed", True, False),
        ]
        pairs = [
            PairCase("p1", "javascript", "ok", "ok", "positive", False, True, "fam", ""),
            PairCase("p2", "javascript", "failed", "ok", "positive", False, True, "fam", ""),
        ]
        extracted = {"ok"}
        allowed = training_allowlist(files, pairs, extracted)
        self.assertEqual(allowed, {"ok"})
        self.assertNotIn("failed", allowed)


class RetrievalTripletTests(unittest.TestCase):
    def _pair(self, pair_id: str, left: str, right: str, label: str, notes: str = "") -> PairCase:
        return PairCase(pair_id, "javascript", left, right, label, False, True, "fam", notes)

    def test_missing_first_positive_selects_another_complete_triplet(self) -> None:
        from scripts.feasibility_pilot.inventory import build_retrieval_triplets

        pairs = [
            self._pair("javascript-pos-copy", "javascript-base", "javascript-copy", "positive"),
            self._pair("javascript-pos-rename", "javascript-base", "javascript-rename", "positive"),
            self._pair("javascript-neg-unrelated", "javascript-base", "javascript-unrelated", "negative", "unrelated negative vs query"),
            self._pair("javascript-neg-hard", "javascript-base", "javascript-unrelated2", "negative", "second unrelated negative vs query"),
        ]
        texts = {
            "javascript-base": "query",
            "javascript-rename": "pos",
            "javascript-unrelated": "negA",
            "javascript-unrelated2": "negB",
        }
        allowed = set(texts)
        files = [type("F", (), {"fixture_id": fid, "sha256": f"h-{fid}"})() for fid in texts]
        triplets = build_retrieval_triplets(pairs, texts, allowed, files)
        self.assertEqual(len(triplets), 1)
        item = triplets[0]
        self.assertEqual(item["queryId"], "javascript-base")
        self.assertEqual(item["positiveId"], "javascript-rename")
        self.assertEqual(item["negativeAId"], "javascript-unrelated")
        self.assertEqual(item["negativeBId"], "javascript-unrelated2")
        self.assertNotEqual(item["negativeA"], item["negativeB"])
        self.assertIn("unrelated", item["negativeALabel"])
        self.assertIn("second unrelated", item["negativeBLabel"])

    def test_missing_negative_is_not_replaced_by_query_or_positive(self) -> None:
        from scripts.feasibility_pilot.inventory import build_retrieval_triplets

        pairs = [
            self._pair("javascript-pos-copy", "javascript-base", "javascript-copy", "positive"),
            self._pair("javascript-neg-unrelated", "javascript-base", "javascript-unrelated", "negative"),
            self._pair("javascript-neg-hard", "javascript-base", "javascript-unrelated2", "negative"),
        ]
        texts = {"javascript-base": "query", "javascript-copy": "pos", "javascript-unrelated": "negA"}
        triplets = build_retrieval_triplets(pairs, texts, set(texts) | {"javascript-unrelated2"})
        self.assertEqual(triplets, [])
        self.assertNotIn("query", [item.get("negativeA") for item in triplets])
        self.assertNotIn("pos", [item.get("negativeB") for item in triplets])


class FixtureProvenanceTests(unittest.TestCase):
    def test_structural_is_not_a_training_negative_and_unrelateds_are_independent(self) -> None:
        from scripts.feasibility_pilot.inventory import load_code_fixtures, load_pairs

        files = {item.fixture_id: item for item in load_code_fixtures()}
        pairs = load_pairs()
        self.assertEqual(len(files), 52)
        self.assertEqual(len(pairs), 30)
        for item in files.values():
            if item.variant == "structural":
                self.assertTrue(item.diagnostic_only)
                self.assertFalse(item.training_allowed)
                self.assertEqual(item.source_family, f"{item.language}-sum")
        for pair in pairs:
            if pair.label == "negative" and pair.training_allowed:
                self.assertFalse(files[pair.right_id].variant == "structural")
                self.assertNotEqual(files[pair.left_id].source_family, files[pair.right_id].source_family)
                self.assertIn(files[pair.right_id].variant, {"unrelated", "unrelated2"})
            if pair.pair_id.endswith("-neg-hard"):
                self.assertEqual(pair.left_id, f"{pair.language}-base")
                self.assertEqual(pair.right_id, f"{pair.language}-unrelated2")
        for language in ("javascript", "typescript", "python", "java", "csharp"):
            first = files[f"{language}-unrelated"]
            second = files[f"{language}-unrelated2"]
            structural = files[f"{language}-structural"]
            self.assertNotEqual(first.source_family, second.source_family)
            self.assertNotEqual(first.rights, second.rights)
            self.assertNotEqual(first.origin, second.origin)
            self.assertNotEqual(first.sha256, second.sha256)
            self.assertNotEqual(first.sha256, structural.sha256)
            self.assertEqual(first.source_family, f"{language}-unrelated")
            self.assertEqual(second.source_family, f"{language}-unrelated2")


if __name__ == "__main__":
    unittest.main()
