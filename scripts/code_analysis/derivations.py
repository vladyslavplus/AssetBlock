"""Source-preserving query derivations; semantic labels remain independently reviewed."""

from scripts.feasibility_pilot.extract import parse_source

from .contracts import canonical, digest

IDENT_TYPES = {"identifier", "simple_identifier"}
FUNCTION_TYPES = {"function_declaration", "function_definition", "method_declaration", "method_definition",
                  "local_function_statement", "arrow_function", "constructor_declaration"}
PROPERTY_PARENTS = {"property_identifier", "shorthand_property_identifier", "property_name",
                    "field_identifier", "field_declaration"}
MEMBER_PARENTS = {"member_expression", "attribute", "member_access_expression", "field_access",
                  "element_access_expression"}


def exact_copy(raw, file):
    if digest(raw) != file["sha256"]:
        raise ValueError("derivation parent source drift")
    if file.get("partition") not in ("train", "validation", "final-test") or not file.get("familyId"):
        raise ValueError("immutable family/partition required before derivation")
    if not file.get("assignmentReviewed") or not file.get("permissions", {}).get("evaluationAllowed"):
        raise ValueError("reviewed assignment and evaluation grant required")
    record = {"parentFileId": file["fileId"], "parentSha256": file["sha256"], "familyId": file["familyId"],
              "partition": file["partition"], "transformation": "EXACT_COPY", "version": "exact-copy-v1",
              "seed": 42, "outputSha256": digest(raw), "changedRanges": [], "status": "DRAFT_UNREVIEWED",
              "similarityLabel": "UNKNOWN", "reviewer": None, "galleryEligible": False}
    record["derivationId"] = digest(canonical(record))
    return raw, record


def comment_format(raw, file):
    language = file.get("dialect", file["language"])
    marker = {"python": b"\n# format-preserving diagnostic comment\n",
              "java": b"\n/* format-preserving diagnostic comment */\n",
              "csharp": b"\n/* format-preserving diagnostic comment */\n"}.get(language, b"\n/* format-preserving diagnostic comment */\n")
    output = raw.replace(b"\n", b"\n\n", 1) + marker if b"\n" in raw else raw + marker
    if digest(output) == file["sha256"]:
        raise ValueError("format transformation produced identical bytes")
    record = {**exact_copy(raw, file)[1], "transformation": "COMMENT_FORMAT", "version": "comment-format-v1",
              "outputSha256": digest(output), "changedRanges": [{"kind": "comment-or-whitespace"}]}
    del record["derivationId"]
    record["derivationId"] = digest(canonical(record))
    return output, record


def _text(raw, node):
    return raw[node.start_byte:node.end_byte]


def _field(node, *names):
    for name in names:
        child = node.child_by_field_name(name)
        if child is not None:
            return child
    return None


def _functions(root):
    found, stack = [], [root]
    while stack:
        node = stack.pop()
        if node.type in FUNCTION_TYPES:
            found.append(node)
        stack.extend(reversed(node.children))
    return found


PARAM_NODE_TYPES = {"parameter", "required_parameter", "optional_parameter", "rest_parameter",
                    "typed_parameter", "default_parameter", "typed_default_parameter", "formal_parameter",
                    "list_splat_pattern", "dictionary_splat_pattern"}
SKIP_PARAM_FIELDS = {"type", "value", "default", "annotation"}
SKIP_PARAM_TOKENS = {"(", ")", ",", ":", "=", "?", "*", "**", "keyword_separator", "positional_separator",
                     "comment"}
UNSUPPORTED_PARAM_TYPES = {"tuple_pattern", "list_pattern", "object_pattern", "array_pattern",
                           "assignment_pattern", "parenthesized_pattern", "destructured_parameter",
                           "object_binding_pattern", "array_binding_pattern"}
BLOCK_BIND_TYPES = {"lexical_declaration", "variable_declaration", "using_declaration",
                    "for_in_statement", "for_of_statement", "except_clause", "catch_clause"}
BLOCK_SCOPE_TYPES = {"statement_block", "block", "compound_statement", "block_statement"}


def _is_binding_name(name):
    if not isinstance(name, (bytes, bytearray)) or len(name) < 2:
        return False
    first, rest = name[:1], name[1:]
    if not (first.isalpha() or first == b"_"):
        return False
    return all((byte >= 65 and byte <= 90) or (byte >= 97 and byte <= 122) or (byte >= 48 and byte <= 57)
               or byte == 95 for byte in rest)


def _simple_ident(node):
    if node is None:
        return None
    if node.type in IDENT_TYPES and node.child_count == 0:
        return node
    if node.type in {"rest_pattern", "list_splat_pattern", "dictionary_splat_pattern"}:
        found = None
        for child in node.children:
            ident = _simple_ident(child)
            if ident is None:
                continue
            if found is not None:
                return None
            found = ident
        return found
    return None


