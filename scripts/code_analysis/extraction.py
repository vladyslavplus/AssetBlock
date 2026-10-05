"""Source-neutral executable AST units, separate context, and raw-byte accounting."""

from pathlib import Path

from scripts.feasibility_pilot.extract import COMMENT_TYPES, parse_source, union_length
from scripts.feasibility_pilot.inventory import byte_to_line_col

from .contracts import CAPS, LANGUAGES, canonical, digest, report, sha
from .splits import parser_identity

EXTRACTION = "executable-ast-units-v1"
FUNCTIONS = {
    "javascript": {"function_declaration", "function_expression", "arrow_function", "method_definition", "generator_function_declaration", "generator_function"},
    "typescript": {"function_declaration", "function_expression", "arrow_function", "method_definition", "generator_function_declaration", "generator_function"},
    "tsx": {"function_declaration", "function_expression", "arrow_function", "method_definition"},
    "python": {"function_definition"},
    "java": {"method_declaration", "constructor_declaration", "compact_constructor_declaration", "lambda_expression"},
    "csharp": {"method_declaration", "constructor_declaration", "local_function_statement", "lambda_expression", "anonymous_method_expression", "destructor_declaration", "accessor_declaration"},
}
CONTEXT = {"class_declaration", "class_definition", "interface_declaration", "type_alias_declaration", "abstract_class_declaration", "record_declaration", "enum_declaration", "import_statement", "import_declaration", "import_from_statement", "using_directive", "namespace_declaration", "file_scoped_namespace_declaration", "package_declaration", "preproc_if", "preproc_def"}
MODULE = {"expression_statement", "lexical_declaration", "variable_declaration", "assignment", "augmented_assignment", "if_statement", "for_statement", "while_statement", "try_statement", "with_statement", "global_statement", "throw_statement", "return_statement"}


def _ranges_complement(size, ranges):
    cursor = 0
    result = []
    for start, end in sorted(ranges):
        if start > cursor: result.append((cursor, start))
        cursor = max(cursor, end)
    if cursor < size: result.append((cursor, size))
    return result


