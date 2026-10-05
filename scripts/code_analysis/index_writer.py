"""Atomic immutable snapshot/index publication, exact retries and row-count fencing."""
import json
import struct
from .contracts import canonical,digest,sha
from .representations import vector_bytes
from .code_index import preflight,require_owned_transaction
from .index_runtime import verify_runtime

def prepare_payload(snapshot,identity,files,fragments,chunks):
    for field in ('snapshot_id','manifest_sha256','rights_sha256','split_sha256'):sha(snapshot[field])
    for field in ('index_key','model_key','gallery_sha256','representation_sha256'):sha(identity[field])
    if identity['partition'] not in ('train','validation','final-test'):raise ValueError('invalid index partition')
    if not 0<len(fragments)<=5000 or not 0<len(chunks)<=12000:raise ValueError('canonical fragment/chunk cap')
    registry={f['fileId']:f for f in files};units={f['fragmentId']:f for f in fragments}
    if len(registry)!=len(files) or len(units)!=len(fragments):raise ValueError('duplicate source/fragment identity')
    for f in fragments:
        for key in ('fragmentId','fileId','sourceSha256','sha256'):sha(f[key])
        parent=registry.get(f['fileId'])
        if not parent or parent['sha256']!=f['sourceSha256'] or parent.get('partition')!=identity['partition'] or parent.get('extraction')!='VALID_EXTRACTED' or not parent.get('assignmentReviewed') or not parent.get('permissions',{}).get('searchIndexAllowed'):raise ValueError('fragment lacks eligible extracted parent/partition')
        if not f.get('canonicalIndexed') or not f.get('searchable') or not f.get('executable'):raise ValueError('noncanonical/nonsearchable unit')
        a,b=f['startByte'],f['endByte']
        if type(a)!=int or type(b)!=int or not 0<=a<b<=parent['bytes']:raise ValueError('raw-bound source fragment span')
    ordered=sorted(chunks,key=lambda c:(c['fragmentId'],c['chunkOrdinal']))
    groups={};vectors=[]
    for c in ordered:
        f=units.get(c['fragmentId']);sha(c['representationSha256'])
        if not f:raise ValueError('chunk fragment missing')
        ordinal=c['chunkOrdinal'];a,b=c['tokenStart'],c['tokenEnd']
        if type(ordinal)!=int or not 0<=ordinal<16 or type(a)!=int or type(b)!=int or not 0<=a<b or b-a>252:raise ValueError('invalid ordinal/token span')
        groups.setdefault(f['fragmentId'],[]).append(ordinal)
        start,end=c.get('sourceStart'),c.get('sourceEnd')
        if (start is None)!=(end is None):raise ValueError('incomplete source mapping')
        if start is not None and (type(start)!=int or type(end)!=int or not f['startByte']<=start<end<=f['endByte']):raise ValueError('chunk mapping outside raw fragment')
        vector_bytes([c['vector']]);vectors.append(c['vector'])
    if set(groups)!=set(units) or any(v!=list(range(len(v))) for v in groups.values()):raise ValueError('missing/duplicate/noncontiguous chunk rows')
    canonical_fragments=sorted(fragments,key=lambda f:f['fragmentId'])
    return {'snapshot':snapshot,'identity':identity,'files':files,'fragments':canonical_fragments,'chunks':ordered,'expectedRows':len(ordered),'vectorSha256':digest(vector_bytes(vectors)),'payloadSha256':digest(canonical([snapshot,identity,canonical_fragments,ordered]))}

def _safe_mismatch_value(value):
    if value is None:
        return 'None'
    if isinstance(value, bool):
        return str(value)
    if isinstance(value, int):
        return str(value)
    if isinstance(value, str):
        lowered = value.lower()
        if any(token in lowered for token in ('password', 'postgres://', 'postgresql://', 'dsn=', 'secret', 'token=')):
            return '<redacted>'
        if len(value) > 400:
            return value[:64] + '...len=' + str(len(value))
        return value
    return type(value).__name__


def _field_mismatch(kind, field, identity, expected, actual):
    return (kind + ' ' + field + ' mismatch ' + identity + ' expected=' + _safe_mismatch_value(expected)
            + ' actual=' + _safe_mismatch_value(actual))


