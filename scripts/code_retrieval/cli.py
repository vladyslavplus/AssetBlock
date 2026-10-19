"""Bounded command line for retrieval data admission.

Only the retrieval-data stage exists. Training, index export, evaluation and evidence
validation are later stages and intentionally absent rather than stubbed.
"""

import argparse

from . import config as config_module, inputs, prepare


def build_parser():
    parser = argparse.ArgumentParser(prog="python -m scripts.code_retrieval")
    stages = parser.add_subparsers(dest="command", required=True)
    stage = stages.add_parser("prepare-data", help="admit training data and freeze experiment identity")
    stage.add_argument("--run-id", required=True, help="new immutable run id")
    stage.add_argument("--inputs", required=True, help="inputs manifest path")
    stage.add_argument("--config", required=True, help="frozen configuration path")
    stage.set_defaults(handler=_prepare_data)
    return parser


def _prepare_data(args):
    loaded = inputs.load(args.inputs)
    config_loaded = config_module.load(args.config)
    result = prepare.run(loaded=loaded, config_loaded=config_loaded, run_id=args.run_id)
    return {"command": args.command, "status": result["payload"]["status"], "runId": args.run_id,
            "directory": str(result["directory"]),
            "artifacts": ["prepare-data.json", "candidate-labels.json", "validation-candidates.json"],
            "derivedDirectory": "derived"}


def main(argv=None):
    args = build_parser().parse_args(argv)
    return args.handler(args)
