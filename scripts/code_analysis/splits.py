"""Conservative AST token duplicate grouping; whole-family deterministic assignments."""

from scripts.feasibility_pilot.extract import parse_source
from importlib.metadata import version

from .contracts import PARTITIONS, canonical, digest

NORMALIZATION = "ast-leaves-identifiers-v1"


def parser_identity():
    return {name: version(name) for name in ("tree-sitter", "tree-sitter-javascript", "tree-sitter-typescript",
                                           "tree-sitter-python", "tree-sitter-java", "tree-sitter-c-sharp")}


def normalized_hash(raw, language):
    tree = parse_source(language, raw)
    if tree.root_node.has_error:
        raise ValueError("invalid source cannot supply normalized duplicate evidence")
    tokens = []
    stack = [tree.root_node]
    while stack:
        node = stack.pop()
        if "comment" in node.type:
            continue
        # Preserve complete literal values, including interpolation, conservatively.
        if "string" in node.type or "literal" in node.type:
            tokens.append([node.type, raw[node.start_byte:node.end_byte].decode("utf-8")])
        elif node.children:
            stack.extend(reversed(node.children))
        else:
            value = "IDENTIFIER" if "identifier" in node.type else raw[node.start_byte:node.end_byte].decode("utf-8")
            tokens.append([node.type, value])
    if not tokens:
        raise ValueError("empty normalization")
    return digest(canonical([NORMALIZATION, language, tokens]))


def assign(config, files):
    sources = {s["sourceId"]: s for s in config["sources"]}
    parent = {sid: sid for sid in sources}

    def find(sid):
        while parent[sid] != sid:
            sid = parent[sid]
        return sid

    def union(a, b):
        a, b = find(a), find(b)
        parent[max(a, b)] = min(a, b)

    seen = {}
    decisions = []
    for sid, source in sorted(sources.items()):
        for lineage in sorted(set(source["lineage"] + [source["repository"].lower()])):
            key = ("lineage", lineage.lower())
            if key in seen:
                union(sid, seen[key])
                decisions.append({"kind": "lineage", "left": sid, "right": seen[key], "identity": lineage})
            seen[key] = sid
    for file in sorted(files, key=lambda f: f["fileId"]):
        for kind in ("sha256", "normalizedSha256"):
            value = file.get(kind)
            if not value:
                if kind == "normalizedSha256" and file.get("normalizationExcludedReason"):
                    decisions.append({"kind": "normalization-exclusion", "fileId": file["fileId"],
                                      "reason": file["normalizationExcludedReason"]})
                    continue
                raise ValueError("missing duplicate evidence")
            key = (kind, value)
            if key in seen:
                union(file["sourceId"], seen[key])
                decisions.append({"kind": kind, "left": file["sourceId"], "right": seen[key], "identity": value})
            seen[key] = file["sourceId"]
    groups = {}
    for sid in sorted(sources):
        groups.setdefault(find(sid), []).append(sid)
    assignments, membership = {}, {}
    for members in groups.values():
        family = digest(canonical(members))
        requested = {sources[sid].get("partition") for sid in members} - {None}
        if len(requested) > 1 or (requested and not requested <= set(PARTITIONS)):
            raise ValueError("conflicting reviewed family assignments")
        if requested:
            partition = requested.pop()
        else:
            bucket = int(digest(canonical([42, family]))[:8], 16) % 10
            partition = "train" if bucket < 6 else "validation" if bucket < 8 else "final-test"
        assignments[family] = {"sources": members, "partition": partition,
                               "assignmentReviewed": all(sources[sid].get("partitionReviewed") is True for sid in members)}
        for sid in members:
            membership[sid] = family
    # File equivalence is separate from source family identity; distinct files in one repo count once each.
    duplicates = {f["fileId"]: f["fileId"] for f in files}

    def df(i):
        while duplicates[i] != i:
            i = duplicates[i]
        return i

    indexes = {}
    for file in sorted(files, key=lambda f: f["fileId"]):
        for kind in ("sha256", "normalizedSha256"):
            if not file.get(kind):
                continue
            key = (kind, file[kind])
            if key in indexes:
                a, b = df(file["fileId"]), df(indexes[key])
                duplicates[max(a, b)] = min(a, b)
            indexes[key] = file["fileId"]
    rows = [{**f, "duplicateGroup": df(f["fileId"]), "familyId": membership[f["sourceId"]],
             "partition": assignments[membership[f["sourceId"]]]["partition"]} for f in sorted(files, key=lambda f: f["fileId"])]
    return {"seed": 42, "algorithm": "family-sha256-v1", "normalization": NORMALIZATION,
            "families": assignments, "duplicateDecisions": decisions, "files": rows}
