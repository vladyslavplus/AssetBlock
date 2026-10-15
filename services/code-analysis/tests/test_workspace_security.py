from __future__ import annotations

import subprocess
from pathlib import Path

import pytest

from app import workspace


def test_restrict_windows_grants_current_process_sid(monkeypatch: pytest.MonkeyPatch, tmp_path: Path) -> None:
    monkeypatch.setenv("SystemRoot", r"C:\Windows")
    calls: list[list[str]] = []
    options: list[dict[str, object]] = []

    def run(argv: list[str], **kwargs: object) -> subprocess.CompletedProcess[bytes]:
        calls.append(argv)
        options.append(kwargs)
        if argv[1:] == ["/user", "/fo", "csv", "/nh"]:
            return subprocess.CompletedProcess(argv, 0, stdout=b'"DOMAIN\\service","S-1-5-21-111-222-333-1001"\r\n', stderr=b"")
        return subprocess.CompletedProcess(argv, 0, stdout=b"restricted ACL\r\n", stderr=b"")

    monkeypatch.setattr(workspace.subprocess, "run", run)
    workspace._restrict_windows(tmp_path)

    assert calls[0][0] == str(Path(r"C:\Windows") / "System32" / "whoami.exe")
    assert calls[0][1:] == ["/user", "/fo", "csv", "/nh"]
    assert calls[1] == ["icacls", str(tmp_path), "/inheritance:r"]
    assert calls[2] == ["icacls", str(tmp_path), "/grant:r", "*S-1-5-21-111-222-333-1001:(OI)(CI)F"]
    assert calls[3] == ["icacls", str(tmp_path)]
    assert all(item == {"capture_output": True, "timeout": 60, "shell": False, "creationflags": 0x08000000} for item in options)


@pytest.mark.parametrize("output", [b"no sid", b"S-1-5-21-1-2-3 S-1-5-21-4-5-6"])
def test_restrict_windows_rejects_missing_or_ambiguous_sid(
    monkeypatch: pytest.MonkeyPatch, tmp_path: Path, output: bytes
) -> None:
    calls = 0

    def run(argv: list[str], **kwargs: object) -> subprocess.CompletedProcess[bytes]:
        nonlocal calls
        calls += 1
        return subprocess.CompletedProcess(argv, 0, stdout=output, stderr=b"")

    monkeypatch.setenv("SystemRoot", r"C:\Windows")
    monkeypatch.setattr(workspace.subprocess, "run", run)

    with pytest.raises(RuntimeError, match="workspace ACL restriction failed"):
        workspace._restrict_windows(tmp_path)
    assert calls == 1
