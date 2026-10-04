"""Language-specific tree-sitter extraction and coverage accounting."""

from __future__ import annotations

import hashlib
from dataclasses import asdict, dataclass
from typing import Any, Iterable

from .inventory import FixtureFile, byte_to_line_col


FRAGMENT_TYPES = {
    "javascript": {
        "function_declaration",
        "function_expression",
        "arrow_function",
        "method_definition",
        "generator_function_declaration",
        "class_declaration",
    },
    "typescript": {
        "function_declaration",
        "function_expression",
        "arrow_function",
        "method_definition",
        "generator_function_declaration",
        "class_declaration",
        "interface_declaration",
        "type_alias_declaration",
        "enum_declaration",
        "abstract_class_declaration",
    },
    "tsx": {
        "function_declaration",
        "function_expression",
        "arrow_function",
        "method_definition",
        "class_declaration",
        "interface_declaration",
        "type_alias_declaration",
    },
    "python": {
        "function_definition",
        "class_definition",
        "decorated_definition",
    },
    "java": {
        "method_declaration",
        "constructor_declaration",
        "class_declaration",
        "record_declaration",
        "lambda_expression",
    },
    "csharp": {
        "method_declaration",
        "constructor_declaration",
        "local_function_statement",
        "class_declaration",
        "record_declaration",
        "lambda_expression",
        "destructor_declaration",
    },
}

COMMENT_TYPES = {
    "comment",
    "line_comment",
    "block_comment",
    "html_comment",
}


@dataclass
class ExtractedFragment:
    fragment_id: str
    fixture_id: str
    language: str
    kind: str
    name: str | None
    start_byte: int
    end_byte: int
    start_line: int
    end_line: int
    nesting: int
    source_sha256: str
    raw_source: str


def union_length(ranges: Iterable[tuple[int, int]]) -> int:
    ordered = sorted((a, b) for a, b in ranges if b > a)
    if not ordered:
        return 0
    total = 0
    cur_a, cur_b = ordered[0]
    for a, b in ordered[1:]:
        if a <= cur_b:
            cur_b = max(cur_b, b)
        else:
            total += cur_b - cur_a
            cur_a, cur_b = a, b
    return total + (cur_b - cur_a)


def parse_source(language_key: str, source: bytes):
    from tree_sitter import Language, Parser

    lang = _language_object(language_key)
    parser = Parser(Language(lang))
    return parser.parse(source)