def _param_binding_node(param):
    if param.type in UNSUPPORTED_PARAM_TYPES:
        return None
    named = _field(param, "name")
    if named is not None:
        return _simple_ident(named)
    pattern = _field(param, "pattern")
    if pattern is not None:
        return _simple_ident(pattern)
    if param.type in IDENT_TYPES:
        return _simple_ident(param)
    found = None
    for index, child in enumerate(param.children):
        field = param.field_name_for_child(index)
        if field in SKIP_PARAM_FIELDS or child.type in SKIP_PARAM_TOKENS:
            continue
        ident = _simple_ident(child)
        if ident is None:
            if child.type in IDENT_TYPES or child.type in UNSUPPORTED_PARAM_TYPES or child.type in PARAM_NODE_TYPES:
                return None
            continue
        if found is not None:
            return None
        found = ident
    return found


def _parameter_names(raw, function):
    params = _field(function, "parameters", "formal_parameters", "parameter_list")
    if params is None:
        return []
    names = []
    for child in params.children:
        if child.type in SKIP_PARAM_TOKENS:
            continue
        if child.type in UNSUPPORTED_PARAM_TYPES:
            return None
        binding = _param_binding_node(child)
        if binding is None:
            return None
        name = _text(raw, binding)
        if _is_binding_name(name):
            names.append((name, binding))
    names.sort(key=lambda item: item[1].start_byte)
    return names


def _ident_in(raw, node, name):
    stack = [node]
    while stack:
        current = stack.pop()
        if current.type in IDENT_TYPES and _text(raw, current) == name and not _is_property(current):
            return True
        stack.extend(current.children)
    return False


def _bind_declares(raw, node, name):
    if node.type in FUNCTION_TYPES:
        return any(item[0] == name for item in (_parameter_names(raw, node) or []))
    target = _field(node, "name", "pattern", "left") or node
    if node.type in {"lexical_declaration", "variable_declaration", "using_declaration"}:
        for child in node.children:
            if child.type in {"variable_declarator", "lexical_declaration"}:
                declared = _field(child, "name", "pattern")
                if declared is not None and _ident_in(raw, declared, name):
                    return True
            elif child.type in IDENT_TYPES and _text(raw, child) == name:
                return True
        return False
    return _ident_in(raw, target, name)


def _local_rebind(raw, function, name):
    stack = list(function.children)
    while stack:
        node = stack.pop()
        if node.type in FUNCTION_TYPES:
            continue
        if node.type in {"assignment", "augmented_assignment", "named_expression"}:
            left = _field(node, "left", "name")
            if left is not None and _ident_in(raw, left, name):
                return True
        stack.extend(node.children)
    return False


def _inner_shadows(raw, function, name, binding=None):
    blocked = []
    stack = list(function.children)
    while stack:
        node = stack.pop()
        if node.type in FUNCTION_TYPES:
            if _bind_declares(raw, node, name) or _local_rebind(raw, node, name):
                blocked.append((node.start_byte, node.end_byte))
            continue
        if node.type in BLOCK_BIND_TYPES and _bind_declares(raw, node, name):
            if (binding is not None and node.start_byte <= binding.start_byte
                    and binding.end_byte <= node.end_byte):
                stack.extend(node.children)
                continue
            scope = node.parent if node.parent is not None and node.parent.type in BLOCK_SCOPE_TYPES else node
            blocked.append((scope.start_byte, scope.end_byte))
            continue
        stack.extend(node.children)
    return blocked


def _has_named_argument(raw, tree, name):
    stack = [tree.root_node]
    while stack:
        node = stack.pop()
        if node.type in IDENT_TYPES and _text(raw, node) == name and _is_keyword_name(node):
            return True
        stack.extend(node.children)
    return False


def _node_in_ranges(node, ranges):
    return any(start <= node.start_byte and node.end_byte <= end for start, end in ranges)


def _is_property(node):
    parent = node.parent
    if parent is None:
        return False
    if parent.type in PROPERTY_PARENTS:
        return True
    if parent.type in MEMBER_PARENTS:
        obj = _field(parent, "object", "value")
        prop = _field(parent, "property", "attribute", "name")
        if prop is not None and node.start_byte == prop.start_byte and node.end_byte == prop.end_byte:
            return True
        if obj is not None and node.start_byte == obj.start_byte and node.end_byte == obj.end_byte:
            return False
        identifiers = [child for child in parent.children if child.type in IDENT_TYPES]
        if identifiers and node is identifiers[-1] and node is not identifiers[0]:
            return True
    return False


