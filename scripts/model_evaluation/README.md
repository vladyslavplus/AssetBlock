# Model evaluation

Pre-registered, write-once evaluation of the retrieval pipelines and the pair verifier.
The package freezes every choice that could change a result in a hashed manifest before
anything is scored, computes end-to-end metrics over full galleries, and refuses
withheld material by construction.

## Commands

```text
python -m scripts.model_evaluation freeze --run-id <new-id> --inputs <inputs.json> --package e0=<evidence-package.json> --package e3=<...> --package verifier=<...> --state e0=<sha256> --state e3=<sha256> --state verifier=<sha256> --threshold <value>
python -m scripts.model_evaluation prepare --run-id <new-id> --manifest-run-id <freeze-id> --manifest-sha256 <hash> --corpus <corpus.json>
python -m scripts.model_evaluation candidates --run-id <new-id> --prepare-run-id <prepare-id> --manifest-sha256 <bound-hash> --content-root <dir> --components <components.json>
python -m scripts.model_evaluation score --run-id <new-id> --prepare-run-id <prepare-id> --candidates-run-id <candidates-id> --manifest-sha256 <hash> --candidates-sha256 <hash> [--rerun-of <score-id> --reason IMPLEMENTATION_DEFECT|INFRASTRUCTURE_FAULT]
python -m scripts.model_evaluation validate-evidence --run-id <new-id> --freeze-run-id <freeze-id> --prepare-run-id <prepare-id> --candidates-run-id <candidates-id> --score-run-id <score-id> [--require-clean] [--attempt-result <score-id>=<sha256> ...]
python -m scripts.model_evaluation parity --run-id <new-id> --prepare-run-id <prepare-id> --manifest-sha256 <bound-hash> --left-run-id <candidates-id> --left-sha256 <hash> --right-run-id <candidates-id> --right-sha256 <hash>
python -m scripts.model_evaluation models-check --run-id <new-id> --manifest-run-id <prepare-id> --manifest-sha256 <hash> [--package e0=<evidence-package.json> ...] [--state e0=<sha256> ...] [--threshold <value>]
```

Outputs go to `artifacts/model_evaluation/runs/<run-id>/` and are write-once.

| Stage | Reads | Writes |
| --- | --- | --- |
| `freeze` | inputs JSON with the manifest sections | `freeze-manifest.json` (canonical; its SHA-256 is the manifest hash) |
| `prepare` | pending freeze manifest, corpus manifest | `prepared-corpus.json` and a new `freeze-manifest.json` whose corpus section is bound and references the previous manifest hash |
| `candidates` | bound manifest, prepared corpus, hash-checked contents, models | `candidates.json`: per query and pipeline the ordered candidates (channel ranks, scores, material flag, verifier scores), emitted findings, window counts and stage timings |
| `score` | bound manifest, prepared corpus, candidate artifact | `score-result.json`: every metric below for every pipeline, retrieval-ranking metrics, paired deltas against `PRODUCT`, claim-gate outcomes, code identity |
| `validate-evidence` | the four run directories of the chain | `evidence-package.json`: hash-only package (file hashes and sizes, manifest hashes, recorded model identities, three separate statuses, code and environment identity, reconstruction ledger, limitations) |
| `parity` | two candidate artifacts, the frozen parity sample | `parity-report.json`: exact-match rate and every disagreement (membership, tie order, rank order, counts, material flags) |

Failures after inputs are accepted leave `failed-run.json` and no success artifact.
Exit codes: 0 success, 1 failure, 3 refused input. `models-check` exits 1 when a model has no verified evidence package.
Run ids match `[A-Za-z0-9][A-Za-z0-9_-]{0,79}`.

### Corpus manifest

`schema` `model-evaluation-corpus-v1`, `files` (`fileId` and `sha256` as 64 hex characters, `language`,
`family`, `partition` in `train|validation|evaluation`, `kind` `gallery|query`, `source` naming the source repository, optional `dialect`) and `queries` (`queryId`,
`queryFileId`, `language`, `family`, `seedGroup`, `stratum`, `dialect` for TypeScript, `judgments`).
Each judgment is `{label, provenance}` with a label of `RELEVANT`, `NOT_RELEVANT` or `UNKNOWN` and the
pooling sources (`TOKEN`, `E0`, `E3`, `RANDOM_SAMPLE`, `EXHAUSTIVE`) that proposed the file. Gallery files
without a judgment become `UNKNOWN` (no provenance), never negatives; an explicit `UNKNOWN` with provenance is a
judged-unknown. A family in more than one partition role, a query outside the closed role allowlist (`evaluation`),
a file whose declared `source` is on the frozen `excludedSources` list (or is undeclared) or a judgment outside the
query language gallery is refused. Counts per
language, stratum and TSX are compared with the frozen minimum counts and recorded as `belowMinimum`.

