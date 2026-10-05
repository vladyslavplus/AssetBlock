import math
import unittest

from scripts.code_analysis.contracts import digest
from scripts.code_analysis.representations import mapped_chunks, read_vectors, vector_bytes


class CharacterTokenizer:
    cls_token_id = 0
    sep_token_id = 2
    unk_token_id = 3
    def convert_tokens_to_ids(self, token): return 4
    def __call__(self, text, **kwargs):
        return {'input_ids': [ord(c) for c in text], 'offset_mapping': [(i, i + 1) for i in range(len(text))]}


class RepresentationTests(unittest.TestCase):
    def chunks(self, text, tokenizer=None):
        raw = text.encode('utf-8')
        fragment = {'fragmentId': 'b' * 64, 'startByte': 0, 'endByte': len(raw), 'sha256': digest(raw)}
        return raw, mapped_chunks(raw, fragment, tokenizer or CharacterTokenizer())

    def test_actual_framing_and_unicode_offsets(self):
        raw, result = self.chunks('привіт\r\nhello')
        chunk = result['chunks'][0]
        self.assertEqual(chunk['inputIds'][:3], [0, 4, 2])
        self.assertEqual(chunk['inputIds'][-1], 2)
        self.assertEqual(chunk['totalInputTokens'], 17)
        first = chunk['sourceTokenSpans'][0]
        self.assertEqual(raw[first['startByte']:first['endByte']], 'п'.encode('utf-8'))
        self.assertFalse(result['partialMLCoverage'])

    def test_bounded_windows_overlap_and_explicit_omissions(self):
        raw, result = self.chunks('і' * 5000)
        self.assertEqual(len(result['chunks']), 16)
        self.assertTrue(result['partialMLCoverage'])
        for previous, current in zip(result['chunks'], result['chunks'][1:]):
            self.assertEqual(previous['tokenEnd'] - current['tokenStart'], 32)
        self.assertTrue(all(c['totalInputTokens'] <= 256 for c in result['chunks']))
        self.assertEqual(result['retainedUniqueTokenCount'] + result['omittedTokenCount'], 5000)
        self.assertEqual(result['omittedSourceSpan']['startByte'], result['retainedUniqueTokenCount'] * 2)
        self.assertEqual(result['omittedSourceSpan']['endByte'], len(raw))

    def test_unknown_mapping_is_disclosed(self):
        tokenizer = CharacterTokenizer()
        tokenizer.__class__ = type('MissingOffsets', (CharacterTokenizer,), {'__call__': lambda s, text, **kw: {'input_ids': [7], 'offset_mapping': [(0, 0)]}})
        _, result = self.chunks('hello', tokenizer)
        self.assertEqual(result['mappingStatus'], 'UNAVAILABLE_TOKENIZER_OFFSETS')
        self.assertIsNone(result['chunks'][0]['sourceTokenSpans'])

    def test_full_fp32_vectors_and_invalid_payloads(self):
        vector = [1 / math.sqrt(768)] * 768
        raw = vector_bytes([vector])
        self.assertEqual(len(raw), 768 * 4)
        self.assertEqual(len(read_vectors(raw, 1)[0]), 768)
        for bad in ([0.] * 768, [float('nan')] * 768, vector[:8]):
            with self.assertRaises(ValueError): vector_bytes([bad])
        with self.assertRaises(ValueError): read_vectors(raw[:-4], 1)

    def test_span_and_tokenizer_payload_validation(self):
        raw = b'hello'
        for start, end in ((-1, 5), (0, 6), (2, 2), (False, 5)):
            with self.assertRaisesRegex(ValueError, 'half-open'):
                mapped_chunks(raw, {'fragmentId': 'b' * 64, 'startByte': start, 'endByte': end,
                                   'sha256': digest(raw[start:end])}, CharacterTokenizer())
        for payload in ({'input_ids': [-1], 'offset_mapping': [(0, 1)]},
                        {'input_ids': [1], 'offset_mapping': [(0,)]},
                        {'input_ids': [True], 'offset_mapping': [(0, 1)]}):
            tokenizer = type('MalformedTokenizer', (CharacterTokenizer,), {'__call__': lambda s, text, **kw: payload})()
            with self.assertRaises(ValueError): self.chunks('hello', tokenizer)
