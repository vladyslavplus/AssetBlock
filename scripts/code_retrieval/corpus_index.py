"""Reconcile reviewed exports with real source bytes, purposes and split membership.

The source inventory, split and extraction are the authority for source identity,
partition and family. Reviewed export metadata may add original fragments or
explicit derived units, but it may never redefine an authoritative file, its
partition/family, or the content that a fragment is bound to. Every admitted
endpoint is materialized from a bounded complete source read so an unsearchable,
partial, invented or source-drifting endpoint cannot be admitted silently.
"""

from collections import Counter
from pathlib import Path

from scripts.code_analysis.contracts import LANGUAGES, PARTITIONS, allowed, canonical, digest
from scripts.code_analysis.exports import supervised_pairs
from scripts.code_analysis.fragment_labels import merge_fragment_exports
from scripts.feasibility_pilot.paths import ensure_inside

TRAINING_PURPOSE = "trainingAllowed"
CONFLICT = "CONFLICTING_FRAGMENT_LABELS"

# Matches contracts.CAPS["fileBytes"]: sources are read completely, never as a prefix.
SOURCE_READ_CAP = 512 * 1024

# Fields a reviewed row must never be allowed to redefine for an authoritative file.
FILE_INVARIANTS = ("sourceId", "path", "partition", "familyId", "language",
                   "sha256", "normalizedSha256", "bytes")
FRAGMENT_INVARIANTS = ("fileId", "sha256", "sourceSha256", "startByte", "endByte")
# A new identity may not shadow an authoritative file's lineage.
SHADOW_INVARIANTS = ("sha256", "partition", "familyId", "language")


def eligible_file(file, source):
    """Mirror the endpoint gate used by supervised pair admission."""
    return (file.get("partition") == "train"
            and file.get("extraction") == "VALID_EXTRACTED"
            and file.get("searchableExecutable") is True
            and file.get("assignmentReviewed") is True
            and allowed(source.get("rights", {}), TRAINING_PURPOSE, source, file)
            and not file.get("generated") and not file.get("vendor")
            and not file.get("normalizationExcludedReason"))


def eligible_fragment(fragment):
    return (fragment.get("searchable") is True and fragment.get("executable") is True
            and not fragment.get("partialMLCoverage"))


def _unique(rows, key, label):
    out = {}
    for row in rows or []:
        ident = row.get(key)
        if not isinstance(ident, str) or not ident:
            raise ValueError("{} row has no {}".format(label, key))
        if ident in out:
            raise ValueError("duplicate {} identity: {}".format(label, ident))
        out[ident] = row
    return out


def _same(row, base, keys):
    return all(row.get(key) == base.get(key) for key in keys)


