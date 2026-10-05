# Feasibility pilot

Isolated check of code extraction, token comparison, license signals, frozen encoder inference, and a real GPU parameter update for two model architectures. This is not product training, publication evidence, or a quality claim.

## Bounds

- Inputs are the authored files under `fixtures/` only.
- Environments, caches, logs, checkpoints, and results stay under `artifacts/feasibility_pilot/`.
- Do not import, compile, or execute fixture source.
- `Purpose=FEASIBILITY`, `CanAuthorizePublication=false`, `SecurityEvidence=NOT_RUN`.

## Setup (online, once)

For a dependency upgrade, use a fresh disposable environment; preserve historical
pilot environments and evidence. Run the hash-bound CPU compatibility smoke
`verify_transformers_5_17_smoke.py` and a full environment `pip-audit`; save outputs
under ignored `artifacts/security-remediation/`. This is a development check, not
publication authority or product license admission. Retain installed LICENSE/NOTICE
files. A skipped audit package remains unverified. See the disposable research
verification scope in `DEPENDENCY-POLICY.md`; no separate ML review framework is
required. Ordinary unit tests must work offline and must not install packages.

From the repository root, using 64-bit Python 3.12:

```powershell
py -3.12 -m venv artifacts\feasibility_pilot\env\ml
artifacts\feasibility_pilot\env\ml\Scripts\python.exe -m pip install --upgrade pip wheel setuptools
artifacts\feasibility_pilot\env\ml\Scripts\python.exe -m pip install torch==2.14.1 --index-url https://download.pytorch.org/whl/cu130
artifacts\feasibility_pilot\env\ml\Scripts\python.exe -m pip install -r scripts\feasibility_pilot\config\requirements-ml.txt
Copy-Item scripts\feasibility_pilot\config\dolos-package.json artifacts\feasibility_pilot\env\dolos\package.json
npm install --prefix artifacts\feasibility_pilot\env\dolos --omit=dev --no-fund --no-audit
artifacts\feasibility_pilot\env\ml\Scripts\python.exe -m scripts.feasibility_pilot setup --run-id setup-local --config scripts\feasibility_pilot\config\pilot.json
```

ScanCode 32.5.0 is installed only into `artifacts/feasibility_pilot/env/scancode`. Reviewed exact-version pypi exceptions cover that isolated prefix, including native libarchive/libmagic wheels. Do not copy those native bundles into a product image, deployment, or distributable package. `extractcode-7z` is not authorized. `pnpm deps:check` does not cover this pip graph. The live gate compares the installed scanner venv graph to the reviewed lock/inventory before a scanner run.

After setup, freeze pip/npm locks under `artifacts/feasibility_pilot/env/` and run later stages offline (`HF_HUB_OFFLINE=1`).

## Stages

```powershell
$pilotPython = 'artifacts\feasibility_pilot\env\ml\Scripts\python.exe'
$pilotConfig = 'scripts/feasibility_pilot/config/pilot.json'
$pilotRun = '<new-run-id>'
& $pilotPython -m unittest discover -s scripts/feasibility_pilot/tests
& $pilotPython -m scripts.feasibility_pilot preflight --config $pilotConfig --run-id $pilotRun
& $pilotPython -m scripts.feasibility_pilot extract --config $pilotConfig --run-id $pilotRun
& $pilotPython -m scripts.feasibility_pilot tools --config $pilotConfig --run-id $pilotRun
& $pilotPython -m scripts.feasibility_pilot frozen --config $pilotConfig --run-id $pilotRun
& $pilotPython -m scripts.feasibility_pilot train --model retrieval --config $pilotConfig --run-id $pilotRun
& $pilotPython -m scripts.feasibility_pilot reload --model retrieval --config $pilotConfig --run-id $pilotRun
& $pilotPython -m scripts.feasibility_pilot train --model pair --config $pilotConfig --run-id $pilotRun
& $pilotPython -m scripts.feasibility_pilot reload --model pair --config $pilotConfig --run-id $pilotRun
& $pilotPython -m scripts.feasibility_pilot summarize --config $pilotConfig --run-id $pilotRun
& $pilotPython -m scripts.feasibility_pilot validate-evidence --config $pilotConfig --run-id $pilotRun
```

Independent stages may continue after a local failure. Failed or skipped mandatory evidence stays in the final status. Training GPU `maxAllocatedBytes` is the peak of the training loop only; the CUDA prerequisite probe is recorded separately and is not a training peak. Historical probe numbers must not be relabeled as training peaks.

## Interpretation

Parser coverage, token coverage, and ML diagnostics are separate. A matched expected parse error can pass a diagnostic case while the file remains invalid and excluded from training.
