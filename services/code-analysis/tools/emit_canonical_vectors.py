"""Emits golden canonical-hash vectors for the cross-language contract test.

Each vector is a JSON document. The expected digest is the canonical encoding
(same rules as contracts.canonical) hashed with SHA-256. The .NET side must
reproduce both the digest and the exact canonical bytes.
"""

from __future__ import annotations

import json
import pathlib
import sys

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[1]))

from app.contracts import canonical, sha256_hex  # noqa: E402

VECTORS: dict[str, str] = {
    "ascii_flat": '{"b":2,"a":1}',
    "key_order_reversed": '{"zebra":1,"alpha":2,"middle":3}',
    "nested_objects": '{"outer":{"z":1,"a":{"y":2,"b":3}},"list":[{"b":1,"a":2}]}',
    "cyrillic": '{"scope":"\u0441\u043a\u0440\u0438\u043f\u0442/\u043c\u043e\u0434\u0443\u043b\u044c.py","spdx":"MIT"}',
    "non_bmp": '{"emoji":"\U0001F512\U0001F4DD","family":"\U0001D11E"}',
    "quotes_and_backslash": '{"path":"dir\\\\sub/\\"quoted\\".ts"}',
    "control_characters": '{"text":"tab\\tnewline\\nreturn\\rbackspace\\bformfeed\\fnull\\u0000unit\\u001f"}',
    "html_sensitive": '{"html":"<script>alert(\'x\')&amp;</script>"}',
    "numbers_integers": '{"i0":0,"i1":-1,"i2":1234567890123456789}',
    "numbers_float": '{"f0":1.5,"f1":-0.25,"f2":1.0,"f3":1e10,"f4":1.5e-7}',
    "numbers_precision": '{"p0":0.1,"p1":1234567890.123456789,"p2":3.141592653589793}',
    "booleans_and_null": '{"t":true,"f":false,"n":null}',
    "empty_containers": '{"o":{},"a":[]}',
    "unicode_keys": '{"\u043a\u043b\u044e\u0447":1,"\U0001F512":2,"plain":3}',
    "mixed_realistic": (
        '{"components":[{"componentId":"c1","license":"Apache-2.0","scopePath":"src/**"},'
        '{"componentId":"c2","license":"UNKNOWN","scopePath":"vendor/"}],'
        '"policyDigest":"' + "f" * 64 + '","coverage":{"analyzedFiles":42,"level":"PARTIAL"},'
        '"note":"\u0441\u043f\u0438\u0441\u043e\u043a \u2014 \U0001F4C4"}'
    ),
    # Code-point ordering differs from UTF-16 ordinal ordering: U+10000 encodes as the surrogate
    # pair D800 DC00, whose first unit sorts below U+E000 even though the code point does not.
    "key_order_bmp_vs_non_bmp": '{"\ue000":"bmp","\U00010000":"non-bmp","\ue001":"bmp2"}',
    "key_order_mixed_astral": '{"\U0001F600":1,"\ue000":2,"\uFFFF":3,"\U00010000":4}',
}


def main() -> int:
    out: list[dict[str, str]] = []
    for name, source in VECTORS.items():
        # Round-trip through the service canonical form first: the vector input is then exactly what
        # the service emits, so the .NET side must reproduce both the bytes and the digest.
        once = canonical(json.loads(source))
        document = once.decode("utf-8").rstrip("\n")
        encoded = canonical(json.loads(document))
        out.append(
            {
                "name": name,
                "document": document,
                "canonicalUtf8": encoded.decode("utf-8"),
                "digest": sha256_hex(encoded),
            }
        )

    target = pathlib.Path(__file__).with_name("canonical_vectors.json")
    target.write_text(json.dumps(out, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(f"wrote {len(out)} vectors to {target}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
