"""Authenticated bounded upload transport.

Authentication precedes materialization: the secret is compared in constant
time from headers before any body byte is stored. Multipart structure is
scanned from the staged file through offsets, never by copying the bulk
archive through RAM. Only framed delimiters (CRLF --boundary followed by
CRLF or --) are structural; the same byte sequence inside payload without
framing is ordinary data. The metadata part must come first so bounded
request identity is available before bulk archive intake.
"""

from __future__ import annotations

import hashlib
import hmac
import json
import mmap
from dataclasses import dataclass
from pathlib import Path

from . import contracts
from .contracts import ContractError

AUTH_HEADER = "x-analysis-secret"
METADATA_PART = "metadata"
ARCHIVE_PART = "archive"
MAX_HEADER_BLOCK = 8192
SCAN_PREFIX_CAP = 256 * 1024
COPY_BLOCK = 1024 * 256


def check_secret(provided: str | None, expected: str) -> None:
    if not provided:
        raise ContractError("AUTH_MISSING")
    if not hmac.compare_digest(provided.encode("utf-8"), expected.encode("utf-8")):
        raise ContractError("AUTH_INVALID")


@dataclass
class UploadParts:
    metadata_raw: bytes
    archive: bytes
    metadata: dict


@dataclass
class StagedUpload:
    metadata_raw: bytes
    metadata: dict
    archive_path: object
    archive_size: int
    archive_sha256: str


def _split_header(value: str) -> tuple[str, dict]:
    pieces = [p.strip() for p in value.split(";")]
    params: dict[str, str] = {}
    for piece in pieces[1:]:
        if "=" in piece:
            key, _, val = piece.partition("=")
            params[key.strip().lower()] = val.strip().strip('"')
    return pieces[0].lower(), params


def _boundary_or_raise(content_type: str) -> bytes:
    main, params = _split_header(content_type or "")
    if main != "multipart/form-data" or "boundary" not in params:
        raise ContractError("MALFORMED_MULTIPART", "content type")
    boundary = params["boundary"]
    if not boundary or len(boundary) > 200 or any(c in boundary for c in "\r\n\"'"):
        raise ContractError("MALFORMED_MULTIPART", "boundary")
    return ("--" + boundary).encode("latin-1")


def parse_multipart(body: bytes, content_type: str) -> UploadParts:
    """In-memory parse for small already-buffered bodies (tests, tooling).

    Bulk service intake uses parse_multipart_file instead.
    """
    delimiter = _boundary_or_raise(content_type)
    (open0, mid), (_mid2, close) = _part_pair(body, delimiter)
    metadata_raw = _part_content(body, open0, mid, METADATA_PART, first=True)
    archive = _part_content(body, mid, close, ARCHIVE_PART, first=False)
    if len(metadata_raw) == 0 or len(metadata_raw) > contracts.MAX_METADATA_BYTES:
        raise ContractError("MALFORMED_METADATA", "metadata size")
    if len(archive) == 0 or len(archive) > contracts.MAX_COMPRESSED_BYTES:
        raise ContractError("BODY_TOO_LARGE", "archive part")
    return UploadParts(metadata_raw=metadata_raw, archive=archive,
                       metadata=_decode_metadata(metadata_raw))


