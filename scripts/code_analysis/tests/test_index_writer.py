import copy,unittest
from scripts.code_analysis.index_writer import prepare_payload

def fixture():
    snapshot={k:'a'*64 for k in ('snapshot_id','manifest_sha256','rights_sha256','split_sha256')}
    identity={k:'b'*64 for k in ('index_key','model_key','gallery_sha256','representation_sha256')};identity['partition']='validation'
    parent={'fileId':'c'*64,'sha256':'d'*64,'partition':'validation','extraction':'VALID_EXTRACTED','assignmentReviewed':True,'permissions':{'searchIndexAllowed':True},'bytes':100}
    fragment={'fragmentId':'e'*64,'fileId':parent['fileId'],'sourceSha256':parent['sha256'],'sha256':'f'*64,'startByte':10,'endByte':30,'language':'python','dialect':'python','canonicalIndexed':True,'searchable':True,'executable':True,'provenanceRef':'authored fixture'}
    chunk={'fragmentId':fragment['fragmentId'],'chunkOrdinal':0,'tokenStart':0,'tokenEnd':4,'sourceStart':10,'sourceEnd':30,'representationSha256':'1'*64,'vector':[1.0]+[0.0]*767}
    return snapshot,identity,[parent],[fragment],[chunk]

class WriterPayloadTests(unittest.TestCase):
    def test_multiple_unsorted_units_normalize_before_hash_and_revalidation(self):
        snapshot,identity,files,fragments,chunks=fixture()
        other={**fragments[0],'fragmentId':'2'*64,'startByte':40,'endByte':60}
        other_chunk={**chunks[0],'fragmentId':other['fragmentId'],'sourceStart':40,'sourceEnd':60}
        prepared=prepare_payload(snapshot,identity,files,[fragments[0],other],[chunks[0],other_chunk])
        checked=prepare_payload(prepared['snapshot'],prepared['identity'],prepared['files'],prepared['fragments'],prepared['chunks'])
        self.assertEqual(prepared,checked)
        reversed_input=prepare_payload(snapshot,identity,files,[other,fragments[0]],[other_chunk,chunks[0]])
        self.assertEqual(prepared,reversed_input)
    def test_complete_full_vector_payload(self):
        p=prepare_payload(*fixture());self.assertEqual(p['expectedRows'],1);self.assertEqual(len(p['vectorSha256']),64)
    def test_incomplete_null_or_out_of_range_mapping(self):
        for start,end in [(10,None),(None,30),(0,30),(10,31),(20,20)]:
            values=fixture();values[4][0].update(sourceStart=start,sourceEnd=end)
            with self.subTest(start=start,end=end),self.assertRaises(ValueError):prepare_payload(*values)
        values=fixture();values[4][0].update(sourceStart=None,sourceEnd=None)
        self.assertEqual(prepare_payload(*values)['expectedRows'],1)
    def test_missing_duplicate_and_noncontiguous_chunk_rows(self):
        for chunks in [[],[fixture()[4][0],fixture()[4][0]],[fixture()[4][0] | {'chunkOrdinal':1}]]:
            values=fixture();values=list(values);values[4]=chunks
            with self.assertRaises(ValueError):prepare_payload(*values)
    def test_eligibility_raw_spans_and_vector_dimensions(self):
        values=fixture();values[2][0]['permissions']['searchIndexAllowed']=False
        with self.assertRaises(ValueError):prepare_payload(*values)
        values=fixture();values[3][0]['endByte']=101
        with self.assertRaises(ValueError):prepare_payload(*values)
        values=fixture();values[4][0]['vector']=[1.0]
        with self.assertRaises(ValueError):prepare_payload(*values)
