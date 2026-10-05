"""Local prerequisite recording and isolated environment setup."""

from __future__ import annotations

import hashlib
import json
import os
import platform
import shutil
import subprocess
import sys
from typing import Any
from pathlib import Path

from .paths import ARTIFACT_ROOT, CONFIG_ROOT, cache_dir, env_dir


REQUESTED_ML = {
    "python": "3.12",
    "torch": "2.14.1+cu130",
    "torchIndex": "https://download.pytorch.org/whl/cu130",
    "transformers": "5.17.0",
    "safetensors": "0.8.0",
    "huggingface_hub": "1.33.0",
    "tokenizers": "0.23.2",
    "tree_sitter": "0.25.2",
    "tree_sitter_javascript": "0.25.0",
    "tree_sitter_typescript": "0.23.2",
    "tree_sitter_python": "0.25.0",
    "tree_sitter_java": "0.23.5",
    "tree_sitter_c_sharp": "0.23.1",
}
REQUESTED_DOLOS = {"package": "@dodona/dolos", "version": "2.9.3"}
REQUESTED_SCANCODE = {"package": "scancode-toolkit", "version": "32.5.0"}
MODEL_ID = "microsoft/unixcoder-base-nine"
MODEL_REVISION = "5f0dc256eb0904af496d427451da5d7f90261676"
PINNED_BIN_SHA256 = "e28385bb916434983692dfdd57f5c78c64f92d4a26614965d1e9d150b4a37145"


def collect_host() -> dict[str, Any]:
    gpu = _nvidia()
    total, _used, free = shutil.disk_usage(ARTIFACT_ROOT.anchor if ARTIFACT_ROOT.anchor else os.getcwd())
    return {
        "pythonExecutable": sys.executable,
        "pythonVersion": sys.version,
        "pythonArchitecture": platform.architecture()[0],
        "platform": platform.platform(),
        "node": _cmd_version(["node", "--version"]),
        "npm": _cmd_version(["npm", "--version"]),
        "gpu": gpu,
        "diskFreeBytes": int(free),
        "diskTotalBytes": int(total),
        "ramReportedGiB": 32,
        "ramNote": "owner-reported host RAM; this value is not measured here",
        "cpuNote": "owner-reported Ryzen 5 5600; this value is not measured here",
    }


def setup_ml_env() -> dict[str, Any]:
    root = env_dir("ml")
    root.parent.mkdir(parents=True, exist_ok=True)
    python = Path(sys.executable)
    if not (root / "Scripts" / "python.exe").exists() and not (root / "bin" / "python").exists():
        subprocess.check_call([str(python), "-m", "venv", str(root)])
    py = _venv_python(root)
    req = CONFIG_ROOT / "requirements-ml.txt"
    cmd = [
        str(py),
        "-m",
        "pip",
        "install",
        "--upgrade",
        "pip",
        "wheel",
        "setuptools",
    ]
    subprocess.check_call(cmd)
    subprocess.check_call(
        [
            str(py),
            "-m",
            "pip",
            "install",
            "--index-url",
            REQUESTED_ML["torchIndex"],
            "--extra-index-url",
            "https://pypi.org/simple",
            f"torch=={REQUESTED_ML['torch'].split('+')[0]}",
        ]
    )
    subprocess.check_call([str(py), "-m", "pip", "install", "-r", str(req)])
    freeze = subprocess.check_output([str(py), "-m", "pip", "freeze"], text=True)
    lock_path = ARTIFACT_ROOT / "env" / "ml-lock.txt"
    lock_path.parent.mkdir(parents=True, exist_ok=True)
    lock_path.write_text(freeze, encoding="utf-8")
    return {"python": str(py), "lock": str(lock_path), "lockSha256": hashlib.sha256(freeze.encode()).hexdigest()}


DOLOS_PARSER_TARGETS = (
    "bash",
    "c",
    "cpp",
    "c_sharp",
    "elm",
    "go",
    "groovy",
    "java",
    "javascript",
    "modelica",
    "ocaml",
    "php",
    "python",
    "r",
    "rust",
    "scala",
    "sql",
    "typescript",
    "verilog",
)


