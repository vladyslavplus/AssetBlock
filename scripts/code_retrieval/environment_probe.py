"""Interpreter probe: a fixed sample of admitted endpoints encoded on CPU, FP32, eval.

Any interpreter can run the probe; CUDA is not needed. The reference probe is written by
the pinned CPU interpreter (artifacts/code_analysis/env/ml). Training and the export and
evaluation stages run the probe again in their own interpreter and refuse to start
unless the two agree on every sample's endpoint, token ids, offsets, chunk-0 window,
loaded upstream state and pooled FP32 vector within PROBE_TOLERANCE. The live
interpreter may differ from the reference only in INTERPRETER_DIFFERENCES.
"""

import base64
import platform
import struct
import sys
from importlib.metadata import PackageNotFoundError, version

import torch

from scripts.code_analysis.contracts import LANGUAGES, canonical, digest
from scripts.code_analysis.representations import vector_bytes

from . import admission, checkpoint, config as config_module, inputs, prepare
from .endpoints import endpoint_windows
from .index_verify import EMBEDDING_DIMENSIONS

PROBE_SCHEMA = "code-retrieval-environment-probe-v2"
PROBE_TOLERANCE = 1e-5
PACKAGES = ("transformers", "safetensors", "tokenizers", "numpy")
# Pinned CPU interpreter: marker of its location and its exact lock versions, from
# artifacts/code_analysis/env/provisioning/pip-artifacts-ml.json and
# scripts/feasibility_pilot/config/requirements-ml.txt.
PINNED_INTERPRETER_MARKER = "artifacts/code_analysis/env/ml/"
PINNED_VERSIONS = {"transformers": "5.17.0", "tokenizers": "0.23.2", "safetensors": "0.8.0"}
PINNED_TORCH_SUFFIX = "+cpu"
# Live interpreter fields allowed to differ from the reference; every other interpreter field must be equal.
INTERPRETER_DIFFERENCES = ("python", "torch", "torchCudaBuild", "cudaAvailable",
                           "transformers", "safetensors", "tokenizers")
EXACT_FIELDS = ("tokenCount", "tokenIdsSha256", "tokenOffsetsSha256", "windowCount", "chunk0Sha256",
                "vectorDimension", "endpointSha256")


def _package_version(name):
    try:
        return version(name)
    except PackageNotFoundError:
        return None


def interpreter_facts():
    facts = {"python": sys.executable, "pythonVersion": platform.python_version(),
             "torch": torch.__version__, "torchCudaBuild": torch.version.cuda,
             "cudaAvailable": torch.cuda.is_available()}
    for name in PACKAGES:
        facts[name] = _package_version(name)
    return facts


def select_samples(triplets, sources_root, tokenizer):
    """Deterministic sample: first admitted endpoint per language, first multi-window, first non-ASCII."""
    picks = []
    for language in LANGUAGES:
        first = next((triplet for triplet in triplets if triplet["language"] == language), None)
        if first is not None:
            picks.append(("language:" + language, first))
    multi = next((triplet for triplet in triplets
                  if len(endpoint_windows(triplet["endpoints"][0], sources_root, tokenizer)) > 1), None)
    if multi is not None:
        picks.append(("multiWindow", multi))
    non_ascii = next((triplet for triplet in triplets if any(byte > 127 for byte in triplet["endpoints"][0]["body"])),
                     None)
    if non_ascii is not None:
        picks.append(("nonAscii", non_ascii))
    merged = {}
    for role, triplet in picks:
        fragment_id = triplet["endpoints"][0]["fragment"]["fragmentId"]
        if fragment_id in merged:
            merged[fragment_id]["roles"].append(role)
        else:
            merged[fragment_id] = {"roles": [role], "triplet": triplet}
    return list(merged.values())


def sample_record(roles, triplet, sources_root, tokenizer, model):
    """One sample: the query endpoint of the triplet, encoded on CPU, FP32, eval."""
    endpoint = triplet["endpoints"][0]
    windows = endpoint_windows(endpoint, sources_root, tokenizer)
    body = endpoint["body"]
    encoded = tokenizer(body.decode("utf-8"), add_special_tokens=False, return_offsets_mapping=True,
                        truncation=False)
    ids, offsets = encoded["input_ids"], encoded["offset_mapping"]
    chunk0 = windows[0]
    vectors = checkpoint.reference_vectors(model, [chunk0["inputIds"]], tokenizer.pad_token_id,
                                           torch.device("cpu"))
    packed = vector_bytes(vectors)
    return {"roles": roles, "language": triplet["language"],
            "sample": {"fragmentId": endpoint["fragment"]["fragmentId"],
                       "endpointSha256": endpoint["fragment"]["sha256"], "bytes": len(body)},
            "endpointSha256": endpoint["fragment"]["sha256"],
            "tokenCount": len(ids), "tokenIdsSha256": digest(canonical(ids)),
            "tokenOffsetsSha256": digest(canonical([list(pair) for pair in offsets])),
            "windowCount": len(windows), "chunk0Sha256": digest(canonical(chunk0)),
            "vectorDimension": EMBEDDING_DIMENSIONS,
            "vectorBytes": len(packed), "vectorSha256": digest(packed),
            "vectorBase64": base64.b64encode(packed).decode("ascii")}


