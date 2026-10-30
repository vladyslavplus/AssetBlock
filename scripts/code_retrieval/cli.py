"""Bounded command line for retrieval data admission and encoder training.

Retrieval data admission (prepare-data) and encoder training (train) are the stages.
Index export, evaluation and evidence validation are later stages and intentionally
absent rather than stubbed. Training modules load lazily, so prepare-data never imports
torch.
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
    train = stages.add_parser("train", help="train the last encoder blocks on admitted train triplets")
    train.add_argument("--run-id", required=True, help="new immutable run id")
    train.add_argument("--inputs", required=True, help="inputs manifest path")
    train.add_argument("--config", required=True, help="frozen configuration path")
    train.add_argument("--prepare-run-id", required=True, help="admitted prepare-data run id to bind")
    train.add_argument("--reference-probe", required=True, help="environment-probe file from the reference interpreter")
    train.set_defaults(handler=_train)
    probe = stages.add_parser("environment-probe", help="encode one fixed admitted endpoint on CPU and write it once")
    probe.add_argument("--inputs", required=True, help="inputs manifest path")
    probe.add_argument("--config", required=True, help="frozen configuration path")
    probe.add_argument("--out", required=True, help="new probe file path")
    probe.set_defaults(handler=_environment_probe)
    reload_check = stages.add_parser("train-reload-check", help="reload every epoch checkpoint in a fresh process")
    reload_check.add_argument("--run-id", required=True, help="training run id")
    reload_check.add_argument("--config", required=True, help="frozen configuration path")
    reload_check.set_defaults(handler=_train_reload_check)
    return parser


def _train(args):
    from . import training
    return training.run(run_id=args.run_id, inputs_path=args.inputs, config_path=args.config,
                        prepare_run_id=args.prepare_run_id, reference_probe_path=args.reference_probe)


def _environment_probe(args):
    from . import environment_probe
    probe = environment_probe.write_probe(args.inputs, args.config, args.out)
    return {"command": "environment-probe", "out": args.out, "sample": probe["sample"],
            "tokenIdsSha256": probe["tokenIdsSha256"], "tokenOffsetsSha256": probe["tokenOffsetsSha256"],
            "chunk0Sha256": probe["chunk0Sha256"], "windowCount": probe["windowCount"],
            "vectorSha256": probe["vectorSha256"], "interpreter": probe["interpreter"]}


def _train_reload_check(args):
    from . import checkpoint, paths
    config_loaded = config_module.load(args.config)
    result = checkpoint.reload_run(paths.run_dir(args.run_id),
                                   epochs=config_loaded["config"]["training"]["epochs"],
                                   root=paths.RETRIEVAL_ROOT, device=checkpoint.cuda_device())
    return {"command": "train-reload-check", "runId": args.run_id, "passed": result["passed"],
            "epochs": [row["epochDirectory"] + ":" + str(row["passed"]) for row in result["epochs"]]}


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
