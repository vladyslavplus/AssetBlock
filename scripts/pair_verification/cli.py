"""Small command interface for immutable pair-data preparation."""

import argparse

from . import evaluation, prepare, training, validation_evidence


def build_parser():
    parser = argparse.ArgumentParser(prog="python -m scripts.pair_verification")
    stages = parser.add_subparsers(dest="command", required=True)
    command = stages.add_parser("prepare-pairs", help="admit reviewed pairs and freeze validation candidates")
    command.add_argument("--run-id", required=True, help="new immutable run id")
    command.add_argument("--inputs", required=True, help="predecessor inputs manifest")
    command.add_argument("--config", required=True, help="pinned predecessor configuration")
    command.add_argument("--prepare-run-id", required=True, help="verified admitted data run")
    command.add_argument("--train-run-id", required=True, help="verified training evidence run")
    command.add_argument("--index-run-id", required=True, help="verified index run")
    command.add_argument("--evaluation-run-id", required=True, help="verified evaluation run")
    command.add_argument("--database-run-id", required=True, help="verified database-check evidence run")
    command.add_argument("--evidence-run-id", required=True, help="verified predecessor evidence package run")
    command.add_argument("--receipt", default=None, help="independent review receipt for a prior candidate package")
    command.add_argument("--candidate-package", default=None,
                         help="prior candidate package bound by the supplied receipt")
    command.add_argument("--dry-run", action="store_true", help="write a non-evidence scratch rehearsal")
    command.set_defaults(handler=_prepare)

    command = stages.add_parser("train", help="train five immutable verifier checkpoints")
    command.add_argument("--run-id", required=True)
    command.add_argument("--prepare-run-id", required=True)
    command.add_argument("--inputs", required=True)
    command.add_argument("--config", required=True)
    command.set_defaults(handler=_train)

    command = stages.add_parser("reload-check", help="verify all checkpoints in a fresh process")
    command.add_argument("--run-id", required=True)
    command.set_defaults(handler=_reload)

    command = stages.add_parser("smoke", help="rehearse real-input binding and checkpoint round trip")
    command.add_argument("--run-id", required=True, help="new dryrun-prefixed scratch directory")
    command.add_argument("--prepare-run-id", required=True)
    command.add_argument("--inputs", required=True)
    command.add_argument("--config", required=True)
    command.set_defaults(handler=_smoke)

    command = stages.add_parser("evaluate", help="score validation candidate pairs and compare controls")
    command.add_argument("--run-id", required=True)
    command.add_argument("--prepare-run-id", required=True)
    command.add_argument("--train-run-id", required=True)
    command.add_argument("--inputs", required=True)
    command.add_argument("--config", required=True)
    command.set_defaults(handler=_evaluate)

    command = stages.add_parser("validate-evidence", help="verify and package pair research evidence")
    command.add_argument("--run-id", required=True)
    command.add_argument("--prepare-run-id", required=True)
    command.add_argument("--train-run-id", required=True)
    command.add_argument("--evaluation-run-id", required=True)
    command.add_argument("--inputs", required=True)
    command.add_argument("--config", required=True)
    command.set_defaults(handler=_validate_evidence)
    return parser


def _prepare(args):
    result = prepare.run(run_id=args.run_id, inputs_path=args.inputs, config_path=args.config,
                         prepare_run_id=args.prepare_run_id, train_run_id=args.train_run_id,
                         index_run_id=args.index_run_id, evaluation_run_id=args.evaluation_run_id,
                         database_run_id=args.database_run_id, evidence_run_id=args.evidence_run_id,
                         receipt_path=args.receipt, candidate_package_path=args.candidate_package,
                         dry_run=args.dry_run)
    return {"command": args.command, "outcome": result["outcome"], "runId": args.run_id,
            "directory": str(result["directory"])}


def _train(args):
    result = training.train(run_id=args.run_id, prepare_run_id=args.prepare_run_id,
                            inputs_path=args.inputs, config_path=args.config)
    return {"command": args.command, "outcome": result["outcome"], "runId": args.run_id,
            "directory": str(result["directory"]), "evidenceSha256": result["evidenceSha256"]}


def _reload(args):
    result = training.reload_check(run_id=args.run_id)
    return {"command": args.command, "outcome": result["outcome"], "runId": args.run_id,
            "directory": str(result["directory"]), "evidenceSha256": result["evidenceSha256"]}


def _smoke(args):
    result = training.smoke(run_id=args.run_id, prepare_run_id=args.prepare_run_id,
                            inputs_path=args.inputs, config_path=args.config)
    return {"command": args.command, "outcome": result["outcome"], "runId": args.run_id,
            "directory": str(result["directory"]), "reportSha256": result["reportSha256"]}


def _evaluate(args):
    result = evaluation.run(run_id=args.run_id, prepare_run_id=args.prepare_run_id,
                            train_run_id=args.train_run_id, inputs_path=args.inputs,
                            config_path=args.config)
    return {"command": args.command, "outcome": result["outcome"], "runId": args.run_id,
            "directory": str(result["directory"]), "evidenceSha256": result["evidenceSha256"],
            "selectedEpoch": result["selectedEpoch"]}


def _validate_evidence(args):
    result = validation_evidence.run(run_id=args.run_id, prepare_run_id=args.prepare_run_id,
                                     train_run_id=args.train_run_id,
                                     evaluation_run_id=args.evaluation_run_id,
                                     inputs_path=args.inputs, config_path=args.config)
    return {"command": args.command, "outcome": result["outcome"], "runId": args.run_id,
            "directory": str(result["directory"]), "evidenceSha256": result["evidenceSha256"],
            "reconstructionSha256": result["reconstructionSha256"]}


def main(argv=None):
    args = build_parser().parse_args(argv)
    result = args.handler(args)
    print(result)
    return 0 if result.get("outcome") in {"SUCCEEDED", "DRY_RUN"} else 1
