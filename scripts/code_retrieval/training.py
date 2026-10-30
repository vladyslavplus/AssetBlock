"""Retrieval encoder training on admitted train triplets only.

The stage binds to an admitted prepare-data run, checks the live interpreter against a
reference interpreter probe, then trains the last encoder blocks with a softmax over one
positive and two query-relative negatives per triplet. Every epoch writes a
manifest-bound checkpoint and a fixed reference; a fresh process then reloads each
checkpoint. Evidence is write-once and records an outcome, never a quality claim.
Validation, selection and index export are later stages.

Interpreter: trained checkpoints are loaded only in the training interpreter
(artifacts/feasibility_pilot/env/ml); the frozen reference probe is the pinned CPU
interpreter's job, see environment_probe.
"""

import ctypes
import json
import math
import os
import random
import subprocess
import sys
import time
from ctypes import wintypes

import torch
import torch.nn.functional as F

from scripts.code_analysis.contracts import canonical, digest
from scripts.code_analysis.corpus import write_once
from scripts.code_analysis.frozen_encoder import tensors_hash
from scripts.feasibility_pilot.paths import REPO_ROOT

from . import admission, checkpoint, environment_probe, inputs, prepare
from . import config as config_module
from .endpoints import endpoint_windows
from .paths import RETRIEVAL_ROOT, run_dir

SCHEMA = checkpoint.TRAIN_EVIDENCE_SCHEMA
PURPOSE = "CODE_RETRIEVAL_TRAINING"
PREPARE_DATA = "prepare-data.json"
REFERENCE_PROBE = "reference-probe.json"
LIVE_PROBE = "live-probe.json"
STEP_LOG = "train-steps.jsonl"
EVIDENCE = "train-evidence.json"
RELOAD_LOG = "reload-process.log"
ENDPOINT_ROLES = ("query", "positive", "negativeA", "negativeB")
ENDPOINTS_PER_TRIPLET = len(ENDPOINT_ROLES)
REFERENCE_COUNT = 8
# A successful step count below this share of the planned steps is a failed run.
MIN_SUCCESS_NUMERATOR, MIN_SUCCESS_DENOMINATOR = 9, 10
# Share of total VRAM the reserved allocator pool may reach before the run aborts.
MEMORY_GUARD_NUMERATOR, MEMORY_GUARD_DENOMINATOR = 9, 10
SECONDS_PER_MINUTE = 60
LAYER_PREFIX = "encoder.layer."


class StageFailure(Exception):
    def __init__(self, outcome, reason):
        super().__init__(reason)
        self.outcome = outcome
        self.reason = reason


def seed_everything(seed):
    random.seed(seed)
    torch.manual_seed(seed)
    torch.cuda.manual_seed_all(seed)


class _ProcessMemoryCounters(ctypes.Structure):
    _fields_ = [("cb", wintypes.DWORD), ("PageFaultCount", wintypes.DWORD),
                ("PeakWorkingSetSize", ctypes.c_size_t), ("WorkingSetSize", ctypes.c_size_t),
                ("QuotaPeakPagedPoolUsage", ctypes.c_size_t), ("QuotaPagedPoolUsage", ctypes.c_size_t),
                ("QuotaPeakNonPagedPoolUsage", ctypes.c_size_t), ("QuotaNonPagedPoolUsage", ctypes.c_size_t),
                ("PagefileUsage", ctypes.c_size_t), ("PeakPagefileUsage", ctypes.c_size_t)]