def setup_dolos_env() -> dict[str, Any]:
    prefix = env_dir("dolos")
    prefix.mkdir(parents=True, exist_ok=True)
    manifest_src = CONFIG_ROOT / "dolos-package.json"
    shutil.copyfile(manifest_src, prefix / "package.json")
    npm = shutil.which("npm")
    if not npm:
        return {"outcome": "FAILED", "reason": "npm not found"}
    env = os.environ.copy()
    py312 = Path(r"C:\Python312\python.exe")
    if py312.exists():
        env["npm_config_python"] = str(py312)
    fetch = subprocess.run(
        [npm, "install", "--omit=dev", "--no-fund", "--no-audit", "--ignore-scripts"],
        cwd=str(prefix),
        capture_output=True,
        text=True,
        check=False,
        shell=False,
        env=env,
    )
    parsers = prefix / "node_modules" / "@dodona" / "dolos-parsers"
    created = []
    if parsers.exists():
        for name in DOLOS_PARSER_TARGETS:
            target = parsers / "build" / name
            target.mkdir(parents=True, exist_ok=True)
            created.append(name)
    from .gyp_mkdir_patch import patch_node_gyp_mkdir

    patched = patch_node_gyp_mkdir(prefix)
    rebuild = subprocess.run(
        [npm, "rebuild", "@dodona/dolos-parsers", "--foreground-scripts"],
        cwd=str(prefix),
        capture_output=True,
        text=True,
        check=False,
        shell=False,
        env=env,
    )
    log_path = ARTIFACT_ROOT / "env" / "dolos-setup.log"
    log_path.parent.mkdir(parents=True, exist_ok=True)
    log_path.write_text(
        "FETCH STDOUT\n"
        + fetch.stdout
        + "\nFETCH STDERR\n"
        + fetch.stderr
        + "\nREBUILD STDOUT\n"
        + rebuild.stdout
        + "\nREBUILD STDERR\n"
        + rebuild.stderr,
        encoding="utf-8",
        errors="replace",
    )
    ok = fetch.returncode == 0 and rebuild.returncode == 0
    return {
        "outcome": "SUCCEEDED" if ok else "FAILED",
        "fetchReturncode": fetch.returncode,
        "rebuildReturncode": rebuild.returncode,
        "gypMkdirPatchApplied": patched,
        "log": str(log_path),
        "prefix": str(prefix),
        "nodeEngines": ">=18",
        "stderr": (rebuild.stderr or fetch.stderr)[-4000:],
    }


def download_model() -> dict[str, Any]:
    from huggingface_hub import snapshot_download

    target = cache_dir() / "unixcoder-base-nine"
    target.parent.mkdir(parents=True, exist_ok=True)
    path = snapshot_download(
        repo_id=MODEL_ID,
        revision=MODEL_REVISION,
        local_dir=str(target),
        local_dir_use_symlinks=False,
    )
    return record_model_identity(Path(path))


def load_trusted_model_identity() -> dict[str, Any]:
    path = CONFIG_ROOT / "model-trusted-identity.json"
    return json.loads(path.read_text(encoding="utf-8"))


