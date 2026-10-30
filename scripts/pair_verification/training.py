"""CUDA pair-verifier training and reload stages for the pinned pilot environment."""

import json
import math
import os
import random
import re
import shutil
import subprocess
import time
from datetime import datetime

import torch
import torch.nn.functional as F

from scripts.code_analysis.contracts import canonical, digest
from scripts.code_analysis.corpus import write_once
from scripts.code_retrieval import checkpoint as upstream_checkpoint
from scripts.feasibility_pilot.paths import REPO_ROOT, ensure_inside

from .architecture import JointVerifier, freeze_except_last_blocks, state_digest
from .checkpoints import (RELOAD_TOLERANCE, load_epoch_checkpoint, write_epoch_checkpoint,
                          write_support_files)
from .training_data import (build_validation_windows, load_bound_prepare,
                            materialize_training_examples)
from .training_utils import epoch_order, require_reload_match, require_step_ceiling

VALIDATION_INFERENCE_BATCH_SIZE = 32
GPU_EVENT_CHECK_TIMEOUT_SECONDS = 15


class NvidiaDriverEventError(RuntimeError):
    def __init__(self, events):
        self.events = events
        super().__init__("NVIDIA driver event 153 detected during training")


def check_gpu_driver_events(run_started_at):
    """Fail closed if Windows records NVIDIA driver event 153 since this run began."""
    since = run_started_at.isoformat(timespec="seconds")
    command = ("$since=[datetime]::Parse('{}'); try {{ "
               "$events=Get-WinEvent -FilterHashtable @{{LogName='System';ProviderName='nvlddmkm';StartTime=$since}} "
               "-ErrorAction Stop | Where-Object {{$_.Id -eq 153}} | "
               "Select-Object @{{Name='timeCreated';Expression={{$_.TimeCreated.ToString('o')}}}},Id; "
               "if ($events) {{$events | ConvertTo-Json -Compress}} else {{'[]'}} "
               "}} catch {{ if ($_.FullyQualifiedErrorId -like '*NoMatchingEventsFound*') {{'[]'}} else {{exit 9}} }}").format(since)
    result = subprocess.run(["powershell.exe", "-NoProfile", "-NonInteractive", "-Command", command],
                            capture_output=True, text=True, timeout=GPU_EVENT_CHECK_TIMEOUT_SECONDS,
                            creationflags=subprocess.CREATE_NO_WINDOW)
    if result.returncode != 0:
        raise RuntimeError("Windows GPU event query failed")
    events = json.loads(result.stdout.strip() or "[]")
    if isinstance(events, dict):
        events = [events]
    if not isinstance(events, list):
        raise ValueError("Windows GPU event query returned an invalid result")
    normalized = [{"timeCreated": row["timeCreated"], "id": row["Id"]}
                  for row in events if isinstance(row, dict) and row.get("Id") == 153]
    if normalized:
        raise NvidiaDriverEventError(normalized)
    return {"checkedAt": datetime.now().astimezone().isoformat(timespec="seconds"),
            "eventCount": 0, "timeoutSeconds": GPU_EVENT_CHECK_TIMEOUT_SECONDS}

def _write_json(path, value, root):
    raw = canonical(value)
    write_once(path, raw, root)
    return digest(raw)


def _ram_peak_bytes():
    import ctypes
    from ctypes import wintypes
    class ProcessMemoryCounters(ctypes.Structure):
        _fields_ = [("cb", wintypes.DWORD), ("PageFaultCount", wintypes.DWORD),
                    ("PeakWorkingSetSize", ctypes.c_size_t), ("WorkingSetSize", ctypes.c_size_t),
                    ("QuotaPeakPagedPoolUsage", ctypes.c_size_t), ("QuotaPagedPoolUsage", ctypes.c_size_t),
                    ("QuotaPeakNonPagedPoolUsage", ctypes.c_size_t), ("QuotaNonPagedPoolUsage", ctypes.c_size_t),
                    ("PagefileUsage", ctypes.c_size_t), ("PeakPagefileUsage", ctypes.c_size_t)]
    counters = ProcessMemoryCounters()
    counters.cb = ctypes.sizeof(counters)
    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
    psapi = ctypes.WinDLL("psapi", use_last_error=True)
    kernel32.GetCurrentProcess.restype = wintypes.HANDLE
    get_memory = psapi.GetProcessMemoryInfo
    get_memory.argtypes = [wintypes.HANDLE, ctypes.POINTER(ProcessMemoryCounters), wintypes.DWORD]
    get_memory.restype = wintypes.BOOL
    if not get_memory(kernel32.GetCurrentProcess(), ctypes.byref(counters), counters.cb):
        raise ctypes.WinError(ctypes.get_last_error())
    if counters.PeakWorkingSetSize <= 0:
        raise ValueError("Windows process working-set measurement is empty")
    return int(counters.PeakWorkingSetSize)


