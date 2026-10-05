"""Purpose- and endpoint-bound supervised exports; uncertainty stays diagnostic."""

from datetime import date

from .contracts import allowed, sha


def supervised_pairs(config, files, fragments, reviews):
    sources = {s['sourceId']: s for s in config['sources']}
    by = {f['fileId']: f for f in files}
    fragments = {f['fragmentId']: f for f in fragments}
    output, excluded, assertions = [], [], {}
    for review in reviews:
        if review.get('status') != 'REVIEWED' or review.get('rubric') != 'implementation-similarity-v1' or not review.get('reviewer') or not review.get('rationale'):
            excluded.append({'reason': 'UNREVIEWED_PAIR'})
            continue
        try:
            date.fromisoformat(review['reviewedOn'])
        except (KeyError, TypeError, ValueError):
            excluded.append({'reason': 'INVALID_REVIEW_DATE'})
            continue
        for pair in review.get('fragmentPairs', []):
            if pair.get('label') == 'UNKNOWN':
                excluded.append({'reason': 'UNKNOWN_LABEL'})
                continue
            if pair.get('label') not in ('SIMILAR', 'DISSIMILAR'): raise ValueError('invalid fragment label')
            endpoints = [fragments.get(pair.get(key)) for key in ('leftFragmentId', 'rightFragmentId')]
            if any(e is None for e in endpoints): raise ValueError('missing fragment endpoint')
            valid = True
            for end, hash_key in zip(endpoints, ('leftSha256', 'rightSha256')):
                file = by.get(end['fileId'])
                if not file or end['sha256'] != pair.get(hash_key): raise ValueError('fragment endpoint/hash drift')
                sha(end['fragmentId']); sha(end['sha256'])
                source = sources[file['sourceId']]
                if end.get('sourceSha256') != file['sha256']:
                    raise ValueError('fragment parent source hash drift')
                if file.get('partition') != 'train' or file.get('extraction') != 'VALID_EXTRACTED' or file.get('searchableExecutable') is not True or file.get('assignmentReviewed') is not True or not allowed(source['rights'], 'trainingAllowed', source, file) or not end.get('searchable') or end.get('executable') is not True or end.get('partialMLCoverage') or file.get('generated') or file.get('vendor') or file.get('normalizationExcludedReason'):
                    valid = False
            if not valid:
                excluded.append({'reason': 'INELIGIBLE_PURPOSE_PARTITION_OR_COVERAGE', 'pair': pair})
                continue
            # Similarity is symmetric: reversed endpoints belong to the same assertion group.
            key = tuple(sorted((end['fragmentId'], end['sha256']) for end in endpoints))
            assertions.setdefault(key, []).append({**pair, 'rubric': review['rubric'], 'reviewer': review['reviewer'], 'rationale': review['rationale'], 'reviewedOn':review['reviewedOn']})
    for key, records in sorted(assertions.items()):
        labels = {r['label'] for r in records}
        if len(labels) > 1:
            excluded.append({'reason': 'CONFLICTING_FRAGMENT_LABELS', 'canonicalEndpoints': list(key),
                             'labels': sorted(labels), 'assertions': sorted(records, key=lambda r: (r['label'], r['reviewer'], r['rationale']))})
            continue
        left, right = key
        evidence = sorted({(r['reviewer'], r['rationale'],r['reviewedOn']) for r in records})
        output.append({'leftFragmentId': left[0], 'leftSha256': left[1], 'rightFragmentId': right[0], 'rightSha256': right[1],
                       'label': next(iter(labels)), 'rubric': 'implementation-similarity-v1',
                       'reviewEvidence': [{'reviewer': reviewer, 'rationale': rationale,'reviewedOn':reviewed_on} for reviewer, rationale,reviewed_on in evidence]})
    return {'pairs': output, 'excluded': excluded, 'status': 'READY' if output else 'EMPTY_NO_ELIGIBLE_REVIEWED_PAIRS'}


def supervised_triplets(pairs):
    """Require a reviewed positive and two distinct reviewed query-relative negatives."""
    by_query = {}
    for pair in pairs:
        if pair["label"] not in {"SIMILAR", "DISSIMILAR"}:
            continue
        by_query.setdefault(pair["leftFragmentId"], {"positives": [], "negatives": [], "evidence": pair.get("reviewEvidence", [])})
        bucket = "positives" if pair["label"] == "SIMILAR" else "negatives"
        by_query[pair["leftFragmentId"]][bucket].append(pair)
    output, excluded = [], []
    for query, groups in sorted(by_query.items()):
        negatives = groups["negatives"]
        positives = [p for p in groups["positives"] if p["rightFragmentId"] != query]
        ids = {p["rightFragmentId"] for p in negatives}
        if not positives or len(ids) < 2:
            excluded.append({"reason": "INCOMPLETE_QUERY_TRIPLET", "leftFragmentId": query})
            continue
        first, second = negatives[0], next(p for p in negatives if p["rightFragmentId"] != negatives[0]["rightFragmentId"])
        output.append({"queryFragmentId": query, "positive": positives[0], "negatives": [first, second],
                       "rubric": "implementation-similarity-v1"})
    return {"triplets": output, "excluded": excluded,
            "status": "READY" if output else "EMPTY_NO_ELIGIBLE_REVIEWED_TRIPLETS"}
