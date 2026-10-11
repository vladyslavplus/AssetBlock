"""HTTP task cancellation: bounded finalizer, wedged fence, clean release."""

import asyncio
import time

import pytest

from conftest import TEST_SECRET, make_metadata, make_zip, requires_containment

_REQUIRES_CONTAINMENT = requires_containment()


def _build_call(service_app, workspace_root, tmp_path, name="cancel-run",
                file_count=300):
    import json as _json
    from pathlib import Path as _Path
    from app import contracts
    from app import policy as policy_module
    from app.service import ServiceConfig
    from app import workspace as workspace_module
    config = service_app.state.service_config
    archive = make_zip({f"src/f{i:04d}.js": b"function alpha() { return 1; }\n"
                        for i in range(file_count)})
    metadata_dict = make_metadata(archive)
    metadata = contracts.validate_metadata(metadata_dict)
    root = _Path(tmp_path) / name
    root.mkdir(exist_ok=True)
    space = workspace_module.Workspace(root, metadata.run_id)
    staged_path = space.path / "archive.bin"
    staged_path.write_bytes(archive)
    from app.transport import StagedUpload
    staged = StagedUpload(metadata_raw=contracts.canonical(metadata_dict),
                          metadata=metadata_dict,
                          archive_path=staged_path,
                          archive_size=len(archive),
                          archive_sha256=contracts.sha256_hex(archive))

    class FakeRequest:
        async def is_disconnected(self):
            return False

    state = service_app.state.pipeline_state
    return {
        "request": FakeRequest(), "config": config, "state": state,
        "app": service_app, "metadata": metadata, "space": space,
        "staged": staged, "snapshot": None,
        "effective": time.time() + 120.0,
    }


def _run_supervised_call(call):
    from app.service import _run_supervised
    return _run_supervised(call["request"], call["config"], call["state"],
                           call["app"], call["metadata"], call["space"],
                           call["staged"], call["snapshot"], call["effective"])


@_REQUIRES_CONTAINMENT
def test_cancel_mid_flight_cleans_and_releases(service_app, workspace_root,
                                              tmp_path):
    call = _build_call(service_app, workspace_root, tmp_path)
    state = call["state"]

    async def scenario():
        task = asyncio.create_task(_run_supervised_call(call))
        await asyncio.sleep(1.0)
        assert not task.done(), "worker finished before cancellation could land"
        task.cancel()
        try:
            await task
        except asyncio.CancelledError:
            pass
        else:
            raise AssertionError("cancellation did not propagate")
        # Bounded finalizer ran despite the cancel: no leftovers, gate free.
        assert not call["space"].path.exists()
        assert state["unconfirmed"] == set()
        assert state["active"] == {}

    asyncio.run(scenario())


@_REQUIRES_CONTAINMENT
def test_cancel_with_unconfirmed_fences_workspace_and_gate(
        service_app, workspace_root, tmp_path, monkeypatch):
    import app.service as service_module
    monkeypatch.setattr(service_module, "confirm_containment",
                        lambda *args, **kwargs: False)
    call = _build_call(service_app, workspace_root, tmp_path, name="cancel-wedged")
    state = call["state"]

    async def scenario():
        task = asyncio.create_task(_run_supervised_call(call))
        await asyncio.sleep(1.0)
        task.cancel()
        try:
            await task
        except asyncio.CancelledError:
            pass
        else:
            raise AssertionError("cancellation did not propagate")
        assert call["space"].path.exists()
        assert len(state["unconfirmed"]) == 1
        # The sweeper never clears fenced evidence.
        from app import workspace as workspace_module
        result = workspace_module.sweep_stale(call["space"].root,
                                              set(state["unconfirmed"]))
        assert call["space"].path.exists()
        assert result["removed"] == 0

    asyncio.run(scenario())