### Pipelines

| Name | Candidates | Emitted findings |
| --- | --- | --- |
| `PRODUCT` | token top-K union frozen base-encoder semantic top-K | candidates whose positive token score equals the query maximum (all ties) |
| `TOKEN_RANK_CUT` | token top-K | token ranks up to the frozen cut |
| `FINE_TUNED_RETRIEVAL` | token top-K union trained-encoder semantic top-K | material candidates plus trained semantic ranks up to the frozen cut |
| `FINE_TUNED_RETRIEVAL_PLUS_VERIFIER` | same as above | candidates whose verifier score, the maximum positive probability over the highest-cosine base-encoder window pairs, reaches the frozen threshold |

Ranking, union and tie handling reuse the functions the analysis service delegates to. The candidate budget
(`tokenTop`, `semanticTop`, `maxCandidates`) and the Dolos shape are pinned to the service contract constants; the manifest
validator refuses any other value, so counts can be compared one to one with the service.
A rank cut has no abstention: `FINE_TUNED_RETRIEVAL` emits a finding on almost every query, including no-match queries, so its spurious-alert rate is a measured property of that rule and is not a statement about the encoder alone.
Models run one at a time (base encoder, trained encoder, verifier); each is released as soon as its phase ends and
reading a component identity never loads a model.

### Product semantic channel

The service pools every window of a query file, asks the index (SQL) for the minimum cosine distance per gallery
file in batches of 80 query windows, merges batches by minimum distance and orders by (distance, file identity)
with the semantic depth as limit. The harness embeds windows from the extracted executable units (the extraction of
the service, one chunk mapping per fragment) and calls the `_exact_retrieval` aggregation function of the service with an
in-memory exact search that follows the SQL semantics (`product_search.py`; tiny-case tests compare it with an independent
brute-force minimum over all window pairs and check the batch handling). Residual divergence from the deployed path, stated for
every result: the deployed index embeds windows at ingestion time, the harness produces them at evaluation time with the
same extraction and mapping; gallery files without an executable fragment are absent from the harness gallery, whereas
the service refuses an index that misses an eligible file; span positions are not reproduced because no ranking uses
them. A database-backed parity adapter against the deployed service stays an open item before the scoring batch.

## Retrieval-ranking metrics

Independent of any emission rule, from the ordered candidate lists only: Recall@5, Recall@10, Recall@20, MRR@20 and
union-budget recall. The recall denominator is every relevant file in the gallery, `UNKNOWN` files are removed before
ranking, and queries without a relevant file are excluded. Values are averaged within a seed, then over seeds in a
language, then equally over languages, with family-clustered bootstrap intervals and paired deltas against `PRODUCT`.

## Evidence validation

`validate-evidence` recomputes every hash link from bytes (freeze manifest, bound manifest, prepared corpus, candidate
artifact, result), refuses a missing, empty, failed, extra or edited artifact, recomputes the result from the verified inputs
and scans the chain documents for withheld-partition text against an explicit allowlist (empty by default). Three statuses
stay separate: the run completed, the numeric claim-gate outcome per pipeline, and reviewer acceptance, which stays
`NOT_RECORDED`. Code identity records the git commit, the dirty flag and the hashes of the executed source closure; the
environment identity records the interpreter and a digest of the installed package list.

## Resource measurement

The candidate artifact records per-stage wall time (p50, p95, max, total) with warmup samples excluded: preprocessing,
encoding, window alignment, verifier inference, ranking, token scoring and end-to-end per query. Peak resident memory is read
from the operating system and peak CUDA allocation only when CUDA is present; otherwise the value is `null`. Transfer and
queue time are listed as unmeasured.

## Interpreters

| Purpose | Interpreter |
| --- | --- |
| Pure stages, metrics, manifest, evidence validation, tests (CPU) | `artifacts/code_analysis/env/ml/Scripts/python.exe` |
| Stages that load a trained or frozen encoder (CUDA) | `artifacts/feasibility_pilot/env/ml/Scripts/python.exe` |
| Code analysis service | `artifacts/code_analysis/env/service/Scripts/python.exe` |

Tests use `unittest`:

