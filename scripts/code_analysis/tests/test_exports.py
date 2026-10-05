import copy
import unittest

from scripts.code_analysis.exports import supervised_pairs


class ExportTests(unittest.TestCase):
    def setUp(self):
        self.files = [{'fileId': str(i)*64, 'sha256': str(i+2)*64, 'sourceId': 'source', 'path': str(i)+'.py',
                       'partition': 'train', 'extraction': 'VALID_EXTRACTED', 'searchableExecutable': True, 'assignmentReviewed': True} for i in (1,2)]
        source = {'sourceId': 'source', 'commit': 'a'*40, 'notices': [{'path':'LICENSE','sha256':'f'*64}]}
        source['rights'] = {'status': 'REVIEWED','reviewer':'Fixture reviewer','reviewedOn':'2026-10-05','rationale':'Authored fixtures','terms':'MIT',
                            'commit':source['commit'],'noticeHashes':{'LICENSE':'f'*64},'fileHashes':{f['path']:f['sha256'] for f in self.files},'grants':{'trainingAllowed':True}}
        self.config = {'sources':[source]}
        self.fragments = [{'fragmentId':str(i+4)*64,'sha256':str(i+6)*64,'fileId':f['fileId'],'sourceSha256':f['sha256'],
                           'searchable':True,'executable':True} for i,f in enumerate(self.files,1)]

    def review(self, label, reverse=False):
        left,right = self.fragments[::-1] if reverse else self.fragments
        return {'status':'REVIEWED','rubric':'implementation-similarity-v1','reviewer':'Fixture reviewer','reviewedOn':'2026-10-05','rationale':'Full authored endpoints reviewed',
                'fragmentPairs':[{'leftFragmentId':left['fragmentId'],'leftSha256':left['sha256'],'rightFragmentId':right['fragmentId'],'rightSha256':right['sha256'],'label':label}]}

    def test_conflicts_reverse_orientation_and_order_are_quarantined(self):
        reviews=[self.review('SIMILAR'),self.review('DISSIMILAR',reverse=True)]
        for sequence in (reviews,reviews[::-1]):
            result=supervised_pairs(self.config,self.files,self.fragments,sequence)
            self.assertEqual(result['pairs'],[])
            self.assertEqual(result['excluded'][0]['reason'],'CONFLICTING_FRAGMENT_LABELS')
            self.assertEqual(result['excluded'][0]['labels'],['DISSIMILAR','SIMILAR'])

    def test_compatible_duplicates_do_not_inflate_and_unknown_stays_diagnostic(self):
        result=supervised_pairs(self.config,self.files,self.fragments,[self.review('SIMILAR'),self.review('SIMILAR',reverse=True),self.review('UNKNOWN')])
        self.assertEqual(len(result['pairs']),1)
        self.assertEqual(result['excluded'][0]['reason'],'UNKNOWN_LABEL')

    def test_parent_extraction_or_hash_cannot_be_faked_by_fragment_flags(self):
        for state in ('PENDING','INVALID_PARSE'):
            files=copy.deepcopy(self.files);files[0]['extraction']=state
            self.assertEqual(supervised_pairs(self.config,files,self.fragments,[self.review('SIMILAR')])['pairs'],[])
        fragments=copy.deepcopy(self.fragments);fragments[0]['sourceSha256']='e'*64
        with self.assertRaisesRegex(ValueError,'parent source hash'):
            supervised_pairs(self.config,self.files,fragments,[self.review('SIMILAR')])

    def test_whole_file_labels_never_assign_fragment_labels(self):
        review=self.review('SIMILAR');review['pairs']=review.pop('fragmentPairs')
        self.assertEqual(supervised_pairs(self.config,self.files,self.fragments,[review])['pairs'],[])
