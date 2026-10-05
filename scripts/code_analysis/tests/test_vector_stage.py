import copy
import struct
import unittest
from scripts.code_analysis.contracts import canonical,digest
from scripts.code_analysis.representations import CHUNKING
from scripts.code_analysis.vector_stage import validate_prepared

class PreparedRepresentationTests(unittest.TestCase):
    def setUp(self):
        self.identity={'fileHashes':{'vocab.json':'old'},'framingIds':[0,7,2],'selectionSha256':'selected'}
        self.extracted={'files':[{'fileId':'file','partition':'train','language':'Java'}],'fragments':[{'fileId':'file','fragmentId':'fragment','canonicalIndexed':True,'startByte':4,'endByte':9}]}
        self.maps=struct.pack('<II',4,8)
        self.rows=[{'fragmentId':'fragment','fileId':'file','partition':'train','language':'Java','chunkOrdinal':0,'inputIds':[0,7,2,99,2],'totalInputTokens':5,'chunking':CHUNKING,'sourceMappingByteOffset':0,'sourceMappingByteLength':8,'representationSha256':digest(canonical([CHUNKING,[99],[{'startByte':4,'endByte':8}]]))}]
        self.metadata={'tokenizerIdentity':copy.deepcopy(self.identity),'chunks':1}
    def test_matching_identity_passes_changed_vocabulary_and_legacy_manifest_refused(self):
        validate_prepared(self.rows,self.maps,self.metadata,self.extracted,self.identity)
        changed=copy.deepcopy(self.identity);changed['fileHashes']['vocab.json']='new'
        with self.assertRaisesRegex(ValueError,'new immutable run'):validate_prepared(self.rows,self.maps,self.metadata,self.extracted,changed)
        legacy=copy.deepcopy(self.metadata);del legacy['tokenizerIdentity']
        with self.assertRaisesRegex(ValueError,'new immutable run'):validate_prepared(self.rows,self.maps,legacy,self.extracted,self.identity)
    def test_self_consistent_parent_framing_mapping_and_membership_drift_refused(self):
        for field,value in [('fileId','unknown'),('partition','final-test'),('chunkOrdinal',1),('inputIds',[0,8,2,99,2])]:
            rows=copy.deepcopy(self.rows);rows[0][field]=value
            with self.assertRaises(ValueError):validate_prepared(rows,self.maps,self.metadata,self.extracted,self.identity)
        maps=struct.pack('<II',0,8);rows=copy.deepcopy(self.rows)
        rows[0]['representationSha256']=digest(canonical([CHUNKING,[99],[{'startByte':0,'endByte':8}]]))
        with self.assertRaisesRegex(ValueError,'source mapping'):validate_prepared(rows,maps,self.metadata,self.extracted,self.identity)
        extracted=copy.deepcopy(self.extracted);extracted['fragments'].append({**extracted['fragments'][0],'fragmentId':'missing'})
        with self.assertRaisesRegex(ValueError,'membership'):validate_prepared(self.rows,self.maps,self.metadata,extracted,self.identity)

if __name__=='__main__':unittest.main()
