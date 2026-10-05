import copy,tempfile,unittest
from pathlib import Path
from unittest.mock import patch
from scripts.code_analysis.index_runtime import validate_entries,verify_runtime
from scripts.feasibility_pilot.paths import ensure_inside,PathEscapeError
from scripts.code_analysis.corpus import ARTIFACT_ROOT

class RuntimeGateTests(unittest.TestCase):
    def setUp(self):
        rows=[{'name':name,'version':version,'scope':'artifacts/code_analysis/env/index','status':'REVIEWED','artifactSha256':'a'*64,'noticeInventory':{'LICENSE':'b'*64},'bootstrapOnly':name=='pip','terms':'reviewed complete terms','reviewedOn':'2026-10-05'} for name,version in [('psycopg','3.2.10'),('psycopg-binary','3.2.10'),('typing-extensions','4.15.0'),('tzdata','2025.2'),('pip','25.2')]]
        self.approval={'exceptions':rows}
        self.policy={'exceptions':[{'ecosystem':'pypi','name':r['name'],'versions':[r['version']],'scope':r['scope'],'artifactSha256':r['artifactSha256'],'noticeInventory':r['noticeInventory'],'bootstrapOnly':r['bootstrapOnly'],'license':r['terms'],'reviewedOn':r['reviewedOn'],'reviewer':'AI reviewer','reason':'exact isolated terms','redistributionAllowed':False} for r in rows]}
        self.graph=[{'name':r['name'],'version':r['version']} for r in self.approval['exceptions']]
    def test_actual_five_exact_entries(self):
        self.assertTrue(validate_entries(self.policy,self.approval,self.graph))
    def test_missing_changed_scope_version_notice_or_wheel_blocks_use(self):
        for key,value in [('scope','other-prefix'),('versions',['0']),('noticeInventory',{}),('artifactSha256','0'*64)]:
            policy=copy.deepcopy(self.policy)
            entry=next(e for e in policy['exceptions'] if e.get('name')=='psycopg' and e.get('scope')=='artifacts/code_analysis/env/index')
            entry[key]=value
            with self.subTest(key=key),self.assertRaises(ValueError):validate_entries(policy,self.approval,self.graph)
        policy=copy.deepcopy(self.policy);policy['exceptions']=[e for e in policy['exceptions'] if e.get('name')!='pip']
        with self.assertRaises(ValueError):validate_entries(policy,self.approval,self.graph)
    def test_bootstrap_cannot_be_omitted_or_runtime_graph_extended(self):
        with self.assertRaises(ValueError):validate_entries(self.policy,self.approval,[r for r in self.graph if r['name']!='pip'])
        with self.assertRaises(ValueError):validate_entries(self.policy,self.approval,self.graph+[{'name':'unreviewed','version':'1'}])

class RuntimePathTests(unittest.TestCase):
    def test_original_prefix_component_wheel_and_payload_reparse_attributes(self):
        with tempfile.TemporaryDirectory(dir=ARTIFACT_ROOT) as temporary:
            root=Path(temporary);review=root/'artifacts/code_analysis/review'
            prefix=root/'artifacts/code_analysis/env/index'
            cases=[prefix,prefix/'Lib',review/'wheels',review/'wheels/pip.whl',prefix/'Lib/site-packages/native.dll']
            for component in cases:
                with self.subTest(component=component),patch('scripts.feasibility_pilot.paths._is_reparse_point',side_effect=lambda p:p==component):
                    with self.assertRaises(PathEscapeError):ensure_inside(component,root)
            with patch('scripts.feasibility_pilot.paths._is_reparse_point',side_effect=lambda p:p==prefix):
                with self.assertRaises(PathEscapeError):verify_runtime(root,review)
    def test_native_inward_and_outward_links(self):
        with tempfile.TemporaryDirectory(dir=ARTIFACT_ROOT) as temporary:
            root=Path(temporary);inside=root/'inside';inside.mkdir()
            for name,target in [('inward',inside),('outward',root.parent)]:
                link=root/name
                try:link.symlink_to(target,target_is_directory=True)
                except OSError as exc:self.skipTest('native symlink creation unavailable: '+str(exc))
                with self.assertRaises(PathEscapeError):ensure_inside(link/'wheel.whl',root)