def bind_lineage(manifest, split, extract):
    """Bind extraction provenance to the source inventory and the authoritative split."""
    sources = {}
    for row in manifest.get("sources") or []:
        source_id = row.get("sourceId")
        if not isinstance(source_id, str) or not source_id or source_id in sources:
            raise ValueError("source manifest identities are invalid or duplicated")
        sources[source_id] = row
    if not sources:
        raise ValueError("source manifest declares no sources")

    inventory = {}
    for source in sources.values():
        for row in source.get("files") or []:
            key = (source["sourceId"], row.get("path"))
            if not isinstance(key[1], str) or key in inventory:
                raise ValueError("source inventory path identity is invalid or duplicated")
            inventory[key] = row.get("sha256")

    families = split.get("families")
    if not isinstance(families, dict) or not families:
        raise ValueError("split families are required")
    split_files = _unique(split.get("files"), "fileId", "split file")
    for row in split_files.values():
        if row.get("sourceId") not in sources:
            raise ValueError("split file names an unknown source")
        if row.get("language") not in LANGUAGES or row.get("partition") not in PARTITIONS:
            raise ValueError("split file language/partition is invalid")
        family = families.get(row.get("familyId"))
        if not isinstance(family, dict) or family.get("partition") != row.get("partition"):
            raise ValueError("split family disagrees with the file partition")
        family_sources = family.get("sources")
        if not isinstance(family_sources, list) or row["sourceId"] not in family_sources:
            raise ValueError("split family does not cover the file's source")
        if inventory.get((row["sourceId"], row.get("path"))) != row.get("sha256"):
            raise ValueError("split file is absent from the source inventory")

    # Canonical original-file identity: one authoritative fileId per (sourceId, path).
    # Floor counts and alias resolution use this map, never a reviewed fileId copy.
    canonical_files = {}
    for file_id, row in split_files.items():
        key = (row.get("sourceId"), row.get("path"))
        if key in canonical_files:
            raise ValueError("split repeats an original source file identity: {}".format(key))
        canonical_files[key] = file_id

    extract_files = _unique(extract.get("files"), "fileId", "extraction file")
    if set(extract_files) != set(split_files):
        raise ValueError("extraction file membership disagrees with the authoritative split")
    for file_id, row in extract_files.items():
        if not _same(row, split_files[file_id], FILE_INVARIANTS):
            raise ValueError("extraction changed a split invariant for " + file_id)
        if inventory.get((row["sourceId"], row.get("path"))) != row.get("sha256"):
            raise ValueError("extraction file is absent from the source inventory")

    final_test = sorted(fid for fid, row in split_files.items() if row["partition"] == "final-test")
    final_extract = sorted(fid for fid, row in extract_files.items() if row["partition"] == "final-test")
    if final_test != final_extract:
        raise ValueError("final-test membership disagrees between split and extraction")
    if not final_test:
        raise ValueError("final-test partition is empty")
    return {"sources": sources, "inventory": inventory, "splitFiles": split_files,
            "extractFiles": extract_files, "families": families, "canonicalFiles": canonical_files,
            "finalTestFileIds": final_test,
            "finalTestMembershipSha256": digest(canonical(final_test))}


def _resolve_extra_file(row, lineage):
    """A new reviewed file identity must be a genuine original source, never a redefinition."""
    source_id, path, sha256 = row.get("sourceId"), row.get("path"), row.get("sha256")
    if lineage["inventory"].get((source_id, path)) != sha256:
        raise ValueError("reviewed extra file is absent from the source inventory")
    if row.get("language") not in LANGUAGES or row.get("partition") not in PARTITIONS:
        raise ValueError("reviewed extra file language/partition is invalid")
    family = lineage["families"].get(row.get("familyId"))
    if not isinstance(family, dict) or family.get("partition") != row.get("partition"):
        raise ValueError("reviewed extra file family/partition is not authoritative")
    family_sources = family.get("sources")
    if not isinstance(family_sources, list) or source_id not in family_sources:
        raise ValueError("reviewed extra file family does not cover its source")
    if not isinstance(row.get("bytes"), int) or row["bytes"] <= 0 or row["bytes"] > SOURCE_READ_CAP:
        raise ValueError("reviewed extra file byte length is invalid")
    for base in lineage["splitFiles"].values():
        if base.get("sourceId") == source_id and base.get("path") == path:
            if not _same(row, base, SHADOW_INVARIANTS):
                raise ValueError("reviewed extra file shadows an authoritative file")
            break


