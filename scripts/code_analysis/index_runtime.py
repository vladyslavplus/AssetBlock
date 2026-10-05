"""Current tracked scope and original installed wheel payload must authorize native use."""
import importlib.metadata
import json
import zipfile
from pathlib import Path
from .contracts import digest
from scripts.feasibility_pilot.paths import ensure_inside

SCOPE='artifacts/code_analysis/env/index'

def validate_entries(policy,approval,graph):
    expected=[]
    for row in approval['exceptions']:
        matches=[e for e in policy['exceptions'] if e.get('ecosystem')=='pypi' and e.get('name')==row['name'] and e.get('versions')==[row['version']] and e.get('scope')==SCOPE]
        if len(matches)!=1:raise ValueError('missing/ambiguous exact tracked index exception')
        entry=matches[0]
        for key in ('artifactSha256','noticeInventory','bootstrapOnly'):
            if entry.get(key)!=row[key]:raise ValueError('tracked artifact/notice/bootstrap scope drift')
        if entry.get('license')!=row['terms'] or entry.get('reviewedOn')!=row['reviewedOn'] or not entry.get('reviewer') or not entry.get('reason') or entry.get('redistributionAllowed') is not False:raise ValueError('incomplete tracked scoped terms review')
        if row.get('scope')!=SCOPE or row.get('status')!='REVIEWED':raise ValueError('unapproved artifact scope')
        expected.append({'name':row['name'],'version':row['version']})
    if len(expected)!=5 or sorted(graph,key=lambda x:x['name'])!=sorted(expected,key=lambda x:x['name']):raise ValueError('runtime and bootstrap installed graph drift')
    return True

def verify_runtime(root,review):
    root=ensure_inside(Path(root),Path(root));review=ensure_inside(Path(review),root/'artifacts/code_analysis')
    prefix=ensure_inside(root/SCOPE,root/'artifacts/code_analysis')
    site=ensure_inside(prefix/'Lib/site-packages',prefix)
    approval_path=ensure_inside(review/'root-approval.json',review);approval=json.loads(approval_path.read_bytes())
    policy_path=ensure_inside(root/'dependency-exceptions.json',root)
    policy=json.loads(policy_path.read_bytes())
    # Metadata enumeration must not traverse any linked distribution/package component.
    for member in site.rglob('*'):ensure_inside(member,site)
    graph=[{'name':d.metadata['Name'].lower().replace('_','-'),'version':d.version} for d in importlib.metadata.distributions(path=[str(site)])]
    validate_entries(policy,approval,graph)
    package_path=ensure_inside(review/'complete-package.json',review)
    supplement_path=ensure_inside(review/'scope-supplement.json',review)
    if digest(package_path.read_bytes())!=approval['packageSha256'] or digest(supplement_path.read_bytes())!=approval['supplementSha256']:raise ValueError('review package/supplement drift')
    checked=0
    for row in approval['exceptions']:
        entry=next(e for e in policy['exceptions'] if e.get('scope')==SCOPE and e.get('name')==row['name'])
        if entry.get('approvalEvidenceSha256')!=digest(approval_path.read_bytes()):raise ValueError('tracked approval binding drift')
        wheels=ensure_inside(review/'wheels',review)
        wheel=ensure_inside(next(wheels.glob(row['name'].replace('-','_')+'-'+row['version']+'-*.whl')),wheels)
        if digest(wheel.read_bytes())!=row['artifactSha256']:raise ValueError('reviewed wheel drift')
        with zipfile.ZipFile(wheel) as z:
            for member,expected_hash in row['noticeInventory'].items():
                if digest(z.read(member))!=expected_hash:raise ValueError('wheel notice drift')
            for info in z.infolist():
                if info.is_dir() or info.filename.endswith('.dist-info/RECORD'):continue
                if '.data/' in info.filename:raise ValueError('unreviewed wheel relocation')
                path=ensure_inside(site/info.filename,site)
                if digest(path.read_bytes())!=digest(z.read(info.filename)):raise ValueError('installed original payload drift')
                checked+=1
    return {'outcome':'CURRENT_TRACKED_SCOPE_GRAPH_PAYLOAD_VERIFIED','graph':graph,'payloadFiles':checked,'policySha256':digest(policy_path.read_bytes()),'approvalSha256':digest(approval_path.read_bytes()),'prefix':str(prefix),'initialInstallPrecededTrackedRecording':True}
