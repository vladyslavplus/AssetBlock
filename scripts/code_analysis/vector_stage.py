"""Bounded canonical window construction and frozen full-vector artifact stage."""
import json,struct,time
from pathlib import Path
from scripts.feasibility_pilot.paths import ensure_inside
from .contracts import canonical,digest,report
from .corpus import ARTIFACT_ROOT,write_once
from .frozen_encoder import file_hash,load_frozen,encode_chunks
from .representations import mapped_chunks,vector_bytes,CHUNKING
from .code_index import model_key,index_key

TOKENIZER_FILES=('config.json','vocab.json','merges.txt','tokenizer_config.json','special_tokens_map.json')

def tokenizer_identity(tokenizer,file_hashes,selection_sha):
    import transformers
    if set(file_hashes)!=set(TOKENIZER_FILES):raise ValueError('complete tokenizer file identity required')
    return {'fileHashes':file_hashes,'class':type(tokenizer).__name__,'transformersVersion':transformers.__version__,'framingIds':[tokenizer.cls_token_id,tokenizer.convert_tokens_to_ids('<encoder-only>'),tokenizer.sep_token_id],'selectionSha256':selection_sha,'selectionImplementationSha256':digest(Path(__file__).with_name('selection.py').read_bytes()),'representationImplementationSha256':digest(Path(__file__).with_name('representations.py').read_bytes()),'chunking':CHUNKING}

def validate_prepared(rows,maps,metadata,extracted,identity):
    if metadata.get('tokenizerIdentity')!=identity:raise ValueError('tokenizer/selection identity changed or absent; prepare a new immutable run')
    fragments={f['fragmentId']:f for f in extracted['fragments']};files={f['fileId']:f for f in extracted['files']};seen={}
    if not 0<len(rows)<=12000 or len(rows)!=metadata['chunks']:raise ValueError('prepared chunk cap/count drift')
    for row in rows:
        fragment=fragments.get(row['fragmentId']);file=files.get(row['fileId'])
        if not fragment or not file or fragment['fileId']!=file['fileId'] or not fragment.get('canonicalIndexed') or row['partition']!=file['partition'] or row['language']!=file['language']:raise ValueError('prepared canonical parent/partition drift')
        seen.setdefault(fragment['fragmentId'],[]).append(row['chunkOrdinal'])
        ids=row['inputIds'];framing=identity['framingIds']
        if not isinstance(ids,list) or not 4<len(ids)<=256 or any(type(x)!=int or x<0 for x in ids) or ids[:3]!=framing or ids[-1]!=framing[-1] or row['totalInputTokens']!=len(ids) or row['chunking']!=CHUNKING:raise ValueError('prepared framing/token identity drift')
        offset,length=row['sourceMappingByteOffset'],row['sourceMappingByteLength']
        if type(offset)!=int or type(length)!=int or offset<0 or length<0 or offset+length>len(maps) or length%8:raise ValueError('invalid mapping payload bounds')
        spans=[{'startByte':a,'endByte':b} for a,b in struct.iter_unpack('<II',maps[offset:offset+length])] if length else None
        if spans and (len(spans)!=len(ids)-4 or any(not fragment['startByte']<=s['startByte']<s['endByte']<=fragment['endByte'] for s in spans)):raise ValueError('prepared source mapping drift')
        if digest(canonical([CHUNKING,ids[3:-1],spans]))!=row['representationSha256']:raise ValueError('representation/token mapping identity drift')
    if set(seen)!=set(fragments) or any(values!=list(range(len(values))) or len(values)>16 for values in seen.values()):raise ValueError('prepared fragment membership/ordinal drift')

def prepare_windows(root,extracted,tokenizer,*,identity):
    files={f['fileId']:f for f in extracted['files']};rows=[];coverage=[];mapping=bytearray();raw_cache={}
    for fragment in extracted['fragments']:
        file=files[fragment['fileId']]
        if not fragment.get('canonicalIndexed') or not file.get('assignmentReviewed') or not file['permissions']['searchIndexAllowed']:raise ValueError('noneligible canonical unit')
        if file['fileId'] not in raw_cache:
            raw=ensure_inside(root/'sources'/file['sourceId']/file['path'],ARTIFACT_ROOT).read_bytes()
            if digest(raw)!=file['sha256']:raise ValueError('whole source hash drift before representation')
            raw_cache[file['fileId']]=raw
        raw=raw_cache[file['fileId']]
        result=mapped_chunks(raw,fragment,tokenizer);coverage.append({k:v for k,v in result.items() if k!='chunks'})
        for chunk in result['chunks']:
            spans=chunk.pop('sourceTokenSpans');offset=len(mapping)
            if spans is not None:
                for span in spans:mapping.extend(struct.pack('<II',span['startByte'],span['endByte']))
            chunk.update(fileId=file['fileId'],language=file['language'],dialect=file.get('dialect',file['language']),partition=file['partition'],sourceMappingByteOffset=offset,sourceMappingByteLength=len(mapping)-offset,sourceStart=min(s['startByte'] for s in spans) if spans else None,sourceEnd=max(s['endByte'] for s in spans) if spans else None)
            rows.append(chunk)
        if len(rows)>12000:
            write_once(root/'windows-cap-gap.json',canonical(report({'actualOutcome':'CHANGES_NEEDED','observedChunks':len(rows),'hardCap':12000,'inferenceExecuted':False,'noSilentGlobalTruncation':True})),ARTIFACT_ROOT)
            raise ValueError('global embedding chunk cap exceeded before inference')
    data=b''.join(canonical(row) for row in rows)
    write_once(root/'representations/chunks.jsonl',data,ARTIFACT_ROOT)
    write_once(root/'representations/source-mapping.u32le',bytes(mapping),ARTIFACT_ROOT)
    metadata=report({'tokenizerIdentity':identity,'chunks':len(rows),'canonicalFragments':len(extracted['fragments']),'chunking':CHUNKING,'chunkRowsSha256':digest(data),'sourceMappingSha256':digest(mapping),'sourceMappingBytes':len(mapping),'byteOrder':'little-endian uint32 start/end pair per payload token','fragmentCoverage':coverage,'extractionSha256':extracted['extractionSha256'],'selectionSha256':next(r['selectionSha256'] for r in extracted['fileResults'] if 'selectionSha256' in r),'finalTestScored':False})
    write_once(root/'representations/manifest.json',canonical(metadata),ARTIFACT_ROOT)
    return rows,metadata

