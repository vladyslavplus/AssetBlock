"""Reviewed pair preparation and evidence binding.

The preparation stage runs in the CUDA interpreter at
``artifacts/feasibility_pilot/env/ml`` because it computes frozen encoder
embeddings for training fragments. Unit tests and pure ranking helpers also run
in the pinned CPU interpreter at ``artifacts/code_analysis/env/ml``.
"""

__version__ = "1.0"
