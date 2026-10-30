"""Checks that canonical run evidence contains identities and counts only."""

from scripts.code_analysis.contracts import canonical, digest

FORBIDDEN_KEYS = {"sourcetext", "body", "vectors", "vector", "dsn", "password", "secret", "token"}


def validate(value):
    def visit(item):
        if isinstance(item, dict):
            for key, nested in item.items():
                if key.lower() in FORBIDDEN_KEYS:
                    raise ValueError("evidence contains a forbidden field")
                visit(nested)
        elif isinstance(item, list):
            for nested in item:
                visit(nested)
        elif isinstance(item, str):
            lowered = item.lower()
            if "postgres://" in lowered or "postgresql://" in lowered or "source text" in lowered:
                raise ValueError("evidence contains a secret or source text")
    visit(value)
    return digest(canonical(value))