def process_working_set():
    """Current and peak working set of this process in bytes; None off Windows.

    A failed Windows call raises with the system error, so a measurement is never
    silently absent.
    """
    if os.name != "nt":
        return None
    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
    psapi = ctypes.WinDLL("psapi", use_last_error=True)
    kernel32.GetCurrentProcess.argtypes = []
    kernel32.GetCurrentProcess.restype = wintypes.HANDLE
    psapi.GetProcessMemoryInfo.argtypes = [wintypes.HANDLE, ctypes.POINTER(_ProcessMemoryCounters), wintypes.DWORD]
    psapi.GetProcessMemoryInfo.restype = wintypes.BOOL
    counters = _ProcessMemoryCounters()
    counters.cb = ctypes.sizeof(_ProcessMemoryCounters)
    if not psapi.GetProcessMemoryInfo(kernel32.GetCurrentProcess(), ctypes.byref(counters), counters.cb):
        raise OSError("GetProcessMemoryInfo failed: " + ctypes.FormatError(ctypes.get_last_error()))
    return {"currentBytes": int(counters.WorkingSetSize), "peakBytes": int(counters.PeakWorkingSetSize)}


def bind_prepare_run(raw, prepare_run_id, *, loaded, config_loaded, recomputed, admitted_count, model_revision,
                     membership):
    """Refuse any drift between the admitted prepare-data bytes and the inputs now loaded."""
    payload = inputs.parse_json(raw, PREPARE_DATA)
    if payload.get("schema") != prepare.SCHEMA:
        raise ValueError("prepare-data schema mismatch")
    if payload.get("runId") != prepare_run_id:
        raise ValueError("prepare run identity mismatch")
    if payload.get("status") != admission.ADMITTED or recomputed["status"] != admission.ADMITTED:
        raise ValueError("training requires an ADMITTED prepare run and recomputed admission")
    training = payload.get("trainingAdmission")
    if not isinstance(training, dict) or training.get("status") != admission.ADMITTED:
        raise ValueError("prepare run trainingAdmission is not ADMITTED")
    identity = payload.get("identity") or {}
    for key, current in (("inputsManifestSha256", loaded["manifestSha256"]),
                         ("configSha256", config_loaded["configSha256"]),
                         ("configByteSha256", config_loaded["byteSha256"]),
                         ("protocolSha256", config_loaded["protocolSha256"])):
        if identity.get(key) != current:
            raise ValueError("prepare identity drift: " + key)
    if config_loaded["config"]["training"]["upstreamModelRevision"] != model_revision:
        raise ValueError("configured upstream revision differs from the trusted identity")
    if payload.get("finalTestMembership") != membership:
        raise ValueError("prepare finalTestMembership differs from the loaded membership report")
    if digest(canonical(training)) != digest(canonical(recomputed)):
        raise ValueError("trainingAdmission digest differs from the recomputed admission")
    if recomputed["totals"]["admittedTriplets"] != admitted_count:
        raise ValueError("admitted triplet count differs from the recomputed admission")
    return {"runId": prepare_run_id, "prepareDataSha256": digest(raw), "prepareDataBytes": len(raw),
            "trainingAdmissionSha256": digest(canonical(training)), "admittedTriplets": admitted_count}


def admitted_binding(loaded, config_loaded, prepare_run_id, index):
    """Recompute admission over the loaded inputs and bind the admitted prepare run to it.

    Returns (binding, admitted triplets, parsed prepare-data payload). Shared by training,
    export-index and evaluate so every stage refuses the same drift.
    """
    sources = inputs.value(loaded, inputs.SOURCE_MANIFEST)["sources"]
    recomputed = admission.evaluate(index, config_loaded["config"], sources=sources,
                                    sources_root=loaded["sourcesRoot"])
    triplets = admission.admitted_triplets(index, config_loaded["config"], sources=sources,
                                           sources_root=loaded["sourcesRoot"])
    prepare_raw = inputs.read_bytes(run_dir(prepare_run_id) / PREPARE_DATA, inputs.MAX_ARTIFACT_BYTES)
    binding = bind_prepare_run(
        prepare_raw, prepare_run_id, loaded=loaded, config_loaded=config_loaded, recomputed=recomputed,
        admitted_count=len(triplets), model_revision=checkpoint.upstream_identity()["modelRevision"],
        membership=prepare.membership_summary(inputs.value(loaded, inputs.FINAL_TEST_MEMBERSHIP)))
    return binding, triplets, inputs.parse_json(prepare_raw, PREPARE_DATA)


