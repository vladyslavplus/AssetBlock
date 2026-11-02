"""Declaration reconciliation: contradiction, missing scope, unknown signal."""

from types import SimpleNamespace

from app.stages import Pipeline, _reconcile_declaration


def _pipe(rows, declared):
    metadata = SimpleNamespace(declared_licenses=tuple(
        SimpleNamespace(scope_path=scope, spdx=spdx) for scope, spdx in declared))
    context = SimpleNamespace(metadata=metadata)
    inventory = SimpleNamespace(files=[
        SimpleNamespace(canonical_path=row["path"], sha256=("a" if i % 2 == 0 else "b") * 64)
        for i, row in enumerate(rows)
    ])
    pipe = Pipeline(context=context, inventory=inventory)
    pipe.license_rows = rows
    return pipe


def test_declared_match_produces_no_finding():
    pipe = _pipe([{"path": "calc/a.js", "expression": "MIT", "copyrightStatements": 1}],
                 [("calc", "MIT")])
    _reconcile_declaration(pipe)
    assert pipe.findings == []


def test_declaration_contradiction_is_a_finding_not_failure():
    pipe = _pipe([{"path": "calc/a.js", "expression": "Apache-2.0",
                   "copyrightStatements": 1}], [("calc", "MIT")])
    _reconcile_declaration(pipe)
    assert [f["type"] for f in pipe.findings] == ["LICENSE_CONTRADICTION"]
    assert pipe.findings[0]["path"] == "calc/a.js"
    assert pipe.findings[0]["fileId"]
    assert "retrieval" not in pipe.findings[0]


def test_unknown_detection_is_a_concern():
    pipe = _pipe([{"path": "calc/a.js", "expression": "UNKNOWN",
                   "copyrightStatements": 0}], [("calc", "MIT")])
    _reconcile_declaration(pipe)
    assert [f["type"] for f in pipe.findings] == ["LICENSE_UNKNOWN"]
    assert pipe.findings[0]["path"] == "calc/a.js"
    assert pipe.findings[0]["fileId"]
    assert "retrieval" not in pipe.findings[0]


def test_unscanned_scope_is_missing_notice_without_file_attribution():
    rows = [{"path": "calc/a.js", "expression": "MIT", "copyrightStatements": 1}]
    pipe = _pipe(rows, [("docs", "MIT")])
    _reconcile_declaration(pipe)
    finding = pipe.findings[0]
    assert [f["type"] for f in pipe.findings] == ["NOTICE_MISSING"]
    assert finding["path"] == "docs"
    assert finding["fileId"] is None
    assert finding["startLine"] is None and finding["endLine"] is None
    assert finding["startByte"] is None and finding["endByte"] is None
    assert finding["ownExcerpt"] == ""
    assert "Declared license scope" in finding["reason"]
    assert "calc/a.js" not in finding["path"]
    assert "retrieval" not in finding


def test_unscanned_scope_finding_does_not_depend_on_archive_order():
    rows = [
        {"path": "calc/a.js", "expression": "MIT", "copyrightStatements": 1},
        {"path": "vendor/b.js", "expression": "MIT", "copyrightStatements": 1},
    ]
    first = _pipe(rows, [("docs", "MIT")])
    second = _pipe(list(reversed(rows)), [("docs", "MIT")])
    _reconcile_declaration(first)
    _reconcile_declaration(second)
    assert first.findings == second.findings
    assert first.findings[0]["path"] == "docs"
    assert first.findings[0]["fileId"] is None