def parse_multipart_file(body_path, content_type: str, dest_dir,
                         archive_name: str = "archive.bin") -> StagedUpload:
    """Split a staged body with offsets; the archive never fully sits in RAM.

    The file is memory-mapped (OS page cache, no bytes copies of the bulk),
    framed delimiters are located by offset, metadata (bounded KiBs) is the
    only part materialized, and the archive range streams once to its own
    file with an incremental hash.
    """
    delimiter = _boundary_or_raise(content_type)
    body_path = Path(body_path)
    size = body_path.stat().st_size
    if size == 0 or size > contracts.MAX_BODY_BYTES:
        raise ContractError("BODY_TOO_LARGE" if size else "MALFORMED_MULTIPART",
                            "body size")
    with open(body_path, "rb") as stream:
        with mmap.mmap(stream.fileno(), 0, access=mmap.ACCESS_READ) as mapped:
            (open0, mid), (_mid2, close) = _part_pair(mapped, delimiter)
            metadata_raw = bytes(_part_view(mapped, open0, mid,
                                            METADATA_PART, first=True))
            archive_range = _part_range(mapped, mid, close, ARCHIVE_PART)
            if len(metadata_raw) == 0 or len(metadata_raw) > contracts.MAX_METADATA_BYTES:
                raise ContractError("MALFORMED_METADATA", "metadata size")
            archive_size = archive_range[1] - archive_range[0]
            if archive_size <= 0 or archive_size > contracts.MAX_COMPRESSED_BYTES:
                raise ContractError("BODY_TOO_LARGE", "archive part")
            archive_path = Path(dest_dir) / archive_name
            digest = _stream_range(mapped, archive_range, archive_path)
    metadata = _decode_metadata(metadata_raw)
    return StagedUpload(metadata_raw=metadata_raw, metadata=metadata,
                        archive_path=archive_path, archive_size=archive_size,
                        archive_sha256=digest)


def scan_prefix_metadata(prefix: bytes, content_type: str) -> dict | None:
    """Best-effort metadata extraction from a bounded intake prefix.

    Returns the decoded metadata once the complete first part is present,
    None when the prefix is a valid fragment needing more bytes, and raises
    on structural violations so misordered bulk never materializes.
    """
    delimiter = _boundary_or_raise(content_type)
    if len(prefix) < len(delimiter):
        if delimiter.startswith(bytes(prefix)):
            return None
        raise ContractError("MALFORMED_MULTIPART", "preamble")
    if bytes(prefix[0:len(delimiter)]) != delimiter:
        raise ContractError("MALFORMED_MULTIPART", "preamble")
    hits = _framed_hits(prefix, delimiter)
    if not hits:
        return None
    found = _headers_of(prefix, hits[0], len(prefix), False)
    if found is not None and _part_name(found[0]) != METADATA_PART:
        raise ContractError("MALFORMED_MULTIPART", "metadata first")
    if len(hits) < 2:
        return None
    metadata_raw = _part_content(prefix, hits[0], hits[1], METADATA_PART, first=True)
    if len(metadata_raw) > contracts.MAX_METADATA_BYTES:
        raise ContractError("MALFORMED_METADATA", "metadata size")
    return _decode_metadata(metadata_raw)


def _part_pair(body, delimiter: bytes):
    """Exactly two parts: opening, middle, and closing framed delimiters."""
    if bytes(body[0:len(delimiter)]) != delimiter:
        raise ContractError("MALFORMED_MULTIPART", "preamble")
    hits = _framed_hits(body, delimiter)
    if len(hits) != 3 or hits[0][0] != 0 or hits[2][1] != "close":
        raise ContractError("MALFORMED_MULTIPART", "part count")
    tail = body[hits[2][0] + len(delimiter) + 2:]
    if bytes(tail).strip(b"\r\n \t") != b"":
        raise ContractError("MALFORMED_MULTIPART", "epilogue")
    return (hits[0], hits[1]), (hits[1], hits[2])


def _framed_hits(body, delimiter: bytes) -> list:
    """All (offset, kind) delimiter hits with strict framing.

    A hit counts only at offset 0 or directly after CRLF, and only when
    followed by CRLF (next part) or -- (closing marker). Unframed payload
    bytes never count, however often they repeat the boundary.
    """
    hits = []
    start = 0
    end = len(body)
    while True:
        at = body.find(delimiter, start, end)
        if at == -1:
            break
        framed_before = at == 0 or bytes(body[at - 2:at]) == b"\r\n"
        after = at + len(delimiter)
        if bytes(body[after:after + 2]) == b"\r\n":
            kind = "part"
        elif bytes(body[after:after + 2]) == b"--":
            kind = "close"
        else:
            kind = None
        if framed_before and kind is not None:
            hits.append((at, kind))
            if kind == "close":
                break
        start = at + 1
    return hits


