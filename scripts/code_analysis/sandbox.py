"""Dedicated loopback pgvector sandbox; volume is never deleted and DSN is never logged."""

import json
import os
import re
import secrets
import subprocess
import time
from pathlib import Path

from .code_index import DATABASE, MARKER, PORT, ROLE, initialize, preflight
from .contracts import digest
from .corpus import ARTIFACT_ROOT

IMAGE = ("pgvector/pgvector:0.8.6-pg16-bookworm"
         "@sha256:ccc6e83d6e35e931dc7c5def2022729d5a6c370318d099181995567ff1fb4d6b")
CONTAINER = os.environ.get("ASSETBLOCK_CODE_INDEX_CONTAINER", "assetblock-code-index")
VOLUME = os.environ.get("ASSETBLOCK_CODE_INDEX_VOLUME", "assetblock-code-index-data")
BIND = "127.0.0.1:55433:5432"


def _redact(text):
    if not text:
        return ""
    return re.sub(r"(password|pwd|POSTGRES_PASSWORD)=[^\s]+", r"\1=<redacted>", text, flags=re.I)


def _docker(argv, *, env=None, timeout=180):
    result = subprocess.run(["docker", *argv], env=env, capture_output=True, text=True,
                            encoding="utf-8", timeout=timeout)
    if result.returncode:
        detail = _redact((result.stderr or result.stdout or "")[-400:]).strip()
        raise ValueError("dedicated Docker operation failed" + (": " + detail if detail else ""))
    return result.stdout


def inspect_volume():
    names = [line for line in _docker(["volume", "ls", "--format", "{{.Name}}"]).splitlines() if line]
    present = VOLUME in names
    inspect = None
    if present:
        inspect = json.loads(_docker(["volume", "inspect", VOLUME]))
    return {"volume": VOLUME, "present": present, "deleted": False,
            "driver": (inspect[0].get("Driver") if inspect else None),
            "labels": (inspect[0].get("Labels") if inspect else None)}


def _names(kind):
    fmt = "{{.Names}}" if kind == "container" else "{{.Name}}"
    cmd = ["ps", "-a", "--format", fmt] if kind == "container" else ["volume", "ls", "--format", fmt]
    return {line for line in _docker(cmd).splitlines() if line}


def ensure_container():
    volume = inspect_volume()
    if not volume["present"]:
        raise ValueError("dedicated volume missing; creating it is a separate provisioning step")
    names = _names("container")
    if CONTAINER in names:
        running = _docker(["inspect", "-f", "{{.State.Running}}", CONTAINER]).strip().lower()
        if running != "true":
            _docker(["start", CONTAINER])
        created = False
    else:
        password = secrets.token_urlsafe(36)
        env = os.environ.copy()
        env["POSTGRES_PASSWORD"] = password
        _docker(["run", "-d", "--name", CONTAINER, "--label", "assetblock.purpose=code-analysis-index",
                 "--publish", BIND, "--mount", "type=volume,source=" + VOLUME + ",target=/var/lib/postgresql/data",
                 "--env", "POSTGRES_PASSWORD", "--env", "POSTGRES_DB=" + DATABASE,
                 "--env", "POSTGRES_USER=" + ROLE, IMAGE], env=env)
        created = True
    return {"container": CONTAINER, "volume": volume, "created": created, "image": IMAGE, "bind": BIND}


def _wait_ready():
    for _ in range(40):
        result = subprocess.run(["docker", "exec", CONTAINER, "pg_isready", "-U", ROLE, "-d", DATABASE],
                                capture_output=True, text=True, encoding="utf-8", timeout=30)
        if result.returncode == 0:
            return True
        time.sleep(1)
    raise ValueError("dedicated sandbox startup timeout")


def rotate_ephemeral_password():
    """Set a process-only password via stdin SQL. Never print or persist it."""
    _wait_ready()
    password = secrets.token_urlsafe(36)
    quoted = "'" + password.replace("'", "''") + "'"
    sql = "ALTER ROLE " + ROLE + " WITH PASSWORD " + quoted + ";\n"
    result = subprocess.run(["docker", "exec", "-i", CONTAINER, "psql", "-U", ROLE, "-d", DATABASE,
                             "-v", "ON_ERROR_STOP=1", "-q"],
                            input=sql, capture_output=True, text=True, encoding="utf-8", timeout=30)
    if result.returncode:
        detail = _redact((result.stderr or result.stdout or "")[-300:]).strip()
        raise ValueError("sandbox role password rotation failed" + (": " + detail if detail else ""))
    os.environ["ASSETBLOCK_CODE_INDEX_DSN"] = (
        "host=127.0.0.1 port=" + str(PORT) + " dbname=" + DATABASE + " user=" + ROLE + " password=" + password
    )
    return True


def schema_initialized(connection):
    with connection.cursor() as cursor:
        cursor.execute("SELECT 1 FROM information_schema.schemata WHERE schema_name='code_lab'")
        if cursor.fetchone() is None:
            return False
        cursor.execute("SELECT marker FROM code_lab.sandbox_marker")
        return cursor.fetchall() == [(MARKER,)]


def ddl_approval(path):
    from scripts.feasibility_pilot.paths import ensure_inside
    approval = json.loads(ensure_inside(Path(path), ARTIFACT_ROOT).read_bytes())
    ddl = Path(__file__).with_name("config") / "index-schema.sql"
    if (approval.get("status") != "REVIEWED" or not approval.get("reviewer")
            or approval.get("ddlSha256") != digest(ddl.read_bytes())
            or approval.get("database") != DATABASE or approval.get("role") != ROLE
            or approval.get("port") != PORT):
        raise ValueError("explicit matching sandbox DDL approval required")
    return approval


def connect_sandbox():
    import psycopg
    dsn = os.environ.get("ASSETBLOCK_CODE_INDEX_DSN")
    if not dsn:
        raise ValueError("sandbox DSN unavailable; live index not checked")
    return psycopg.connect(dsn, autocommit=True)


def initialize_if_empty(connection, *, approval_path=None):
    if schema_initialized(connection):
        preflight(connection)
        return {"ddl": "EXISTING_PRESERVED"}
    if approval_path is None:
        raise ValueError("empty sandbox requires an explicit DDL approval manifest")
    initialize(connection, ddl_approval(approval_path))
    return {"ddl": "INITIALIZED"}


def runtime_review_root(path=None):
    from scripts.feasibility_pilot.paths import ensure_inside
    root = ensure_inside(Path(path), ARTIFACT_ROOT) if path else ARTIFACT_ROOT / "review/dependencies"
    required = ("root-approval.json", "complete-package.json", "scope-supplement.json")
    if all((root / name).is_file() for name in required) and (root / "wheels").is_dir():
        return root
    return None