def extract_fixture(fixture: FixtureFile) -> dict[str, Any]:
    source = fixture.path.read_bytes()
    text = source.decode("utf-8")
    language_key = _parser_key(fixture)
    try:
        tree = parse_source(language_key, source)
    except Exception as exc:  # noqa: BLE001 - record operational failure
        return {
            "fixtureId": fixture.fixture_id,
            "actualOutcome": "FAILED",
            "operationalFailure": True,
            "error": str(exc),
            "fragments": [],
            "diagnostics": [],
            "omissions": [],
            "coverage": _empty_coverage(len(source)),
        }

    root = tree.root_node
    fragments: list[ExtractedFragment] = []
    diagnostics: list[dict[str, Any]] = []
    comment_ranges: list[tuple[int, int]] = []
    _walk(
        node=root,
        source=source,
        text=text,
        fixture=fixture,
        fragments=fragments,
        diagnostics=diagnostics,
        comment_ranges=comment_ranges,
        nesting=0,
    )

    fragment_ranges = [(item.start_byte, item.end_byte) for item in fragments]
    module_ranges = _module_level_ranges(root, fragment_ranges)
    for start, end in module_ranges:
        chunk = source[start:end]
        if not chunk.strip():
            continue
        start_line, _ = byte_to_line_col(text, start)
        end_line, _ = byte_to_line_col(text, max(start, end - 1))
        fragments.append(
            ExtractedFragment(
                fragment_id=f"{fixture.fixture_id}:module:{start}",
                fixture_id=fixture.fixture_id,
                language=fixture.language,
                kind="module_chunk",
                name="__module__",
                start_byte=start,
                end_byte=end,
                start_line=start_line,
                end_line=end_line,
                nesting=0,
                source_sha256=hashlib.sha256(chunk).hexdigest(),
                raw_source=chunk.decode("utf-8"),
            )
        )
        fragment_ranges.append((start, end))

    covered = union_length(fragment_ranges)
    comments = union_length(comment_ranges)
    error_ranges = [(d["startByte"], d["endByte"]) for d in diagnostics if d.get("kind") == "PARSE_ERROR"]
    missing = union_length(error_ranges)
    accounted = union_length(fragment_ranges + comment_ranges + error_ranges)
    omitted_other = max(0, len(source) - accounted)
    whitespace = _whitespace_omission(source, fragment_ranges, comment_ranges, error_ranges)

    outcome = _actual_outcome(tree, diagnostics, fixture)
    omissions = [
        {"reason": "COMMENT", "byteCount": comments},
        {"reason": "PARSE_ERROR", "byteCount": missing},
        {"reason": "WHITESPACE_OR_PUNCTUATION", "byteCount": whitespace},
        {"reason": "UNSUPPORTED_OR_UNACCOUNTED", "byteCount": omitted_other},
    ]
    return {
        "fixtureId": fixture.fixture_id,
        "language": fixture.language,
        "dialect": fixture.dialect,
        "parserKey": language_key,
        "actualOutcome": outcome,
        "operationalFailure": False,
        "hasError": tree.root_node.has_error,
        "fragments": [asdict(item) | {"raw_source": item.raw_source} for item in fragments],
        "namedFragments": [item.name for item in fragments if item.name and item.name != "__module__"],
        "diagnostics": diagnostics,
        "omissions": omissions,
        "coverage": {
            "sourceBytes": len(source),
            "coveredBytes": covered,
            "commentBytes": comments,
            "parseErrorBytes": missing,
            "omittedBytes": max(0, len(source) - covered),
            "unionCoveredBytes": covered,
        },
    }


def _parser_key(fixture: FixtureFile) -> str:
    if fixture.dialect == "tsx" or fixture.relative_path.endswith(".tsx"):
        return "tsx"
    return fixture.language


def _language_object(language_key: str):
    if language_key == "javascript":
        import tree_sitter_javascript as lang

        return lang.language()
    if language_key == "typescript":
        import tree_sitter_typescript as lang

        return lang.language_typescript()
    if language_key == "tsx":
        import tree_sitter_typescript as lang

        return lang.language_tsx()
    if language_key == "python":
        import tree_sitter_python as lang

        return lang.language()
    if language_key == "java":
        import tree_sitter_java as lang

        return lang.language()
    if language_key == "csharp":
        import tree_sitter_c_sharp as lang

        return lang.language()
    raise ValueError(f"unsupported parser key {language_key}")


def _walk(
    node,
    source: bytes,
    text: str,
    fixture: FixtureFile,
    fragments: list[ExtractedFragment],
    diagnostics: list[dict[str, Any]],
    comment_ranges: list[tuple[int, int]],
    nesting: int,
) -> None:
    if node.type in COMMENT_TYPES:
        comment_ranges.append((node.start_byte, node.end_byte))
    if node.type == "ERROR" or node.is_missing:
        start_line, start_col = byte_to_line_col(text, node.start_byte)
        end_line, end_col = byte_to_line_col(text, max(node.start_byte, node.end_byte - 1 if node.end_byte else node.start_byte))
        diagnostics.append(
            {
                "kind": "PARSE_ERROR",
                "startByte": node.start_byte,
                "endByte": node.end_byte,
                "startLine": start_line,
                "startColumn": start_col,
                "endLine": end_line,
                "endColumn": end_col,
                "missing": bool(node.is_missing),
            }
        )
    language_key = _parser_key(fixture)
    if node.type in FRAGMENT_TYPES[language_key]:
        name = _node_name(node, source)
        chunk = source[node.start_byte : node.end_byte]
        start_line, _ = byte_to_line_col(text, node.start_byte)
        end_line, _ = byte_to_line_col(text, max(node.start_byte, node.end_byte - 1))
        fragments.append(
            ExtractedFragment(
                fragment_id=f"{fixture.fixture_id}:{node.type}:{node.start_byte}",
                fixture_id=fixture.fixture_id,
                language=fixture.language,
                kind=node.type,
                name=name,
                start_byte=node.start_byte,
                end_byte=node.end_byte,
                start_line=start_line,
                end_line=end_line,
                nesting=nesting,
                source_sha256=hashlib.sha256(chunk).hexdigest(),
                raw_source=chunk.decode("utf-8"),
            )
        )
        child_nesting = nesting + 1
    else:
        child_nesting = nesting
    for child in node.children:
        _walk(child, source, text, fixture, fragments, diagnostics, comment_ranges, child_nesting)