def require_train_endpoints(triplet):
    for endpoint in triplet["endpoints"][:ENDPOINTS_PER_TRIPLET]:
        if endpoint["parent"].get("partition") != "train":
            raise ValueError("training refuses a non-train endpoint: " + endpoint["fragment"]["fragmentId"])


def tokenize_triplets(triplets, sources_root, tokenizer):
    """Chunk-0 token ids per endpoint, plus the window accounting the evidence reports."""
    items = []
    for triplet in triplets:
        require_train_endpoints(triplet)
        endpoints = triplet["endpoints"]
        windows = [endpoint_windows(endpoint, sources_root, tokenizer)
                   for endpoint in endpoints[:ENDPOINTS_PER_TRIPLET]]
        items.append({"queryFragmentId": triplet["queryFragmentId"], "language": triplet["language"],
                      "fragmentIds": [endpoint["fragment"]["fragmentId"]
                                      for endpoint in endpoints[:ENDPOINTS_PER_TRIPLET]],
                      "ids": [chunks[0]["inputIds"] for chunks in windows],
                      "windowCounts": [len(chunks) for chunks in windows],
                      "positiveIdentical": endpoints[1]["fragment"]["sha256"] == endpoints[0]["fragment"]["sha256"],
                      "negativesBeyondTrained": len(endpoints) - ENDPOINTS_PER_TRIPLET})
    return items


def reference_rows(items, count):
    """First distinct endpoints in admitted order: the fixed reference set for every epoch."""
    seen, rows, ids = set(), [], []
    for item in items:
        for fragment_id, row in zip(item["fragmentIds"], item["ids"]):
            if fragment_id not in seen and len(rows) < count:
                seen.add(fragment_id)
                rows.append(fragment_id)
                ids.append(row)
    return rows, ids


def select_trainable(model, blocks):
    """Only the last `blocks` encoder layers train; everything else stays frozen."""
    layers = len(model.encoder.layer)
    if type(blocks) is not int or not 1 <= blocks <= layers:
        raise ValueError("trainable encoder block count out of range")
    prefixes = tuple(LAYER_PREFIX + str(index) + "." for index in range(layers - blocks, layers))
    for name, param in model.named_parameters():
        param.requires_grad_(name.startswith(prefixes))
    names = sorted(name for name, param in model.named_parameters() if param.requires_grad)
    if not names:
        raise ValueError("no trainable encoder parameters selected")
    return names


def epoch_order(count, seed, epoch):
    """Deterministic permutation of triplet indices for one epoch."""
    generator = torch.Generator().manual_seed(seed + epoch)
    return torch.randperm(count, generator=generator).tolist()


def accumulation_groups(order, size):
    """Consecutive groups of the epoch order; the final group keeps its real, smaller size."""
    return [order[start:start + size] for start in range(0, len(order), size)]


def triplet_loss(model, item, pad_id, device, temperature):
    """Cross-entropy over [query.positive, query.negA, query.negB] / temperature, target 0."""
    vectors = checkpoint.encode_rows(model, item["ids"], pad_id, device, amp=True)
    logits = (vectors[1:] @ vectors[0]) / temperature
    target = torch.zeros(1, dtype=torch.long, device=device)
    return F.cross_entropy(logits.unsqueeze(0), target)


def finish_step(*, optimizer, scaler, parameters, finite_loss, clip):
    """Unscale, gate on finite gradients, clip, then step. Returns (applied, grad_norm)."""
    scaler.unscale_(optimizer)
    grads_finite = all(bool(torch.isfinite(param.grad).all()) for param in parameters if param.grad is not None)
    if not finite_loss or not grads_finite:
        optimizer.zero_grad(set_to_none=True)
        scaler.update()
        return False, None
    norm = float(torch.nn.utils.clip_grad_norm_(parameters, clip))
    scaler.step(optimizer)
    scaler.update()
    optimizer.zero_grad(set_to_none=True)
    return True, norm


