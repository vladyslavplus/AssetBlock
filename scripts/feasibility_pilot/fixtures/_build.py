"""Author reviewed fixtures and freeze hashes into manifest.json / pairs.json."""

from __future__ import annotations

import hashlib
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parent
ORIGIN = "Newly authored for this isolated feasibility pilot. Permission granted for analysis and training."
RIGHTS = "Original work of the repository owner; analysis and training allowed for this pilot only."
ORIGIN_UNRELATED2 = "Independently authored second unrelated fixture for this isolated feasibility pilot. Distinct from the sum helper and the first unrelated fixtures. Permission granted for analysis and training."
RIGHTS_UNRELATED2 = "Original independent work of the repository owner in a separate source family from the sum helper and first unrelated fixtures; analysis and training allowed for this pilot only."


def _h(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def _write(rel: str, text: str, newline: str = "lf") -> dict:
    path = ROOT / rel
    path.parent.mkdir(parents=True, exist_ok=True)
    payload = text.replace("\r\n", "\n").replace("\n", "\r\n" if newline == "crlf" else "\n")
    raw = payload.encode("utf-8")
    path.write_bytes(raw)
    return {"relativePath": rel.replace("\\", "/"), "sha256": _h(raw), "sizeBytes": len(raw), "newline": newline}


def js_base(name: str, extra: str = "") -> str:
    return (
        f"// café sample: sum helper\n"
        f"export function {name}(values) {{\n"
        f"  let total = 0;\n"
        f"  for (const value of values) {{\n"
        f"    total += Number(value);\n"
        f"  }}\n"
        f"  return total{extra};\n"
        f"}}\n"
        f"export async function {name}Async(values) {{\n"
        f"  return Promise.resolve({name}(values));\n"
        f"}}\n"
    )


def ts_base(name: str) -> str:
    return (
        f"export interface Totaller {{ total(values: number[]): number; }}\n"
        f"export function {name}<T extends number>(values: readonly T[]): number {{\n"
        f"  return values.reduce((acc, value) => acc + value, 0);\n"
        f"}}\n"
        f"export class {name}Service implements Totaller {{\n"
        f"  total(values: number[]): number {{\n"
        f"    return {name}(values);\n"
        f"  }}\n"
        f"}}\n"
    )


def py_base(name: str) -> str:
    return (
        f"# naïve totaliser\n"
        f"from functools import wraps\n\n"
        f"def traced(fn):\n"
        f"    @wraps(fn)\n"
        f"    def inner(*args, **kwargs):\n"
        f"        return fn(*args, **kwargs)\n"
        f"    return inner\n\n"
        f"@traced\n"
        f"def {name}(values):\n"
        f"    total = 0\n"
        f"    for value in values:\n"
        f"        total += int(value)\n"
        f"    return total\n\n"
        f"async def {name}_async(values):\n"
        f"    return {name}(values)\n\n"
        f"def nested_wrapper(values):\n"
        f"    def inner():\n"
        f"        return {name}(values)\n"
        f"    return inner()\n"
        f"MODULE_FLAG = True\n"
    )


def java_base(name: str) -> str:
    return (
        f"package fixtures;\n\n"
        f"import java.util.List;\n\n"
        f"public final class {name} {{\n"
        f"    public {name}() {{}}\n"
        f"    public int total(List<Integer> values) {{\n"
        f"        int acc = 0;\n"
        f"        for (Integer value : values) {{\n"
        f"            acc += value;\n"
        f"        }}\n"
        f"        return acc;\n"
        f"    }}\n"
        f"    public int totalLambda(List<Integer> values) {{\n"
        f"        return values.stream().mapToInt(v -> v).sum();\n"
        f"    }}\n"
        f"}}\n"
    )


def cs_base(name: str) -> str:
    return (
        f"#nullable enable\n"
        f"using System;\n"
        f"using System.Linq;\n\n"
        f"public static class {name} {{\n"
        f"    public static int Total(int[] values) {{\n"
        f"        int Local() {{\n"
        f"            return values.Sum();\n"
        f"        }}\n"
        f"        return Local();\n"
        f"    }}\n"
        f"    public static int TotalFn(int[] values) => values.Aggregate(0, (a, b) => a + b);\n"
        f"}}\n"
    )


def main() -> None:
    code_entries = []
    specs = [
        ("javascript", "javascript", "js", "sumRange", js_base, "sumRange", "sumRangeAsync"),
        ("typescript", "typescript", "ts", "sumRange", ts_base, "sumRange", "sumRangeService"),
        ("python", "python", "py", "sum_range", py_base, "sum_range", "sum_range_async"),
        ("java", "java", "java", "SumRange", java_base, "SumRange", "total"),
        ("csharp", "csharp", "cs", "SumRange", cs_base, "SumRange", "Total"),
    ]
    for language, dialect, ext, stem, builder, frag_a, frag_b in specs:
        family = f"{language}-sum"
        variants = {
            "base": builder(stem),
            "copy": builder(stem),
            "rename": builder("Accumulate" if language in {"java", "csharp"} else ("accumulate_range" if language == "python" else "accumulate")),
            "structural": builder(stem).replace("return ", "return 0 + ", 1),
            "unrelated": _unrelated(language, dialect),
            "unrelated2": _unrelated2(language),
            "boilerplate": _boilerplate(language),
            "long": builder(stem) + (("# padding\n" if language == "python" else "    // padding\n") * 80),
            "modern": _modern(language),
        }
        newline = "crlf" if language == "csharp" else "lf"
        rename_frag = "accumulate" if language != "python" else "accumulate_range"
        if language == "java":
            rename_frag = "Accumulate"
        if language == "csharp":
            rename_frag = "Accumulate"
        expected_names = {
            "base": [frag_a],
            "copy": [frag_a],
            "rename": [rename_frag],
            "structural": [frag_a],
            "unrelated": _unrelated_names(language),
            "unrelated2": _unrelated2_names(language),
            "boilerplate": _boilerplate_names(language),
            "long": [frag_a],
            "modern": _modern_names(language),
        }
        for variant, source in variants.items():
            rel = f"code/{language}/{stem}_{variant}.{ext}"
            if language == "java":
                class_name = stem if variant != "rename" else "Accumulate"
                if variant == "unrelated":
                    class_name = "WidgetFactory"
                elif variant == "unrelated2":
                    class_name = "LedgerHasher"
                elif variant == "boilerplate":
                    class_name = "LicenseHeaderDemo"
                elif variant == "modern":
                    class_name = "PointRecord"
                rel = f"code/{language}/{class_name}.java"
                if variant not in {"base", "copy"} and variant not in {"unrelated", "unrelated2", "boilerplate", "modern"}:
                    rel = f"code/{language}/{class_name}_{variant}.java"
                if variant == "copy":
                    rel = f"code/{language}/{stem}Copy.java"
                    source = source.replace(f"class {stem}", f"class {stem}Copy", 1)
                    expected_names["copy"] = [f"{stem}Copy"]
            meta = _write(rel, source, newline)
            training = variant in {"base", "copy", "rename", "unrelated", "unrelated2"}
            if variant == "unrelated":
                family_id = f"{language}-unrelated"
                origin = ORIGIN
                rights = RIGHTS
                notes = "Independently authored unrelated fixture for this smoke experiment; label is not a legal conclusion."
            elif variant == "unrelated2":
                family_id = f"{language}-unrelated2"
                origin = ORIGIN_UNRELATED2
                rights = RIGHTS_UNRELATED2
                notes = "Second independently authored unrelated fixture; distinct source family from the sum helper and first unrelated fixture. Smoke label only, not a legal conclusion."
            elif variant == "structural":
                family_id = family
                origin = ORIGIN
                rights = RIGHTS
                notes = "Shared-origin structural variation of the sum helper for this smoke experiment; diagnostic example, not an independent negative and not a legal conclusion."
            else:
                family_id = family
                origin = ORIGIN
                rights = RIGHTS
                notes = "Reviewed authored fixture. Shared-origin labels in this smoke experiment are not infringement or a legal conclusion."
            code_entries.append(
                {
                    "id": f"{language}-{variant}",
                    **meta,
                    "language": language,
                    "dialect": dialect,
                    "origin": origin,
                    "rights": rights,
                    "sourceFamily": family_id,
                    "variant": variant,
                    "expectedOutcome": "VALID_EXTRACTED",
                    "expectedFragmentIds": expected_names[variant],
                    "expectedDiagnostics": [],
                    "expectedOmissions": [],
                    "requiredCase": True,
                    "diagnosticOnly": variant in {"structural", "boilerplate", "long", "modern"},
                    "trainingAllowed": training,
                    "vendorOrGenerated": False,
                    "notes": notes,
                }
            )
        broken = _broken(language)
        brel = f"code/{language}/broken.{ext}"
        bmeta = _write(brel, broken["source"], "lf")
        code_entries.append(
            {
                "id": f"{language}-broken",
                **bmeta,
                "language": language,
                "dialect": dialect,
                "origin": ORIGIN,
                "rights": RIGHTS,
                "sourceFamily": f"{language}-broken",
                "variant": "broken",
                "expectedOutcome": "INVALID_PARSE",
                "expectedFragmentIds": [],
                "expectedDiagnostics": [{"kind": "PARSE_ERROR", "startLine": broken["line"]}],
                "expectedOmissions": [{"reason": "PARSE_ERROR"}],
                "requiredCase": True,
                "diagnosticOnly": True,
                "trainingAllowed": False,
                "vendorOrGenerated": False,
                "notes": "Intentionally malformed; remains invalid even if diagnostic matches",
            }
        )

    tsx_a = (
        "export function Panel(props: { title: string }): JSX.Element {\n"
        "  return <section data-title={props.title}>{props.title}</section>;\n"
        "}\n"
    )
    tsx_b = (
        "export function Badge(props: { label: string }): JSX.Element {\n"
        "  return <span className=\"badge\">{props.label}</span>;\n"
        "}\n"
    )
    for fid, rel, src, names in (
        ("tsx-panel", "code/tsx/Panel.tsx", tsx_a, ["Panel"]),
        ("tsx-badge", "code/tsx/Badge.tsx", tsx_b, ["Badge"]),
    ):
        meta = _write(rel, src, "lf")
        code_entries.append(
            {
                "id": fid,
                **meta,
                "language": "typescript",
                "dialect": "tsx",
                "origin": ORIGIN,
                "rights": RIGHTS,
                "sourceFamily": "tsx-ui",
                "variant": "component",
                "expectedOutcome": "VALID_EXTRACTED",
                "expectedFragmentIds": names,
                "expectedDiagnostics": [],
                "expectedOmissions": [],
                "requiredCase": True,
                "diagnosticOnly": True,
                "trainingAllowed": False,
                "vendorOrGenerated": False,
                "notes": "TSX diagnostic fixture; ML experimental",
            }
        )

    assert len(code_entries) == 52, len(code_entries)

    pairs = []
    for language in ("javascript", "typescript", "python", "java", "csharp"):
        pairs.extend(
            [
                _pair(
                    f"{language}-pos-copy",
                    language,
                    f"{language}-base",
                    f"{language}-copy",
                    "positive",
                    False,
                    True,
                    f"{language}-sum",
                    "Shared-origin positive copy versus the query. Smoke label only, not a legal conclusion.",
                ),
                _pair(
                    f"{language}-pos-rename",
                    language,
                    f"{language}-base",
                    f"{language}-rename",
                    "positive",
                    False,
                    True,
                    f"{language}-sum",
                    "Shared-origin renamed positive versus the query. Smoke label only, not a legal conclusion.",
                ),
                _pair(
                    f"{language}-neg-unrelated",
                    language,
                    f"{language}-base",
                    f"{language}-unrelated",
                    "negative",
                    False,
                    True,
                    f"{language}-sum",
                    "Query-relative negative: independently authored unrelated fixture versus the sum helper. Smoke label only, not a legal conclusion.",
                ),
                _pair(
                    f"{language}-neg-hard",
                    language,
                    f"{language}-base",
                    f"{language}-unrelated2",
                    "negative",
                    False,
                    True,
                    f"{language}-sum",
                    "Query-relative negative: second independently authored unrelated fixture versus the same query. Distinct from *-unrelated. Smoke label only, not a legal conclusion.",
                ),
                _pair(
                    f"{language}-boilerplate",
                    language,
                    f"{language}-base",
                    f"{language}-boilerplate",
                    "diagnostic",
                    True,
                    False,
                    f"{language}-sum",
                    "Diagnostic pair; not used as a training negative.",
                ),
                _pair(
                    f"{language}-independent",
                    language,
                    f"{language}-structural",
                    f"{language}-unrelated",
                    "diagnostic",
                    True,
                    False,
                    f"{language}-sum",
                    "Diagnostic: shared-origin structural derivative versus independently authored unrelated. Structural is not a training negative.",
                ),
            ]
        )
    assert len(pairs) == 30

    licenses = _licenses()
    manifest = {
        "author": "Repository owner",
        "createdFor": "isolated feasibility pilot",
        "trainingPolicy": "Only fixtures with trainingAllowed=true and successful VALID_EXTRACTED may enter training.",
        "code": code_entries,
        "licenses": licenses,
    }
    (ROOT / "manifest.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    (ROOT / "pairs.json").write_text(json.dumps({"pairs": pairs}, indent=2) + "\n", encoding="utf-8")
    print(f"wrote {len(code_entries)} code fixtures and {len(pairs)} pairs")


def _pair(pid, language, left, right, label, diagnostic, training, family, notes: str):
    return {
        "pairId": pid,
        "language": language,
        "leftId": left,
        "rightId": right,
        "label": label,
        "diagnosticOnly": diagnostic,
        "trainingAllowed": training,
        "sourceFamily": family,
        "notes": notes,
    }


def _unrelated(language: str, dialect: str) -> str:
    if language == "javascript":
        return "export function parseQuery(text) {\n  return Object.fromEntries(new URLSearchParams(text));\n}\n"
    if language == "typescript":
        return "export function parseQuery(text: string): Record<string, string> {\n  return Object.fromEntries(new URLSearchParams(text));\n}\n"
    if language == "python":
        return "from urllib.parse import parse_qs\n\ndef parse_query(text):\n    return parse_qs(text)\n"
    if language == "java":
        return "package fixtures;\n\npublic final class WidgetFactory {\n    public String label(int id) {\n        return \"widget-\" + id;\n    }\n}\n"
    return "public static class WidgetFactory {\n    public static string Label(int id) => $\"widget-{id}\";\n}\n"


def _unrelated_names(language: str) -> list[str]:
    if language == "java":
        return ["WidgetFactory"]
    if language == "csharp":
        return ["WidgetFactory"]
    if language == "python":
        return ["parse_query"]
    return ["parseQuery"]


def _unrelated2(language: str) -> str:
    if language == "javascript":
        return (
            "export function formatIsoDate(epochMs) {\n"
            "  const date = new Date(epochMs);\n"
            "  return date.toISOString().slice(0, 10);\n"
            "}\n"
        )
    if language == "typescript":
        return (
            "export function formatIsoDate(epochMs: number): string {\n"
            "  return new Date(epochMs).toISOString().slice(0, 10);\n"
            "}\n"
        )
    if language == "python":
        return (
            "from datetime import datetime, timezone\n\n"
            "def format_iso_date(epoch_ms):\n"
            "    return datetime.fromtimestamp(epoch_ms / 1000, tz=timezone.utc).strftime(\"%Y-%m-%d\")\n"
        )
    if language == "java":
        return (
            "package fixtures;\n\n"
            "public final class LedgerHasher {\n"
            "    public String fingerprint(String seed, int salt) {\n"
            "        return Integer.toHexString(seed.hashCode() ^ salt);\n"
            "    }\n"
            "}\n"
        )
    return (
        "public static class LedgerHasher {\n"
        "    public static string Fingerprint(string seed, int salt) => (seed.GetHashCode() ^ salt).ToString(\"x\");\n"
        "}\n"
    )


def _unrelated2_names(language: str) -> list[str]:
    if language == "java":
        return ["LedgerHasher"]
    if language == "csharp":
        return ["LedgerHasher"]
    if language == "python":
        return ["format_iso_date"]
    return ["formatIsoDate"]


def _boilerplate(language: str) -> str:
    header = "// Copyright 2026 Example Authors. All rights reserved.\n// Utility placeholders.\n"
    if language == "python":
        header = "# Copyright 2026 Example Authors. All rights reserved.\n"
        return header + "def noop():\n    pass\n"
    if language == "java":
        return "package fixtures;\n\npublic final class LicenseHeaderDemo {\n    public void noop() {}\n}\n"
    if language == "csharp":
        return header + "public static class LicenseHeaderDemo { public static void Noop() {} }\n"
    return header + "export function noop() { return null; }\n"


def _boilerplate_names(language: str) -> list[str]:
    if language in {"java", "csharp"}:
        return ["LicenseHeaderDemo"]
    return ["noop"]


def _modern(language: str) -> str:
    if language == "javascript":
        return "export const doubled = (xs) => xs.map((x) => x * 2);\nexport class Box { #value; constructor(v) { this.#value = v; } get value() { return this.#value; } }\n"
    if language == "typescript":
        return "export type Result<T> = { ok: true; value: T } | { ok: false };\nexport function wrap<T>(value: T): Result<T> { return { ok: true, value }; }\n"
    if language == "python":
        return "def match_status(code: int) -> str:\n    match code:\n        case 200:\n            return 'ok'\n        case _:\n            return 'other'\n"
    if language == "java":
        return "package fixtures;\n\npublic record PointRecord(int x, int y) {\n    public int mag() { return Math.abs(x) + Math.abs(y); }\n}\n"
    return "public readonly record struct Point(int X, int Y);\npublic static class PointOps { public static int Mag(Point p) => Math.Abs(p.X) + Math.Abs(p.Y); }\n"


def _modern_names(language: str) -> list[str]:
    if language == "javascript":
        return ["Box"]
    if language == "typescript":
        return ["wrap"]
    if language == "python":
        return ["match_status"]
    if language == "java":
        return ["PointRecord"]
    return ["PointOps"]


def _broken(language: str) -> dict:
    if language == "javascript":
        return {"line": 3, "source": "export function broken(values) {\n  let total = 0;\n  for (const value of values {\n    total += value;\n  }\n}\n"}
    if language == "typescript":
        return {"line": 1, "source": "export function broken(values: number[] {\n  return values.reduce((a, b) => a + b, 0);\n}\n"}
    if language == "python":
        return {"line": 3, "source": "def broken(values):\n    total = 0\n    for value in values\n        total += value\n    return total\n"}
    if language == "java":
        return {"line": 5, "source": "package fixtures;\npublic class Broken {\n    public int total(int[] values) {\n        int acc = 0;\n        for (int value : values {\n            acc += value;\n        }\n        return acc;\n    }\n}\n"}
    return {"line": 4, "source": "public class Broken {\n    public int Total(int[] values) {\n        int acc = 0;\n        foreach (var value in values {\n            acc += value;\n        }\n        return acc;\n    }\n}\n"}


def _licenses() -> list[dict]:
    mit = (
        "MIT License\n\nCopyright (c) 2026 Pilot Author\n\nPermission is hereby granted, free of charge, to any person obtaining a copy\n"
        "of this software and associated documentation files (the \"Software\"), to deal\n"
        "in the Software without restriction, including without limitation the rights\n"
        "to use, copy, modify, merge, publish, distribute, sublicense, and/or sell\n"
        "copies of the Software, and to permit persons to whom the Software is\n"
        "furnished to do so, subject to the following conditions:\n\n"
        "The above copyright notice and this permission notice shall be included in all\n"
        "copies or substantial portions of the Software.\n\nTHE SOFTWARE IS PROVIDED \"AS IS\", WITHOUT WARRANTY OF ANY KIND.\n"
    )
    bsd = (
        "Copyright (c) 2026 Pilot Author\nAll rights reserved.\n\nRedistribution and use in source and binary forms, with or without modification,\n"
        "are permitted provided that the following conditions are met:\n"
        "1. Redistributions of source code must retain the above copyright notice.\n"
        "2. Redistributions in binary form must reproduce the above copyright notice.\n"
        "3. Neither the name of the copyright holder nor the names of its contributors may be used to endorse or promote products derived from this software without specific prior written permission.\n"
    )
    apache_notice = "Pilot Notice\nCopyright 2026 Pilot Author\nThis product includes software developed for an isolated feasibility pilot.\n"
    packages = []

    def pkg(pid, name, files, expected):
        resolved = []
        for rel, text in files:
            meta = _write(rel, text, "lf")
            resolved.append(meta)
        packages.append({"id": pid, "name": name, "expectedSignals": expected, "files": resolved})

    pkg(
        "lic-mit",
        "mit-header",
        [
            ("licenses/mit/LICENSE", mit),
            ("licenses/mit/index.js", "/* SPDX-License-Identifier: MIT */\nexport const n = 1;\n"),
            ("licenses/mit/package.json", json.dumps({"name": "mit-sample", "license": "MIT", "private": True}, indent=2) + "\n"),
        ],
        {"license": "MIT", "noticePresent": True},
    )
    pkg(
        "lic-bsd",
        "bsd3",
        [
            ("licenses/bsd3/LICENSE", bsd),
            ("licenses/bsd3/lib.py", "# SPDX-License-Identifier: BSD-3-Clause\nVALUE = 2\n"),
            ("licenses/bsd3/package.json", json.dumps({"name": "bsd-sample", "license": "BSD-3-Clause", "private": True}, indent=2) + "\n"),
        ],
        {"license": "BSD-3-Clause"},
    )
    pkg(
        "lic-apache",
        "apache-notice",
        [
            ("licenses/apache/LICENSE", "Apache License 2.0 text excerpt for fixture use. Provenance: https://www.apache.org/licenses/LICENSE-2.0\n"),
            ("licenses/apache/NOTICE", apache_notice),
            ("licenses/apache/src.java", "/* SPDX-License-Identifier: Apache-2.0 */\nclass A {}\n"),
            ("licenses/apache/package.json", json.dumps({"name": "apache-sample", "license": "Apache-2.0", "private": True}, indent=2) + "\n"),
        ],
        {"license": "Apache-2.0", "noticePresent": True},
    )
    pkg(
        "lic-mit-missing-notice",
        "declared-mit-no-notice",
        [
            ("licenses/mit-declared/package.json", json.dumps({"name": "declared-mit", "license": "MIT", "private": True}, indent=2) + "\n"),
            ("licenses/mit-declared/main.js", "export const x = 1;\n"),
        ],
        {"declared": "MIT", "noticePresent": False, "concern": "missing-notice"},
    )
    pkg(
        "lic-conflict",
        "conflict-header",
        [
            ("licenses/conflict/package.json", json.dumps({"name": "conflict", "license": "MIT", "private": True}, indent=2) + "\n"),
            ("licenses/conflict/app.py", "# SPDX-License-Identifier: Apache-2.0\nX = 1\n"),
        ],
        {"declared": "MIT", "header": "Apache-2.0", "discrepancy": True},
    )
    pkg(
        "lic-custom",
        "custom-unknown",
        [
            ("licenses/custom/LICENSE", "Do whatever, but you must send a postcard. Not an OSI license.\n"),
            ("licenses/custom/package.json", json.dumps({"name": "custom", "license": "SEE LICENSE IN LICENSE", "private": True}, indent=2) + "\n"),
        ],
        {"license": "UNKNOWN_OR_CUSTOM"},
    )
    return packages


if __name__ == "__main__":
    main()
