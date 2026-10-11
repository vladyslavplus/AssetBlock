"""Archive safety: traversal, links, collisions, caps are rejected before work."""

import io
import stat
import zipfile

from conftest import JS_HELLO, make_zip

from app import archive_safety, contracts


def test_clean_zip_passes_inventory():
    inventory = archive_safety.inspect_zip(make_zip({"src/a.js": JS_HELLO}))
    assert len(inventory.files) == 1
    assert inventory.files[0].canonical_path == "src/a.js"


def test_traversal_rejected():
    buffer = io.BytesIO()
    with zipfile.ZipFile(buffer, "w") as archive:
        archive.writestr("../evil.js", JS_HELLO)
    try:
        archive_safety.inspect_zip(buffer.getvalue())
    except contracts.ContractError as exc:
        assert exc.code == "ARCHIVE_PATH_TRAVERSAL"
    else:
        raise AssertionError("traversal accepted")


def test_absolute_path_rejected():
    buffer = io.BytesIO()
    with zipfile.ZipFile(buffer, "w") as archive:
        archive.writestr("/abs/evil.js", JS_HELLO)
    try:
        archive_safety.inspect_zip(buffer.getvalue())
    except contracts.ContractError as exc:
        assert exc.code == "ARCHIVE_ABSOLUTE_PATH"
    else:
        raise AssertionError("absolute path accepted")


def test_symlink_rejected():
    buffer = io.BytesIO()
    with zipfile.ZipFile(buffer, "w") as archive:
        info = zipfile.ZipInfo("link.js")
        info.external_attr = (stat.S_IFLNK | 0o777) << 16
        archive.writestr(info, "target.js")
    try:
        archive_safety.inspect_zip(buffer.getvalue())
    except contracts.ContractError as exc:
        assert exc.code == "ARCHIVE_SYMLINK_NOT_ALLOWED"
    else:
        raise AssertionError("symlink accepted")


def test_case_collision_rejected():
    try:
        archive_safety.inspect_zip(make_zip({"SRC/A.js": JS_HELLO, "src/a.js": JS_HELLO}))
    except contracts.ContractError as exc:
        assert exc.code == "ARCHIVE_DUPLICATE_ENTRY"
    else:
        raise AssertionError("case collision accepted")


def test_compression_ratio_rejected():
    bomb = b"0" * (2 * 1024 * 1024)
    try:
        archive_safety.inspect_zip(make_zip({"bomb.js": bomb}))
    except contracts.ContractError as exc:
        assert exc.code in {"ARCHIVE_COMPRESSION_RATIO_EXCEEDED",
                            "ARCHIVE_TOTAL_SIZE_EXCEEDED",
                            "ARCHIVE_ENTRY_TOO_LARGE"}
    else:
        raise AssertionError("ratio bomb accepted")


def test_too_many_entries_rejected():
    entries = {f"f{i:05d}.txt": b"x" for i in range(contracts.MAX_ENTRIES + 1)}
    try:
        archive_safety.inspect_zip(make_zip(entries))
    except contracts.ContractError as exc:
        assert exc.code == "ARCHIVE_TOO_MANY_ENTRIES"
    else:
        raise AssertionError("entry flood accepted")


def test_encrypted_entry_rejected():
    raw = make_zip({"secret.js": JS_HELLO})
    # Set the general-purpose encryption bit in local and central headers:
    # stdlib writestr clears it, but real encrypted uploads carry it and the
    # reader reports the central-directory flags.
    patched = bytearray(raw)
    for signature in (b"PK\x03\x04", b"PK\x01\x02"):
        start = 0
        while True:
            at = bytes(patched).find(signature, start)
            if at == -1:
                break
            patched[at + 8] |= 0x1
            start = at + 4
    try:
        archive_safety.inspect_zip(bytes(patched))
    except contracts.ContractError as exc:
        assert exc.code == "ARCHIVE_ENCRYPTED"
    else:
        raise AssertionError("encrypted entry accepted")


def test_non_zip_rejected():
    try:
        archive_safety.inspect_zip(b"not a zip at all" * 10)
    except contracts.ContractError as exc:
        assert exc.code == "ARCHIVE_UNSUPPORTED_FORMAT"
    else:
        raise AssertionError("non-zip accepted")


def test_empty_zip_rejected():
    assert_empty = make_zip({})
    try:
        archive_safety.inspect_zip(assert_empty)
    except contracts.ContractError as exc:
        assert exc.code == "ARCHIVE_EMPTY"
    else:
        raise AssertionError("empty archive accepted")


def test_nested_archive_is_inventoried_not_expanded():
    inner = make_zip({"inner.js": JS_HELLO})
    inventory = archive_safety.inspect_zip(make_zip({"bundle.zip": inner}))
    assert len(inventory.files) == 1
    assert inventory.files[0].is_nested_archive is True