def _device_resources():
    return {"peakAllocatedBytes": int(torch.cuda.max_memory_allocated()),
            "peakReservedBytes": int(torch.cuda.max_memory_reserved()),
            "ramPeakBytes": _ram_peak_bytes()}


def _batch(examples, indices, swapped, pad_id, device):
    rows, labels = [], []
    for index, reverse in zip(indices, swapped):
        item = examples[index]
        ids = item["swappedInputIds"] if reverse else item["inputIds"]
        rows.append(list(ids))
        labels.append(item["label"])
    length = max(map(len, rows))
    input_ids = torch.tensor([row + [pad_id] * (length - len(row)) for row in rows],
                             dtype=torch.long, device=device)
    mask = torch.tensor([[1] * len(row) + [0] * (length - len(row)) for row in rows],
                        dtype=torch.long, device=device)
    return input_ids, mask, torch.tensor(labels, dtype=torch.long, device=device)


def _validation_records(source, tokenizer):
    return list(build_validation_windows(source["bound"], tokenizer))


def _forward_logits(model, rows, device, *, pad_id=1, batch_size=16):
    results = []
    model.eval()
    with torch.inference_mode():
        for start in range(0, len(rows), batch_size):
            batch = rows[start:start + batch_size]
            width = max(len(ids) for ids in batch)
            ids = torch.tensor([list(x) + [pad_id] * (width - len(x)) for x in batch],
                               dtype=torch.long, device=device)
            mask = torch.tensor([[1] * len(x) + [0] * (width - len(x)) for x in batch],
                                dtype=torch.long, device=device)
            logits = model(ids, mask).float().cpu().tolist()
            if any(not math.isfinite(value) for row in logits for value in row):
                raise ValueError("non-finite validation logits at batch {} with shape {}".format(
                    start // batch_size, tuple(ids.shape)))
            results.extend(logits)
    return results


def _probe_rows(validation_records, maximum=32):
    rows = []
    for record in validation_records:
        for pair in record["windowPairs"]:
            rows.append({"inputIds": list(pair["inputIds"]),
                         "swappedInputIds": list(pair["swappedInputIds"])})
            if len(rows) >= maximum:
                return rows
    return rows


def _reference_logits(model, probe_rows, pad_id, device):
    original = [row["inputIds"] for row in probe_rows]
    swapped = [row["swappedInputIds"] for row in probe_rows]
    logits = _forward_logits(model, original + swapped, device, pad_id=pad_id, batch_size=8)
    return [row[:2] for row in logits]


def _validation_order_evidence(model, validation_records, pad_id, device):
    original, swapped, descriptors = [], [], []
    for record in validation_records:
        for pair in record["windowPairs"]:
            original.append(pair["inputIds"])
            swapped.append(pair["swappedInputIds"])
            descriptors.append(record)
    logits = _forward_logits(model, original + swapped, device, pad_id=pad_id,
                             batch_size=VALIDATION_INFERENCE_BATCH_SIZE)
    probabilities = [torch.softmax(torch.tensor(row), dim=-1)[1].item() for row in logits]
    n = len(original)
    per_candidate = {}
    for index, record in enumerate(descriptors):
        key = (record["model"], record["language"], record["queryId"], record["fileId"])
        values = per_candidate.setdefault(key, [[], []])
        values[0].append(probabilities[index])
        values[1].append(probabilities[index + n])
    language_counts = {}
    for model_label, language, _, _ in per_candidate:
        label = model_label + "/" + language
        language_counts[label] = language_counts.get(label, 0) + 1
    diffs, agreements = [], []
    for left, right in per_candidate.values():
        p, q = max(left), max(right)
        diffs.append(abs(p - q))
        agreements.append((p >= .5) == (q >= .5))
    truncated_pairs = sum(any(pair["truncated"] for pair in row["windowPairs"])
                          for row in validation_records)
    return {"candidatePairCount": len(per_candidate), "windowPairCount": n,
            "candidatePairsPerModelLanguage": language_counts,
            "inferenceBatchSize": VALIDATION_INFERENCE_BATCH_SIZE,
            "swapAgreementAtHalf": sum(agreements) / len(agreements) if agreements else 1.0,
            "meanProbabilityDifference": sum(diffs) / len(diffs) if diffs else 0.0,
            "maximumProbabilityDifference": max(diffs, default=0.0),
            "truncatedCandidatePairs": truncated_pairs,
            "truncatedCandidatePairShare": truncated_pairs / len(per_candidate) if per_candidate else 0.0,
            "truncatedWindowPairs": sum(pair["truncated"] for row in validation_records
                                         for pair in row["windowPairs"])}


def _step_optimizer(scaler, optimizer, trainable_parameters, gradient_clip):
    scaler.unscale_(optimizer)
    torch.nn.utils.clip_grad_norm_(trainable_parameters, gradient_clip)
    previous_scale = scaler.get_scale()
    scaler.step(optimizer)
    scaler.update()
    return scaler.get_scale() < previous_scale


def _safe_failure_reason(exc):
    message = str(exc).strip().splitlines()[0] if str(exc).strip() else type(exc).__name__
    message = re.sub(r"(?i)\b[A-Z]:\\[^\s\"']*", "[path]", message)
    message = re.sub(r"(?i)\b(?:password|passwd|secret|token|dsn|authorization)\s*[:=]\s*\S+",
                     "[redacted]", message)
    message = re.sub(r"https?://\S+", "[url]", message)
    return message[:240]


def _validate_reload_evidence(evidence, run_id, process_id):
    if evidence.get("outcome") != "TRAINED" or evidence.get("runId") != run_id:
        raise ValueError("training evidence is not successful")
    if evidence.get("processId") == process_id:
        raise ValueError("reload check must run in a fresh process")
    checkpoints = evidence.get("checkpoints", [])
    if len(checkpoints) != 5 or [row.get("epoch") for row in checkpoints] != [1, 2, 3, 4, 5]:
        raise ValueError("training evidence does not bind exactly five ordered checkpoints")
    return checkpoints


def _failure_progress(current_epoch, current_step, current_loss, current_epoch_losses,
                      completed_epochs, attempted_steps, skipped_steps):
    return {"epoch": current_epoch, "step": current_step, "lastLoss": current_loss,
            "currentEpochLossCount": len(current_epoch_losses),
            "currentEpochMeanLoss": (sum(current_epoch_losses) / len(current_epoch_losses)
                                      if current_epoch_losses else None),
            "completedEpochs": completed_epochs,
            "optimizerStepsAttempted": attempted_steps,
            "optimizerStepsSkipped": skipped_steps}


def _read_verified_probe(probe_path, checkpoint_row):
    probe_raw = probe_path.read_bytes()
    if digest(probe_raw) != checkpoint_row.get("probeArtifactSha256"):
        raise ValueError("reload probe artifact differs from training evidence")
    probe = json.loads(probe_raw)
    if (digest(canonical(probe.get("logits"))) != probe.get("logitsSha256")
            or digest(canonical(probe.get("inputIds"))) != probe.get("inputIdsSha256")):
        raise ValueError("reload probe contents differ from their internal identities")
    return probe


def _identity(source, run_id):
    return {"runId": run_id, "prepareRunId": source["evidence"]["runId"],
            "prepareEvidenceSha256": source["evidenceSha256"],
            "trainingPairsSha256": source["trainingSha256"],
            "configSha256": source["configSha256"],
            "candidateArtifactSha256": source["validationSha256"],
            "upstreamModelRevision": source["bound"]["upstream"]["modelRevision"]}


def _order_metrics(logits, pair_count):
    probabilities = [torch.softmax(torch.tensor(row), dim=-1)[1].item() for row in logits]
    if len(probabilities) != pair_count * 2:
        raise ValueError("pair-order probe dimensions differ")
    differences = [abs(left - right) for left, right in zip(probabilities[:pair_count],
                                                            probabilities[pair_count:])]
    agreements = [(left >= 0.5) == (right >= 0.5)
                  for left, right in zip(probabilities[:pair_count], probabilities[pair_count:])]
    return {"probePairCount": pair_count,
            "swapAgreementAtHalf": sum(agreements) / pair_count if pair_count else 1.0,
            "meanProbabilityDifference": sum(differences) / pair_count if pair_count else 0.0,
            "maximumProbabilityDifference": max(differences, default=0.0)}


def train(*, run_id, prepare_run_id, inputs_path, config_path):
    run_root = ensure_inside(REPO_ROOT / "artifacts" / "pair_verification" / "runs", REPO_ROOT / "artifacts")
    run = ensure_inside(run_root / run_id, run_root)
    if run.exists():
        raise FileExistsError("immutable training run collision")
    run.mkdir(parents=True)
    started = time.monotonic()
    evidence_path = run / "training-evidence.json"
    run_started_at = datetime.now().astimezone()
    driver_checks = []
    source = None
    disk_space = {"budgetBytes": None, "freeBytesAtPreflight": None, "sufficient": False}
    epoch_rows, checkpoint_rows = [], []
    total_attempted = total_skipped = 0
    current_epoch = current_step = None
    current_epoch_losses = []
    current_loss = None
    try:
        driver_checks.append(check_gpu_driver_events(run_started_at))
        source = load_bound_prepare(prepare_run_id=prepare_run_id, inputs_path=inputs_path,
                                    config_path=config_path)
        optimization = source["optimization"]
        disk_budget = optimization["checkpointDiskBudgetBytes"]
        free_bytes = shutil.disk_usage(run_root).free
        disk_space = {"budgetBytes": disk_budget, "freeBytesAtPreflight": free_bytes,
                      "sufficient": free_bytes >= disk_budget}
        if not disk_space["sufficient"]:
            raise ValueError("free disk space is below the frozen checkpoint budget")
        upstream_checkpoint.configure_numerics()
        device = upstream_checkpoint.cuda_device()
        torch.manual_seed(optimization["seed"])
        torch.cuda.manual_seed_all(optimization["seed"])
        random.seed(optimization["seed"])
        tokenizer, encoder, load_info, upstream = upstream_checkpoint.load_upstream(device)
        examples, data_facts = materialize_training_examples(source, tokenizer)
        validation_records = _validation_records(source, tokenizer)
        validation_windows_sha = _write_json(run / "validation-window-pairs.json", validation_records, run)
        probe_rows = _probe_rows(validation_records)
        model = JointVerifier(encoder).to(device)
        trainable_names = freeze_except_last_blocks(model, 4)
        initial_state = state_digest(model)
        initial_encoder = state_digest(model.encoder)
        initial_trainable = state_digest(model, trainable_names)
        initial_frozen = state_digest(model, [name for name, parameter in model.named_parameters()
                                              if not parameter.requires_grad])
        groups = [{"params": [p for n, p in model.named_parameters()
                                if p.requires_grad and n.startswith("encoder.")],
                   "lr": optimization["encoderLearningRate"]},
                  {"params": [p for n, p in model.named_parameters() if p.requires_grad and n.startswith("head.")],
                   "lr": optimization["headLearningRate"]}]
        optimizer = torch.optim.AdamW(groups, weight_decay=optimization["weightDecay"])
        scaler = torch.amp.GradScaler("cuda")
        support_dir = upstream_checkpoint.UPSTREAM_DIR
        support = write_support_files(run / "support", root=run, upstream=upstream,
                                      source_dir=support_dir)
        (run / "checkpoints").mkdir()
        torch.cuda.reset_peak_memory_stats(device)
        for epoch in range(1, optimization["epochs"] + 1):
            current_epoch = epoch
            current_step = 0
            current_epoch_losses = []
            current_loss = None
            model.train()
            ordering = epoch_order(examples, seed=optimization["seed"], epoch=epoch)
            epoch_losses, epoch_attempted, epoch_skipped = [], 0, 0
            optimizer.zero_grad(set_to_none=True)
            micro_batch = optimization["microBatchPairs"]
            accum = optimization["gradientAccumulation"]
            batches = [ordering[i:i + micro_batch] for i in range(0, len(ordering), micro_batch)]
            for batch_index, batch_rows in enumerate(batches):
                current_step = batch_index + 1
                if time.monotonic() - started > optimization["deadlineMinutes"] * 60:
                    raise TimeoutError("training deadline exceeded")
                indices = [row["index"] for row in batch_rows]
                swaps = [row["swapped"] for row in batch_rows]
                ids, mask, labels = _batch(examples, indices, swaps, tokenizer.pad_token_id, device)
                with torch.autocast(device_type="cuda", dtype=torch.float16):
                    logits = model(ids, mask)
                    loss = F.cross_entropy(logits, labels)
                if not torch.isfinite(loss):
                    raise ValueError("non-finite training loss")
                current_loss = float(loss.detach().cpu())
                epoch_losses.append(current_loss)
                current_epoch_losses = epoch_losses
                group_start = (batch_index // accum) * accum
                group_size = min(accum, len(batches) - group_start)
                scaler.scale(loss / group_size).backward()
                should_step = (batch_index + 1) % accum == 0 or batch_index + 1 == len(batches)
                if should_step:
                    skipped = _step_optimizer(scaler, optimizer,
                                              [p for p in model.parameters() if p.requires_grad],
                                              optimization["gradientClip"])
                    epoch_attempted += 1
                    epoch_skipped += int(skipped)
                    total_attempted += 1
                    total_skipped += int(skipped)
                    optimizer.zero_grad(set_to_none=True)
            epoch_loss = sum(epoch_losses) / len(epoch_losses)
            require_step_ceiling(skipped_steps=epoch_skipped, attempted_steps=epoch_attempted,
                                 epoch_losses=[epoch_loss],
                                 maximum_fraction=optimization["failedRunCeiling"]["maximumSkippedStepFraction"])
            model.eval()
            reference_logits = _reference_logits(model, probe_rows, tokenizer.pad_token_id, device)
            probe_order = _order_metrics(reference_logits, len(probe_rows))
            validation_order = _validation_order_evidence(
                model, validation_records, tokenizer.pad_token_id, device)
            reference = {"schema": "pair-verification-reload-probe-v1", "epoch": epoch,
                         "inputIds": probe_rows, "logits": reference_logits,
                         "inputIdsSha256": digest(canonical(probe_rows)),
                         "logitsSha256": digest(canonical(reference_logits)),
                         "tolerance": RELOAD_TOLERANCE}
            probe_sha = _write_json(run / ("reload-probes-epoch-" + str(epoch) + ".json"), reference, run)
            epoch_fact = {"epoch": epoch, "meanTrainingLoss": epoch_loss,
                          "microBatchCount": len(batches), "optimizerStepsAttempted": epoch_attempted,
                          "optimizerStepsSkipped": epoch_skipped,
                          "optimizerStepsApplied": epoch_attempted - epoch_skipped,
                          "orderSha256": digest(canonical(ordering)),
                          "probeLogitsSha256": reference["logitsSha256"],
                          "probeArtifactSha256": probe_sha,
                          "trainFreeProbeOrder": probe_order,
                          "validationPairOrder": validation_order,
                          "selectedForUse": False, "approvedForUse": False,
                          "selection": "PENDING_VALIDATION"}
            checkpoint_path = run / "checkpoints" / ("epoch-" + str(epoch) + ".safetensors")
            manifest, manifest_sha = write_epoch_checkpoint(
                checkpoint_path, root=run, model=model, upstream=upstream, support_hashes=support,
                trainable_names=trainable_names, run_id=run_id, config_sha256=source["configSha256"],
                prepare_evidence_sha256=source["evidenceSha256"],
                training_pairs_sha256=source["trainingSha256"], epoch=epoch, epoch_facts=epoch_fact)
            checkpoint_rows.append({"epoch": epoch, "relativePath": "checkpoints/" + checkpoint_path.name,
                                    "sha256": manifest["weights"]["sha256"],
                                    "bytes": manifest["weights"]["bytes"],
                                    "manifestSha256": manifest_sha,
                                    "probeArtifactSha256": probe_sha,
                                    "modelStateSha256": manifest["modelStateSha256"],
                                    "encoderStateSha256": manifest["encoderStateSha256"],
                                    "selectedForUse": False, "approvedForUse": False,
                                    "selection": "PENDING_VALIDATION"})
            epoch_rows.append(epoch_fact)
            driver_checks.append(check_gpu_driver_events(run_started_at))
        require_step_ceiling(skipped_steps=total_skipped, attempted_steps=total_attempted,
                             epoch_losses=[row["meanTrainingLoss"] for row in epoch_rows],
                             maximum_fraction=optimization["failedRunCeiling"]["maximumSkippedStepFraction"])
        final_encoder = state_digest(model.encoder)
        final_trainable = state_digest(model, trainable_names)
        final_frozen = state_digest(model, [name for name, parameter in model.named_parameters()
                                            if not parameter.requires_grad])
        if final_frozen != initial_frozen:
            raise ValueError("frozen parameters changed during training")
        if final_trainable == initial_trainable or final_encoder == initial_encoder:
            raise ValueError("encoder parameters did not change during training")
        evidence = {"schema": "pair-verification-training-evidence-v1", "runId": run_id,
                    "outcome": "TRAINED", "identity": _identity(source, run_id),
                    "counts": {"trainingPairs": len(examples), "examplesPerLanguage": {
                        language: sum(row["language"] == language for row in examples)
                        for language in sorted({row["language"] for row in examples})}},
                    "data": data_facts, "training": {"epochs": epoch_rows,
                        "optimizerStepsAttempted": total_attempted,
                        "optimizerStepsSkipped": total_skipped,
                        "skippedStepFraction": total_skipped / total_attempted,
                        "trainableParameterNames": trainable_names,
                        "trainableParameterNamesSha256": digest(canonical(trainable_names)),
                        "initialFullStateSha256": initial_state,
                        "finalFullStateSha256": state_digest(model),
                        "initialEncoderStateSha256": initial_encoder,
                        "finalEncoderStateSha256": final_encoder,
                        "finalTrainableStateSha256": final_trainable,
                        "initialFrozenStateSha256": initial_frozen,
                        "finalFrozenStateSha256": final_frozen,
                        "encoderParametersChanged": initial_encoder != final_encoder,
                        "frozenParametersUnchanged": initial_frozen == final_frozen,
                        "gradientInputPartition": "train only",
                        "precision": "CUDA FP16 with loss scaling"},
                    "checkpoints": checkpoint_rows, "reloadProbeCount": len(probe_rows),
                    "validationWindowPairsSha256": validation_windows_sha,
                    "diskSpace": disk_space,
                    "resources": _device_resources(), "elapsedSeconds": time.monotonic() - started,
                    "processId": os.getpid(),
                    "gpuDriverLogChecks": {"provider": "nvlddmkm", "eventId": 153,
                                           "runStartedAt": run_started_at.isoformat(timespec="seconds"),
                                           "checks": driver_checks},
                    "environment": {"python": __import__("sys").version.split()[0],
                                    "torch": torch.__version__, "cuda": torch.version.cuda,
                                    "device": torch.cuda.get_device_name(device)}}
        _write_json(evidence_path, evidence, run)
        return {"outcome": "TRAINED", "directory": run, "evidenceSha256": digest(canonical(evidence))}
    except BaseException as exc:
        failed = {"schema": "pair-verification-training-evidence-v1", "runId": run_id,
                  "outcome": "FAILED", "reason": type(exc).__name__,
                  "failureReason": _safe_failure_reason(exc),
                  "identity": _identity(source, run_id) if source is not None else None,
                  "diskSpace": disk_space,
                  "failureProgress": _failure_progress(
                      current_epoch, current_step, current_loss, current_epoch_losses,
                      epoch_rows, total_attempted, total_skipped),
                  "gpuDriverEvents": getattr(exc, "events", [])}
        if not evidence_path.exists():
            _write_json(evidence_path, failed, run)
        raise


def smoke(*, run_id, prepare_run_id, inputs_path, config_path):
    import re
    if not re.fullmatch(r"dryrun-[a-z0-9-]+", run_id):
        raise ValueError("scratch rehearsal identifiers must use the dryrun prefix")
    scratch_root = ensure_inside(REPO_ROOT / "artifacts" / "pair_verification", REPO_ROOT / "artifacts")
    scratch = ensure_inside(scratch_root / run_id, scratch_root)
    if scratch.exists():
        raise FileExistsError("scratch rehearsal directory collision")
    scratch.mkdir()
    started = time.monotonic()
    failure_context = {}
    stage = "chain-binding"
    try:
        source = load_bound_prepare(prepare_run_id=prepare_run_id, inputs_path=inputs_path,
                                    config_path=config_path)
        upstream_checkpoint.configure_numerics()
        device = upstream_checkpoint.cuda_device()
        torch.manual_seed(source["optimization"]["seed"])
        stage = "train-input-materialization"
        tokenizer, encoder, _, upstream = upstream_checkpoint.load_upstream(device)
        examples, data_facts = materialize_training_examples(source, tokenizer)
        stage = "validation-window-materialization"
        validation_records = _validation_records(source, tokenizer)
        validation_sha = _write_json(scratch / "validation-window-pairs.json", validation_records, scratch)
        model = JointVerifier(encoder).to(device)
        trainable_names = freeze_except_last_blocks(model, 4)
        stage = "one-step-training"
        optimizer = torch.optim.AdamW(
            [{"params": [p for name, p in model.named_parameters() if p.requires_grad and name.startswith("encoder.")],
              "lr": source["optimization"]["encoderLearningRate"]},
             {"params": [p for name, p in model.named_parameters() if p.requires_grad and name.startswith("head.")],
              "lr": source["optimization"]["headLearningRate"]}],
            weight_decay=source["optimization"]["weightDecay"])
        scaler = torch.amp.GradScaler("cuda")
        order = epoch_order(examples, seed=source["optimization"]["seed"], epoch=1)[:2]
        ids, mask, labels = _batch(examples, [row["index"] for row in order],
                                   [row["swapped"] for row in order], tokenizer.pad_token_id, device)
        model.train()
        optimizer.zero_grad(set_to_none=True)
        with torch.autocast(device_type="cuda", dtype=torch.float16):
            loss = F.cross_entropy(model(ids, mask), labels)
        if not torch.isfinite(loss):
            raise ValueError("non-finite rehearsal loss")
        scaler.scale(loss).backward()
        scaler.unscale_(optimizer)
        torch.nn.utils.clip_grad_norm_([p for p in model.parameters() if p.requires_grad],
                                       source["optimization"]["gradientClip"])
        scaler.step(optimizer)
        scaler.update()
        model.eval()
        support = write_support_files(scratch / "support", root=scratch, upstream=upstream,
                                      source_dir=upstream_checkpoint.UPSTREAM_DIR)
        (scratch / "checkpoints").mkdir()
        path = scratch / "checkpoints" / "epoch-1.safetensors"
        manifest, manifest_sha = write_epoch_checkpoint(
            path, root=scratch, model=model, upstream=upstream, support_hashes=support,
            trainable_names=trainable_names, run_id=run_id, config_sha256=source["configSha256"],
            prepare_evidence_sha256=source["evidenceSha256"], training_pairs_sha256=source["trainingSha256"],
            epoch=1, epoch_facts={"rehearsal": True, "loss": float(loss.detach().cpu())})
        stage = "checkpoint-reload"
        expected = {"runId": run_id, "configSha256": source["configSha256"],
                    "prepareEvidenceSha256": source["evidenceSha256"],
                    "trainingPairsSha256": source["trainingSha256"]}
        loaded, _ = load_epoch_checkpoint(path, root=scratch, expected_manifest_sha256=manifest_sha,
                                          expected_training_evidence=expected, upstream=upstream, device=device)
        probe = validation_records[0]["windowPairs"][0]
        expected_logits = _forward_logits(model, [probe["inputIds"], probe["swappedInputIds"]],
                                          device, pad_id=tokenizer.pad_token_id, batch_size=2)
        actual_logits = _forward_logits(loaded, [probe["inputIds"], probe["swappedInputIds"]],
                                        device, pad_id=tokenizer.pad_token_id, batch_size=2)
        differences = [abs(left - right) for expected_row, actual_row in zip(expected_logits, actual_logits)
                       for left, right in zip(expected_row, actual_row)]
        failure_context["reloadMaximumAbsoluteDifference"] = max(differences, default=0.0)
        failure_context["reloadTolerance"] = RELOAD_TOLERANCE
        reload = require_reload_match(expected_logits, actual_logits, tolerance=RELOAD_TOLERANCE)
        stage = "validation-pair-order"
        validation_order = _validation_order_evidence(model, validation_records, tokenizer.pad_token_id, device)
        report = {"schema": "pair-verification-training-smoke-v1", "outcome": "SMOKE_COMPLETED",
                  "prepareEvidenceSha256": source["evidenceSha256"],
                  "trainingPairsSha256": source["trainingSha256"],
                  "configSha256": source["configSha256"],
                  "candidateArtifactSha256": source["validationSha256"],
                  "validationWindowPairsSha256": validation_sha,
                  "trainingPairCount": len(examples), "data": data_facts,
                  "validationCandidatePairCount": validation_order["candidatePairCount"],
                  "validationOrder": validation_order, "oneStepLoss": float(loss.detach().cpu()),
                  "checkpointSha256": manifest["weights"]["sha256"],
                  "checkpointBytes": manifest["weights"]["bytes"], "reload": reload,
                  "elapsedSeconds": time.monotonic() - started,
                  "resources": _device_resources(), "noTrainingEvidence": True}
        report_sha = _write_json(scratch / "smoke-report.json", report, scratch)
        return {"outcome": "SMOKE_COMPLETED", "directory": scratch, "reportSha256": report_sha}
    except Exception as exc:
        failed = {"schema": "pair-verification-training-smoke-v1", "outcome": "SMOKE_FAILED",
                  "reason": type(exc).__name__, "failedStage": stage, **failure_context}
        if not (scratch / "smoke-report.json").exists():
            _write_json(scratch / "smoke-report.json", failed, scratch)
        raise


def reload_check(*, run_id):
    _, run = _reload_run_directory(run_id)
    try:
        return _reload_check(run_id=run_id)
    except Exception as exc:
        failure_path = run / "reload-evidence.json"
        if not failure_path.exists():
            failure = {"schema": "pair-verification-reload-evidence-v1", "runId": run_id,
                       "outcome": "FAILED", "reason": type(exc).__name__,
                       "failureReason": _safe_failure_reason(exc),
                       "gpuDriverEvents": getattr(exc, "events", [])}
            _write_json(failure_path, failure, run)
        raise


def _reload_run_directory(run_id):
    if not isinstance(run_id, str) or not re.fullmatch(r"pairs-train-[0-9]{8}-[0-9]{2}", run_id):
        raise ValueError("invalid training run identifier")
    run_root = REPO_ROOT / "artifacts" / "pair_verification" / "runs"
    run = ensure_inside(run_root / run_id, run_root)
    if not run.is_dir():
        raise FileNotFoundError("training run directory does not exist")
    return run_root, run


def _reload_check(*, run_id):
    _, run = _reload_run_directory(run_id)
    evidence_raw = (run / "training-evidence.json").read_bytes()
    evidence = json.loads(evidence_raw)
    evidence_sha = digest(evidence_raw)
    checkpoints = _validate_reload_evidence(evidence, run_id, os.getpid())
    device = upstream_checkpoint.cuda_device()
    upstream_checkpoint.configure_numerics()
    source = upstream_checkpoint.upstream_identity()
    training_identity = evidence["identity"]
    driver_block = evidence.get("gpuDriverLogChecks") or {}
    if driver_block.get("provider") != "nvlddmkm" or driver_block.get("eventId") != 153:
        raise ValueError("training evidence lacks the GPU stability monitor binding")
    run_started_at = datetime.fromisoformat(driver_block["runStartedAt"])
    driver_checks = [check_gpu_driver_events(run_started_at)]
    reload_rows, probe_differences = [], []
    for checkpoint_row in checkpoints:
        epoch = checkpoint_row["epoch"]
        expected = {"runId": run_id, "configSha256": training_identity["configSha256"],
                    "prepareEvidenceSha256": training_identity["prepareEvidenceSha256"],
                    "trainingPairsSha256": training_identity["trainingPairsSha256"]}
        checkpoint_path = ensure_inside(run / checkpoint_row["relativePath"], run)
        model, manifest = load_epoch_checkpoint(
            checkpoint_path, root=run, expected_manifest_sha256=checkpoint_row["manifestSha256"],
            expected_training_evidence=expected, upstream=source, device=device)
        probe_path = ensure_inside(run / ("reload-probes-epoch-" + str(epoch) + ".json"), run)
        probe = _read_verified_probe(probe_path, checkpoint_row)
        ids = [row["inputIds"] for row in probe["inputIds"]]
        swapped = [row["swappedInputIds"] for row in probe["inputIds"]]
        # tokenizer pad ID comes from the immutable upstream support file.
        from transformers import RobertaTokenizerFast
        tokenizer = RobertaTokenizerFast.from_pretrained(str(run / "support"), local_files_only=True,
                                                          trust_remote_code=False)
        actual = _forward_logits(model, ids + swapped, device, pad_id=tokenizer.pad_token_id, batch_size=8)
        expected_logits = probe["logits"]
        comparison = require_reload_match(expected_logits, actual, tolerance=RELOAD_TOLERANCE)
        probe_differences.append(comparison["maximumAbsoluteDifference"])
        order = _order_metrics(actual, len(ids))
        reload_rows.append({"epoch": epoch, "checkpointSha256": checkpoint_row["sha256"],
                            "modelStateSha256": manifest["modelStateSha256"],
                            "trainFreeProbeOrder": order, **comparison})
        driver_checks.append(check_gpu_driver_events(run_started_at))
        if epoch == 5:
            validation_windows_path = ensure_inside(run / "validation-window-pairs.json", run)
            if digest(validation_windows_path.read_bytes()) != evidence.get("validationWindowPairsSha256"):
                raise ValueError("validation window-pair bytes differ from training evidence")
            validation_records = json.loads(validation_windows_path.read_bytes())
            validation_order = _validation_order_evidence(model, validation_records,
                                                          tokenizer.pad_token_id, device)
    driver_checks.append(check_gpu_driver_events(run_started_at))
    result = {"schema": "pair-verification-reload-evidence-v1", "runId": run_id,
              "outcome": "SUCCEEDED", "trainingEvidenceSha256": evidence_sha,
              "trainingProcessId": evidence["processId"], "reloadProcessId": os.getpid(),
              "freshProcess": True,
              "epochs": reload_rows, "allEpochsWithinTolerance": True,
              "maximumReloadDifference": max(probe_differences, default=0.0),
              "tolerance": RELOAD_TOLERANCE,
              "pairOrder": validation_order,
              "gpuDriverLogChecks": {"provider": "nvlddmkm", "eventId": 153,
                                     "runStartedAt": driver_block["runStartedAt"],
                                     "checks": driver_checks},
              "resourcePeakBytes": int(torch.cuda.max_memory_allocated())}
    raw = canonical(result)
    write_once(run / "reload-evidence.json", raw, run)
    write_once(run / "SUCCEEDED", (digest(raw) + "\n").encode("ascii"), run)
    return {"outcome": "SUCCEEDED", "directory": run, "evidenceSha256": digest(raw)}


