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
# A catch/except clause binds only its declared parameter or alias pattern. An
# ordinary write to an enclosing local inside the clause body is not a
# declaration, so such a clause must not shadow that local.
CATCH_CLAUSE_TYPES = {"catch_clause", "except_clause"}
CATCH_PARAMETER_TYPES = {"catch_formal_parameter", "catch_declaration", "catch_parameter"}


class _UnresolvedBinding(Exception):
    """A scope binding could not be resolved from the grammar.

    The rename candidate is then refused instead of being applied partially.
    """
# Python statements that rebind a name in an enclosing/module scope. Renaming such
# a name is not behavior preserving, so those bindings are never rename candidates.
GLOBAL_BIND_TYPES = {"global_statement", "nonlocal_statement"}


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


def _is_identifier_text(name):
    """A plain identifier spelling; single-character names are legitimate."""
    if not isinstance(name, (bytes, bytearray)) or len(name) < 1:
        return False
    first, rest = name[:1], name[1:]
    if not (first.isalpha() or first == b"_"):
        return False
    return all((byte >= 65 and byte <= 90) or (byte >= 97 and byte <= 122) or (byte >= 48 and byte <= 57)
               or byte == 95 for byte in rest)


def _catch_binding(raw, node):
    """The identifier a catch/except clause actually declares, if any.

    Only the declared parameter or alias pattern counts. A plain assignment or
    reference inside the clause body never declares anything, so an enclosing
    local written there stays visible to the rename. When the grammar does not
    expose a resolvable binding, the status is "unresolved" and the caller
    refuses the rename candidate rather than emitting a partial rewrite.
    """
    if node.type == "except_clause":
        value = _field(node, "value")
        if value is None:
            return ("unresolved", None)
        if value.type == "as_pattern":
            targets = [child for child in value.children if child.type == "as_pattern_target"]
            if len(targets) != 1:
                return ("unresolved", None)
            name = _text(raw, targets[0])
            return ("bound", name) if _is_identifier_text(name) else ("unresolved", None)
        if value.type in IDENT_TYPES:
            # `except ValueError:` deletes the name and declares nothing.
            return ("none", None)
        return ("unresolved", None)
    parameter = None
    for child in node.children:
        if child.type in CATCH_PARAMETER_TYPES:
            parameter = child
            break
    if parameter is None:
        return ("unresolved", None)
    name_node = _field(parameter, "name")
    if name_node is None:
        if parameter.type == "catch_declaration":
            # C# `catch (Exception)` declares no variable.
            return ("none", None)
        return ("unresolved", None)
    name = _text(raw, name_node)
    return ("bound", name) if _is_identifier_text(name) else ("unresolved", None)


def _bind_declares(raw, node, name):
    if node.type in CATCH_CLAUSE_TYPES:
        status, bound = _catch_binding(raw, node)
        if status == "unresolved":
            raise _UnresolvedBinding(node.type)
        return status == "bound" and bound == name
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


def _global_bound_names(raw, function):
    """Names a function rebinds in an enclosing or module scope (global/nonlocal)."""
    names = set()
    stack = list(function.children)
    while stack:
        node = stack.pop()
        if node.type in FUNCTION_TYPES:
            continue
        if node.type in GLOBAL_BIND_TYPES:
            for child in node.children:
                if child.type in IDENT_TYPES and _is_binding_name(_text(raw, child)):
                    names.add(_text(raw, child))
            continue
        stack.extend(node.children)
    return names


def _nonlocal_bound_names(raw, function):
    """Names a function rebinds in an enclosing function scope (Python `nonlocal`)."""
    names = set()
    stack = list(function.children)
    while stack:
        node = stack.pop()
        if node.type in FUNCTION_TYPES:
            continue
        if node.type == "nonlocal_statement":
            for child in node.children:
                if child.type in IDENT_TYPES and _is_binding_name(_text(raw, child)):
                    names.add(_text(raw, child))
            continue
        stack.extend(node.children)
    return names


def _nested_functions(function):
    found, stack = [], list(function.children)
    while stack:
        node = stack.pop()
        if node.type in FUNCTION_TYPES:
            found.append(node)
            continue
        stack.extend(node.children)
    return found


def _ident_occurs(raw, scope, name):
    stack = [scope]
    while stack:
        node = stack.pop()
        if node.type in IDENT_TYPES and _text(raw, node) == name:
            return True
        stack.extend(node.children)
    return False


