from types import SimpleNamespace

import pytest

from scripts.code_analysis import code_index


class _Cursor:
    def __init__(self, connection):
        self.connection = connection
        self.row = None

    def __enter__(self):
        return self

    def __exit__(self, *_args):
        return False

    def execute(self, query):
        if "current_database()" in query:
            self.row = (self.connection.database, self.connection.role,
                        str(self.connection.server_version))
        elif "sandbox_marker" in query and "SELECT marker" in query:
            self.rows = [(code_index.MARKER,)] if self.connection.marker else []
        elif "pg_extension" in query:
            self.row = (self.connection.vector_version,)
        elif "transaction_read_only" in query:
            self.row = ("on" if self.connection.readonly else "off",
                        self.connection.has_write_privilege)
        elif "has_schema_privilege" in query:
            self.row = (self.connection.can_create_schema,)
        else:
            raise AssertionError("unexpected preflight query")

    def fetchone(self):
        return self.row

    def fetchall(self):
        return self.rows


class _Connection:
    def __init__(self, **overrides):
        values = {
            "host": "127.0.0.1", "port": code_index.SERVICE_RUNTIME_PORT,
            "database": code_index.DATABASE, "role": code_index.ROLE,
            "server_version": 180006, "marker": True,
            "vector_version": "0.8.6", "readonly": True,
            "has_write_privilege": False, "can_create_schema": False,
        }
        values.update(overrides)
        self.info = SimpleNamespace(host=values["host"], port=values["port"],
                                    transaction_status=0)
        self.autocommit = True
        for name, value in values.items():
            setattr(self, name, value)

    def cursor(self):
        return _Cursor(self)


def test_service_uses_only_its_pinned_target():
    code_index.service_runtime_preflight(_Connection())
    with pytest.raises(ValueError, match="target"):
        code_index.service_runtime_preflight(_Connection(port=code_index.PORT))


@pytest.mark.parametrize("change", [
    {"host": "0.0.0.0"}, {"port": 55433}, {"database": "postgres"},
    {"role": "postgres"}, {"server_version": 180007},
    {"marker": False}, {"vector_version": "0.8.5"},
    {"readonly": False}, {"has_write_privilege": True},
    {"can_create_schema": True},
])
def test_service_target_refuses_unowned_or_unverified_targets(change):
    with pytest.raises(ValueError):
        code_index.service_runtime_preflight(
            _Connection(**change))


def test_service_target_cannot_skip_markers():
    with pytest.raises(ValueError, match="initialized"):
        code_index.service_runtime_preflight(_Connection(), initialized=False)


def test_service_config_keeps_loopback_with_pinned_index_target(monkeypatch):
    from app.main import build_config
    monkeypatch.setenv("CODE_ANALYSIS_SECRET", "test-secret-value-at-least-32-bytes!!")
    for name in ("CODE_ANALYSIS_NODE_EXE", "CODE_ANALYSIS_DOLOS_CLI", "CODE_ANALYSIS_SCANCODE_EXE"):
        monkeypatch.delenv(name, raising=False)
    config, bind = build_config()
    assert bind == "127.0.0.1"
    assert config.index_dsn is None