```text
python -B -m unittest discover -s scripts/model_evaluation/tests -t .
```

No stage installs packages or uses the network. Encoder, verifier and token-tool loaders are described by a
components JSON and run in the CUDA interpreter. Subprocess launches (none in this package) must go
through `scripts.feasibility_pilot.subprocess_util.run_bounded`, which hides the console window.

## Metrics

Judgments are `RELEVANT`, `NOT_RELEVANT` or `UNKNOWN` over the full gallery.

- Recall counts every relevant file in the gallery, retrieved or not.
- `UNKNOWN` is excluded from every quality denominator and counted, never treated as negative.
- A query with no relevant file but reviewed negatives is a no-match query; the spurious-alert
  rate is the share of fully reviewed no-match queries with at least one emitted finding.
- A pipeline may emit nothing; abstention coverage is reported next to precision.
- Per-query values are averaged within a seed, seeds equally within a language and languages
  equally (language-equal macro); micro values are reported beside them.
- Strata and the TypeScript dialects (`ts`, `tsx`) each report their own denominators.
- Confidence intervals come from a seeded family-clustered percentile bootstrap, also for
  paired differences against a baseline on the same queries.
- Claim gates return `VALIDATED`, `UNVALIDATED` (below the frozen minimum counts) or `FAILED`
  with the failing gate names. A failed gate is a result; it never triggers retuning.

The candidate-conditional metrics in `scripts/pair_verification/evaluation_metrics.py` are
unchanged.

## Freeze binding

The manifest binds what runs: per model the recorded identity, the checkpoint state hash and the evidence package hash;
the tokenizer, the windowing, the Dolos shape and the device policy; the operating points and the threshold; the pooling
rule; the pre-registered replacement baselines (`PRODUCT`, `TOKEN_RANK_CUT`); the excluded sources; and the rerun rule.
`candidates` compares the declared identity of every component with the manifest before encoding anything; `score` and
`validate-evidence` compare the identities recorded in the candidate artifact with the manifest. `freeze` always runs the
`models-check` pre-flight (no optional path; the CLI requires `--package` and `--state` for e0, e3 and verifier and `--threshold`): every model must verify its package hash and its
checkpoint state hash, and the verifier threshold must equal the frozen one, otherwise nothing is written. Preprocessing
(`framing`, `chunking`) is compared with the representation the code enforces (`mechanisms.actual_representation()`), and loaders report the tokenizer
file hashes and chunking they actually loaded; `LazyComponent` checks them against the declaration on load. Every manifest field is covered by a behavioural test that changes it and requires a stage to refuse or its output to change. `prepare`, `candidates`, `score` and `parity`
verify the manifest against a hash supplied on the command line, not against the bytes of the file itself.

## Claim gates

Gates are named constants in `manifest.py`. An overall claim (detector, replacement, product) needs every supported language
group (javascript, typescript, python, java, csharp) and the TSX dialect adequately sampled; otherwise it is
`UNVALIDATED` and names the missing groups, and a failed group stays `FAILED`. The overall values, their bootstrap intervals
and the point estimates use the same population. Replacement is judged against the stronger of the two pre-registered
baselines on the same queries: the margin and floor gates, the per-language loss gate, every detector gate (false findings
per positive query, spurious-alert rate) and a paired family-bootstrap lower bound of the F1 difference above zero; a
non-positive lower bound is `UNVALIDATED` (inconclusive). The bootstrap resamples whole families inside each language; a
seed group belongs to exactly one family.

## Pooling

The candidate artifact records the top-`depthK` files of TOKEN, E0 and E3 per query and a deterministic random recall
sample. `score` refuses to run unless every pooled file is judged (or an explicit judged-unknown) and, under the union
rule, its provenance names every system whose top-`depthK` holds it; the refusal lists the unjudged files.

## Withheld material

Final-test and sealed material is never read, listed, hashed or inspected by this package. A marker is refused
unconditionally; there is no flag or other form that admits it, and no scoring of such material exists here.
Identifiers are normalised before the marker check (Unicode compatibility folding, removal of zero-width and
combining characters, common Cyrillic and Greek lookalikes, case, separators, and the digit substitutions
1, 0 and 3), and the markers `seal`, `finaltest`, `heldout`, `holdout` and `withheld` are matched inside the
result; `test` and `testing` are refused as roles. The corpus role allowlist is `evaluation` only. Structural
protection does not depend on spelling: the frozen `excludedSources` list names source repositories supplied by the
coordinator, and `prepare` refuses any file whose declared source is on it or is undeclared. A components description
is scanned for markers before it is used. Documents in an evidence chain are scanned the same way, so ordinary prose
that happens to spell a marker across a word boundary is refused too (fail closed).

