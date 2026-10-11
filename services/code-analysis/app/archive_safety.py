"""Bounded, link-safe archive inventory for uploaded analysis material.

Zip only in this policy. Nested archives are inventoried ordinary files and
are never expanded. Every accepted file is staged under a content-hash name;
submitter filenames never become output paths.
"""

from __future__ import annotations

import hashlib
import io
import re
import stat
import zipfile
from dataclasses import dataclass, field

from . import contracts
from .contracts import ContractError

_WINDOWS_RESERVED_RE = re.compile(
    r"(?i)^(con|prn|aux|nul|com[1-9]|lpt[1-9])(\..*)?$")
_NESTED_SUFFIXES = (".zip", ".tar", ".tgz", ".tar.gz", ".gz", ".rar", ".7z")


@dataclass
class InventoriedFile:
    canonical_path: str
    size: int
    sha256: str
    compressed_size: int
    is_nested_archive: bool
    staged_name: str


@dataclass
class Inventory:
    files: list = field(default_factory=list)
    total_expanded_bytes: int = 0
    entry_count: int = 0


def canonicalize_path(raw: str) -> str:
    if raw is None:
        raise ContractError("ARCHIVE_EMPTY_PATH")
    normalized = raw.replace("\\", "/")
    while "//" in normalized:
        normalized = normalized.replace("//", "/")
    if normalized.startswith("/") or ":" in normalized:
        raise ContractError("ARCHIVE_ABSOLUTE_PATH")
    normalized = normalized.strip("/")
    if not normalized:
        raise ContractError("ARCHIVE_EMPTY_PATH")
    if len(normalized) > contracts.MAX_PATH_LENGTH:
        raise ContractError("ARCHIVE_PATH_TOO_LONG")
    segments = [s for s in normalized.split("/") if s]
    if len(segments) > contracts.MAX_PATH_DEPTH:
        raise ContractError("ARCHIVE_PATH_TOO_DEEP")
    for segment in segments:
        if segment in (".", ".."):
            raise ContractError("ARCHIVE_PATH_TRAVERSAL")
        if segment.endswith((".", " ")) or _WINDOWS_RESERVED_RE.fullmatch(segment):
            raise ContractError("ARCHIVE_PATH_TRAVERSAL", f"unsafe segment {segment}")
        if any(c in segment for c in ("\x00",)):
            raise ContractError("ARCHIVE_PATH_TRAVERSAL")
    return "/".join(segments)


def _is_link(info: zipfile.ZipInfo) -> bool:
    mode = (info.external_attr >> 16) & 0o170000
    return mode in (stat.S_IFLNK,) or bool(info.external_attr & 0xA0000000)


def inspect_zip(data: bytes) -> Inventory:
    """Validate and inventory a complete bounded zip upload."""
    if len(data) < 4 or data[:2] != b"PK":
        raise ContractError("ARCHIVE_UNSUPPORTED_FORMAT")
    try:
        archive = zipfile.ZipFile(io.BytesIO(data))
    except zipfile.BadZipFile as exc:
        raise ContractError("ARCHIVE_CORRUPT", str(exc)[:200])
    inventory = Inventory()
    seen: set[str] = set()
    try:
        names = archive.namelist()
    except Exception as exc:
        archive.close()
        raise ContractError("ARCHIVE_CORRUPT", str(exc)[:200])
    try:
        for name in names:
            try:
                info = archive.getinfo(name)
            except KeyError:
                raise ContractError("ARCHIVE_CORRUPT", name[:200])
            canonical = canonicalize_path(name)
            lowered = canonical.lower()
            if lowered in seen:
                raise ContractError("ARCHIVE_DUPLICATE_ENTRY", canonical[:200])
            seen.add(lowered)
            if _is_link(info):
                raise ContractError("ARCHIVE_SYMLINK_NOT_ALLOWED", canonical[:200])
            if info.is_dir():
                inventory.entry_count += 1
                _check_entry_count(inventory)
                continue
            if info.flag_bits & 0x1:
                raise ContractError("ARCHIVE_ENCRYPTED", canonical[:200])
            inventory.entry_count += 1
            _check_entry_count(inventory)
            expanded = _stream_entry(archive, info, canonical, inventory)
            digest = hashlib.sha256(expanded).hexdigest()
            nested = lowered.endswith(_NESTED_SUFFIXES)
            staged = f"{digest}.bin"
            inventory.files.append(InventoriedFile(
                canonical_path=canonical,
                size=len(expanded),
                sha256=digest,
                compressed_size=info.compress_size,
                is_nested_archive=nested,
                staged_name=staged,
            ))
            inventory.total_expanded_bytes += len(expanded)
            if inventory.total_expanded_bytes > contracts.MAX_EXPANDED_BYTES:
                raise ContractError("ARCHIVE_TOTAL_SIZE_EXCEEDED")
    finally:
        archive.close()
    if not inventory.files:
        raise ContractError("ARCHIVE_EMPTY")
    return inventory


def _check_entry_count(inventory: Inventory) -> None:
    if inventory.entry_count > contracts.MAX_ENTRIES:
        raise ContractError("ARCHIVE_TOO_MANY_ENTRIES")


def _stream_entry(archive: zipfile.ZipFile, info: zipfile.ZipInfo,
                  canonical: str, inventory: Inventory) -> bytes:
    remaining = contracts.MAX_EXPANDED_BYTES - inventory.total_expanded_bytes
    chunks: list[bytes] = []
    total = 0
    try:
        with archive.open(info, mode="r") as stream:
            while True:
                block = stream.read(1024 * 256)
                if not block:
                    break
                total += len(block)
                if total > remaining:
                    raise ContractError("ARCHIVE_TOTAL_SIZE_EXCEEDED", canonical[:200])
                chunks.append(block)
    except ContractError:
        raise
    except Exception as exc:
        raise ContractError("ARCHIVE_CORRUPT", str(exc)[:200])
    if info.compress_size > 0 and total > 0:
        if total / info.compress_size > contracts.MAX_COMPRESSION_RATIO:
            raise ContractError("ARCHIVE_COMPRESSION_RATIO_EXCEEDED", canonical[:200])
    return b"".join(chunks)
