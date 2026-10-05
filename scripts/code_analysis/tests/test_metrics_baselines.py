import unittest

from scripts.code_analysis.baselines import bind_token_pairs, identity_sha_diagnostic_rank, rank_files, rrf_order, union_candidates
from scripts.code_analysis.evaluation import language_equal_mean, query_metrics, seed_macro, timed_percentiles
from scripts.code_analysis.exports import supervised_triplets


class BaselineUnionTests(unittest.TestCase):
    def test_union_and_separate_rrf(self):
        token = ['a' * 64, 'b' * 64]
        semantic = ['b' * 64, 'c' * 64]
        union = union_candidates(token, semantic, budget=40)
        self.assertEqual(union, token + ['c' * 64])
        rrf = rrf_order(token, semantic, constant=60)
        self.assertEqual(rrf[0], 'b' * 64)

    def test_token_bind_uses_fileid_filename_stem(self):
        left, right = 'a' * 64, 'b' * 64
        allow = [{'fileId': left, 'path': left + '.js', 'sha256': 'c' * 64},
                 {'fileId': right, 'path': right + '.js', 'sha256': 'd' * 64}]
        bound = bind_token_pairs([{'leftFilePath': r'D:\tmp\inputs\\' + left + '.js', 'rightFilePath': right + '.js',
                                   'leftFileId': '12', 'rightFileId': '9', 'similarity': 0.4}], allow)
        self.assertEqual(bound[0]['leftId'], left)
        ranking = rank_files(bound, left, [left, right], limit=20)
        self.assertEqual(ranking[0]['fileId'], right)
        missing = rank_files([], left, [left, right], limit=20)
        self.assertEqual(missing, [])
        identity = identity_sha_diagnostic_rank([left, right], query_sha256='c' * 64, gallery_shas={left: 'c' * 64, right: 'd' * 64})
        self.assertEqual(identity[0]['method'], 'identity-sha-diagnostic')


class TripletTests(unittest.TestCase):
    def test_incomplete_triplet_rejected(self):
        pairs = [{'leftFragmentId': '1' * 64, 'rightFragmentId': '1' * 64, 'label': 'SIMILAR'}]
        result = supervised_triplets(pairs)
        self.assertEqual(result['status'], 'EMPTY_NO_ELIGIBLE_REVIEWED_TRIPLETS')

    def test_positive_and_two_negatives(self):
        q, p, n1, n2 = ('1' * 64, '2' * 64, '3' * 64, '4' * 64)
        pairs = [
            {'leftFragmentId': q, 'rightFragmentId': p, 'label': 'SIMILAR'},
            {'leftFragmentId': q, 'rightFragmentId': n1, 'label': 'DISSIMILAR'},
            {'leftFragmentId': q, 'rightFragmentId': n2, 'label': 'DISSIMILAR'},
        ]
        result = supervised_triplets(pairs)
        self.assertEqual(len(result['triplets']), 1)


class MetricGateTests(unittest.TestCase):
    def test_insufficient_gallery_is_diagnostic(self):
        metrics = query_metrics(['a'], {'a'}, ['a'], 5, corpus_minima_met=True)
        self.assertEqual(metrics['status'], 'DIAGNOSTIC_INSUFFICIENT_GALLERY')

    def test_seed_macro_ignores_duplicate_bytes(self):
        case = {'seedGroup': 'a' * 64, 'querySha256': 'b' * 64, 'metrics': {'recall': 1.0, 'mrr': 1.0}}
        other = {'seedGroup': 'c' * 64, 'querySha256': 'd' * 64, 'metrics': {'recall': 0.0, 'mrr': 0.0}}
        result = seed_macro([case, case, other])
        self.assertEqual(result['uniqueSeeds'], 2)
        self.assertEqual(result['recall'], 0.5)

    def test_language_equal_weight_and_latency_floor(self):
        mean = language_equal_mean([{'recall': 1.0, 'mrr': 1.0}, {'recall': 0.0, 'mrr': 0.0}])
        self.assertEqual(mean['recall'], 0.5)
        with self.assertRaises(ValueError):
            timed_percentiles([0.1] * 29)
        timed = timed_percentiles([0.01] * 30)
        self.assertEqual(timed['count'], 30)
