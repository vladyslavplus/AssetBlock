"""Static, non-executing behavior-preserving transformations of an original fragment.

Transformations are candidates only. They carry no reviewer identity and no
accepted label; an independent reviewer decides whether the changed bytes remain
behavior-preserving before the pair can enter an admitted training triplet.

Existing `scripts.code_analysis.derivations` support is reused: an AST-bound
function-local rename when the fragment parses cleanly, otherwise the accepted
comment/whitespace format transformation.
"""

from scripts.code_analysis.contracts import digest
from scripts.code_analysis.derivations import ast_rename, comment_format

AST_RENAME = "AST_BOUND_RENAME"
COMMENT_FORMAT = "COMMENT_FORMAT"


def ast_rename_positive(raw, file):
    try:
        output, record = ast_rename(raw, file)
    except ValueError:
        return None
    if digest(output) == digest(raw):
        return None
    return output, record


def comment_format_positive(raw, file):
    try:
        output, record = comment_format(raw, file)
    except ValueError:
        return None
    if digest(output) == digest(raw):
        return None
    return output, record


def propose_positive(raw, file):
    """Return a candidate positive fragment or None when no supported rewrite applies."""
    renamed = ast_rename_positive(raw, file)
    if renamed is not None:
        output, record = renamed
        return {"transformation": AST_RENAME, "version": record["version"], "output": output,
                "changedRanges": record.get("changedRanges", []),
                "rationale": ("A single function-local binding is renamed at every bound "
                              "identifier reference. Global/nonlocal bindings, shadowing, argument names "
                              "and scope collisions refuse the candidate, and the rewrite must re-parse "
                              "cleanly. Reviewers still decide whether the changed bytes are equivalent.")}
    formatted = comment_format_positive(raw, file)
    if formatted is None:
        return None
    output, record = formatted
    return {"transformation": COMMENT_FORMAT, "version": record["version"], "output": output,
            "changedRanges": record.get("changedRanges", []),
            "rationale": ("A blank line and a trailing diagnostic comment are added outside "
                          "executable expressions; comments and whitespace are inert, so the "
                          "fragment bytes differ while executed behavior is unchanged.")}


def positive_identity(output):
    return digest(output)
