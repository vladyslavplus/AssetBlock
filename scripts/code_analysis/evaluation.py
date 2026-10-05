"""File-level metrics with nontrivial galleries and equal independent seed weight."""

from collections import defaultdict
import math

from .contracts import canonical, digest, sha


def freeze_protocol(protocol):
    if protocol.get('finalTestScoringAllowed') is not False or protocol.get('candidateUnit') != 'source-file' or not protocol.get('methodologyFrozen'):
        raise ValueError('invalid frozen phase protocol')
    return {'protocol': protocol, 'sha256': digest(canonical(protocol))}


def query_metrics(ranking, relevant, gallery, k, *, corpus_minima_met):
    if type(k) is not int or k < 1: raise ValueError('invalid metric K')
    if len(gallery) != len(set(gallery)) or len(ranking) != len(set(ranking)) or not set(ranking) <= set(gallery):
        raise ValueError('unknown/duplicate candidate file identity')
    relevant = set(relevant)
    if not relevant or not relevant <= set(gallery): raise ValueError('nonempty eligible relevance required')
    top = ranking[:k]
    recall = len(set(top) & relevant) / len(relevant)
    reciprocal = next((1 / (i + 1) for i, file_id in enumerate(top) if file_id in relevant), 0.)
    status = 'PRIMARY'
    if len(gallery) <= k: status = 'DIAGNOSTIC_INSUFFICIENT_GALLERY'
    elif not corpus_minima_met: status = 'DIAGNOSTIC_INSUFFICIENT_CORPUS'
    return {'recall': recall, 'mrr': reciprocal, 'k': k, 'galleryCount': len(gallery), 'relevantCount': len(relevant), 'candidateCount': len(top), 'status': status}


def seed_macro(cases):
    if not cases: raise ValueError('empty query metrics')
    grouped, seen = defaultdict(list), {}
    for case in cases:
        sha(case['seedGroup']); sha(case['querySha256'])
        key = (case['seedGroup'], case['querySha256'])
        if key in seen:
            if seen[key] != case['metrics']: raise ValueError('identical query bytes produced conflicting metrics')
            continue
        seen[key] = case['metrics']
        grouped[case['seedGroup']].append(case['metrics'])
    seed_means = {seed: {metric: sum(item[metric] for item in items) / len(items) for metric in ('recall', 'mrr')} for seed, items in sorted(grouped.items())}
    return {'uniqueSeeds': len(seed_means), 'uniqueQueryCases': len(seen), 'casesPerSeed': {seed: len(items) for seed, items in sorted(grouped.items())},
            'seedMeans': seed_means, 'recall': sum(s['recall'] for s in seed_means.values()) / len(seed_means), 'mrr': sum(s['mrr'] for s in seed_means.values()) / len(seed_means)}


def require_scoring_partition(partition):
    if partition not in ('train', 'validation'): raise ValueError('final-test scoring prohibited in this phase')


def language_equal_mean(language_results):
    if not language_results:
        raise ValueError('language results required')
    recall = sum(item['recall'] for item in language_results) / len(language_results)
    mrr = sum(item['mrr'] for item in language_results) / len(language_results)
    return {'languages': len(language_results), 'recall': recall, 'mrr': mrr, 'equalLanguageWeight': True}


def cpu_rank(query_vectors, gallery_rows, *, key, limit=40):
    """Exhaustive max-chunk cosine keyed by fileId or fragmentId."""
    if not query_vectors or not gallery_rows:
        raise ValueError('nonempty query/gallery vectors required')
    if key not in ('fileId', 'fragmentId'):
        raise ValueError('ranking key must be fileId or fragmentId')
    best = {}
    for row in gallery_rows:
        identity = row[key]
        score = max(sum(a * b for a, b in zip(q, row['vector'])) for q in query_vectors)
        current = best.get(identity)
        if current is None or score > current:
            best[identity] = score
    ordered = sorted(best, key=lambda item: (-best[item], item))
    return [{key: item, 'score': best[item], 'rank': index} for index, item in enumerate(ordered[:limit], 1)]


def cpu_file_rank(query_vectors, gallery_rows, *, limit=40):
    return cpu_rank(query_vectors, gallery_rows, key='fileId', limit=limit)


def timed_percentiles(samples):
    if len(samples) < 30:
        raise ValueError('timed query sample below 30 after warmup exclusion')
    ordered = sorted(samples)
    def at(p):
        index = min(len(ordered) - 1, max(0, math.ceil(p / 100 * len(ordered)) - 1))
        return ordered[index]
    return {'count': len(samples), 'p50': at(50), 'p95': at(95)}