def probe_document(records, loaded_state_sha256):
    return {"schema": PROBE_SCHEMA, "interpreter": interpreter_facts(), "device": "cpu",
            "dtype": "float32", "mode": "eval", "loadedFullStateSha256": loaded_state_sha256,
            "samples": records}


def build_probe(triplets, sources_root):
    """Load the pinned upstream on CPU and probe the fixed sample of admitted endpoints."""
    tokenizer, model, load_info, _ = checkpoint.load_upstream(torch.device("cpu"))
    picks = select_samples(triplets, sources_root, tokenizer)
    records = [sample_record(pick["roles"], pick["triplet"], sources_root, tokenizer, model) for pick in picks]
    return probe_document(records, load_info["loadedFullStateSha256"])


def write_probe_file(path, probe):
    """Write-once canonical probe file; an existing file is an immutable collision."""
    with open(path, "xb") as stream:
        stream.write(canonical(probe))


def write_probe(inputs_path, config_path, out_path):
    """CLI entry: probe the fixed sample of the frozen inputs and write it once."""
    loaded = inputs.load(inputs_path)
    config = config_module.load(config_path)["config"]
    index = prepare.reviewed_index(loaded)
    sources = inputs.value(loaded, inputs.SOURCE_MANIFEST)["sources"]
    triplets = admission.admitted_triplets(index, config, sources=sources, sources_root=loaded["sourcesRoot"])
    if not triplets:
        raise ValueError("environment probe needs at least one admitted triplet")
    probe = build_probe(triplets, loaded["sourcesRoot"])
    write_probe_file(out_path, probe)
    return probe


def _vector(record):
    raw = base64.b64decode(record["vectorBase64"], validate=True)
    if len(raw) != record["vectorBytes"] or digest(raw) != record["vectorSha256"]:
        raise ValueError("probe vector bytes drift")
    values = struct.unpack("<" + "f" * record["vectorDimension"], raw)
    if not all(value == value and abs(value) != float("inf") for value in values):
        raise ValueError("probe vector is not finite")
    return values


def _pinned_reference(reference):
    """The reference must come from the pinned CPU interpreter and its locked versions."""
    facts = reference["interpreter"]
    if PINNED_INTERPRETER_MARKER not in facts["python"].replace("\\", "/"):
        raise ValueError("reference probe was not produced by the pinned interpreter")
    if not str(facts["torch"]).endswith(PINNED_TORCH_SUFFIX) or facts["cudaAvailable"] is not False:
        raise ValueError("reference probe torch is not the pinned CPU build")
    for name, expected in PINNED_VERSIONS.items():
        if facts.get(name) != expected:
            raise ValueError("reference probe {} is not the locked version {}".format(name, expected))
    if reference["device"] != "cpu" or reference["dtype"] != "float32" or reference["mode"] != "eval":
        raise ValueError("reference probe must be CPU FP32 eval")


def compare(reference, live):
    """Refuse to start unless both probes describe the same encoder behaviour on the same samples."""
    for probe in (reference, live):
        if probe.get("schema") != PROBE_SCHEMA:
            raise ValueError("probe schema mismatch")
    _pinned_reference(reference)
    if (live["device"] != reference["device"] or live["dtype"] != reference["dtype"]
            or live["mode"] != reference["mode"]):
        raise ValueError("probe device, dtype or mode differs")
    differing = []
    for key in sorted(set(reference["interpreter"]) | set(live["interpreter"])):
        if reference["interpreter"].get(key) != live["interpreter"].get(key):
            if key not in INTERPRETER_DIFFERENCES:
                raise ValueError("live interpreter differs in a field not allowed to differ: " + key)
            differing.append(key)
    if reference["loadedFullStateSha256"] != live["loadedFullStateSha256"]:
        raise ValueError("probe field differs between interpreters: loadedFullStateSha256")
    if len(reference["samples"]) != len(live["samples"]):
        raise ValueError("probe sample count differs between interpreters")
    worst = 0.0
    for left, right in zip(reference["samples"], live["samples"]):
        if (left["roles"] != right["roles"] or left["sample"] != right["sample"]
                or left["language"] != right["language"]):
            raise ValueError("probe sample endpoint differs between interpreters")
        for field in EXACT_FIELDS:
            if left.get(field) != right.get(field):
                raise ValueError("probe field differs between interpreters: " + field)
        a, b = _vector(left), _vector(right)
        if len(a) != len(b):
            raise ValueError("probe vector dimension differs")
        difference = max(abs(x - y) for x, y in zip(a, b))
        if difference > PROBE_TOLERANCE:
            raise ValueError("probe vectors differ by {} (tolerance {})".format(difference, PROBE_TOLERANCE))
        worst = max(worst, difference)
    return {"result": "MATCH", "samplesCompared": len(reference["samples"]),
            "exactFieldsCompared": list(EXACT_FIELDS) + ["sample", "roles", "language", "loadedFullStateSha256"],
            "interpreterDifferences": differing, "vectorMaxAbsDifference": worst, "tolerance": PROBE_TOLERANCE}
