"""Declaration reconciliation: contradiction, missing scope, unknown signal."""

from types import SimpleNamespace

from app.stages import Pipeline, _reconcile_declaration


def _pipe(rows, declared):
    metadata = SimpleNamespace(declared_licenses=tuple(
        SimpleNamespace(scope_path=s, spdx=p) for s, p in declared))
    context = SimpleNamespace(metadata=metadata)
    pipe = Pipeline(context=context)
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
    assert pipe.findings[0]["path"] is None


def test_unknown_detection_is_a_concern():
    pipe = _pipe([{"path": "calc/a.js", "expression": "UNKNOWN",
                   "copyrightStatements": 0}], [("calc", "MIT")])
    _reconcile_declaration(pipe)
    assert [f["type"] for f in pipe.findings] == ["LICENSE_UNKNOWN"]


def test_unscanned_scope_is_missing_notice():
    pipe = _pipe([{"path": "calc/a.js", "expression": "MIT",
                   "copyrightStatements": 1}], [("docs", "MIT")])
    _reconcile_declaration(pipe)
    assert [f["type"] for f in pipe.findings] == ["NOTICE_MISSING"]