def state_digests(model, trainable_names):
    state = model.state_dict()
    trainable = {name: tensor for name, tensor in state.items() if name in trainable_names}
    frozen = {name: tensor for name, tensor in state.items() if name not in trainable_names}
    return {"fullStateSha256": tensors_hash(state), "trainableStateSha256": tensors_hash(trainable),
            "frozenStateSha256": tensors_hash(frozen)}


def tensor_deltas(model, baseline):
    rows = []
    for name, param in model.named_parameters():
        if name in baseline:
            delta = (param.detach().cpu().float() - baseline[name].float()).norm().item()
            rows.append({"name": name, "l2Delta": delta})
    return rows


def block_summary(deltas):
    """Per trainable block: tensors that moved, and the block's combined L2 change."""
    blocks = {}
    for row in deltas:
        layer = row["name"].split(".")[2]
        blocks.setdefault(layer, []).append(row["l2Delta"])
    return [{"layer": int(layer), "tensors": len(values), "changedTensors": sum(v > 0 for v in values),
             "l2Delta": math.sqrt(sum(v * v for v in values))}
            for layer, values in sorted(blocks.items(), key=lambda pair: int(pair[0]))]


def encoder_changed(deltas):
    return bool(deltas) and all(row["l2Delta"] > 0 for row in deltas)


def decide(*, failure, epochs_done, epochs_expected, planned, successful, nonfinite_losses,
           encoder_changed_flag, frozen_unchanged, reload_passed):
    """Outcome from recorded facts only; SUCCEEDED needs every gate to hold."""
    if failure is not None:
        return failure
    if epochs_done != epochs_expected:
        return ("FAILED_INCOMPLETE", "completed {} of {} epochs".format(epochs_done, epochs_expected))
    if nonfinite_losses:
        return ("FAILED_NONFINITE_LOSS", "{} optimizer step(s) had a non-finite loss".format(nonfinite_losses))
    if successful * MIN_SUCCESS_DENOMINATOR < planned * MIN_SUCCESS_NUMERATOR:
        return ("FAILED_STEP_SHORTFALL", "{} of {} planned steps applied".format(successful, planned))
    if not encoder_changed_flag:
        return ("FAILED_ENCODER_UNCHANGED", "a trainable tensor did not change from upstream")
    if not frozen_unchanged:
        return ("FAILED_FROZEN_CHANGED", "a frozen tensor differs from upstream")
    if not reload_passed:
        return ("FAILED_RELOAD", "fresh-process reload did not reproduce every checkpoint")
    return ("SUCCEEDED", None)


