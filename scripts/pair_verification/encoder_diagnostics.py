"""Failure-only frozen encoder diagnostics; executed with the pilot CUDA interpreter."""

import json
import os
from pathlib import Path
import shutil
import subprocess

from scripts.code_analysis.corpus import write_once
from scripts.feasibility_pilot.paths import REPO_ROOT, ensure_inside

NVIDIA_SMI_TIMEOUT_SECONDS = 5


def _nvidia_command(executable, arguments, creationflags):
    try:
        result = subprocess.run([executable, *arguments], capture_output=True, text=True, check=False,
                                creationflags=creationflags, timeout=NVIDIA_SMI_TIMEOUT_SECONDS)
    except subprocess.TimeoutExpired:
        return {"returnCode": None, "output": "", "timedOut": True}
    return {"returnCode": result.returncode, "output": result.stdout.strip(), "timedOut": False}


def _nvidia_snapshot():
    executable = shutil.which("nvidia-smi")
    if not executable:
        return {"available": False, "timeoutSeconds": NVIDIA_SMI_TIMEOUT_SECONDS}
    creationflags = subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0
    gpu = _nvidia_command(
        executable,
        ["--query-gpu=timestamp,name,temperature.gpu,clocks.current.graphics,clocks.current.sm,"
         "clocks.current.memory,power.draw,power.limit,utilization.gpu,memory.used,memory.total",
         "--format=csv,noheader,nounits"],
        creationflags)
    processes = _nvidia_command(
        executable, ["--query-compute-apps=pid,process_name,used_gpu_memory", "--format=csv,noheader"],
        creationflags)
    return {"available": True, "timeoutSeconds": NVIDIA_SMI_TIMEOUT_SECONDS,
            "gpuReturnCode": gpu["returnCode"], "gpu": gpu["output"], "gpuTimedOut": gpu["timedOut"],
            "processReturnCode": processes["returnCode"], "computeApps": processes["output"],
            "processesTimedOut": processes["timedOut"]}


def _batch_tensors(torch, chunks, tokenizer, device):
    lengths = [len(row["inputIds"]) for row in chunks]
    maximum = max(lengths)
    ids = torch.tensor(
        [row["inputIds"] + [tokenizer.pad_token_id] * (maximum - len(row["inputIds"])) for row in chunks],
        dtype=torch.long, device=device)
    mask = torch.tensor(
        [[1] * length + [0] * (maximum - length) for length in lengths],
        dtype=torch.long, device=device)
    return ids, mask, lengths


def _forward_finite(torch, model, ids, mask, *, backend=None, layer_status=None):
    from contextlib import nullcontext

    context = nullcontext()
    if backend == "math":
        from torch.nn.attention import SDPBackend, sdpa_kernel
        context = sdpa_kernel(backends=[SDPBackend.MATH])
    hooks = []
    if layer_status is not None:
        modules = []
        if hasattr(model, "embeddings"):
            modules.append(("embeddings", model.embeddings))
        modules.extend((f"encoder.layer.{index}", layer)
                       for index, layer in enumerate(getattr(getattr(model, "encoder", None), "layer", [])))

        def make_hook(name):
            def record(_module, _inputs, output):
                value = output[0] if isinstance(output, (tuple, list)) and output else output
                if torch.is_tensor(value):
                    layer_status[name] = bool(torch.isfinite(value).all().item())
                else:
                    layer_status[name] = None
            return record

        hooks.extend(module.register_forward_hook(make_hook(name)) for name, module in modules)
    try:
        with context, torch.inference_mode():
            hidden = model(input_ids=ids, attention_mask=mask).last_hidden_state.float()
            pooled = (hidden * mask.unsqueeze(-1)).sum(1) / mask.sum(1).unsqueeze(-1)
            vectors = torch.nn.functional.normalize(pooled, p=2, dim=-1).cpu().tolist()
        from scripts.code_analysis.representations import vector_bytes
        vector_bytes(vectors)
        return {"finite": True}
    except Exception as exc:
        return {"finite": False, "errorType": type(exc).__name__}
    finally:
        for handle in hooks:
            handle.remove()


def _cpu_reencode(torch, model, chunks, tokenizer):
    import copy

    cpu_model = copy.deepcopy(model).to("cpu").eval()
    for parameter in cpu_model.parameters():
        parameter.requires_grad_(False)
    ids, mask, _ = _batch_tensors(torch, chunks, tokenizer, "cpu")
    return _forward_finite(torch, cpu_model, ids, mask, backend="math")


def capture_encoder_failure(path, *, model, tokenizer, chunks, batch_start, batch_size, device):
    """Re-encode only the failed batch, record finite states and write one private run diagnostic."""
    import torch

    directory = Path(path).parent
    ensure_inside(directory, REPO_ROOT / "artifacts" / "pair_verification")
    ids, mask, lengths = _batch_tensors(torch, chunks, tokenizer, device)
    telemetry = _nvidia_snapshot()
    valid_counts = [int(value) for value in mask.sum(1).tolist()]
    layer_status = {}
    same_backend = _forward_finite(torch, model, ids, mask, layer_status=layer_status)
    first_nonfinite_layer = next((name for name, finite in layer_status.items() if finite is False), None)
    math_backend = _forward_finite(torch, model, ids, mask, backend="math") if device.type == "cuda" else None
    single_rows = []
    if device.type == "cuda":
        for row_index, row in enumerate(chunks):
            row_ids, row_mask, _ = _batch_tensors(torch, [row], tokenizer, device)
            single_rows.append({"rowIndex": row_index,
                                **_forward_finite(torch, model, row_ids, row_mask)})
    cpu_result = _cpu_reencode(torch, model, chunks, tokenizer)
    padding = int(ids.numel() - sum(lengths))
    diagnostic = {
        "schema": "pair-verification-encoder-failure-diagnostics-v1",
        "trigger": "nonfinite vector",
        "batchIndex": batch_start // batch_size,
        "batchSize": batch_size,
        "batchStartWindow": batch_start,
        "batchRows": len(chunks),
        "inputIdsShape": list(ids.shape),
        "attentionMaskShape": list(mask.shape),
        "sequenceLengths": lengths,
        "maskStats": {"validTokensPerRow": valid_counts,
                      "minimumValidTokens": min(valid_counts),
                      "maximumValidTokens": max(valid_counts),
                      "fullyMaskedRows": sum(count == 0 for count in valid_counts),
                      "zeroValidTokenRows": sum(count == 0 for count in valid_counts),
                      "paddingTokens": padding},
        "reencode": {"sameDeviceDefaultBackend": same_backend,
                     "sameDeviceMathBackend": math_backend,
                     "singleRowDefaultBackend": single_rows,
                     "cpuMathBackend": cpu_result},
        "layerFiniteStatus": layer_status,
        "firstNonFiniteLayer": first_nonfinite_layer,
        "nvidiaSmi": telemetry,
    }
    raw = json.dumps(diagnostic, ensure_ascii=True, sort_keys=True, separators=(",", ":")).encode("ascii")
    write_once(Path(path), raw, REPO_ROOT / "artifacts" / "pair_verification")
