"""Interpreter probe: one fixed admitted endpoint encoded on CPU, compared across interpreters.

Any interpreter can run the probe (no CUDA needed). The training run runs it again in
its own interpreter and refuses to start unless the two probes agree on the sample
endpoint, token ids, offsets, chunk-0 window, loaded upstream state and the pooled
FP32 vector within PROBE_TOLERANCE.
"""

import base64
import platform
import struct
import sys
from importlib.metadata import PackageNotFoundError, version

import torch

from scripts.code_analysis.contracts import canonical, digest
from scripts.code_analysis.representations import vector_bytes

from . import admission, checkpoint, config as config_module, inputs, prepare
from .endpoints import endpoint_windows

PROBE_SCHEMA = "code-retrieval-environment-probe-v1"
PROBE_TOLERANCE = 1e-5
PACKAGES = ("transformers", "safetensors", "tokenizers", "numpy")
EXACT_FIELDS = ("tokenCount", "tokenIdsSha256", "tokenOffsetsSha256", "windowCount", "chunk0Sha256",
                "loadedFullStateSha256", "vectorDimension")


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


def sample_probe(triplet, sources_root, tokenizer, model, *, loaded_state_sha256):
    """Fixed sample: the first endpoint of the first admitted triplet, CPU FP32 eval."""
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
    return {"schema": PROBE_SCHEMA, "interpreter": interpreter_facts(), "device": "cpu",
            "dtype": "float32", "mode": "eval",
            "sample": {"fragmentId": endpoint["fragment"]["fragmentId"],
                       "endpointSha256": endpoint["fragment"]["sha256"], "bytes": len(body)},
            "tokenCount": len(ids), "tokenIdsSha256": digest(canonical(ids)),
            "tokenOffsetsSha256": digest(canonical([list(pair) for pair in offsets])),
            "windowCount": len(windows), "chunk0Sha256": digest(canonical(chunk0)),
            "loadedFullStateSha256": loaded_state_sha256, "vectorDimension": checkpoint.EMBEDDING_DIMENSIONS,
            "vectorBytes": len(packed), "vectorSha256": digest(packed),
            "vectorBase64": base64.b64encode(packed).decode("ascii")}


def build_probe(triplet, sources_root):
    """Load the pinned upstream on CPU and probe one admitted endpoint."""
    tokenizer, model, load_info, _ = checkpoint.load_upstream(torch.device("cpu"))
    return sample_probe(triplet, sources_root, tokenizer, model,
                        loaded_state_sha256=load_info["loadedFullStateSha256"])


def write_probe_file(path, probe):
    """Write-once canonical probe file; an existing file is an immutable collision."""
    with open(path, "xb") as stream:
        stream.write(canonical(probe))


def write_probe(inputs_path, config_path, out_path):
    """CLI entry: probe the first admitted endpoint of the frozen inputs and write it once."""
    loaded = inputs.load(inputs_path)
    config = config_module.load(config_path)["config"]
    index = prepare.reviewed_index(loaded)
    sources = inputs.value(loaded, inputs.SOURCE_MANIFEST)["sources"]
    triplets = admission.admitted_triplets(index, config, sources=sources, sources_root=loaded["sourcesRoot"])
    if not triplets:
        raise ValueError("environment probe needs at least one admitted triplet")
    probe = build_probe(triplets[0], loaded["sourcesRoot"])
    write_probe_file(out_path, probe)
    return probe


def _vectors(probe):
    raw = base64.b64decode(probe["vectorBase64"], validate=True)
    if len(raw) != probe["vectorBytes"] or digest(raw) != probe["vectorSha256"]:
        raise ValueError("probe vector bytes drift")
    values = struct.unpack("<" + "f" * probe["vectorDimension"], raw)
    if not all(value == value and abs(value) != float("inf") for value in values):
        raise ValueError("probe vector is not finite")
    return values


def compare(reference, live):
    """Refuse to start unless both probes describe the same encoder behaviour on the same sample."""
    for probe in (reference, live):
        if probe.get("schema") != PROBE_SCHEMA:
            raise ValueError("probe schema mismatch")
    if reference["sample"] != live["sample"]:
        raise ValueError("probe sample endpoint differs between interpreters")
    for field in EXACT_FIELDS:
        if reference.get(field) != live.get(field):
            raise ValueError("probe field differs between interpreters: " + field)
    if reference.get("device") != live.get("device") or reference.get("dtype") != live.get("dtype"):
        raise ValueError("probe device or dtype differs")
    left, right = _vectors(reference), _vectors(live)
    if len(left) != len(right):
        raise ValueError("probe vector dimension differs")
    difference = max(abs(a - b) for a, b in zip(left, right))
    if difference > PROBE_TOLERANCE:
        raise ValueError("probe vectors differ by {} (tolerance {})".format(difference, PROBE_TOLERANCE))
    return {"result": "MATCH", "exactFieldsCompared": list(EXACT_FIELDS) + ["sample"],
            "vectorMaxAbsDifference": difference, "tolerance": PROBE_TOLERANCE}
