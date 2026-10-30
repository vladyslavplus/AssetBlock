# Code retrieval

Retrieval tooling for code search. It admits training data, trains the last encoder
blocks of a pinned UniXcoder checkpoint, exports one validation index per compared
encoder, evaluates the encoders against token and semantic baselines, checks a
pgvector index, and verifies the evidence chain. This stage does not open the final
test, and the tooling does not authorize product changes.

## Interpreters

Each stage runs in one interpreter. Use the role the stage names:

- Training: `artifacts/feasibility_pilot/env/ml` (CUDA, torch). Runs `train`,
  `train-reload-check`, `export-index`, `evaluate`, and `index-publish-check --stage query-vectors`.
- Reference: `artifacts/code_analysis/env/ml` (pinned CPU, torch). Runs `environment-probe`.
- Index: `artifacts/code_analysis/env/index` (psycopg, no torch). Runs
  `index-publish-check --stage publish-and-rank` and `validate-evidence`.
- `prepare-data` does not import torch. Any interpreter with the repository dependencies runs it.

The index role must never import torch. The evidence package records `torchImported`
for the index run.

## Stages in pipeline order

1. `prepare-data` admits training data and freezes the experiment identity.
2. `environment-probe` encodes one fixed endpoint on CPU and writes the probe file once.
3. `train` trains the last encoder blocks on admitted triplets. It binds to a prepare
   run and requires the reference probe.
4. `train-reload-check` reloads every epoch checkpoint in a fresh process. Each result
   goes to a new file; existing results are never overwritten.
5. `export-index` writes one immutable validation index per compared encoder.
6. `evaluate` scores E0 to E3 on the validation indexes and writes the selection.
7. `index-publish-check` runs `query-vectors` in the training interpreter, then
   `publish-and-rank` in the index interpreter. Publishing requires a reviewed runtime
   package directory under `artifacts/code_analysis/`.
8. `validate-evidence` verifies the chain from bytes, scans the chain documents for
   final-test text, and writes the evidence package. It takes `--runtime-review` (the
   reviewed runtime package directory) and one `--superseded-run-id` for each earlier
   evidence run that a rerun replaces.

The frozen configuration is `scripts/code_retrieval/config/training-config.json`.
Most stages take `--inputs` and `--config`. Run `python -m scripts.code_retrieval <stage>
--help` for the arguments of each stage.

## Outputs and run rules

Run outputs go to `artifacts/code_retrieval/runs/<run-id>/`.

- Each output is written once per run id. An existing output is an immutable collision and
  the stage refuses to run.
- `validate-evidence` writes `evidence-package.json` and `RECONSTRUCTION.md` only when every
  check passes. Success is recorded as `outcome: SUCCEEDED` inside the package; no marker
  file is written. A refused chain writes a package with `outcome: FAILED_ERROR`, a reason
  with no machine path, and no reconstruction file.
- The package's command ledger lists each stage's arguments from the stage parser. A test
  parses every ledger entry with that parser.

## Quality label

A trained model is `NO_MEASURABLE_DIFFERENCE_NEAR_CEILING` when the largest absolute
seed-paired mean delta on semantic@20 MRR and Recall is at most 0.005 and the
rank-one share of both the frozen and the trained model is at least 0.95. The 0.005
threshold equals one flipped query at 201 queries (1/201 is about 0.005). Otherwise
the label is `MEASURABLE_DIFFERENCE`, with the numbers in the package. Each model also
records `worseOnAnySeed` and `meanDeltaSign`, so a small loss stays visible.

## What the evidence package proves

- The inputs manifest, the configuration, the evaluation protocol, the trusted upstream
  identity, and each named run file match their recorded SHA256 and byte size.
- The runs in the chain agree on run ids, protocol identities, the trusted upstream model,
  the admitted training triplets, checkpoint hashes, reload results, index manifests,
  query vectors, and the database check.
- The 297 gallery files, 201 queries, and 20 published indexes are consistent across the
  chain. They are not checked against an independent source.
- The JSON chain documents listed in `finalTest.chainDocumentsScanned` were scanned for
  final-test text. Prepare membership records the final test as not scored, not used for
  tuning, and not rank-inspected.
- Three statuses are kept apart: `trainingCompleted`, `qualityOutcome`, and
  `reviewerAcceptance`. The last is always `NOT_RECORDED_IN_EVIDENCE`. The package
  never claims reviewer acceptance.

## What the evidence package does not prove

- That this stage opened the final test. It has no code path that does. Independence of
  the earlier stages from the final test is not proven.
- Quality beyond the validation set. A near-ceiling result is reported as
  `NO_MEASURABLE_DIFFERENCE_NEAR_CEILING`, not as an improvement.
- Product authorization, reviewer acceptance, redistribution rights for the source corpus,
  or independence from upstream pretraining data.
- The validation report digest is cross-checked against the stored value, not recomputed.
  The publish gate's runtime review wheels are not re-hashed by this stage.

## Local-only artifacts

Checkpoints, query vectors, index vectors, source text, and the evidence outputs stay
under `artifacts/`, which is git-ignored. The evidence package carries hashes, byte
counts, and counts only. Moving any artifact out of the repository needs a separate
rights decision.
