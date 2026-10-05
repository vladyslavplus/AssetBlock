import unittest
from types import SimpleNamespace
from unittest.mock import patch

import torch

from scripts.code_analysis.contracts import digest
from scripts.code_analysis.frozen_encoder import independently_accepted, encode_query_only, state_hash, encode_chunks


class TinyFrozen(torch.nn.Module):
    def __init__(self):
        super().__init__()
        self.weight = torch.nn.Parameter(torch.tensor(2.), requires_grad=False)
        self.register_buffer('counter', torch.tensor(0))
        self.eval()

    def forward(self, input_ids, attention_mask):
        hidden = torch.zeros((*input_ids.shape, 768), device=input_ids.device)
        hidden[:, :, 0] = input_ids.float() * self.weight
        hidden[:, :, 1] = 1
        return SimpleNamespace(last_hidden_state=hidden)


def accepted_stamp():
    return {"reviewerApproval": True, "reviewer": "independent", "status": "INDEPENDENTLY_ACCEPTED"}


def query_record(query_id, source, *, stratum="COMMENT_FORMAT"):
    return {
        **accepted_stamp(),
        "derivationId": query_id,
        "outputSha256": digest(source),
        "parentFileId": digest(b"parent-file"),
        "parentSha256": digest(b"parent-bytes"),
        "transformation": stratum,
        "partition": "train",
        "language": "python",
    }


def query_chunk(query_id, source, *, stratum="COMMENT_FORMAT"):
    return {
        "inputIds": [1, 2, 3, 4, 5],
        "framingTokens": 4,
        "queryId": query_id,
        "stratum": stratum,
        "partition": "train",
        "language": "python",
        "parentFileId": digest(b"parent-file"),
        "parentSha256": digest(b"parent-bytes"),
        "querySha256": digest(source),
        "sourceSha256": digest(source),
        "galleryRow": False,
    }


class FrozenTests(unittest.TestCase):
    def test_complete_state_detects_buffer_and_scalar_change(self):
        model = TinyFrozen()
        before = state_hash(model)
        model.counter += 1
        self.assertNotEqual(before, state_hash(model))
        model.weight += 1
        self.assertNotEqual(before, state_hash(model))

    def test_mean_mask_and_all_full_vector_rows(self):
        model = TinyFrozen()
        tokenizer = SimpleNamespace(pad_token_id=0)
        chunks = [{"inputIds": [1, 2, 3, 4, 5], "framingTokens": 4},
                  {"inputIds": [1, 2, 3, 4, 5, 6], "framingTokens": 4}]
        vectors, evidence = encode_chunks(model, tokenizer, chunks)
        self.assertEqual(len(vectors), 2)
        self.assertEqual(len(vectors[0]), 768)
        self.assertAlmostEqual(vectors[0][0] / vectors[0][1], 6., places=5)
        self.assertTrue(evidence["fullStateUnchanged"])
        self.assertEqual(evidence["vectorBytes"], 2 * 768 * 4)

    def test_training_or_grad_enabled_refused(self):
        model = TinyFrozen()
        model.train()
        with self.assertRaises(ValueError):
            encode_chunks(model, SimpleNamespace(pad_token_id=0), [{"inputIds": [1] * 5, "framingTokens": 4}])

    def test_query_only_refuses_unaccepted_drafts_and_gallery_rows(self):
        model = TinyFrozen()
        tokenizer = SimpleNamespace(pad_token_id=0)
        source = b"accepted-query"
        query_id = "a" * 64
        chunks = [query_chunk(query_id, source)]
        records = [query_record(query_id, source)]
        sources = {query_id: source}
        with self.assertRaisesRegex(ValueError, "independently accepted"):
            encode_query_only(model, tokenizer, chunks, accepted_drafts={"reviewerApproval": False, "status": "DRAFT_UNREVIEWED"},
                              accepted_records=records, sources=sources)
        with self.assertRaisesRegex(ValueError, "independently accepted"):
            encode_query_only(model, tokenizer, chunks, accepted_drafts={"reviewerApproval": True, "reviewer": None, "status": "REVIEWED"},
                              accepted_records=records, sources=sources)
        self.assertFalse(independently_accepted({"reviewerApproval": True, "reviewer": "x", "status": "DRAFT_UNREVIEWED"}))
        gallery = [{**chunks[0], "galleryRow": True}]
        with self.assertRaisesRegex(ValueError, "gallery rows"):
            encode_query_only(model, tokenizer, gallery, accepted_drafts=accepted_stamp(),
                              accepted_records=records, sources=sources)
        vectors, evidence = encode_query_only(model, tokenizer, chunks, accepted_drafts=accepted_stamp(),
                                              accepted_records=records, sources=sources)
        self.assertEqual(len(vectors), 1)
        self.assertTrue(evidence["fullStateUnchanged"])

    def test_query_only_requires_bound_accepted_records_before_encoder(self):
        model = TinyFrozen()
        tokenizer = SimpleNamespace(pad_token_id=0)
        source_a, source_b = b"query-a", b"query-b"
        id_a, id_b = "a" * 64, "b" * 64
        record_a = query_record(id_a, source_a)
        chunk_a = query_chunk(id_a, source_a)
        chunk_b = query_chunk(id_b, source_b)
        drafts = accepted_stamp()
        with patch("scripts.code_analysis.frozen_encoder.encode_chunks") as encoder:
            with self.assertRaisesRegex(ValueError, "unaccepted query"):
                encode_query_only(model, tokenizer, [chunk_b], accepted_drafts=drafts,
                                  accepted_records=[record_a], sources={id_a: source_a})
            encoder.assert_not_called()
            with self.assertRaisesRegex(ValueError, "source bytes drifted"):
                encode_query_only(model, tokenizer, [chunk_a], accepted_drafts=drafts,
                                  accepted_records=[record_a], sources={id_a: b"tampered-bytes"})
            encoder.assert_not_called()
            mismatched = {**chunk_a, "stratum": "AST_BOUND_RENAME", "partition": "validation",
                          "parentFileId": digest(b"other-parent")}
            with self.assertRaisesRegex(ValueError, "does not match accepted record"):
                encode_query_only(model, tokenizer, [mismatched], accepted_drafts=drafts,
                                  accepted_records=[record_a], sources={id_a: source_a})
            encoder.assert_not_called()
            with self.assertRaisesRegex(ValueError, "unaccepted query"):
                encode_query_only(model, tokenizer, [chunk_a, chunk_b], accepted_drafts=drafts,
                                  accepted_records=[record_a], sources={id_a: source_a})
            encoder.assert_not_called()
        vectors, evidence = encode_query_only(model, tokenizer, [chunk_a], accepted_drafts=drafts,
                                              accepted_records=[record_a], sources={id_a: source_a})
        self.assertEqual(len(vectors), 1)
        self.assertTrue(evidence["fullStateUnchanged"])
