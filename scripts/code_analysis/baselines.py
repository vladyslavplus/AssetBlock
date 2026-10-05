"""Manifest-bound token candidates, independent branches, union, and separate RRF."""

import shutil
from pathlib import Path

from scripts.feasibility_pilot.paths import ARTIFACT_ROOT as PILOT_ROOT
from scripts.feasibility_pilot.paths import ensure_inside
from scripts.feasibility_pilot.tools import run_dolos_batch, run_scancode

from .contracts import LANGUAGES, canonical, digest, report, sha
from .corpus import ARTIFACT_ROOT

SUFFIX = {"javascript": ".js", "typescript": ".ts", "python": ".py", "java": ".java", "csharp": ".cs"}


def bind_token_pairs(raw_pairs, allowlist):
    """Map Dolos path/IDs onto allowlisted file identities; unknown/duplicate IDs fail."""
    by_path = {row["path"]: row for row in allowlist}
    by_id = {row["fileId"]: row for row in allowlist}
    bound = []
    seen = set()
    for pair in raw_pairs:
        left_path = (pair.get("leftFilePath") or pair.get("leftPath") or pair.get("path1") or pair.get("leftFile")
                     or pair.get("file1") or pair.get("left") or "")
        right_path = (pair.get("rightFilePath") or pair.get("rightPath") or pair.get("path2") or pair.get("rightFile")
                      or pair.get("file2") or pair.get("right") or "")
        left_id = pair.get("leftFileId") or pair.get("id1") or ""
        right_id = pair.get("rightFileId") or pair.get("id2") or ""
        if not (isinstance(left_id, str) and len(left_id) == 64):
            left_id = ""
        if not (isinstance(right_id, str) and len(right_id) == 64):
            right_id = ""
        left = _match_token_id(left_id, by_id, by_path) or _match_token_id(left_path, by_id, by_path)
        right = _match_token_id(right_id, by_id, by_path) or _match_token_id(right_path, by_id, by_path)
        if left is None or right is None:
            raise ValueError("token pair references unknown allowlisted identity")
        key = tuple(sorted((left["fileId"], right["fileId"])))
        if key in seen:
            raise ValueError("duplicate token pair identity")
        seen.add(key)
        score = pair.get("similarity") or pair.get("score") or pair.get("similarityScore")
        bound.append({"leftId": left["fileId"], "rightId": right["fileId"], "tokenScore": float(score),
                      "leftSha256": left["sha256"], "rightSha256": right["sha256"]})
    return bound


def rank_files(pairs, query_id, gallery_ids, *, limit=20):
    gallery = set(gallery_ids)
    scores = {}
    for pair in pairs:
        other = pair["rightId"] if pair["leftId"] == query_id else pair["leftId"] if pair["rightId"] == query_id else None
        if other is None or other not in gallery:
            continue
        scores[other] = max(scores.get(other, 0.0), pair["tokenScore"])
    ordered = sorted(scores, key=lambda file_id: (-scores[file_id], file_id))
    return [{"fileId": file_id, "score": scores[file_id], "rank": index} for index, file_id in enumerate(ordered[:limit], 1)]


def union_candidates(token_ids, semantic_ids, *, budget=40):
    ordered, seen = [], set()
    for file_id in list(token_ids) + list(semantic_ids):
        sha(file_id)
        if file_id in seen:
            continue
        seen.add(file_id)
        ordered.append(file_id)
        if len(ordered) == budget:
            break
    return ordered


def rrf_order(token_ids, semantic_ids, *, constant=60, budget=40):
    ranks = {}
    for source in (token_ids, semantic_ids):
        for rank, file_id in enumerate(source, 1):
            ranks[file_id] = ranks.get(file_id, 0.0) + 1.0 / (constant + rank)
    return [file_id for file_id, _ in sorted(ranks.items(), key=lambda item: (-item[1], item[0]))[:budget]]


def _match_token_id(raw, by_id, by_path):
    value = str(raw or "").replace("\\", "/")
    if not value:
        return None
    if value in by_id:
        return by_id[value]
    if value in by_path:
        return by_path[value]
    base = value.rsplit("/", 1)[-1]
    if base in by_path:
        return by_path[base]
    stem = base.rsplit(".", 1)[0]
    return by_id.get(stem)


def exact_copy_query_id(parent_file_id):
    sha(parent_file_id)
    return digest(canonical(["exact-copy-query-v1", parent_file_id]))


