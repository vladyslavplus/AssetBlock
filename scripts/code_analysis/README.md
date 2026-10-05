# Code analysis corpus and baselines

Isolated research tooling for curated source intake, executable extraction,
reviewed labels, frozen representations, token/semantic baselines, and a dedicated
pgvector index. This package does not execute downloaded source, train models,
score the final test, or authorize product publication.

## Inputs and outputs

All downloaded source, notices, manifests, review judgments, vectors, model caches,
environments, logs, and measured results belong under ignored
`artifacts/code_analysis/`. Repository code does not select a historical run or
synthesize a reviewer identity. Preserve and transfer required artifacts separately
when reproducing a measured run; a Git checkout alone contains no measured corpus.

`config/sources.json` is an unreviewed intake example (five sources, ten files),
not the accepted evaluation corpus. Select an immutable source/rights manifest
explicitly. Rights, source-family assignments, pair labels, and transformations
require hash-bound review records. UNKNOWN and absent relationships never become
negatives. Similarity, shared origin, and redistribution rights remain distinct.

`materialize-labels` consumes an explicitly reviewed file-level label manifest via
`--labels`. The manifest includes status, reviewer, reviewedOn, rationale,
extractionSha256, pairReviews, and seeds. Each pair review supplies its own
provenance and explicit file IDs/hashes. Fragment adoption accepts a materialized
review export (pairReviews, extraFiles, extraFragments, trainTriplets,
diagnosticGalleries); it never invents labels from names, spans, or copy provenance.
Session-specific registry expansion scripts remain local evidence, not reusable
approval logic.

## Commands

Use the existing parser/ML environment. No installation occurs through these
commands; missing prerequisites fail explicitly.

```powershell
python -m scripts.code_analysis intake --run-id example --config artifacts/code_analysis/inputs/sources.json
python -m scripts.code_analysis split --run-id example --config artifacts/code_analysis/inputs/sources.json
python -m scripts.code_analysis extract --run-id example --config artifacts/code_analysis/inputs/sources.json
python -m scripts.code_analysis materialize-labels --run-id example --config artifacts/code_analysis/inputs/sources.json --labels artifacts/code_analysis/inputs/reviewed-labels.json
python -m scripts.code_analysis embed-frozen --run-id example --config artifacts/code_analysis/inputs/sources.json --parent-run-id frozen-example
python -m scripts.code_analysis index-check --run-id example --config artifacts/code_analysis/inputs/sources.json --parent-run-id frozen-example
python -m scripts.code_analysis evaluate --run-id example --config artifacts/code_analysis/inputs/sources.json --parent-run-id frozen-example
python -m scripts.code_analysis validate-evidence --run-id example --config artifacts/code_analysis/inputs/sources.json --parent-run-id frozen-example
python -m unittest discover -s scripts/code_analysis/tests
```

Stage outputs are exclusive; retries use a new RunId. Frozen reuse checks extract,
protocol, full vectors, chunk manifests, and source mapping hashes. Missing local
representations require explicit `--parent-run-id`; no dated run is a fallback.
`embed-frozen` verifies existing frozen representations, not a silent rebuild.
`index-build` refuses overwriting an existing index. Live `index-check` requires
process-only ASSETBLOCK_CODE_INDEX_DSN and reports READY_VERIFIED only after stored
rows, metadata, ModelKey and IndexKeys match. Unavailable evidence remains a gap.

## Sandbox and dependency policy

Use a separate loopback database/role, never the product database. Runtime terms,
exact dependency exceptions, wheels, and graph evidence remain necessary.
`--runtime-review` selects the reviewed dependency directory for an explicit
restore; the default directory is `artifacts/code_analysis/review/dependencies`.
Empty-schema initialization also requires a matching DDL approval manifest.
Nothing automatically installs packages or provisions a database.

Sandbox defaults are `assetblock-code-index` and `assetblock-code-index-data`.
Set ASSETBLOCK_CODE_INDEX_CONTAINER and ASSETBLOCK_CODE_INDEX_VOLUME explicitly
when reconnecting an existing differently named sandbox. Existing containers,
volumes, READY rows, and measured artifacts must not be renamed or deleted for
repository cleanup. The index proposal is scoped dependency/provisioning data,
not proof that a new checkout already has an approved installed environment.

## Evaluation and verification

All five languages have explicit coverage and omissions. Family splits precede
transformations. Requirements count unique files and seeds; derivatives cannot
inflate corpus minima. File and fragment labels stay separate. Rankings aggregate
across the permitted partition-local gallery before top-K. Token and semantic
candidates are independent; low token similarity cannot suppress semantic matches.
Small transformation samples are diagnostics, not evidence of general ML quality.
Final-test inputs remain sealed and unscored.

Query-only inference validates accepted record membership, metadata and source
hashes before encoding. No optimizer is created and gallery vectors are preserved.
The validator resolves hash-bound stage output bytes, including reuse receipts;
READY_FOR_REVIEW is distinct from independent reviewer approval.

Unit tests use authored temporary fixtures, not historical downloaded corpora.
Parser/model libraries are optional local prerequisites for corresponding tests;
unavailable prerequisites and native symlink permissions are reported as skips.
Measured corpus/index/scanner evidence requires separately retained artifacts.
Path checks reject original symlinks/reparse points before resolved containment;
this is not race-free filesystem isolation.
