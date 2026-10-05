"""Deterministic encoder framing, mapped windows and complete FP32 vector bytes."""

import math
import struct

from .contracts import canonical, digest

CHUNKING = "encoder-only-256-overlap32-max16-v1"


def mapped_chunks(raw, fragment, tokenizer):
    start_byte, end_byte = fragment['startByte'], fragment['endByte']
    if type(start_byte) is not int or type(end_byte) is not int or not 0 <= start_byte < end_byte <= len(raw):
        raise ValueError('invalid half-open source span')
    if digest(raw[fragment['startByte']:fragment['endByte']]) != fragment['sha256']:
        raise ValueError('fragment slice drift')
    text = raw[fragment['startByte']:fragment['endByte']].decode('utf-8')
    encoded = tokenizer(text, add_special_tokens=False, return_offsets_mapping=True, truncation=False)
    ids, offsets = encoded['input_ids'], encoded['offset_mapping']
    if not isinstance(ids, (list, tuple)) or not isinstance(offsets, (list, tuple)) or len(ids) != len(offsets) or not ids:
        raise ValueError('empty or inconsistent tokenizer output')
    if any(type(token) is not int or token < 0 for token in ids): raise ValueError('invalid tokenizer ID')
    if any(not isinstance(offset, (list, tuple)) or len(offset) != 2 for offset in offsets):
        raise ValueError('malformed tokenizer offsets')
    cls, sep = tokenizer.cls_token_id, tokenizer.sep_token_id
    marker = tokenizer.convert_tokens_to_ids('<encoder-only>')
    if any(not isinstance(x, int) or x < 0 for x in (cls, sep, marker)) or marker == tokenizer.unk_token_id:
        raise ValueError('actual encoder-only framing unavailable')
    byte_offsets = [0]
    for char in text: byte_offsets.append(byte_offsets[-1] + len(char.encode('utf-8')))
    mapping_ok = all(type(a) is int and type(b) is int and 0 <= a < b <= len(text) for a, b in offsets)
    capacity, overlap, maximum = 252, 32, 16
    windows, start = [], 0
    while start < len(ids) and len(windows) < maximum:
        end = min(start + capacity, len(ids))
        source_spans = None
        if mapping_ok:
            source_spans = [{'startByte': fragment['startByte'] + byte_offsets[a], 'endByte': fragment['startByte'] + byte_offsets[b]} for a, b in offsets[start:end]]
        payload = ids[start:end]
        windows.append({'fragmentId': fragment['fragmentId'], 'chunkOrdinal': len(windows), 'tokenStart': start, 'tokenEnd': end,
                        'inputIds': [cls, marker, sep, *payload, sep], 'framingTokens': 4, 'totalInputTokens': len(payload) + 4,
                        'sourceTokenSpans': source_spans, 'mappingStatus': 'EXACT_TOKENIZER_CHARACTER_OFFSETS' if mapping_ok else 'UNAVAILABLE_TOKENIZER_OFFSETS',
                        'representationSha256': digest(canonical([CHUNKING, payload, source_spans])), 'chunking': CHUNKING})
        if end == len(ids): break
        start = end - overlap
    retained = windows[-1]['tokenEnd']
    omitted = len(ids) - retained
    omitted_span = None
    if omitted and mapping_ok:
        omitted_span = {'startByte': fragment['startByte'] + byte_offsets[offsets[retained][0]], 'endByte': fragment['endByte']}
    return {'fragmentId': fragment['fragmentId'], 'originalTokenCount': len(ids), 'retainedUniqueTokenCount': retained,
            'omittedTokenCount': omitted, 'omittedSourceSpan': omitted_span, 'partialMLCoverage': omitted > 0,
            'mappingStatus': 'EXACT_TOKENIZER_CHARACTER_OFFSETS' if mapping_ok else 'UNAVAILABLE_TOKENIZER_OFFSETS', 'chunks': windows}


def vector_bytes(vectors, dimension=768):
    """Full little-endian FP32 rows; invalid and preview-only vectors are rejected."""
    if not vectors: raise ValueError('empty vector payload')
    output = bytearray()
    for vector in vectors:
        if len(vector) != dimension: raise ValueError('vector dimension mismatch')
        if any(not isinstance(x, (int, float)) or not math.isfinite(x) for x in vector): raise ValueError('nonfinite vector')
        norm = math.sqrt(sum(x * x for x in vector))
        if norm == 0 or abs(norm - 1) > 1e-5: raise ValueError('nonzero normalized vector required')
        try: row = struct.pack('<' + 'f' * dimension, *vector)
        except (OverflowError, struct.error) as exc: raise ValueError('FP32 vector overflow') from exc
        if not all(math.isfinite(x) for x in struct.unpack('<' + 'f' * dimension, row)): raise ValueError('nonfinite FP32 vector')
        output.extend(row)
    return bytes(output)


def read_vectors(raw, rows, dimension=768):
    if rows <= 0 or len(raw) != rows * dimension * 4: raise ValueError('vector byte count mismatch')
    result = [list(struct.unpack_from('<' + 'f' * dimension, raw, i * dimension * 4)) for i in range(rows)]
    vector_bytes(result, dimension)
    return result