def identity_sha_diagnostic_rank(gallery_ids, *, query_sha256, gallery_shas, limit=20):
    """Named identity fast-path only; never substitutes for the Dolos token branch."""
    hits = [file_id for file_id in gallery_ids if gallery_shas.get(file_id) == query_sha256]
    return [{"fileId": file_id, "score": 1.0, "rank": index, "method": "identity-sha-diagnostic"}
            for index, file_id in enumerate(hits[:limit], 1)]


def token_stage(root, language, files, output_dir, *, k=23, window=17, timeout_s=600):
    dest = ensure_inside(Path(output_dir), PILOT_ROOT)
    if dest.exists():
        shutil.rmtree(dest)
    inputs = dest / "inputs"
    inputs.mkdir(parents=True)
    staged, allowlist = [], []
    suffix = SUFFIX[language]
    for row in files:
        src = ensure_inside(Path(row["absolutePath"]), ARTIFACT_ROOT)
        name = row["fileId"] + suffix
        target = inputs / name
        shutil.copyfile(src, target)
        staged.append(target)
        allowlist.append({"fileId": row["fileId"], "path": name, "sha256": row["sha256"]})
    result = run_dolos_batch(language=language, files=staged, output_dir=dest / "dolos", k=k, window=window, timeout_s=timeout_s)
    if result.get("outcome") != "SUCCEEDED":
        return report({**result, "status": "FAILED", "k": k, "window": window})
    bound = bind_token_pairs(result.get("pairs") or [], allowlist)
    return report({**result, "boundPairs": bound, "k": k, "window": window, "status": "SUCCEEDED",
                   "pairs": [], "stderr": result.get("stderr", "")[-500:]})


def token_corpus(extract, sources_root, dest_root, *, queries=(), timeout_s=600):
    sources_root = ensure_inside(Path(sources_root), ARTIFACT_ROOT)
    dest_root = ensure_inside(Path(dest_root), PILOT_ROOT)
    batches = {}
    all_bound = []
    query_index = {}
    for query in queries:
        query_index.setdefault((query["language"], query["partition"]), []).append(query)
    for partition in ("train", "validation"):
        for language in LANGUAGES:
            gallery = [{**row, "absolutePath": str(sources_root / row["sourceId"] / row["path"])}
                       for row in extract["files"]
                       if row.get("searchableExecutable") and not row.get("generated")
                       and row["partition"] == partition and row["language"] == language]
            extra = []
            for query in query_index.get((language, partition), ()):
                extra.append({"fileId": query["queryId"], "sha256": query["sha256"],
                              "absolutePath": query["absolutePath"], "path": query["queryId"] + SUFFIX[language]})
            files = gallery + extra
            key = language + "/" + partition
            if len(gallery) < 2:
                batches[key] = {"status": "FAILED", "reason": "gallery too small for token comparison"}
                continue
            result = token_stage(sources_root, language, files, dest_root / (language + "-" + partition),
                                 k=23, window=17, timeout_s=timeout_s)
            batches[key] = {"status": result.get("status"), "outcome": result.get("outcome"),
                            "reason": result.get("reason"), "boundPairCount": len(result.get("boundPairs") or []),
                            "csvSha256": result.get("csvSha256"), "durationSeconds": result.get("durationSeconds"),
                            "queryCount": len(extra)}
            all_bound.extend(result.get("boundPairs") or [])
    failed = [key for key, row in batches.items() if row.get("status") != "SUCCEEDED"]
    return report({"status": "SUCCEEDED" if not failed else "FAILED", "k": 23, "window": 17,
                   "finalTestScored": False, "failedBatches": failed, "batches": batches, "boundPairs": all_bound,
                   "distinctQueryIds": True})


def license_stage(input_dir, output_json, timeout_s=3600, *, expected_paths=()):
    staged = ensure_inside(PILOT_ROOT / "runs/code-analysis-scancode/sources", PILOT_ROOT)
    output = ensure_inside(Path(output_json), PILOT_ROOT)
    src = ensure_inside(Path(input_dir), ARTIFACT_ROOT)
    if staged.exists():
        shutil.rmtree(staged)
    shutil.copytree(src, staged)
    result = run_scancode(staged, output, timeout_s)
    scanned = {_norm_scan(item.get("path")) for item in result.get("files") or []}
    required = [_norm_scan(path) for path in expected_paths] or [_norm_scan(str(path.relative_to(src)))
                                                                for path in src.rglob("*") if path.is_file()]
    missing = [path for path in required if path not in scanned and not any(item.endswith("/" + path) or item.endswith(path) for item in scanned)]
    return report({**result, "coverageComplete": result.get("outcome") == "SUCCEEDED" and not missing,
                   "coverageMissing": missing[:50], "expectedPathCount": len(required)})


def _norm_scan(value):
    return str(value or "").replace("\\", "/").lstrip("./")
