"""Freeze manifest, end-to-end metrics and guards for model evaluation.

Pure stages run in the pinned CPU interpreter at ``artifacts/code_analysis/env/ml``.
Stages that load a trained or frozen encoder run in the CUDA interpreter at
``artifacts/feasibility_pilot/env/ml``.
"""

__version__ = "1.0"