def _is_keyword_name(node):
    parent = node.parent
    if parent is None:
        return False
    if parent.type in {"keyword_argument", "named_argument"}:
        name = _field(parent, "name")
        return name is not None and node.start_byte == name.start_byte
    if parent.type == "assignment" and parent.parent and parent.parent.type in {"argument_list", "arguments"}:
        left = _field(parent, "left")
        return left is not None and node.start_byte == left.start_byte
    return False


def _is_shorthand_property(node):
    return node.type == "shorthand_property_identifier" or (
        node.parent is not None and node.parent.type == "shorthand_property_identifier")


def _function_local_bindings(raw, function):
    parameters = _parameter_names(raw, function)
    if parameters is None:
        return None
    param_names = {name for name, _ in parameters}
    found = []
    seen = set()
    stack = list(function.children)
    while stack:
        node = stack.pop()
        if node.type in FUNCTION_TYPES:
            continue
        ident = None
        if node.type in {"assignment", "augmented_assignment", "named_expression"}:
            ident = _simple_ident(_field(node, "left", "name"))
        elif node.type == "variable_declarator":
            ident = _simple_ident(_field(node, "name", "pattern"))
        elif node.type in {"lexical_declaration", "variable_declaration", "using_declaration",
                           "local_declaration_statement"}:
            for child in node.children:
                declared = None
                if child.type in {"variable_declarator", "variable_declaration"}:
                    declared = _field(child, "name", "pattern")
                elif child.type in IDENT_TYPES:
                    declared = child
                child_ident = _simple_ident(declared) if declared is not None else None
                if child_ident is None:
                    continue
                name = _text(raw, child_ident)
                if _is_binding_name(name) and name not in param_names and name not in seen:
                    found.append((name, child_ident))
                    seen.add(name)
        if ident is not None:
            name = _text(raw, ident)
            if _is_binding_name(name) and name not in param_names and name not in seen:
                found.append((name, ident))
                seen.add(name)
        stack.extend(reversed(node.children))
    found.sort(key=lambda item: item[1].start_byte)
    return found


def ast_rename(raw, file, *, replacement=b"analysis_renamed_local"):
    language = file.get("dialect", file["language"])
    tree = parse_source(language, raw)
    if tree.root_node.has_error:
        raise ValueError("AST rename refused invalid source")
    for function in sorted(_functions(tree.root_node), key=lambda node: (node.start_byte, -(node.end_byte - node.start_byte))):
        locals_found = _function_local_bindings(raw, function)
        if locals_found is None:
            continue
        for name, param in locals_found:
            if name == replacement:
                continue
            chosen = replacement if replacement != name else b"analysis_renamed_localx"
            if _scope_contains_identifier(raw, function, chosen) and chosen != name:
                continue
            if _has_named_argument(raw, tree, name):
                continue
            blocked = _inner_shadows(raw, function, name, binding=param)
            replacements = []
            stack = [function]
            while stack:
                node = stack.pop()
                if _node_in_ranges(node, blocked) and node is not function:
                    continue
                token = _text(raw, node)
                if node.type in IDENT_TYPES | {"shorthand_property_identifier"} and token == name:
                    if _is_keyword_name(node) or _is_property(node):
                        stack.extend(reversed(node.children))
                        continue
                    if _is_shorthand_property(node):
                        replacements.append((node.start_byte, node.end_byte, name + b": " + chosen))
                    else:
                        replacements.append((node.start_byte, node.end_byte, chosen))
                stack.extend(reversed(node.children))
            if not replacements:
                continue
            ordered = sorted(replacements, key=lambda item: item[0], reverse=True)
            if any(ordered[index][1] > ordered[index - 1][0] for index in range(1, len(ordered))):
                continue
            output = bytearray(raw)
            for start, end, text in ordered:
                output[start:end] = text
            output = bytes(output)
            rewritten = parse_source(language, output)
            if rewritten.root_node.has_error:
                continue
            record = {**exact_copy(raw, file)[1], "transformation": "AST_BOUND_RENAME", "version": "ast-rename-v4",
                      "outputSha256": digest(output),
                      "changedRanges": [{"kind": "binding-identifier", "from": name.decode("utf-8"),
                                         "to": chosen.decode("utf-8"),
                                         "replacements": len(replacements)}]}
            del record["derivationId"]
            record["derivationId"] = digest(canonical(record))
            return output, record
    raise ValueError("no binding-safe local identifier available for AST rename")


def _scope_contains_identifier(raw, function, name):
    stack = [function]
    while stack:
        node = stack.pop()
        if node.type in IDENT_TYPES and _text(raw, node) == name:
            return True
        stack.extend(node.children)
    return False