def extract_source(raw, file):
    """Parse bytes only; never import, compile or execute submitted source."""
    sha(file["fileId"])
    if not raw or len(raw) > CAPS["fileBytes"] or digest(raw) != file["sha256"]:
        raise ValueError("empty/oversized/hash-drift source")
    text = raw.decode("utf-8", errors="strict")
    if b"\0" in raw: raise ValueError("binary source")
    language = file["language"]
    parser_key = file.get("dialect", language)
    if language not in LANGUAGES or parser_key not in FUNCTIONS:
        return report({"fileId": file["fileId"], "actualOutcome": "UNSUPPORTED_DIALECT", "fragments": [], "searchableExecutable": False})
    identity = {"algorithm": EXTRACTION, "implementationSha256": digest(Path(__file__).read_bytes()), "parsers": parser_identity(), "parserKey": parser_key}
    identity_hash = digest(canonical(identity))
    try:
        root = parse_source(parser_key, raw).root_node
    except Exception as exc:
        return report({"fileId": file["fileId"], "actualOutcome": "FAILED", "operationalFailure": True, "error": type(exc).__name__, "fragments": [], "searchableExecutable": False})
    fragments, contexts, comments, errors = [], [], [], []

    def span(node): return (node.start_byte, node.end_byte)

    def unit(node, kind, start=None):
        start = node.start_byte if start is None else start
        end = node.end_byte
        if end <= start: return
        name_node = node.child_by_field_name("name")
        name = raw[name_node.start_byte:name_node.end_byte].decode("utf-8") if name_node else None
        start_line, start_col = byte_to_line_col(text, start)
        # Display inclusive end at the final complete codepoint, never an interior UTF-8 byte.
        last_codepoint = end - 1
        while last_codepoint > start and raw[last_codepoint] & 0xC0 == 0x80:
            last_codepoint -= 1
        end_line, end_col = byte_to_line_col(text, last_codepoint)
        identity_values = [file["fileId"], file["sha256"], identity_hash, kind, start, end]
        fragments.append({"fragmentId": digest(canonical(identity_values)), "fileId": file["fileId"], "sourceSha256": file["sha256"], "language": language, "dialect": parser_key, "kind": kind, "name": name, "startByte": start, "endByte": end, "startLine": start_line, "startColumn": start_col, "endLine": end_line, "endColumn": end_col, "sha256": digest(raw[start:end]), "searchable": not root.has_error and not file.get("generated") and not file.get("vendor") and not file.get("normalizationExcludedReason"), "executable": True, "parentFragmentId": None, "contextIds": [], "overlapIds": []})

    stack = [root]
    while stack:
        node = stack.pop()
        if node.type in COMMENT_TYPES: comments.append(span(node))
        if node.type == "ERROR" or node.is_missing:
            errors.append({"kind": "PARSE_ERROR", "startByte": node.start_byte, "endByte": node.end_byte, "missing": bool(node.is_missing)})
        if node.type in CONTEXT:
            contexts.append({"contextId": digest(canonical([file['fileId'], identity_hash, node.type, *span(node)])), "kind": node.type, "startByte": node.start_byte, "endByte": node.end_byte, "searchable": False})
        if node.type in FUNCTIONS[parser_key]:
            body = node.child_by_field_name("body")
            has_body = body is not None or node.type in {"lambda_expression", "arrow_function", "anonymous_method_expression"} or any(c.type in {"block", "constructor_body", "arrow_expression_clause"} for c in node.children)
            if has_body:
                decorated = node.parent and node.parent.type == "decorated_definition"
                unit(node, "decorated_function" if decorated else node.type, node.parent.start_byte if decorated else None)
        stack.extend(reversed(node.children))
    occupied = [(f["startByte"], f["endByte"]) for f in fragments]
    # Only complete direct AST statements become residual units; no arbitrary byte slicing.
    for node in root.named_children:
        # Default exported invocation/delegation executes module logic; export lists alone do not.
        exported_logic = False
        if node.type == 'export_statement':
            descendants = list(node.children)
            while descendants:
                child = descendants.pop()
                if child.type in {'call_expression', 'new_expression', 'await_expression', 'assignment_expression'}:
                    exported_logic = True
                    break
                descendants.extend(child.children)
        if (node.type in MODULE or exported_logic) and not any(node.start_byte <= a and b <= node.end_byte for a, b in occupied):
            unit(node, "module_statement")
    if len(fragments) > 5000: raise ValueError("fragment cap exceeded")
    for fragment in fragments:
        parents = [p for p in fragments if p is not fragment and p["startByte"] <= fragment["startByte"] and fragment["endByte"] <= p["endByte"]]
        if parents: fragment["parentFragmentId"] = min(parents, key=lambda p: p["endByte"] - p["startByte"])["fragmentId"]
        fragment["contextIds"] = [c["contextId"] for c in contexts if c["startByte"] <= fragment["startByte"] and fragment["endByte"] <= c["endByte"]]
        fragment["overlapIds"] = sorted(p["fragmentId"] for p in fragments if p is not fragment and max(p["startByte"], fragment["startByte"]) < min(p["endByte"], fragment["endByte"]))
    searchable_ranges = [(f['startByte'], f['endByte']) for f in fragments if f['searchable']]
    ranges = [(f['startByte'], f['endByte']) for f in fragments]
    context_ranges = [(c['startByte'], c['endByte']) for c in contexts]
    error_ranges = [(e['startByte'], e['endByte']) for e in errors]
    gaps = _ranges_complement(len(raw), ranges + context_ranges + comments + error_ranges)
    omissions = [{"startByte": a, "endByte": b, "reason": "WHITESPACE" if not raw[a:b].strip() else "UNSUPPORTED_OR_UNACCOUNTED", "sha256": digest(raw[a:b])} for a, b in gaps]
    outcome = "INVALID_PARSE" if root.has_error or errors else "VALID_EXTRACTED"
    return report({"fileId": file['fileId'], "sourceSha256": file['sha256'], "actualOutcome": outcome, "operationalFailure": False, "extractionIdentity": identity, "extractionSha256": identity_hash, "fragments": sorted(fragments, key=lambda f: (f['startByte'], f['endByte'], f['fragmentId'])), "contexts": contexts, "diagnostics": errors, "comments": [{"startByte": a, "endByte": b} for a, b in comments], "omissions": omissions, "searchableExecutable": bool(searchable_ranges), "coverage": {"sourceBytes": len(raw), "unionExecutableBytes": union_length(ranges), "unionSearchableBytes": union_length(searchable_ranges), "unionAccountedBytes": union_length(ranges + context_ranges + comments + error_ranges), "omittedBytes": sum(b - a for a, b in gaps)}})