def _part_content(body: bytes, open_hit, close_hit, expected: str,
                  first: bool) -> bytes:
    found = _headers_of(body, open_hit, len(body), True)
    assert found is not None
    head, content_start = found
    name = _part_name(head)
    if name != expected:
        raise ContractError("MALFORMED_MULTIPART",
                            "metadata first" if first else "unexpected part")
    content_end = _close_content_end(body, close_hit)
    return body[content_start:content_end]


def _headers_of(body, open_hit, cap: int, strict: bool):
    """Header block between the delimiter line and the blank line.

    Returns None when the prefix may still grow (non-strict scan); raises
    once the cap is reached or the complete body is malformed.
    """
    start, _kind = open_hit
    limit = min(start + 256 + MAX_HEADER_BLOCK, cap, len(body))
    line_end = body.find(b"\r\n", start, limit)
    if line_end == -1:
        if strict:
            raise ContractError("MALFORMED_MULTIPART", "delimiter framing")
        return None
    head_start = line_end + 2
    head_end = body.find(b"\r\n\r\n", head_start,
                         min(head_start + MAX_HEADER_BLOCK, cap, len(body)))
    if head_end == -1:
        if strict:
            raise ContractError("MALFORMED_MULTIPART", "part headers")
        return None
    return body[head_start:head_end], head_end + 4


def _part_view(mapped, open_hit, close_hit, expected: str, first: bool):
    found = _headers_of(mapped, open_hit, len(mapped), True)
    assert found is not None
    head, content_start = found
    name = _part_name(head)
    if name != expected:
        raise ContractError("MALFORMED_MULTIPART",
                            "metadata first" if first else "unexpected part")
    return mapped[content_start:_close_content_end(mapped, close_hit)]


def _part_range(mapped, open_hit, close_hit, expected: str) -> tuple:
    found = _headers_of(mapped, open_hit, len(mapped), True)
    assert found is not None
    head, content_start = found
    name = _part_name(head)
    if name != expected:
        raise ContractError("MALFORMED_MULTIPART", "unexpected part")
    return (content_start, _close_content_end(mapped, close_hit))


def _close_content_end(body, close_hit) -> int:
    close_start, kind = close_hit
    if kind not in ("part", "close"):
        raise ContractError("MALFORMED_MULTIPART", "framing")
    return close_start - 2


def _part_name(head: bytes) -> str:
    headers: dict[str, str] = {}
    for line in bytes(head).split(b"\r\n"):
        if not line:
            continue
        if b":" not in line:
            raise ContractError("MALFORMED_MULTIPART", "part header line")
        key, _, val = line.partition(b":")
        headers[key.decode("latin-1").strip().lower()] = val.decode("latin-1").strip()
    disposition, disp_params = _split_header(headers.get("content-disposition", ""))
    if disposition != "form-data" or "name" not in disp_params:
        raise ContractError("MALFORMED_MULTIPART", "disposition")
    return disp_params["name"]


def _stream_range(mapped, archive_range: tuple, archive_path) -> str:
    hasher = hashlib.sha256()
    size = 0
    start, end = archive_range
    with open(archive_path, "wb") as stream:
        offset = start
        while offset < end:
            block = mapped[offset:min(offset + COPY_BLOCK, end)]
            stream.write(block)
            hasher.update(block)
            size += len(block)
            offset += len(block)
    if size != end - start:
        raise ContractError("MALFORMED_MULTIPART", "archive range")
    return hasher.hexdigest()


def _decode_metadata(metadata_raw: bytes) -> dict:
    try:
        metadata = json.loads(metadata_raw.decode("utf-8"))
    except (UnicodeDecodeError, json.JSONDecodeError):
        raise ContractError("MALFORMED_METADATA", "metadata encoding")
    if not isinstance(metadata, dict):
        raise ContractError("MALFORMED_METADATA", "metadata shape")
    return metadata


def sha256_hex(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()