class Trainer:
    def __init__(self, *, model, pad_id, items, reference_ids, device, settings, seed, identity, target, started):
        self.model = model
        self.pad_id = pad_id
        self.items = items
        self.reference_ids = reference_ids
        self.device = device
        self.seed = seed
        self.identity = identity
        self.target = target
        self.temperature = settings["temperature"]
        self.accumulation = settings["gradientAccumulation"]
        self.clip = settings["gradientClip"]
        self.epochs = settings["epochs"]
        self.blocks = settings["trainableEncoderBlocks"]
        self.started = started
        self.deadline = started + settings["deadlineMinutes"] * SECONDS_PER_MINUTE
        self.names = select_trainable(model, self.blocks)
        self.trainable_names = set(self.names)
        self.parameters = [param for name, param in model.named_parameters() if name in self.trainable_names]
        self.optimizer = torch.optim.AdamW(self.parameters, lr=settings["learningRate"])
        self.scaler = torch.amp.GradScaler(device.type, enabled=device.type == "cuda")
        self.baseline = state_digests(model, self.trainable_names)
        self.baseline_tensors = {name: param.detach().cpu().clone() for name, param in model.named_parameters()
                                 if name in self.trainable_names}
        self.steps_per_epoch = [math.ceil(len(items) / self.accumulation)] * self.epochs
        self.planned = sum(self.steps_per_epoch)
        self.successful = 0
        self.skipped = 0
        self.nonfinite = 0
        self.epoch_records = []
        self.log = (target / STEP_LOG).open("x", encoding="utf-8")

    def close(self):
        self.log.close()

    def _limits(self):
        if time.monotonic() > self.deadline:
            raise StageFailure("FAILED_DEADLINE", "training passed the configured deadline")
        if self.device.type == "cuda":
            total = torch.cuda.get_device_properties(self.device).total_memory
            if torch.cuda.max_memory_reserved(self.device) * MEMORY_GUARD_DENOMINATOR > total * MEMORY_GUARD_NUMERATOR:
                raise StageFailure("FAILED_MEMORY_GUARD", "reserved VRAM passed 90 percent of total")

    def _log(self, row):
        self.log.write(json.dumps(row, sort_keys=True) + "\n")
        self.log.flush()

    def train_epoch(self, epoch):
        if self.device.type == "cuda":
            torch.cuda.reset_peak_memory_stats(self.device)
        self.model.train()
        order = epoch_order(len(self.items), self.seed, epoch)
        step_means, norms, epoch_skipped, epoch_successful, epoch_nonfinite = [], [], 0, 0, 0
        for group in accumulation_groups(order, self.accumulation):
            losses = []
            for index in group:
                self._limits()
                loss = triplet_loss(self.model, self.items[index], self.pad_id, self.device, self.temperature)
                losses.append(loss.item())
                self.scaler.scale(loss / len(group)).backward()
            finite = all(math.isfinite(value) for value in losses)
            applied, norm = finish_step(optimizer=self.optimizer, scaler=self.scaler, parameters=self.parameters,
                                        finite_loss=finite, clip=self.clip)
            if applied:
                self.successful += 1
                epoch_successful += 1
                norms.append(norm)
            else:
                self.skipped += 1
                epoch_skipped += 1
            if not finite:
                self.nonfinite += 1
                epoch_nonfinite += 1
            mean = sum(losses) / len(losses) if finite else None
            if mean is not None:
                step_means.append(mean)
            self._log({"epoch": epoch, "step": self.successful + self.skipped, "groupSize": len(group),
                       "meanLoss": mean, "gradNormBeforeClip": norm, "scale": float(self.scaler.get_scale()),
                       "skipped": not applied, "elapsedSeconds": round(time.monotonic() - self.started, 3)})
        return {"stepMeans": step_means, "norms": norms, "successful": epoch_successful,
                "skipped": epoch_skipped, "nonfinite": epoch_nonfinite}

    def finish_epoch(self, epoch, measured, epoch_started):
        digests = state_digests(self.model, self.trainable_names)
        deltas = tensor_deltas(self.model, self.baseline_tensors)
        epoch_dir = self.target / ("epoch-" + str(epoch))
        peak = {"peakAllocatedVRAM": None, "peakReservedVRAM": None}
        if self.device.type == "cuda":
            peak = {"peakAllocatedVRAM": torch.cuda.max_memory_allocated(self.device),
                    "peakReservedVRAM": torch.cuda.max_memory_reserved(self.device)}
        manifest = checkpoint.write_epoch_checkpoint(
            epoch_dir, root=self.identity["root"], model=self.model, upstream=self.identity["upstream"],
            fields={"epoch": epoch, "runId": self.identity["runId"], "trainableNames": self.names,
                    "trainableStateSha256": digests["trainableStateSha256"],
                    "frozenStateSha256": digests["frozenStateSha256"],
                    "configSha256": self.identity["configSha256"],
                    "inputsManifestSha256": self.identity["inputsManifestSha256"],
                    "prepare": self.identity["prepare"],
                    "optimizerSteps": {"plannedThisEpoch": self.steps_per_epoch[epoch - 1],
                                       "successfulCumulative": self.successful,
                                       "skippedCumulative": self.skipped}})
        reference = checkpoint.write_reference(epoch_dir, root=self.identity["root"], model=self.model,
                                               chunk_ids=self.reference_ids, pad_id=self.pad_id,
                                               device=self.device)
        steps = measured["stepMeans"]
        record = {"epoch": epoch, "plannedSteps": self.steps_per_epoch[epoch - 1],
                  "successfulSteps": measured["successful"], "skippedSteps": measured["skipped"],
                  "nonFiniteStepLosses": measured["nonfinite"],
                  "meanLoss": sum(steps) / len(steps) if steps else None,
                  "gradNorm": ({"min": min(measured["norms"]), "mean": sum(measured["norms"]) / len(measured["norms"]),
                                "max": max(measured["norms"])} if measured["norms"] else None),
                  "elapsedSeconds": round(time.monotonic() - epoch_started, 3),
                  **peak, "workingSet": process_working_set(),
                  "digests": digests, "encoderChanged": encoder_changed(deltas),
                  "frozenUnchanged": digests["frozenStateSha256"] == self.baseline["frozenStateSha256"],
                  "blocks": block_summary(deltas), "trainableDeltas": deltas,
                  "referenceSha256": reference["vectorSha256"],
                  "checkpointManifestSha256": digest(canonical(manifest))}
        self.epoch_records.append(record)
        return record

    def run_epochs(self):
        for epoch in range(1, self.epochs + 1):
            epoch_started = time.monotonic()
            measured = self.train_epoch(epoch)
            self.finish_epoch(epoch, measured, epoch_started)