def _node_name(node, source: bytes) -> str | None:
    for child in node.children:
        if child.type in {"identifier", "property_identifier", "type_identifier", "name"}:
            return source[child.start_byte : child.end_byte].decode("utf-8")
        nested = _node_name(child, source)
        if nested:
            return nested
    return None


def _module_level_ranges(root, fragment_ranges: list[tuple[int, int]]) -> list[tuple[int, int]]:
    occupied = sorted(fragment_ranges)
    ranges: list[tuple[int, int]] = []
    cursor = root.start_byte
    for start, end in occupied:
        if start > cursor:
            ranges.append((cursor, start))
        cursor = max(cursor, end)
    if cursor < root.end_byte:
        ranges.append((cursor, root.end_byte))
    return ranges


def _whitespace_omission(
    source: bytes,
    fragment_ranges: list[tuple[int, int]],
    comment_ranges: list[tuple[int, int]],
    error_ranges: list[tuple[int, int]],
) -> int:
    occupied = set()
    for start, end in fragment_ranges + comment_ranges + error_ranges:
        occupied.update(range(start, end))
    count = 0
    for index, byte in enumerate(source):
        if index in occupied:
            continue
        if byte in b" \t\r\n":
            count += 1
    return count


def _actual_outcome(tree, diagnostics: list[dict[str, Any]], fixture: FixtureFile) -> str:
    if tree.root_node.has_error or diagnostics:
        if fixture.expected_outcome in {"INVALID_PARSE", "PARTIAL"}:
            return fixture.expected_outcome
        return "INVALID_PARSE"
    return "VALID_EXTRACTED"


def _empty_coverage(size: int) -> dict[str, int]:
    return {
        "sourceBytes": size,
        "coveredBytes": 0,
        "commentBytes": 0,
        "parseErrorBytes": 0,
        "omittedBytes": size,
        "unionCoveredBytes": 0,
    }


def expectation_matched(fixture: FixtureFile, result: dict[str, Any]) -> bool:
    if result.get("operationalFailure"):
        return False
    if result["actualOutcome"] != fixture.expected_outcome:
        return False
    named = set(result.get("namedFragments", []))
    if fixture.expected_outcome == "VALID_EXTRACTED":
        if any(name not in named for name in fixture.expected_fragment_ids):
            return False
    for expected in fixture.expected_diagnostics:
        if not _diagnostic_matches(expected, result.get("diagnostics", [])):
            return False
    actual_omissions = {
        item.get("reason"): item for item in result.get("omissions") or [] if isinstance(item, dict)
    }
    for expected in fixture.expected_omissions:
        reason = expected.get("reason")
        actual = actual_omissions.get(reason)
        if actual is None:
            return False
        if expected.get("byteCount") is not None and actual.get("byteCount") != expected["byteCount"]:
            return False
        if reason == "PARSE_ERROR" and int(actual.get("byteCount") or 0) <= 0:
            if not any(item.get("kind") == "PARSE_ERROR" for item in result.get("diagnostics") or []):
                return False
    return True


def _diagnostic_matches(expected: dict[str, Any], actual: list[dict[str, Any]]) -> bool:
    for item in actual:
        if item.get("kind") != expected.get("kind"):
            continue
        if expected.get("startLine") is not None and item.get("startLine") != expected["startLine"]:
            continue
        if expected.get("startByteMin") is not None and item.get("startByte", -1) < expected["startByteMin"]:
            continue
        if expected.get("startByteMax") is not None and item.get("startByte", 10**9) > expected["startByteMax"]:
            continue
        return True
    return False