def frozen_stage(root,model_dir,cache_root,expected_model,*,device='cpu',batch_size=1):
    if batch_size!=1:raise ValueError('stage microbatch1 required; measured safe2 needs separately reviewed configuration')
    extracted=json.loads((root/'extract.json').read_bytes());record=json.loads((root/'extract-stage.json').read_bytes())
    if record['ActualOutcome']!='SUCCEEDED' or digest(canonical(extracted))!=record['outputs']['extract.json']:raise ValueError('extraction evidence drift')
    started=time.perf_counter()
    from transformers import RobertaTokenizerFast
    checked=ensure_inside(Path(model_dir),Path(cache_root))
    for name in TOKENIZER_FILES:
        if file_hash(ensure_inside(checked/name,checked))!=expected_model[name]:raise ValueError('pinned tokenizer drift')
    tokenizer=RobertaTokenizerFast.from_pretrained(str(checked),local_files_only=True,trust_remote_code=False)
    identity=tokenizer_identity(tokenizer,{name:file_hash(checked/name) for name in TOKENIZER_FILES},next(r['selectionSha256'] for r in extracted['fileResults'] if 'selectionSha256' in r))
    if (root/'representations/manifest.json').exists():
        metadata=json.loads(ensure_inside(root/'representations/manifest.json',ARTIFACT_ROOT).read_bytes())
        data=ensure_inside(root/'representations/chunks.jsonl',ARTIFACT_ROOT).read_bytes()
        maps=ensure_inside(root/'representations/source-mapping.u32le',ARTIFACT_ROOT).read_bytes()
        if digest(data)!=metadata['chunkRowsSha256'] or digest(maps)!=metadata['sourceMappingSha256'] or metadata['extractionSha256']!=extracted['extractionSha256']:raise ValueError('prepared representation drift')
        rows=[json.loads(line) for line in data.splitlines()]
        validate_prepared(rows,maps,metadata,extracted,identity)
    else:rows,metadata=prepare_windows(root,extracted,tokenizer,identity=identity)
    tokenizer,model,loaded=load_frozen(model_dir,cache_root,expected_model,device=device)
    startup=time.perf_counter()-started
    identity={**loaded,'framing':{'tokens':[tokenizer.cls_token_id,tokenizer.convert_tokens_to_ids('<encoder-only>'),tokenizer.sep_token_id],'total256':True,'serviceTokens':4},'pooling':'masked mean all nonpadding including framing then L2 FP32','mask':'bidirectional encoder attention, padding excluded','preprocessing':'raw UTF-8 source fragment, no identifier/comment stripping','chunking':CHUNKING,'selectionSha256':metadata['selectionSha256'],'extractionSha256':extracted['extractionSha256'],'computePrecision':'float32'}
    # Device is resource provenance, not an embedding-space configuration.
    identity.pop('device');key=model_key(identity)
    _,warmup=encode_chunks(model,tokenizer,rows[:1],batch_size=1)
    vectors,evidence=encode_chunks(model,tokenizer,rows,batch_size=1)
    if evidence['loadedStateBeforeSha256']!=loaded['loadedFullStateSha256']:raise ValueError('state drift since actual verified checkpoint load')
    raw=vector_bytes(vectors);write_once(root/'representations/frozen-vectors.fp32le',raw,ARTIFACT_ROOT)
    manifests=[]
    for partition in ('train','validation','final-test'):
        gallery=sorted(f['fileId'] for f in extracted['files'] if f['partition']==partition and f['searchableExecutable'] and f['permissions']['searchIndexAllowed'] and not f.get('generated'))
        gallery_hash=digest(canonical(gallery));snapshot=json.loads((root/'intake.json').read_bytes())['snapshotId']
        manifests.append({'partition':partition,'galleryFileIds':gallery,'gallerySha256':gallery_hash,'indexKey':index_key(key,snapshot,gallery_hash,extracted['extractionSha256']),'rowOrdinals':[i for i,r in enumerate(rows) if r['partition']==partition]})
    result=report({'modelIdentity':identity,'modelKey':key,'startupSeconds':startup,'warmup':warmup,'frozenEvidence':evidence,'partitions':manifests,'representationManifestSha256':digest(canonical(metadata)),'fullVectorSha256':digest(raw),'device':device,'microBatch':1,'finalTestScored':False,'noOptimizerCreated':True})
    write_once(root/'frozen.json',canonical(result),ARTIFACT_ROOT)
    return result
