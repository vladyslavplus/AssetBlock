"""Command interface for the model evaluation harness.

Exit codes: 0 success, 1 failure, 3 refused input. Every stage is implemented; a stage
that cannot complete reports a failure and never prints a success.
"""

import argparse
import json
import sys

from . import (candidates, components, guards, manifest, models_check, parity, prepare, score, validation)

EXIT_REFUSED = 3
EXIT_FAILED = 1


def _stage(stages, name, help_text, handler, arguments):
    command = stages.add_parser(name, help=help_text)
    command.add_argument("--run-id", required=True, help="new immutable run id")
    for flag, options in arguments:
        command.add_argument(flag, **options)
    command.set_defaults(handler=handler)


def _model_checks(mandatory=False):
    """Options of the models pre-flight; ``freeze`` makes every one of them mandatory."""
    extra = {"required": True} if mandatory else {"default": []}
    return [("--package", {"action": "append", "metavar": "MODEL=PATH", **extra,
                           "help": "evidence package for a model (e0, e3 and verifier)"}),
            ("--state", {"action": "append", "metavar": "MODEL=SHA256", **extra,
                         "help": "checkpoint state hash read from the checkpoint (e0, e3 and verifier)"}),
            ("--threshold", {"type": float, "help": "verifier threshold read from its evidence",
                             **({"required": True} if mandatory else {"default": None})})]


def _pairs(items):
    result = {}
    for item in items:
        name, separator, value = item.partition("=")
        if not separator:
            raise ValueError("expected MODEL=VALUE")
        result[name] = value
    return result


def build_parser():
    parser = argparse.ArgumentParser(prog="python -m scripts.model_evaluation")
    stages = parser.add_subparsers(dest="command", required=True)
    required = {"required": True}
    _stage(stages, "freeze", "write the hashed freeze manifest from an inputs document", _freeze,
           [("--inputs", {**required, "help": "JSON document with the manifest input sections"}),
            *_model_checks(mandatory=True)])
    _stage(stages, "prepare", "bind the evaluation corpus and write a new freeze version", _prepare,
           [("--manifest-run-id", {**required, "help": "run holding the pending freeze manifest"}),
            ("--manifest-sha256", {**required, "help": "hash of that manifest, recorded outside the run"}),
            ("--corpus", {**required, "help": "evaluation corpus manifest (JSON)"})])
    _stage(stages, "candidates", "build candidate lists and findings for every pipeline", _candidates,
           [("--prepare-run-id", required), ("--manifest-sha256", required), ("--content-root", {**required, "help": "directory of file contents"}),
            ("--components", {**required, "help": "JSON naming the encoders, verifier and token tool"})])
    _stage(stages, "score", "score the frozen pipelines once", _score,
           [("--prepare-run-id", required), ("--candidates-run-id", required), ("--manifest-sha256", required),
            ("--candidates-sha256", required),
            ("--rerun-of", {"default": None, "help": "earlier scoring run id (reruns only)"}),
            ("--reason", {"default": None, "choices": guards.RERUN_REASONS})])
    _stage(stages, "validate-evidence", "verify the chain from bytes and write a hash-only package", _validate,
           [("--freeze-run-id", required), ("--prepare-run-id", required), ("--candidates-run-id", required),
            ("--score-run-id", required), ("--require-clean", {"action": "store_true"}),
            ("--attempt-result", {"action": "append", "default": [], "metavar": "RUN_ID=SHA256",
                                  "help": "result hash of every registered scoring attempt (later runs)"})])
    _stage(stages, "parity", "compare two candidate artifacts on the frozen parity sample", _parity,
           [("--prepare-run-id", required), ("--manifest-sha256", required), ("--left-run-id", required), ("--left-sha256", required), ("--right-run-id", required),
            ("--right-sha256", required)])
    _stage(stages, "models-check", "check manifest model identities against evidence packages", _models,
           [("--manifest-run-id", required), ("--manifest-sha256", required), *_model_checks()])
    return parser


def _freeze(args):
    result = manifest.freeze(run_id=args.run_id, inputs_path=args.inputs, evidence_paths=_pairs(args.package),
                             state_hashes=_pairs(args.state), threshold=args.threshold)
    return {"command": args.command, "outcome": result["outcome"], "runId": args.run_id,
            "directory": str(result["directory"]), "manifestSha256": result["manifestSha256"]}