def reload_in_fresh_process(run_id, config_path, root, expectations_path):
    """Run the reload proof in a new interpreter; no console window is ever shown."""
    creation = getattr(subprocess, "CREATE_NO_WINDOW", 0)
    completed = subprocess.run(
        [sys.executable, "-B", "-m", "scripts.code_retrieval", "train-reload-check", "--run-id", run_id,
         "--config", str(config_path), "--expectations", str(expectations_path)],
        cwd=str(REPO_ROOT), capture_output=True, text=True, creationflags=creation, check=False)
    write_once(run_dir(run_id) / RELOAD_LOG,
               ("exit code " + str(completed.returncode) + "\n--- stdout ---\n" + completed.stdout
                + "\n--- stderr ---\n" + completed.stderr).encode("utf-8"), root)
    return completed.returncode


def reload_passed(run_id, epochs):
    for epoch in range(1, epochs + 1):
        path = run_dir(run_id) / ("epoch-" + str(epoch)) / checkpoint.RELOAD_NAME
        if not path.is_file():
            return False
        if not inputs.parse_json(inputs.read_bytes(path, inputs.MAX_ARTIFACT_BYTES), "reload check")["passed"]:
            return False
    return True


def device_facts(device):
    if device.type != "cuda":
        return {"device": device.type}
    return {"device": str(device), "gpu": torch.cuda.get_device_name(device),
            "gpuTotalVRAM": torch.cuda.get_device_properties(device).total_memory}


def run(*, run_id, inputs_path, config_path, prepare_run_id, reference_probe_path):
    started = time.monotonic()
    loaded = inputs.load(inputs_path)
    config_loaded = config_module.load(config_path)
    config = config_loaded["config"]
    settings = config["training"]
    if settings["microbatchTriplets"] != 1:
        raise ValueError("training microbatch is one triplet")
    index = prepare.reviewed_index(loaded)
    prepare_binding, triplets, _ = admitted_binding(loaded, config_loaded, prepare_run_id, index)
    reference_raw = inputs.read_bytes(reference_probe_path, inputs.MAX_ARTIFACT_BYTES)
    reference_probe = inputs.parse_json(reference_raw, "reference probe")
    device = checkpoint.cuda_device()
    if not triplets:
        raise ValueError("training needs at least one admitted triplet")
    live_probe = environment_probe.build_probe(triplets, loaded["sourcesRoot"])
    comparison = environment_probe.compare(reference_probe, live_probe)
    checkpoint.configure_numerics()
    seed_everything(config["seed"])
    target = run_dir(run_id)
    target.mkdir(parents=True, exist_ok=False)
    return _execute(run_id=run_id, target=target, root=RETRIEVAL_ROOT, loaded=loaded, config_loaded=config_loaded,
                    config_path=config_path, settings=settings, triplets=triplets,
                    prepare_binding=prepare_binding, reference_probe_raw=reference_raw,
                    reference_probe=reference_probe, live_probe=live_probe, comparison=comparison,
                    device=device, started=started)