## One scoring run

The scoring registry key is the sha256 over the three evidence-package hashes and the three state hashes (e0, e3, verifier). A scoring whose three package hashes OR whose three state hashes equal those of any recorded attempt is a rerun, so swapping one checkpoint inside the same evidence packages, or re-packaging the same checkpoints, is not a fresh first run: the first scoring of a set of models is the only free one.
The corpus identity (query ids, query files, gallery files) and the label hash (judgments and provenance) are recorded per attempt and
recomputed from the prepared files and queries by `score` and `validate-evidence`, so relabelling, adding queries or re-freezing is not a way
around the rule. A further run of the same models, under any manifest, corpus or labels, needs a new run id, a reason of
`IMPLEMENTATION_DEFECT` or `INFRASTRUCTURE_FAULT` (the frozen rerun rule) and a reference to an earlier run, whose output
stays in place. `validate-evidence` lists every registered attempt with its result hash and refuses evidence for a
later run unless the hash of every attempt is supplied. `score` prints `attempt hash to record ...: <run id>=<result sha256>` (also `attemptHashToRecord` in its JSON); record every such line and pass it to `validate-evidence --attempt-result`. `validate-evidence` also refuses when a score result exists under the output root whose run id is not in the registry. The scoring gate also refuses a manifest that is not canonical
or does not hash to the supplied value, a corpus section that is not bound, a candidate artifact whose hash differs
from the supplied one, that embeds another manifest or corpus hash or that records other model identities, and unjudged
pool files. Order is proven by hash binding alone: an artifact cannot contain a hash that did not exist before it.

## CPU/GPU parity and its sample

The manifest freezes `cpuGpuParity.devices` as `["cpu", "cuda"]`. `candidates` accepts either device (all three models on the same one) and
records the device in the artifact. `parity` takes two artifacts of the same manifest hash that differ only in the recorded device, one per frozen
device; equal devices, a device outside the pair or any other difference is refused. The parity query ids are drawn deterministically
from the prepared evaluation queries with the frozen `cpuGpuParity.seed` by `parity.parity_sample`; the size, `topK` and PRODUCT
pipeline are frozen too, and nothing is chosen at the command line. The artifacts must carry the bound corpus hash and candidates
schema. No validation-partition path exists for this sample, because the corpus role
allowlist stays `evaluation` only and a second role would widen what `prepare` accepts. Both device runs therefore execute over the same
prepared evaluation corpus and the comparison looks only at the sampled query ids.

## Package-bound model facts (`models-check`)

Each model entry carries `packageBinding` (read by `models-check` from the parsed evidence package at fixed key paths and compared with the manifest)
and `unboundByPackage`. Paths, from the real packages: e0 `identities.trustedModelFileHashes`; e3 `identities.checkpointManifestSha256.E3`
(retrieval package, `scripts/code_retrieval/evidence_package.py`); verifier `selectedEpoch`, `identities.checkpointHashes[selectedEpoch - 1]` and the
threshold `metrics.pipelines.E3.threshold.threshold`, which must equal the frozen threshold (pair package,
`scripts/pair_verification/validation_evidence.py`). A package that lacks a path is refused (fail closed). No package carries the raw loaded-state hash (it
is inside the checkpoint manifest, which the package binds only by hash), so `stateSha256` is recorded as `unboundByPackage` with that reason and is
protected by the registry key instead. The e0 tokenizer hashes are not compared with the package (loader-checked on load).

## Marker guard: accepted rare words

Long markers (`finaltest`, `heldout`, `holdout`, `withheld`) are matched as a substring of a separator-delimited word, so `myholdout`, `evalfinaltest`,
`hOldout` and `fiNaltest` are refused; the cost is that an identifier such as `thresholdOutput` is refused too and must be renamed. `seal` is
matched at the start of a word or token, as a run of single characters, and as a proper prefix completed by the next token (`se_al`, `sea` + `l`; `hel_dout`);
the remainder of at most two characters must be matched exactly. Accepted on purpose: `closeAll`, `parseAll`, `release_all`, `database_alias`,
`use all`, `these alone`, `falseAlarm`, `Rosealba`, `sea level`, `se alias`. Pure-hex words of 32 to 64 characters (digests, file ids, hash CLI values,
dictionary keys) are not read as names; shorter hex-looking words are.