def build(manifest, split, extract, payloads):
    """Merge reviewed payloads, bind lineage, re-derive gated pairs, keep reviewed triplets."""
    lineage = bind_lineage(manifest, split, extract)
    merged = merge_fragment_exports(*payloads)

    files = dict(lineage["extractFiles"])
    file_aliases = {}
    for row in merged["extraFiles"]:
        file_id = row.get("fileId")
        base = lineage["splitFiles"].get(file_id) or lineage["extractFiles"].get(file_id)
        if base is not None:
            if not _same(row, base, FILE_INVARIANTS):
                raise ValueError("reviewed extra file redefines an authoritative file: " + str(file_id))
            continue
        canonical_id = lineage["canonicalFiles"].get((row.get("sourceId"), row.get("path")))
        if canonical_id is not None:
            # A reviewed copy of an already-authoritative original file is an alias
            # of that file, never a second original file: floors count canonical
            # original-file identity, so the alias is recorded but never added.
            base = files[canonical_id]
            if not _same(row, base, FILE_INVARIANTS):
                raise ValueError(
                    "reviewed extra file aliases an authoritative file with changed identity: "
                    + str(file_id))
            file_aliases[file_id] = canonical_id
            continue
        if file_id in files:
            raise ValueError("duplicate reviewed extra file identity: " + str(file_id))
        _resolve_extra_file(row, lineage)
        files[file_id] = row

    fragments = _unique(extract.get("fragments"), "fragmentId", "extraction fragment")
    for row in merged["extraFragments"]:
        frag_id = row.get("fragmentId")
        base = fragments.get(frag_id)
        if base is not None:
            if not _same(row, base, FRAGMENT_INVARIANTS):
                raise ValueError("reviewed extra fragment redefines extraction data: " + str(frag_id))
            continue
        alias = file_aliases.get(row.get("fileId"))
        if alias is not None:
            # Aliased parents resolve to their canonical original file, so a
            # fragment cannot introduce a parallel file identity through its parent.
            row = {**row, "fileId": alias}
        fragments[frag_id] = row

    pairs = supervised_pairs(manifest, list(files.values()), list(fragments.values()),
                             merged["pairReviews"])
    conflicts = [row for row in pairs["excluded"] if row.get("reason") == CONFLICT]
    if conflicts:
        raise ValueError("reviewed fragment labels contradict for {} endpoint group(s)".format(len(conflicts)))
    return {"merged": merged, "files": list(files.values()), "fragments": list(fragments.values()),
            "pairs": pairs["pairs"], "pairExcluded": pairs["excluded"],
            "triplets": merged["trainTriplets"], "lineage": lineage,
            "fileAliases": dict(sorted(file_aliases.items()))}


def read_capped(stream, label):
    """Read at most SOURCE_READ_CAP + 1 bytes; one extra byte proves an over-cap source."""
    raw = stream.read(SOURCE_READ_CAP + 1)
    if not isinstance(raw, bytes) or len(raw) > SOURCE_READ_CAP:
        raise ValueError("source exceeds the accepted read cap: " + label)
    return raw


def source_bytes(sources_root, file):
    """Read the complete source file within the cap; drift fails closed."""
    sources_root = Path(sources_root)
    path = ensure_inside(sources_root / file["sourceId"] / file["path"], sources_root)
    label = file["sourceId"] + "/" + file["path"]
    try:
        with path.open("rb") as stream:
            raw = read_capped(stream, label)
    except FileNotFoundError:
        # A vanished source is drift, not a crash: callers get a fail-closed ValueError.
        raise ValueError("source file missing: " + label)
    declared = file.get("bytes")
    if not isinstance(declared, int) or declared <= 0:
        raise ValueError("source file lacks a declared byte length: " + str(file.get("fileId")))
    if len(raw) != declared:
        raise ValueError("source byte length drift: " + label)
    if digest(raw) != file["sha256"]:
        raise ValueError("source byte drift: " + label)
    return raw


def fragment_bytes(sources_root, file, fragment):
    """Materialize an original fragment as an exact, validated source slice."""
    raw = source_bytes(sources_root, file)
    start, end = fragment.get("startByte"), fragment.get("endByte")
    if not isinstance(start, int) or not isinstance(end, int) or start < 0 or end <= start or end > len(raw):
        raise ValueError("fragment span is invalid: " + str(fragment.get("fragmentId")))
    body = raw[start:end]
    if digest(body) != fragment["sha256"]:
        raise ValueError("fragment byte drift: " + str(fragment.get("fragmentId")))
    if fragment.get("sourceSha256") != file["sha256"]:
        raise ValueError("fragment source binding drift: " + str(fragment.get("fragmentId")))
    return body


def exclusion_counts(excluded):
    return dict(sorted(Counter(row.get("reason", "UNKNOWN") for row in excluded).items()))