def _verify_rows(cursor,payload):
    key=payload['identity']['index_key'];snapshot=payload['snapshot']['snapshot_id']
    cursor.execute('SELECT fragment_id,chunk_ordinal,token_start,token_end,source_start,source_end,representation_sha256,embedding::text FROM code_lab.code_embeddings WHERE index_key=%s ORDER BY fragment_id,chunk_ordinal',(key,))
    rows=cursor.fetchall()
    if len(rows)!=payload['expectedRows']:raise ValueError('stored index row count drift')
    vectors=[]
    embed_fields=(('fragmentId',0),('chunkOrdinal',1),('tokenStart',2),('tokenEnd',3),('sourceStart',4),('sourceEnd',5),('representationSha256',6))
    for stored,expected in zip(rows,payload['chunks']):
        wanted=(expected['fragmentId'],expected['chunkOrdinal'],expected['tokenStart'],expected['tokenEnd'],expected.get('sourceStart'),expected.get('sourceEnd'),expected['representationSha256'])
        if tuple(stored[:7])!=wanted:
            identity='fragmentId='+str(expected['fragmentId'])+' chunkOrdinal='+str(expected['chunkOrdinal'])
            for name,index in embed_fields:
                actual=stored[index];value=wanted[index]
                if actual!=value:
                    raise ValueError(_field_mismatch('stored embedding', name, identity, value, actual))
            raise ValueError('stored immutable representation drift '+identity)
        vector=json.loads(stored[7]);vectors.append(vector)
    if digest(vector_bytes(vectors))!=payload['vectorSha256']:raise ValueError('stored full FP32 vector digest drift')
    fragment_fields=(('fileId',0),('sourceSha256',1),('sha256',2),('language',3),('dialect',4),('startByte',5),('endByte',6),('provenanceRef',7))
    for f in payload['fragments']:
        cursor.execute('SELECT file_id,source_sha256,fragment_sha256,language,dialect,start_byte,end_byte,provenance_ref FROM code_lab.code_fragments WHERE snapshot_id=%s AND fragment_id=%s',(snapshot,f['fragmentId']))
        stored=cursor.fetchone()
        wanted=(f['fileId'],f['sourceSha256'],f['sha256'],f['language'],f['dialect'],f['startByte'],f['endByte'],f['provenanceRef'])
        if stored!=wanted:
            identity='fragmentId='+f['fragmentId']
            if stored is None:
                raise ValueError('stored fragment missing '+identity)
            for name,index in fragment_fields:
                if stored[index]!=wanted[index]:
                    raise ValueError(_field_mismatch('stored fragment', name, identity, wanted[index], stored[index]))
            raise ValueError('stored fragment source identity/span drift '+identity)

