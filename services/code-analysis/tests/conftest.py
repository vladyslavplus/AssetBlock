import io
import sys
import uuid
import zipfile
from pathlib import Path

TESTS_DIR = Path(__file__).resolve().parent
SERVICE_DIR = TESTS_DIR.parent
REPO_ROOT = SERVICE_DIR.parents[1]
if str(SERVICE_DIR) not in sys.path:
    sys.path.insert(0, str(SERVICE_DIR))
if str(REPO_ROOT) not in sys.path:
    sys.path.insert(0, str(REPO_ROOT))

import pytest  # noqa: E402

from app import contracts, policy as policy_module  # noqa: E402
from app.service import ServiceConfig, create_app  # noqa: E402

TEST_SECRET = "test-secret-value-at-least-32-bytes!!"


def make_zip(entries: dict[str, bytes]) -> bytes:
    buffer = io.BytesIO()
    with zipfile.ZipFile(buffer, "w", zipfile.ZIP_DEFLATED) as archive:
        for name, data in entries.items():
            archive.writestr(name, data)
    return buffer.getvalue()


def make_metadata(archive: bytes, **overrides) -> dict:
    content = contracts.sha256_hex(archive)
    policy_digest = policy_module.POLICY_DIGEST
    base = {
        "runId": str(uuid.uuid4()),
        "assetId": "asset_1",
        "versionId": "version_1",
        "contentSha256": content,
        "inputDigest": "0" * 64,
        "policyVersion": contracts.POLICY_ID,
        "declarationDigest": "a" * 64,
        "materialDigest": "b" * 64,
        "reservation": {
            "serviceKey": "code-analysis",
            "slotNumber": 0,
            "generation": 1,
            "leaseToken": "c" * 64,
            "hardDeadlineUtc": "2030-01-01T00:00:00Z",
        },
        "securityEvidence": {
            "contentSha256": content,
            "archiveEvidenceId": "archive_evidence_1",
            "malwareEvidenceId": "malware_evidence_1",
            "securityGeneration": 1,
        },
        "declaredLicenses": [],
    }
    base.update(overrides)
    validated = contracts.validate_metadata(base)
    digest = contracts.compute_analysis_digest(validated, policy_digest, None)
    base["inputDigest"] = digest
    return base


def make_body(metadata: dict, archive: bytes,
              boundary: str = "test-boundary-123") -> tuple[bytes, str]:
    meta = contracts.canonical(metadata)
    body = (
        f"--{boundary}\r\n"
        'Content-Disposition: form-data; name="metadata"\r\n'
        "Content-Type: application/json\r\n\r\n"
    ).encode() + meta + (
        f"\r\n--{boundary}\r\n"
        'Content-Disposition: form-data; name="archive"; filename="upload.zip"\r\n'
        "Content-Type: application/octet-stream\r\n\r\n"
    ).encode() + archive + f"\r\n--{boundary}--\r\n".encode()
    return body, f"multipart/form-data; boundary={boundary}"


@pytest.fixture()
def workspace_root(tmp_path):
    root = tmp_path / "workspace"
    root.mkdir()
    return root


@pytest.fixture()
def app_config(workspace_root):
    return ServiceConfig(secret=TEST_SECRET, workspace_root=workspace_root,
                         service_dir=SERVICE_DIR,
                         worker_path_entries=(str(SERVICE_DIR), str(REPO_ROOT)))


@pytest.fixture()
def client(app_config):
    from fastapi.testclient import TestClient
    from app.service import create_app
    return TestClient(create_app(app_config))


@pytest.fixture()
def service_app(app_config):
    from app.service import create_app
    return create_app(app_config)


JS_HELLO = b"function hello(name) { return 'hi ' + name; }\nmodule.exports = { hello };\n"


def requires_containment():
    """Skip live-worker tests where no permitted job runtime exists.

    The skip itself records the BLOCKED verdict: fail-closed refusal is
    covered by dedicated tests that run everywhere.
    """
    import pytest as _pytest
    from app.supervisor import probe_containment
    return _pytest.mark.skipif(
        not probe_containment(),
        reason="BLOCKED: host forbids nested job assignment")


@pytest.fixture()
def make_metadata_archive():
    archive = make_zip({"src/hello.js": JS_HELLO})
    return make_metadata(archive), archive
