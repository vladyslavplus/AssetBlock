"""Joint-input verifier architecture for the pilot CUDA interpreter."""

import torch
from torch import nn


class JointVerifier(nn.Module):
    def __init__(self, encoder):
        super().__init__()
        self.encoder = encoder
        self.head = nn.Linear(encoder.config.hidden_size, 2)

    def forward(self, input_ids, attention_mask):
        output = self.encoder(input_ids=input_ids, attention_mask=attention_mask)
        return self.head(output.last_hidden_state[:, 0, :])


def freeze_except_last_blocks(model, block_count):
    layers = getattr(getattr(model.encoder, "encoder", None), "layer", None)
    if not isinstance(layers, (nn.ModuleList, list)) or len(layers) < block_count or block_count <= 0:
        raise ValueError("encoder block layout cannot satisfy the trainable selection")
    for parameter in model.encoder.parameters():
        parameter.requires_grad_(False)
    expected = set()
    for index, layer in enumerate(layers[-block_count:], start=len(layers) - block_count):
        for name, parameter in layer.named_parameters():
            parameter.requires_grad_(True)
            expected.add(f"encoder.encoder.layer.{index}.{name}")
    for parameter in model.head.parameters():
        parameter.requires_grad_(True)
    expected.update(name for name, _ in model.named_parameters() if name.startswith("head."))
    actual = {name for name, parameter in model.named_parameters() if parameter.requires_grad}
    if actual != expected:
        raise ValueError("trainable parameter selection differs from the frozen contract")
    return sorted(actual)


def state_digest(model, names=None):
    from scripts.code_analysis.frozen_encoder import tensors_hash

    state = model.state_dict()
    selected = state if names is None else {name: state[name] for name in names}
    return tensors_hash(selected)
