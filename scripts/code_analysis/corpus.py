"""Explicit immutable HTTPS blobs, bounded reads, offline hash verification."""

import urllib.request
import re
from pathlib import Path
from urllib.parse import quote, unquote, urlsplit

from scripts.feasibility_pilot.paths import REPO_ROOT, ensure_inside

from .contracts import CAPS, allowed, canonical, digest, relative, report, validate_config

ARTIFACT_ROOT = REPO_ROOT / "artifacts" / "code_analysis"


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        raise ValueError("redirect rejected")


def blob_url(source, row):
    return "https://raw.githubusercontent.com/{}/{}/{}".format(
        source["repository"], source["commit"], quote(row["path"], safe="/"))


def fetch_blob(url):
    # URL is constructed exclusively from validated config, never caller-supplied redirects.
    parsed = urlsplit(url)
    match = re.fullmatch(r"/([A-Za-z0-9_.-]+)/([A-Za-z0-9_.-]+)/([0-9a-f]{40})/(.+)", parsed.path)
    if parsed.scheme != "https" or parsed.netloc != "raw.githubusercontent.com" or parsed.query or parsed.fragment or not match:
        raise ValueError("only immutable allowlisted HTTPS blob URLs accepted")
    relative(unquote(match[4]))
    opener = urllib.request.build_opener(NoRedirect())
    request = urllib.request.Request(url, headers={"User-Agent": "AssetBlock-corpus-intake"})
    with opener.open(request, timeout=30) as response:
        if response.status != 200 or response.geturl() != url:
            raise ValueError("unexpected HTTP response/URL")
        data = response.read(CAPS["fileBytes"] + 1)
    if len(data) > CAPS["fileBytes"]:
        raise ValueError("blob byte cap exceeded")
    return data


def write_once(path, data, root):
    path = ensure_inside(path, root)
    path.parent.mkdir(parents=True, exist_ok=True)
    ensure_inside(path, root)
    with path.open("xb") as stream:
        stream.write(data)


def intake(config, root, *, offline=False, fetch=fetch_blob):
    validate_config(config)
    root = Path(root)
    ensure_inside(root, ARTIFACT_ROOT)
    records, total = [], 0
    for source in config["sources"]:
        for row in source["notices"] + source["files"]:
            path = ensure_inside(root / "sources" / source["sourceId"] / row["path"], ARTIFACT_ROOT)
            if offline:
                with path.open("rb") as stream:
                    raw = stream.read(CAPS["fileBytes"] + 1)
            else:
                raw = fetch(blob_url(source, row))
            if not raw or len(raw) > CAPS["fileBytes"]:
                raise ValueError("empty/oversized source")
            total += len(raw)
            if total > CAPS["totalBytes"]:
                raise ValueError("total intake byte cap exceeded")
            if digest(raw) != row["sha256"]:
                raise ValueError("source hash drift: " + source["sourceId"] + "/" + row["path"])
            raw.decode("utf-8", errors="strict")
            if b"\0" in raw:
                raise ValueError("binary content rejected")
            if not offline:
                write_once(path, raw, ARTIFACT_ROOT)
            if row in source["files"]:
                from .classification import generator_header
                detected_generator = generator_header(raw)
                identity = digest(canonical([source["sourceId"], source["commit"], row["path"], row["sha256"]]))
                records.append({**row, "fileId": identity, "sourceId": source["sourceId"],
                                "bytes": len(raw), "newline": "crlf" if b"\r\n" in raw else "lf",
                                "encoding": "utf-8", "original": True,
                                "generated": row.get("generated", False) or bool(detected_generator), "generatedHeaderEvidence": detected_generator, "vendor": row.get("vendor", False),
                                "extraction": "PENDING",
                                "permissions": {p: allowed(source.get("rights", {}), p, source, row)
                                                for p in ("searchIndexAllowed", "trainingAllowed", "evaluationAllowed",
                                                          "rawRedistributionAllowed", "derivedArtifactRedistributionAllowed")}})
    rights_hash = digest(canonical({s["sourceId"]: s.get("rights", {}) for s in config["sources"]}))
    file_hash = digest(canonical(records))
    config_hash = digest(canonical(config))
    return report({"snapshotId": digest(canonical([config_hash, rights_hash, file_hash])),
            "rightsSha256": rights_hash, "filesSha256": file_hash,
            "files": records, "fetchedBytes": total, "configSha256": config_hash})
