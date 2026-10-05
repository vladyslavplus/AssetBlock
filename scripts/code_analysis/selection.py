"""Deterministic bounded canonical units; complete extraction inventory remains intact."""
from .contracts import canonical,digest
from scripts.feasibility_pilot.extract import union_length

POLICY={'version':'fragment-selection-v1','maximumPerFile':5,'order':['startByte','endByte','fragmentId'],'callableFirst':True,'moduleFallbackOnlyWithoutCallables':True,'overlap':'exclude any overlap with already selected unit','canonicalFragmentCap':5000,'totalEmbeddingChunkCap':12000}

def select_fragments(extraction):
    fragments=extraction['fragments']
    eligible=[f for f in fragments if f['executable'] and f['searchable']]
    callables=[f for f in eligible if f['kind']!='module_statement']
    candidates=sorted(callables or eligible,key=lambda f:(f['startByte'],f['endByte'],f['fragmentId']))
    selected=[];reasons={}
    for f in candidates:
        if any(max(f['startByte'],p['startByte'])<min(f['endByte'],p['endByte']) for p in selected):
            reasons[f['fragmentId']]='NESTED_OVERLAP_SELECTION'
        elif len(selected)>=POLICY['maximumPerFile']:
            reasons[f['fragmentId']]='NON_INDEXED_CAP_SELECTION'
        else:selected.append(f)
    ids={f['fragmentId'] for f in selected}
    for f in fragments:
        f['canonicalIndexed']=f['fragmentId'] in ids
        if not f['canonicalIndexed']:
            f['nonIndexedReason']=reasons.get(f['fragmentId'],'MODULE_EXCLUDED_CALLABLE_SELECTION' if f in eligible else 'NOT_SEARCHABLE_EXECUTABLE')
    extraction['selectionIdentity']={**POLICY,'implementationSha256':digest(__import__('pathlib').Path(__file__).read_bytes())}
    extraction['selectionSha256']=digest(canonical(extraction['selectionIdentity']))
    extraction['indexedCoverage']={'indexedFragments':len(selected),'indexedUnionBytes':union_length([(f['startByte'],f['endByte']) for f in selected]),'nonIndexedUnits':[{'fragmentId':f['fragmentId'],'startByte':f['startByte'],'endByte':f['endByte'],'sha256':f['sha256'],'reason':f['nonIndexedReason']} for f in fragments if not f['canonicalIndexed']]}
    extraction['searchableExecutable']=bool(selected)
    return extraction

def enforce_fragment_cap(extractions):
    count=sum(r['indexedCoverage']['indexedFragments'] for r in extractions)
    if count>POLICY['canonicalFragmentCap']:raise ValueError('canonical searchable fragment cap exceeded')
    return count
