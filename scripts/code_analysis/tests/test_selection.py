import copy,unittest
from unittest.mock import patch
from scripts.code_analysis.selection import select_fragments,enforce_fragment_cap
from scripts.code_analysis.selection import POLICY

def fragment(i,start,end,kind='function_declaration'):
    return {'fragmentId':str(i),'startByte':start,'endByte':end,'kind':kind,'sha256':str(i),'executable':True,'searchable':True}

class SelectionTests(unittest.TestCase):
    def test_order_nested_cap_and_full_omissions(self):
        units=[fragment(1,0,20),fragment(2,2,5)]+[fragment(i,30+i*10,35+i*10) for i in range(3,9)]
        a=select_fragments({'fragments':copy.deepcopy(units)})
        b=select_fragments({'fragments':list(reversed(copy.deepcopy(units)))})
        self.assertEqual(sorted(f['fragmentId'] for f in a['fragments'] if f['canonicalIndexed']),sorted(f['fragmentId'] for f in b['fragments'] if f['canonicalIndexed']))
        self.assertEqual(a['indexedCoverage']['indexedFragments'],5)
        self.assertEqual(a['indexedCoverage']['indexedUnionBytes'],40)
        omitted={r['fragmentId']:r for r in a['indexedCoverage']['nonIndexedUnits']}
        self.assertEqual(omitted['2']['reason'],'NESTED_OVERLAP_SELECTION')
        self.assertEqual((omitted['2']['startByte'],omitted['2']['endByte'],omitted['2']['sha256']),(2,5,'2'))
        self.assertEqual(omitted['8']['reason'],'NON_INDEXED_CAP_SELECTION')
    def test_module_fallback_and_callable_precedence(self):
        a=select_fragments({'fragments':[fragment(1,0,8,'module_statement')]})
        self.assertEqual(a['indexedCoverage']['indexedFragments'],1)
        b=select_fragments({'fragments':[fragment(1,0,8,'module_statement'),fragment(2,20,30)]})
        self.assertFalse(b['fragments'][0]['canonicalIndexed'])
        self.assertEqual(b['fragments'][0]['nonIndexedReason'],'MODULE_EXCLUDED_CALLABLE_SELECTION')
        self.assertEqual(a['selectionSha256'],b['selectionSha256'])
    def test_hard_cap_no_truncation(self):
        row=select_fragments({'fragments':[fragment(i,i*10,i*10+5) for i in range(5)]})
        self.assertEqual(enforce_fragment_cap([row]*1000),5000)
        with self.assertRaises(ValueError):enforce_fragment_cap([row]*1001)
    def test_policy_identity_drift_changes_hash(self):
        before=select_fragments({'fragments':[fragment(1,0,5)]})['selectionSha256']
        with patch.dict(POLICY,maximumPerFile=4):after=select_fragments({'fragments':[fragment(1,0,5)]})['selectionSha256']
        self.assertNotEqual(before,after)