def publish_index(connection,payload,*,runtime_root,runtime_review):
    """One transaction: other readers cannot observe partially built index.

    Failed attempts roll back all inserted state. Existing incomplete state is
    fenced for inspection rather than overwritten; exact READY retries verify bytes.
    Caller must pass current runtime gate before creating this native connection.
    """
    require_owned_transaction(connection)
    verify_runtime(runtime_root,runtime_review)
    # Revalidate mutable caller dictionaries before any DDL/DML use.
    verified=prepare_payload(payload['snapshot'],payload['identity'],payload['files'],payload['fragments'],payload['chunks'])
    if verified!=payload:raise ValueError('prepared immutable payload drift')
    preflight(connection);s=payload['snapshot'];i=payload['identity'];key=i['index_key']
    with connection.transaction():
        with connection.cursor() as c:
            lock=struct.unpack('!q',bytes.fromhex(key[:16]))[0]
            c.execute('SELECT pg_advisory_xact_lock(%s)',(lock,))
            c.execute('SELECT manifest_sha256,rights_sha256,split_sha256,state FROM code_lab.corpus_snapshots WHERE snapshot_id=%s FOR UPDATE',(s['snapshot_id'],))
            existing=c.fetchone();snapshot_values=(s['manifest_sha256'],s['rights_sha256'],s['split_sha256'])
            if existing and tuple(existing[:3])!=snapshot_values:raise ValueError('immutable snapshot identity conflict')
            if existing and existing[3] not in ('FROZEN','BUILDING'):raise ValueError('existing incomplete snapshot fenced')
            c.execute('SELECT snapshot_id,model_key,gallery_sha256,representation_sha256,partition,dimension,expected_rows,vector_sha256,state FROM code_lab.code_indexes WHERE index_key=%s FOR UPDATE',(key,))
            row=c.fetchone();expected=(s['snapshot_id'],i['model_key'],i['gallery_sha256'],i['representation_sha256'],i['partition'],768,payload['expectedRows'],payload['vectorSha256'])
            if row:
                if tuple(row[:8])!=expected:raise ValueError('existing index conflict/incomplete state fenced')
                if row[8]=='READY':
                    _verify_rows(c,payload)
                    return {'status':'VERIFIED_EXACT_RETRY','rows':payload['expectedRows']}
                if row[8]!='BUILDING':raise ValueError('existing index conflict/incomplete state fenced')
                c.execute('SELECT count(*) FROM code_lab.code_embeddings WHERE index_key=%s',(key,))
                inserted=c.fetchone()[0]
                remaining=payload['chunks'][inserted:]
                for row_chunk in remaining:
                    vector='['+','.join(format(v,'.9g') for v in row_chunk['vector'])+']'
                    c.execute('INSERT INTO code_lab.code_embeddings VALUES(%s,%s,%s,%s,%s,%s,%s,%s,%s,%s::vector)',(key,s['snapshot_id'],row_chunk['fragmentId'],row_chunk['chunkOrdinal'],row_chunk['tokenStart'],row_chunk['tokenEnd'],row_chunk.get('sourceStart'),row_chunk.get('sourceEnd'),row_chunk['representationSha256'],vector))
                _verify_rows(c,payload)
                c.execute("UPDATE code_lab.code_indexes SET state='READY' WHERE index_key=%s AND state='BUILDING'",(key,))
                if c.rowcount!=1:raise ValueError('index publication fence lost')
                return {'status':'RESUMED_VERIFIED_BATCHES','rows':payload['expectedRows'],'resumedFrom':inserted}
            if not existing:c.execute('INSERT INTO code_lab.corpus_snapshots VALUES(%s,%s,%s,%s,\'BUILDING\')',(s['snapshot_id'],*snapshot_values))
            for f in payload['fragments']:
                values=(s['snapshot_id'],f['fragmentId'],f['fileId'],f['sourceSha256'],f['sha256'],f['language'],f['dialect'],f['startByte'],f['endByte'],f['provenanceRef'])
                c.execute('SELECT snapshot_id,fragment_id,file_id,source_sha256,fragment_sha256,language,dialect,start_byte,end_byte,provenance_ref FROM code_lab.code_fragments WHERE snapshot_id=%s AND fragment_id=%s',(s['snapshot_id'],f['fragmentId']))
                stored=c.fetchone()
                if stored is None:c.execute('INSERT INTO code_lab.code_fragments VALUES(%s,%s,%s,%s,%s,%s,%s,%s,%s,%s)',values)
                elif tuple(stored)!=values:raise ValueError('immutable fragment conflict')
            c.execute('INSERT INTO code_lab.code_indexes VALUES(%s,%s,%s,%s,%s,%s,%s,%s,%s,\'BUILDING\')',(key,*expected))
            for row in payload['chunks']:
                vector='['+','.join(format(v,'.9g') for v in row['vector'])+']'
                c.execute('INSERT INTO code_lab.code_embeddings VALUES(%s,%s,%s,%s,%s,%s,%s,%s,%s,%s::vector)',(key,s['snapshot_id'],row['fragmentId'],row['chunkOrdinal'],row['tokenStart'],row['tokenEnd'],row.get('sourceStart'),row.get('sourceEnd'),row['representationSha256'],vector))
            _verify_rows(c,payload)
            c.execute("UPDATE code_lab.code_indexes SET state='READY' WHERE index_key=%s AND state='BUILDING'",(key,))
            if c.rowcount!=1:raise ValueError('index publication fence lost')
            if not existing:c.execute("UPDATE code_lab.corpus_snapshots SET state='FROZEN' WHERE snapshot_id=%s AND state='BUILDING'",(s['snapshot_id'],))
    return {'status':'PUBLISHED_COMPLETE','rows':payload['expectedRows']}