def _sha256_file(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def verify_pinned_model_identity(model_dir: Path, *, trusted: dict[str, Any] | None = None) -> dict[str, Any]:
    """Read-only identity gate for smoke/loads. Never converts or rewrites the cache."""
    trusted = trusted or load_trusted_model_identity()
    if trusted.get("modelId") != MODEL_ID or trusted.get("modelRevision") != MODEL_REVISION:
        raise ValueError("trusted model identity metadata does not match env_setup pins")
    if not model_dir.is_dir():
        raise FileNotFoundError(f"model cache directory is missing: {model_dir}")

    expected: dict[str, str] = dict(trusted.get("fileHashes") or {})
    bound_config = list(trusted.get("boundTokenizerAndConfigFiles") or [])
    observed: dict[str, str] = {}

    bin_path = model_dir / "pytorch_model.bin"
    if not bin_path.is_file():
        raise FileNotFoundError("pytorch_model.bin is missing from the model cache")
    observed["pytorch_model.bin"] = _sha256_file(bin_path)
    if observed["pytorch_model.bin"] != PINNED_BIN_SHA256:
        raise ValueError(
            "pytorch_model.bin hash does not match the pinned revision blob "
            f"(expected {PINNED_BIN_SHA256}, observed {observed['pytorch_model.bin']})"
        )

    for rel in bound_config:
        path = model_dir / rel
        if not path.is_file():
            raise FileNotFoundError(f"bound tokenizer/config file is missing: {rel}")
        observed[rel] = _sha256_file(path)
        want = expected.get(rel)
        if not want or observed[rel] != want:
            raise ValueError(f"{rel} hash does not match trusted identity (expected {want}, observed {observed[rel]})")

    safetensors_path = model_dir / "model.safetensors"
    use_safetensors = False
    selected_weights: dict[str, Any]
    if safetensors_path.is_file():
        observed["model.safetensors"] = _sha256_file(safetensors_path)
        want_safe = expected.get("model.safetensors")
        if not want_safe:
            raise ValueError("model.safetensors present but no trusted conversion hash is recorded")
        if observed["model.safetensors"] != want_safe:
            raise ValueError(
                "model.safetensors hash does not match trusted conversion evidence "
                f"(expected {want_safe}, observed {observed['model.safetensors']})"
            )
        use_safetensors = True
        selected_weights = {
            "format": "model.safetensors",
            "path": "model.safetensors",
            "sha256": observed["model.safetensors"],
        }
    else:
        selected_weights = {
            "format": "pytorch_model.bin",
            "path": "pytorch_model.bin",
            "sha256": observed["pytorch_model.bin"],
        }

    return {
        "outcome": "VERIFIED",
        "modelId": MODEL_ID,
        "modelRevision": MODEL_REVISION,
        "localDir": str(model_dir),
        "trustRemoteCode": bool(trusted.get("trustRemoteCode", False)),
        "localFilesOnly": bool(trusted.get("localFilesOnly", True)),
        "useSafetensors": use_safetensors,
        "selectedWeights": selected_weights,
        "fileHashes": observed,
        "trustedIdentityPath": str(CONFIG_ROOT / "model-trusted-identity.json"),
    }


def parse_ml_requirement_pins() -> dict[str, str]:
    req = CONFIG_ROOT / "requirements-ml.txt"
    pins: dict[str, str] = {}
    for raw in req.read_text(encoding="utf-8").splitlines():
        line = raw.strip()
        if not line or line.startswith("#") or "==" not in line:
            continue
        name, version = line.split("==", 1)
        pins[name.strip()] = version.strip()
    return pins


def verify_ml_smoke_package_pins(*, torch_version: str | None = None) -> dict[str, str]:
    """Exact pins for the transformers 5.17 compatibility smoke (torch base 2.14.1)."""
    import importlib.metadata as metadata

    pins = parse_ml_requirement_pins()
    observed: dict[str, str] = {}
    for pkg, want in pins.items():
        dist_name = pkg.replace("_", "-")
        try:
            got = metadata.version(dist_name)
        except metadata.PackageNotFoundError:
            raise RuntimeError(f"required ML package is not installed: {dist_name}=={want}") from None
        if got != want:
            raise RuntimeError(f"{dist_name} version mismatch (expected {want}, observed {got})")
        observed[dist_name] = got
    if torch_version is not None:
        base = torch_version.split("+", 1)[0]
        if base != "2.14.1":
            raise RuntimeError(f"torch version mismatch for smoke (expected 2.14.1 base, observed {torch_version})")
        observed["torch"] = torch_version
    return observed


def record_model_identity(model_dir: Path) -> dict[str, Any]:
    if not model_dir.exists():
        return {
            "outcome": "FAILED",
            "modelId": MODEL_ID,
            "modelRevision": MODEL_REVISION,
            "localDir": str(model_dir),
            "reason": "model cache directory is missing",
        }
    files: dict[str, str] = {}
    for item in model_dir.rglob("*"):
        if item.is_file() and item.suffix in {".json", ".bin", ".safetensors", ".txt", ".model"}:
            files[item.relative_to(model_dir).as_posix()] = hashlib.sha256(item.read_bytes()).hexdigest()
    bin_hash = files.get("pytorch_model.bin")
    if bin_hash != PINNED_BIN_SHA256:
        return {
            "outcome": "FAILED",
            "modelId": MODEL_ID,
            "modelRevision": MODEL_REVISION,
            "localDir": str(model_dir),
            "fileHashes": files,
            "reason": "pytorch_model.bin hash does not match the pinned revision blob",
            "expectedBinSha256": PINNED_BIN_SHA256,
            "observedBinSha256": bin_hash,
        }
    safetensors = model_dir / "model.safetensors"
    conversion: dict[str, Any] = {"performed": False}
    if not safetensors.exists():
        conversion = _convert_bin_to_safetensors(model_dir)
        if safetensors.exists():
            files["model.safetensors"] = hashlib.sha256(safetensors.read_bytes()).hexdigest()
    return {
        "outcome": "SUCCEEDED",
        "modelId": MODEL_ID,
        "modelRevision": MODEL_REVISION,
        "localDir": str(model_dir),
        "fileHashes": files,
        "trustRemoteCode": False,
        "localFilesOnly": True,
        "binLoadPath": "hash-pinned pytorch_model.bin via Transformers local_files_only, trust_remote_code=False, then convert to model.safetensors for later loads",
        "preferSafetensors": safetensors.exists(),
        "safetensorsConversion": conversion,
        "weightFormats": sorted({Path(name).suffix for name in files}),
        "gpuBinding": "prior GPU stage is credible only because the same pytorch_model.bin SHA-256 was already verified against this revision",
    }


def _convert_bin_to_safetensors(model_dir: Path) -> dict[str, Any]:
    try:
        import torch
        from safetensors.torch import save_file
    except ImportError as exc:
        return {"performed": False, "reason": f"conversion libraries missing: {exc}"}
    bin_path = model_dir / "pytorch_model.bin"
    try:
        state = torch.load(str(bin_path), map_location="cpu", weights_only=True)
        tensors = {key: value.contiguous() for key, value in state.items()}
        save_file(tensors, str(model_dir / "model.safetensors"))
    except Exception as exc:  # noqa: BLE001 — conversion is best-effort; bin remains the verified fallback
        return {"performed": False, "reason": str(exc)}
    return {"performed": True, "method": "torch.load(weights_only=True) + safetensors.torch.save_file"}


def scancode_blocked_reason() -> str:
    return (
        "ScanCode 32.5.0 is authorized for the isolated prefix only. "
        "pypi exception schema does not add pip inventory or pip-audit to pnpm deps:check."
    )


def _venv_python(root: Path) -> Path:
    windows = root / "Scripts" / "python.exe"
    unix = root / "bin" / "python"
    return windows if windows.exists() else unix


def _cmd_version(argv: list[str]) -> str | None:
    try:
        completed = subprocess.run(argv, capture_output=True, text=True, check=False, shell=False, timeout=15)
    except (FileNotFoundError, subprocess.TimeoutExpired):
        return None
    if completed.returncode != 0:
        return None
    return (completed.stdout or completed.stderr).strip()


def _nvidia() -> dict[str, Any]:
    try:
        completed = subprocess.run(
            [
                "nvidia-smi",
                "--query-gpu=name,memory.total,driver_version",
                "--format=csv,noheader",
            ],
            capture_output=True,
            text=True,
            check=False,
            shell=False,
            timeout=20,
        )
    except (FileNotFoundError, subprocess.TimeoutExpired) as exc:
        return {"available": False, "error": str(exc)}
    if completed.returncode != 0:
        return {"available": False, "error": completed.stderr.strip()}
    line = completed.stdout.strip().splitlines()[0]
    parts = [part.strip() for part in line.split(",")]
    return {
        "available": True,
        "name": parts[0] if parts else None,
        "memory": parts[1] if len(parts) > 1 else None,
        "driver": parts[2] if len(parts) > 2 else None,
        "cudaUmdNote": "recorded separately from nvidia-smi header when collected during preflight",
    }
