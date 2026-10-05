"""Eligibility-gated local source stages, with immutable snapshots and failure evidence."""

from scripts.feasibility_pilot.paths import ensure_inside

from .contracts import canonical, digest
from .corpus import ARTIFACT_ROOT, write_once
from .derivations import ast_rename, comment_format, exact_copy
from .extraction import extract_source
from .exports import supervised_pairs, supervised_triplets
from .fragment_labels import materialize_fragment_reviews
from .selection import select_fragments,enforce_fragment_cap


def extraction_stage(config, split, root):
    results, files, fragments = [], [], []
    for file in split['files']:
        file = {**file, 'assignmentReviewed': split['families'][file['familyId']]['assignmentReviewed']}
        if not file['assignmentReviewed']:
            raise ValueError('source-family assignments not reviewed')
        if not (file.get('permissions', {}).get('searchIndexAllowed') or file.get('permissions', {}).get('evaluationAllowed')):
            results.append({'fileId': file['fileId'], 'actualOutcome': 'EXCLUDED_RIGHTS', 'fragments': [], 'searchableExecutable': False})
            files.append({**file, 'extraction': 'EXCLUDED_RIGHTS', 'searchableExecutable': False})
            continue
        path = ensure_inside(root / 'sources' / file['sourceId'] / file['path'], ARTIFACT_ROOT)
        result = extract_source(path.read_bytes(), file)
        results.append(result)
        if result['actualOutcome'] != 'VALID_EXTRACTED':
            # Persist actual failure before refusing the stage; no diagnostic relabeling.
            write_once(root / 'unexpected-extraction.json', canonical({'fileId': file['fileId'], 'result': result}), ARTIFACT_ROOT)
            raise ValueError('unexpected eligible-source extraction failure: ' + file['fileId'])
        select_fragments(result)
        fragments.extend(f for f in result['fragments'] if f['canonicalIndexed'])
        enforce_fragment_cap([r for r in results if 'indexedCoverage' in r])
        files.append({**file, 'extraction': result['actualOutcome'], 'searchableExecutable': result['searchableExecutable']})
    inventory=[]
    for result in results:
        if 'indexedCoverage' not in result:
            inventory.append(result)
            continue
        relative='extraction-inventory/'+result['fileId']+'.json'
        write_once(root/relative,canonical(result),ARTIFACT_ROOT)
        inventory.append({k:result[k] for k in ('fileId','sourceSha256','actualOutcome','extractionIdentity','extractionSha256','selectionIdentity','selectionSha256','coverage','indexedCoverage','searchableExecutable')})
        inventory[-1].update(completeInventoryPath=relative,completeInventorySha256=digest(canonical(result)),completeFragmentCount=len(result['fragments']))
    return {'files': files, 'fragments': fragments, 'fileResults': inventory, 'extractionSha256': digest(canonical(results)),
            'status': 'RECORDED_COVERAGE_PENDING_ACCEPTANCE', 'corpusMinimaClaimed': False}


def derivation_stage(config, extracted, root):
    files = {f['fileId']: f for f in extracted['files']}
    seeds = config.get('seeds', [])
    records = []
    counts = {'train': 0, 'validation': 0, 'final-test': 0}
    extras = {}
    for seed in seeds:
        file = files.get(seed.get('seedFileId'))
        if not file or file.get('extraction') != 'VALID_EXTRACTED' or not file.get('searchableExecutable'):
            raise ValueError('query seed lacks valid executable source')
        path = ensure_inside(root / 'sources' / file['sourceId'] / file['path'], ARTIFACT_ROOT)
        raw = path.read_bytes()
        transforms = [exact_copy]
        key = (file['language'], file['partition'])
        if file['partition'] != 'final-test' and extras.get(key, 0) < 5:
            transforms.extend((comment_format, ast_rename))
            extras[key] = extras.get(key, 0) + 1
        for transform in transforms:
            try:
                output, record = transform(raw, file)
            except ValueError:
                if transform is exact_copy:
                    raise
                continue
            counts[file['partition']] += 1
            if counts['train'] + counts['validation'] > 300 or counts['final-test'] > 100:
                raise ValueError('query-file cap exceeded')
            write_once(root / 'queries' / (record['derivationId'] + '.source'), output, ARTIFACT_ROOT)
            records.append(record)
    return {'derivations': records, 'queryCounts': counts, 'status': 'DRAFTS_REQUIRE_INDEPENDENT_REVIEW' if records else 'EMPTY_NO_REVIEWED_QUERY_SEEDS',
            'finalTestScored': False, 'seedsInflatedByVariants': False, 'transformations': sorted({r['transformation'] for r in records})}


def export_stage(config, extracted):
    extra = materialize_fragment_reviews(extracted, config["fragmentAdoption"]) if config.get("fragmentAdoption") else {
        "pairReviews": [], "extraFiles": [], "extraFragments": []}
    files = list(extracted["files"]) + list(extra.get("extraFiles") or [])
    fragments = list(extracted["fragments"]) + list(extra.get("extraFragments") or [])
    reviews = list(config.get("pairReviews") or []) + list(extra.get("pairReviews") or [])
    pairs = supervised_pairs(config, files, fragments, reviews)
    triplets = extra.get("trainTriplets") or supervised_triplets(pairs["pairs"])["triplets"]
    triplet_status = extra.get("trainTriplets") and "READY" or supervised_triplets(pairs["pairs"])["status"]
    return {**pairs, "triplets": triplets, "trainTriplets": extra.get("trainTriplets") or triplets,
            "extraFiles": extra.get("extraFiles") or [], "extraFragments": extra.get("extraFragments") or [],
            "pairReviews": extra.get("pairReviews") or [],
            "diagnosticGalleries": extra.get("diagnosticGalleries") or {},
            "tripletExcluded": supervised_triplets(pairs["pairs"])["excluded"],
            "tripletStatus": triplet_status, "fragmentMaterialization": extra}