def _prepare(args):
    result = prepare.run(run_id=args.run_id, manifest_run_id=args.manifest_run_id,
                         manifest_sha256=args.manifest_sha256, corpus_path=args.corpus)
    return {"command": args.command, "outcome": result["outcome"], "runId": args.run_id,
            "directory": str(result["directory"]), "manifestSha256": result["manifestSha256"],
            "preparedCorpusSha256": result["preparedCorpusSha256"],
            "previousManifestSha256": result["previousManifestSha256"]}


def _candidates(args):
    store = components.DirectoryContentStore(args.content_root)
    loaded = components.load_components(args.components)
    result = candidates.run(run_id=args.run_id, prepare_run_id=args.prepare_run_id,
                            manifest_sha256=args.manifest_sha256, components=loaded, store=store)
    return {"command": args.command, "outcome": result["outcome"], "runId": args.run_id,
            "directory": str(result["directory"]), "candidatesSha256": result["candidatesSha256"]}


def _score(args):
    result = score.run(run_id=args.run_id, prepare_run_id=args.prepare_run_id,
                       candidates_run_id=args.candidates_run_id, manifest_sha256=args.manifest_sha256,
                       candidates_sha256=args.candidates_sha256, rerun_of=args.rerun_of, reason=args.reason)
    record = "%s=%s" % (args.run_id, result["resultSha256"])
    print("attempt hash to record (validate-evidence --attempt-result): " + record, file=sys.stderr)
    return {"command": args.command, "outcome": result["outcome"], "runId": args.run_id,
            "directory": str(result["directory"]), "resultSha256": result["resultSha256"],
            "attemptHashToRecord": record}


def _validate(args):
    result = validation.run(run_id=args.run_id, freeze_run_id=args.freeze_run_id,
                            prepare_run_id=args.prepare_run_id, candidates_run_id=args.candidates_run_id,
                            score_run_id=args.score_run_id, require_clean=args.require_clean,
                            attempt_results=_pairs(args.attempt_result) or None)
    return {"command": args.command, "outcome": result["outcome"], "runId": args.run_id,
            "directory": str(result["directory"]), "packageSha256": result["packageSha256"],
            "statuses": result["statuses"]}


def _parity(args):
    result = parity.run(run_id=args.run_id, prepare_run_id=args.prepare_run_id,
                        manifest_sha256=args.manifest_sha256, left_run_id=args.left_run_id, left_sha256=args.left_sha256,
                        right_run_id=args.right_run_id, right_sha256=args.right_sha256)
    return {"command": args.command, "outcome": result["outcome"], "runId": args.run_id,
            "directory": str(result["directory"]), "reportSha256": result["reportSha256"],
            "exactMatchRate": result["exactMatchRate"], "disagreements": result["disagreements"]}


def _models(args):
    bound, _ = prepare.read_manifest(guards.run_dir(args.manifest_run_id), args.manifest_sha256)
    report = models_check.check(bound, _pairs(args.package), state_hashes=_pairs(args.state),
                                threshold=args.threshold)
    return {"command": args.command, "outcome": "SUCCEEDED" if report["allVerified"] else "NOT_VERIFIED",
            "runId": args.run_id, "models": report["models"], "threshold": report["threshold"]}


def main(argv=None):
    arguments = list(sys.argv[1:] if argv is None else argv)
    try:
        guards.refuse_sealed(cli_args=arguments)
        args = build_parser().parse_args(arguments)
        guards.validate_run_id(args.run_id)
        result = args.handler(args)
    except guards.SealedInputRefused as exc:
        print(json.dumps({"outcome": "REFUSED", "error": str(exc)}), file=sys.stderr)
        return EXIT_REFUSED
    except (ValueError, FileExistsError, OSError, KeyError) as exc:
        print(json.dumps({"outcome": "FAILED", "error": str(exc)}), file=sys.stderr)
        return EXIT_FAILED
    print(json.dumps(result, sort_keys=True))
    return 0 if result["outcome"] == "SUCCEEDED" else EXIT_FAILED