def _nested_shadows(raw, nested, name, language):
    """True when the nested scope binds `name` to its own variable.

    A nested scope that binds its own `name` may keep the original identifier while
    the enclosing binding is renamed. When it does not, the blocked occurrences
    still refer to the enclosing binding, so renaming only the enclosing scope would
    change which variable the nested scope reads or writes.
    """
    if any(item[0] == name for item in (_parameter_names(raw, nested) or [])):
        return True
    if language == "python":
        # An assignment makes the name local to the nested function unless the
        # function declares it `nonlocal`, which links it to the enclosing scope.
        return name not in _nonlocal_bound_names(raw, nested)
    stack = list(nested.children)
    while stack:
        node = stack.pop()
        if node.type in FUNCTION_TYPES:
            continue
        if node.type in {"lexical_declaration", "variable_declaration", "using_declaration",
                         "variable_declarator"} and _bind_declares(raw, node, name):
            return True
        stack.extend(node.children)
    return False


def _blocked_nested_conflict(raw, function, name, blocked, language):
    """A blocked nested scope that still binds the enclosing scope's `name`.

    The rename must not be applied: the nested occurrences are skipped while the
    enclosing binding is rewritten, so the nested scope would keep naming a
    variable that no longer exists (a Python `nonlocal`, or an undeclared write to
    an outer binding in languages without implicit locals).
    """
    if not blocked:
        return False
    for nested in _nested_functions(function):
        if not any(nested.start_byte < end and start < nested.end_byte for start, end in blocked):
            continue
        if _nested_shadows(raw, nested, name, language):
            continue
        return True
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


def _pattern_identifiers(raw, node):
    """Identifiers a pattern binds, even when the pattern is not a simple name.

    A destructuring pattern cannot be renamed safely, so the names it binds are
    reserved: they are never rename candidates.
    """
    names = []
    stack = [node]
    while stack:
        current = stack.pop()
        if current.type in IDENT_TYPES:
            names.append(_text(raw, current))
        stack.extend(current.children)
    return names


def _declared_names(raw, node):
    """Binding names a declaration container declares, from declarator fields only.

    Only a grammar-declared variable declarator/name/pattern field names a
    binding. A declaration's TYPE subtree never names a local: `Exception fail`
    declares `fail`, not `Exception`. A declarator whose pattern is not a plain
    identifier reserves the names that pattern binds.
    """
    names = []
    reserved = []
    for child in node.children:
        if child.type == "variable_declarator":
            named = _field(child, "name", "pattern")
            if named is None:
                continue
            ident = _simple_ident(named)
            if ident is None:
                reserved.extend(_pattern_identifiers(raw, named))
                continue
            names.append((_text(raw, ident), ident))
        elif child.type in {"variable_declaration", "lexical_declaration"}:
            sub_names, sub_reserved = _declared_names(raw, child)
            names.extend(sub_names)
            reserved.extend(sub_reserved)
    return names, reserved


def _function_local_bindings(raw, function):
    parameters = _parameter_names(raw, function)
    if parameters is None:
        return None
    param_names = {name for name, _ in parameters}
    found = []
    seen = set()
    reserved = set()
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
            declared, reserved_names = _declared_names(raw, node)
            reserved.update(reserved_names)
            for name, child_ident in declared:
                if _is_binding_name(name) and name not in param_names and name not in seen:
                    found.append((name, child_ident))
                    seen.add(name)
        if ident is not None:
            name = _text(raw, ident)
            if _is_binding_name(name) and name not in param_names and name not in seen:
                found.append((name, ident))
                seen.add(name)
        stack.extend(reversed(node.children))
    # A name bound by a pattern that cannot be renamed is never a candidate.
    found = [item for item in found if item[0] not in reserved]
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
        global_bound = _global_bound_names(raw, function)
        for name, param in locals_found:
            if name == replacement or name in global_bound:
                continue
            chosen = replacement if replacement != name else b"analysis_renamed_localx"
            if _scope_contains_identifier(raw, function, chosen) and chosen != name:
                continue
            if _has_named_argument(raw, tree, name):
                continue
            try:
                blocked = _inner_shadows(raw, function, name, binding=param)
                if _blocked_nested_conflict(raw, function, name, blocked, language):
                    continue
            except _UnresolvedBinding:
                # A nested scope binding could not be resolved from the grammar, so
                # the rename candidate is refused instead of rewriting only part
                # of the name's occurrences.
                continue
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
