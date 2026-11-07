import tempfile
import unittest
from pathlib import Path

import torch
from transformers import RobertaTokenizerFast

from scripts.code_analysis.contracts import digest
from scripts.code_retrieval.tests import _tiny_encoder as tiny
from scripts.model_evaluation import candidates, guards, pipelines
from scripts.model_evaluation.components import Components, TorchPairVerifier, TorchRetrievalEncoder
from scripts.model_evaluation.tests._pipeline import ShingleTokenScorer, Workspace, fid, full_identity
from scripts.pair_verification.architecture import JointVerifier


class TinyEncoderTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.temp = tempfile.TemporaryDirectory()
        cls.support = Path(cls.temp.name) / "upstream"
        tiny.write_upstream(cls.support)
        cls.tokenizer = RobertaTokenizerFast.from_pretrained(str(cls.support), local_files_only=True)

    @classmethod
    def tearDownClass(cls):
        cls.temp.cleanup()

    def encoder(self, seed):
        model = tiny.build_model(self.support, seed=seed)
        for parameter in model.parameters():
            parameter.requires_grad_(False)
        return TorchRetrievalEncoder(self.tokenizer, model, full_identity("e%d" % (seed * 3)))

    def components(self):
        verifier_model = JointVerifier(tiny.build_model(self.support, seed=2))
        verifier_model.eval()
        for parameter in verifier_model.parameters():
            parameter.requires_grad_(False)
        return Components(token_scorer=ShingleTokenScorer(), e0=self.encoder(0), e3=self.encoder(1),
                          verifier=TorchPairVerifier(verifier_model, self.tokenizer.pad_token_id,
                                                     torch.device("cpu"), full_identity("verifier")))

    def test_encoder_windows_and_vectors_are_deterministic_and_normalised(self):
        raw = b"def f(value):\n    return value + 1\n"
        first, second = self.encoder(0), self.encoder(0)
        file = {"fileId": digest(raw), "sha256": digest(raw), "language": "python"}
        windows = first.windows(raw, file)
        self.assertEqual(windows["omittedTokenCount"], 0)
        self.assertEqual(windows["chunks"][0]["framingTokens"], 4)
        vectors = first.embed(windows["chunks"])
        self.assertEqual(vectors, second.embed(second.windows(raw, file)["chunks"]))
        self.assertAlmostEqual(sum(value * value for value in vectors[0]), 1.0, places=4)
        self.assertNotEqual(vectors, self.encoder(1).embed(windows["chunks"]))

    def test_a_fragment_that_cannot_be_mapped_is_counted_apart_from_omitted_tokens(self):
        from unittest.mock import patch
        from scripts.code_analysis import representations
        raw = b"def a(x):\n    return x + 1\n\n\ndef b(y):\n    return y * 2\n"
        file = {"fileId": digest(raw), "sha256": digest(raw), "language": "python"}
        with patch.object(representations, "mapped_chunks", side_effect=ValueError("no mapping")):
            windows = self.encoder(0).windows(raw, file)
        self.assertEqual((windows["omittedFragmentCount"], windows["omittedTokenCount"], windows["chunks"]),
                         (2, 0, []))

    def test_verifier_probabilities_are_deterministic_and_bounded(self):
        verifier = self.components().verifier
        rows = [(0, 5, 2, 7, 8, 9, 2, 7, 8, 9, 2), (0, 5, 2, 7, 8, 2, 11, 12, 2)]
        first = verifier.probabilities(rows)
        self.assertEqual(first, self.components().verifier.probabilities(rows))
        self.assertTrue(all(0.0 <= value <= 1.0 for value in first))

    def test_full_candidate_stage_runs_with_the_tiny_models_and_repeats_exactly(self):
        ws = Workspace(self)
        prepared, cands = ws.full_run(components=self.components())
        again = ws.candidates(run_id="cand-02", components=self.components())
        document = guards.run_dir("cand-01", ws.run_root) / candidates.CANDIDATES_NAME
        import json
        first = json.loads(document.read_bytes())
        second = json.loads((guards.run_dir("cand-02", ws.run_root) / candidates.CANDIDATES_NAME).read_bytes())
        for query in (first, second):
            for record in query["queries"].values():
                record.pop("stageSeconds")
                for pipeline in record["pipelines"].values():
                    pipeline.pop("timingsSeconds")
        self.assertEqual(first["queries"], second["queries"])
        record = first["queries"]["qa"]["pipelines"][pipelines.FINE_TUNED_RETRIEVAL_PLUS_VERIFIER]
        self.assertTrue(all(0.0 <= row["verifierScore"] <= 1.0 for row in record["candidates"]))
        self.assertEqual(first["queries"]["qa"]["pipelines"][pipelines.PRODUCT]["candidates"][0]["fileId"] in
                         {fid("g1"), fid("g1b")}, True)
        self.assertEqual(again["outcome"], "SUCCEEDED")
        self.assertEqual(ws.score(prepared, cands)["outcome"], "SUCCEEDED")


if __name__ == "__main__":
    unittest.main()
