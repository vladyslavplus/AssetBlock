"""Safetensors-only epoch snapshots and bound reload loading for the pilot CUDA interpreter."""

from pathlib import Path

from safetensors.torch import load_file, save

from scripts.code_analysis.contracts import canonical, digest, sha
from scripts.code_analysis.corpus import write_once
from scripts.code_analysis.frozen_encoder import file_hash, state_hash
from scripts.feasibility_pilot.paths import ensure_inside

from .architecture import JointVerifier, state_digest

CHECKPOINT_SCHEMA = "pair-verification-epoch-checkpoint-v1"
SUPPORT_FILES = ("config.json", "vocab.json", "merges.txt", "tokenizer_config.json", "special_tokens_map.json")
RELOAD_TOLERANCE = 1e-5


def write_support_files(support_dir, *, root, upstream, source_dir):
    hashes = {}
    for name in SUPPORT_FILES:
        source = ensure_inside(Path(source_dir) / name, source_dir)
        raw = source.read_bytes()
        expected = sha(upstream["fileHashes"].get(name))
        if digest(raw) != expected:
            raise ValueError("upstream support file bytes differ")
        write_once(Path(support_dir) / name, raw, root)
        hashes[name] = {"sha256": expected, "bytes": len(raw)}
    return hashes


def write_epoch_checkpoint(path, *, root, model, upstream, support_hashes, trainable_names,
                           run_id, config_sha256, prepare_evidence_sha256, training_pairs_sha256,
                           epoch, epoch_facts):
    path = Path(path)
    if path.exists():
        raise FileExistsError("immutable checkpoint collision")
    state = {name: tensor.detach().cpu().contiguous() for name, tensor in model.state_dict().items()}
    raw = save(state, metadata={"format": "pt"})
    write_once(path, raw, root)
    manifest = {
        "schema": CHECKPOINT_SCHEMA,
        "runId": run_id,
        "epoch": epoch,
        "configSha256": sha(config_sha256),
        "prepareEvidenceSha256": sha(prepare_evidence_sha256),
        "trainingPairsSha256": sha(training_pairs_sha256),
        "upstream": {"modelRevision": upstream["modelRevision"], "fileHashes": upstream["fileHashes"]},
        "supportFiles": support_hashes,
        "weights": {"relativePath": path.name, "sha256": digest(raw), "bytes": len(raw)},
        "modelStateSha256": state_hash(model),
        "encoderStateSha256": state_digest(model.encoder),
        "trainableParameterNames": trainable_names,
        "trainableParameterNamesSha256": digest(canonical(trainable_names)),
        "selectedForUse": False,
        "approvedForUse": False,
        "selection": "PENDING_VALIDATION",
        "epochFacts": epoch_facts,
    }
    manifest_path = path.with_suffix(".json")
    write_once(manifest_path, canonical(manifest), root)
    return manifest, digest(canonical(manifest))


def load_epoch_checkpoint(path, *, root, expected_manifest_sha256, expected_training_evidence,
                          upstream, device):
    path = ensure_inside(Path(path), root)
    manifest_path = path.with_suffix(".json")
    raw = manifest_path.read_bytes()
    if digest(raw) != sha(expected_manifest_sha256):
        raise ValueError("checkpoint manifest identity differs from training evidence")
    import json
    manifest = json.loads(raw)
    if manifest.get("schema") != CHECKPOINT_SCHEMA or manifest.get("weights", {}).get("relativePath") != path.name:
        raise ValueError("checkpoint manifest schema or path mismatch")
    expected_fields = {"runId": expected_training_evidence["runId"],
                       "configSha256": expected_training_evidence["configSha256"],
                       "prepareEvidenceSha256": expected_training_evidence["prepareEvidenceSha256"],
                       "trainingPairsSha256": expected_training_evidence["trainingPairsSha256"]}
    if any(manifest.get(key) != value for key, value in expected_fields.items()):
        raise ValueError("checkpoint is not bound to the training evidence")
    if (manifest.get("selectedForUse") is not False or manifest.get("approvedForUse") is not False
            or manifest.get("selection") != "PENDING_VALIDATION"):
        raise ValueError("checkpoint status differs from the frozen pending selection")
    support = path.parent.parent / "support"
    actual_support = {}
    for name in SUPPORT_FILES:
        support_path = ensure_inside(support / name, root)
        item = manifest["supportFiles"].get(name) or {}
        actual_support[name] = {"sha256": file_hash(support_path), "bytes": support_path.stat().st_size}
        if actual_support[name] != item or actual_support[name]["sha256"] != upstream["fileHashes"].get(name):
            raise ValueError("checkpoint support file drift")
    expected_weights = manifest.get("weights") or {}
    if (expected_weights.get("bytes") != path.stat().st_size
            or file_hash(path) != sha(expected_weights.get("sha256"))):
        raise ValueError("checkpoint safetensors bytes drift")
    from transformers import RobertaConfig, RobertaModel
    config = RobertaConfig.from_pretrained(str(support), local_files_only=True, trust_remote_code=False)
    encoder = RobertaModel(config)
    model = JointVerifier(encoder).to(device)
    state = load_file(str(path), device=str(device))
    model.load_state_dict(state, strict=True)
    if state_hash(model) != sha(manifest.get("modelStateSha256")):
        raise ValueError("loaded checkpoint full state differs from manifest")
    if state_digest(model.encoder) != sha(manifest.get("encoderStateSha256")):
        raise ValueError("loaded checkpoint encoder state differs from manifest")
    model.eval()
    for parameter in model.parameters():
        parameter.requires_grad_(False)
    return model, manifest