def _execute(*, run_id, target, root, loaded, config_loaded, config_path, settings, triplets, prepare_binding,
             reference_probe_raw, reference_probe, live_probe, comparison, device, started):
    """Everything after the run directory exists. Any failure is recorded as evidence, never raised past it."""
    config = config_loaded["config"]
    trainer = None
    load_info = None
    upstream = None
    items = []
    reference_fragments = []
    failure, reload_code = None, None
    try:
        write_once(target / REFERENCE_PROBE, reference_probe_raw, root)
        write_once(target / LIVE_PROBE, canonical(live_probe), root)
        tokenizer, model, load_info, upstream = checkpoint.load_upstream(device)
        if load_info["dimension"] != settings["embeddingDimensions"]:
            raise ValueError("loaded encoder dimension differs from the configuration")
        items = tokenize_triplets(triplets, loaded["sourcesRoot"], tokenizer)
        reference_fragments, reference_ids = reference_rows(items, REFERENCE_COUNT)
        identity = {"root": root, "runId": run_id, "configSha256": config_loaded["configSha256"],
                    "inputsManifestSha256": loaded["manifestSha256"], "prepare": prepare_binding,
                    "upstream": upstream}
        trainer = Trainer(model=model, pad_id=tokenizer.pad_token_id, items=items, reference_ids=reference_ids,
                          device=device, settings=settings, seed=config["seed"], identity=identity, target=target,
                          started=started)
        trainer.run_epochs()
    except StageFailure as exc:
        failure = (exc.outcome, exc.reason)
    except torch.OutOfMemoryError as exc:
        failure = ("FAILED_OUT_OF_MEMORY", str(exc).splitlines()[0])
    except Exception as exc:  # the evidence must record every failed run, whatever raised it
        failure = ("FAILED_ERROR", type(exc).__name__ + ": " + str(exc))
    finally:
        if trainer is not None:
            trainer.close()
    passed = False
    if failure is None:
        try:
            identity_keys = {key: trainer.identity[key] for key in checkpoint.IDENTITY_KEYS}
            expectations = {"schema": checkpoint.EXPECTATIONS_SCHEMA, "runId": run_id,
                            "epochs": {str(record["epoch"]): {"checkpointManifestSha256":
                                                              record["checkpointManifestSha256"],
                                                              "identity": identity_keys}
                                       for record in trainer.epoch_records}}
            write_once(target / checkpoint.EXPECTATIONS_NAME, canonical(expectations), root)
            reload_code = reload_in_fresh_process(run_id, config_path, root, target / checkpoint.EXPECTATIONS_NAME)
            passed = reload_code == 0 and reload_passed(run_id, settings["epochs"])
        except FileExistsError as exc:
            failure = ("FAILED_ERROR", "reload output collision: " + str(exc))
        except OSError as exc:
            failure = ("FAILED_RELOAD", "reload process could not start: " + str(exc))
        except (ValueError, KeyError) as exc:
            failure = ("FAILED_RELOAD", "reload check unreadable: " + str(exc))
        except Exception as exc:
            failure = ("FAILED_ERROR", type(exc).__name__ + ": " + str(exc))
    epochs = trainer.epoch_records if trainer is not None else []
    outcome, reason = decide(failure=failure, epochs_done=len(epochs), epochs_expected=settings["epochs"],
                             planned=trainer.planned if trainer is not None else 0,
                             successful=trainer.successful if trainer is not None else 0,
                             nonfinite_losses=trainer.nonfinite if trainer is not None else 0,
                             encoder_changed_flag=bool(epochs) and all(row["encoderChanged"] for row in epochs),
                             frozen_unchanged=bool(epochs) and all(row["frozenUnchanged"] for row in epochs),
                             reload_passed=passed)
    evidence = {"schema": SCHEMA, "Purpose": PURPOSE, "runId": run_id, "outcome": outcome, "reason": reason,
                "CanAuthorizePublication": False, "SecurityEvidence": "NOT_RUN",
                "selection": {"selectedForUse": False, "selection": checkpoint.SELECTION},
                "identity": {"inputsManifestSha256": loaded["manifestSha256"],
                             "configSha256": config_loaded["configSha256"],
                             "configByteSha256": config_loaded["byteSha256"],
                             "protocolSha256": config_loaded["protocolSha256"],
                             "prepare": prepare_binding,
                             "upstream": None if upstream is None else {
                                 "modelRevision": upstream["modelRevision"], "fileHashes": upstream["fileHashes"]},
                             "upstreamLoadedFullStateSha256": None if load_info is None
                             else load_info["loadedFullStateSha256"],
                             "upstreamBaselineFullStateSha256": None if trainer is None
                             else trainer.baseline["fullStateSha256"]},
                "environment": {"python": sys.executable, "torch": torch.__version__,
                                "torchCudaBuild": torch.version.cuda, **device_facts(device)},
                "referenceProbe": {"reference": reference_probe, "live": live_probe, "comparison": comparison,
                                   "referenceProbeSha256": digest(reference_probe_raw),
                                   "liveProbeSha256": digest(canonical(live_probe))},
                "data": {"admittedTriplets": len(items),
                         "perLanguage": _count(item["language"] for item in items),
                         "positiveIdenticalToAnchor": sum(item["positiveIdentical"] for item in items),
                         "endpointsWithMultipleWindows": {role: sum(item["windowCounts"][position] > 1
                                                                    for item in items)
                                                          for position, role in enumerate(ENDPOINT_ROLES)},
                         "negativesBeyondTrained": sum(item["negativesBeyondTrained"] for item in items),
                         "referenceFragmentIds": reference_fragments},
                "hyperparameters": {"optimizer": "AdamW", "learningRate": settings["learningRate"],
                                    "optimizerDefaults": _json_defaults(trainer.optimizer.defaults)
                                    if trainer is not None else None,
                                    "temperature": settings["temperature"],
                                    "gradientClip": settings["gradientClip"],
                                    "gradientAccumulation": settings["gradientAccumulation"],
                                    "epochs": settings["epochs"], "seed": config["seed"],
                                    "trainableEncoderBlocks": settings["trainableEncoderBlocks"],
                                    "trainableNames": [] if trainer is None else trainer.names,
                                    "dropout": "model.train() active",
                                    "windowPolicy": "chunk-0 only",
                                    "amp": "CUDA FP16 autocast with GradScaler",
                                    "deadlineMinutes": settings["deadlineMinutes"]},
                "steps": {"planned": trainer.planned if trainer is not None else 0,
                          "successful": trainer.successful if trainer is not None else 0,
                          "skipped": trainer.skipped if trainer is not None else 0,
                          "nonFiniteLosses": trainer.nonfinite if trainer is not None else 0},
                "epochs": epochs,
                "reload": {"exitCode": reload_code, "passed": passed, "log": RELOAD_LOG},
                "elapsedSeconds": round(time.monotonic() - started, 3)}
    try:
        payload = canonical(evidence)
    except ValueError as exc:  # a non-finite value must not leave the run without evidence
        outcome = "FAILED_ERROR"
        reason = "evidence not serializable: " + str(exc)
        payload = canonical({"schema": SCHEMA, "runId": run_id, "outcome": outcome, "reason": reason})
    write_once(target / EVIDENCE, payload, root)
    return {"command": "train", "runId": run_id, "outcome": outcome, "reason": reason,
            "directory": str(target), "evidence": EVIDENCE}


def _count(values):
    counts = {}
    for value in values:
        counts[value] = counts.get(value, 0) + 1
    return dict(sorted(counts.items()))


def _json_defaults(defaults):
    return {key: list(value) if isinstance(value, tuple) else value for key, value in sorted(defaults.items())}
