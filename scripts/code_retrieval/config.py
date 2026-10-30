"""Frozen experiment and admission configuration, validated before any stage runs.

The accepted admission floors, caps and experiment bounds are a reviewed protocol
identity, not free parameters. Validation requires the configuration to match that
protocol exactly; producing a fresh digest for a weaker configuration is not
approval to run it.
"""

import json
import re
from pathlib import Path

from scripts.code_analysis.contracts import LANGUAGES, canonical, digest, sha

CONFIG_SCHEMA = "code-retrieval-config-v1"
MODEL_REVISION = re.compile("[0-9a-f]{40}")
TRANSFORMATIONS = ("AST_BOUND_RENAME", "COMMENT_FORMAT")

MAX_CONFIG_BYTES = 1024 * 1024

# The single reviewed protocol identity. Changing any frozen floor, cap or
# experiment bound requires a new independently reviewed protocol revision,
# which is not something a configuration file can grant to itself.
ACCEPTED_PROTOCOL_REVISION = "code-retrieval-protocol-v1"

PROTOCOL = {
    "admission": {
        "minAnchorsPerLanguage": 20,
        "maxAnchorsPerLanguage": 60,
        "minNegativesPerAnchor": 2,
        "minNonIdenticalPositiveAnchorsPerLanguage": 10,
        "positiveTransformations": ["AST_BOUND_RENAME", "COMMENT_FORMAT"],
    },
    "proposal": {"anchorsPerLanguage": 30, "negativesPerAnchor": 2},
    "training": {
        "upstreamModelRevision": "5f0dc256eb0904af496d427451da5d7f90261676",
        "optimizer": "AdamW",
        "maxTokens": 256,
        "framingTokens": 4,
        "embeddingDimensions": 768,
        "trainableEncoderBlocks": 2,
        "microbatchTriplets": 1,
        "gradientAccumulation": 4,
        "epochs": 3,
        "maxAdmittedTriplets": 300,
        "maxAnchorsPerLanguage": 60,
        "learningRate": 1e-05,
        "temperature": 0.07,
        "gradientClip": 1.0,
        "deadlineMinutes": 90,
    },
    "selection": {"metric": "seedMacroMRR@20", "tieBreak": ["Recall@20", "earlierEpoch"]},
    "finalTest": {"sealed": True, "scored": False, "tuning": False},
}

PROTOCOL_SHA256 = digest(canonical(PROTOCOL))


def _int(config, section, key, minimum=0):
    value = config[section].get(key)
    if type(value) is not int or value < minimum:
        raise ValueError("invalid integer {}.{}".format(section, key))
    return value


def validate(config):
    if not isinstance(config, dict) or config.get("schema") != CONFIG_SCHEMA:
        raise ValueError("unexpected configuration schema")
    for section in ("admission", "proposal", "training", "selection", "finalTest"):
        if not isinstance(config.get(section), dict):
            raise ValueError("missing configuration section: " + section)
    if sorted(config.get("languages") or []) != sorted(LANGUAGES):
        raise ValueError("configuration must enumerate exactly the supported languages")
    if config.get("seed") != 42:
        raise ValueError("frozen seed is 42")
    if config.get("protocolRevision") != ACCEPTED_PROTOCOL_REVISION:
        raise ValueError("an approved protocol revision is required; hashing a configuration is not approval")

    for section, fields in PROTOCOL.items():
        for key, value in fields.items():
            if config[section].get(key) != value:
                raise ValueError("frozen protocol field changed: {}.{}".format(section, key))

    admission = config["admission"]
    training = config["training"]
    revision = training.get("upstreamModelRevision")
    if not isinstance(revision, str) or not MODEL_REVISION.fullmatch(revision):
        raise ValueError("pinned upstream model revision required")
    if not set(admission["positiveTransformations"]) <= set(TRANSFORMATIONS):
        raise ValueError("unsupported behavior-preserving transformation declared")
    if training["framingTokens"] >= training["maxTokens"]:
        raise ValueError("framing tokens must be smaller than the token budget")
    if not 1 <= training["trainableEncoderBlocks"] < training["embeddingDimensions"]:
        raise ValueError("trainable encoder block count is out of range")
    if training["maxAnchorsPerLanguage"] != admission["maxAnchorsPerLanguage"]:
        raise ValueError("admission and training anchor caps disagree")
    proposal = config["proposal"]
    if not admission["minAnchorsPerLanguage"] <= proposal["anchorsPerLanguage"] <= admission["maxAnchorsPerLanguage"]:
        raise ValueError("proposal anchor count must sit within admission bounds")
    if admission["minNonIdenticalPositiveAnchorsPerLanguage"] > admission["minAnchorsPerLanguage"]:
        raise ValueError("non-identical-positive floor exceeds the anchor floor")
    if admission["minNegativesPerAnchor"] < 2 or proposal["negativesPerAnchor"] < 2:
        raise ValueError("at least two query-relative negatives are required")
    return config


def load(path):
    with Path(path).open("rb") as stream:
        raw = stream.read(MAX_CONFIG_BYTES + 1)
    if len(raw) > MAX_CONFIG_BYTES:
        raise ValueError("configuration byte cap exceeded")
    try:
        config = json.loads(raw)
    except json.JSONDecodeError as exc:
        raise ValueError("configuration is not valid JSON") from exc
    validate(config)
    return {"config": config, "configSha256": digest(canonical(config)),
            "byteSha256": digest(raw), "protocolSha256": PROTOCOL_SHA256,
            "protocolRevision": ACCEPTED_PROTOCOL_REVISION}


def frozen_identity(loaded):
    sha(loaded["configSha256"])
    sha(loaded["byteSha256"])
    return {"configSha256": loaded["configSha256"], "byteSha256": loaded["byteSha256"],
            "protocolSha256": PROTOCOL_SHA256, "protocolRevision": ACCEPTED_PROTOCOL_REVISION}
