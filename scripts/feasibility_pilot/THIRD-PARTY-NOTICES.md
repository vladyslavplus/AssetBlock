# Third-party notices for the isolated feasibility pilot

This file attributes tools and models used by `scripts/feasibility_pilot`. It does not replace repository-root notices or authorize publication.

## tree-sitter and language grammars

- `tree-sitter` Python bindings: MIT. https://github.com/tree-sitter/py-tree-sitter
- `tree-sitter-javascript`: MIT. https://github.com/tree-sitter/tree-sitter-javascript
- `tree-sitter-typescript` (TypeScript and TSX): MIT. https://github.com/tree-sitter/tree-sitter-typescript
- `tree-sitter-python`: MIT. https://github.com/tree-sitter/tree-sitter-python
- `tree-sitter-java`: MIT. https://github.com/tree-sitter/tree-sitter-java
- `tree-sitter-c-sharp`: MIT. https://github.com/tree-sitter/tree-sitter-c-sharp

Exact versions are recorded in `config/requirements-ml.txt` and the frozen environment lock after setup.

## Dolos

- `@dodona/dolos` 2.9.3: MIT. https://github.com/dodona-edu/dolos
- Installed only into `artifacts/feasibility_pilot/env/dolos` from the copied `config/dolos-package.json`. Transitive licenses must be recorded from the resolved npm lock after install.

## UniXcoder

- Model `microsoft/unixcoder-base-nine`: Apache-2.0 (model card).
- Immutable revision: `5f0dc256eb0904af496d427451da5d7f90261676`.
- Verified `pytorch_model.bin` SHA-256: `e28385bb916434983692dfdd57f5c78c64f92d4a26614965d1e9d150b4a37145`.
- Load path: Transformers `local_files_only=True`, `trust_remote_code=False`; pickle `.bin` is hash-pinned then converted to `model.safetensors` in the artifact cache for later loads.
- Reference encoder description: Microsoft MIT-licensed UniXcoder code. This pilot reimplements encoder-only pooling and pair framing; it does not vend the upstream module.

## PyTorch / Transformers

- `torch` CUDA wheel: BSD-style license from the official PyTorch index.
- `transformers`, `huggingface_hub`: Apache-2.0.
- `safetensors`: Apache-2.0.

## ScanCode (isolated prefix)

- Installed `scancode-toolkit==32.5.0` into `artifacts/feasibility_pilot/env/scancode` only. `extractcode-7z` is not installed.
- Root exceptions dated 2026-10-04 cover scancode-toolkit, intbitset 4.1.2, publicsuffix2 2.20191221, text-unidecode 1.3 (Artistic alternative), certifi 2026.7.22, typing-extensions 4.16.0, commoncode 33.0.0, pyahocorasick 2.3.1, extractcode-libarchive 3.5.1.210531, and typecode-libmagic 5.39.210531.
- Attribution from upstream NOTICE: Copyright (c) nexB Inc. and others. ScanCode is a trademark of nexB Inc. SPDX-License-Identifier: Apache-2.0 AND CC-BY-4.0.
- Native libarchive/libmagic wheels retain bundled GPL/LGPL license files in `licenses/`. They stay in the isolated prefix and must not enter a product image, deployment, or distributable package.
- `pypi` in the exception schema does not mean `pnpm deps:check` inventories pip.

## Fixture license texts

Short MIT, BSD-3-Clause, and Apache-2.0 excerpts in `fixtures/licenses/` are public license texts copied for detection tests, with provenance recorded in the fixture manifest. They are not a grant of those licenses onto product source.
