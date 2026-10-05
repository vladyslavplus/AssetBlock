"""Dedicated pgvector target checks and exhaustive file aggregation; no product fallback."""

from pathlib import Path

from .contracts import canonical, digest, sha
from .representations import vector_bytes

MARKER = 'assetblock-code-index-sandbox-v1'
DATABASE = 'assetblock_code_lab'
ROLE = 'assetblock_code_index'
PORT = 55433


def require_owned_transaction(connection):
    if connection.autocommit is not True or connection.info.transaction_status != 0:
        raise ValueError('explicit autocommit=True and IDLE connection required; caller transaction preserved')


def preflight(connection, *, initialized=True):
    if connection.info.host not in ('127.0.0.1', 'localhost', '::1') or connection.info.port != PORT:
        raise ValueError('refusing unknown/nonloopback sandbox target')
    with connection.cursor() as cursor:
        cursor.execute('SELECT current_database(),current_user,current_setting(\'server_version_num\')')
        database, role, server = cursor.fetchone()
        if database != DATABASE or role != ROLE or not 160000 <= int(server) < 170000:
            raise ValueError('refusing product/unknown database, role or Postgres version')
        if initialized:
            cursor.execute('SELECT marker FROM code_lab.sandbox_marker')
            if cursor.fetchall() != [(MARKER,)]: raise ValueError('sandbox marker mismatch')
            cursor.execute("SELECT extversion FROM pg_extension WHERE extname='vector'")
            if cursor.fetchone() != ('0.8.6',): raise ValueError('pgvector version mismatch')


def initialize(connection, approval):
    ddl = Path(__file__).parent / 'config/index-schema.sql'
    if approval.get('status') != 'REVIEWED' or approval.get('ddlSha256') != digest(ddl.read_bytes()) or approval.get('database') != DATABASE or approval.get('role') != ROLE or approval.get('port') != PORT or not approval.get('reviewer'):
        raise ValueError('exact sandbox DDL/target approval required')
    require_owned_transaction(connection)
    preflight(connection, initialized=False)
    with connection.transaction():
        with connection.cursor() as cursor:
            cursor.execute(ddl.read_text(encoding='utf-8'))
    preflight(connection)


def exact_file_query(connection, index_key, vector, gallery_ids, language, *, partition, limit=40):
    if partition not in ('train', 'validation'):
        raise ValueError('final-test scoring prohibited in this phase')
    sha(index_key)
    vector_bytes([vector])
    if not gallery_ids or len(set(gallery_ids)) != len(gallery_ids): raise ValueError('nonempty unique gallery required')
    for file_id in gallery_ids: sha(file_id)
    if type(limit) is not int or not 1 <= limit <= 40: raise ValueError('invalid file candidate budget')
    preflight(connection)
    text = '[' + ','.join(format(x, '.9g') for x in vector) + ']'
    # Aggregate all allowlisted chunks before file LIMIT; chunk top-K would not be exact.
    query = '''SELECT f.file_id, min(e.embedding <=> %s::vector) AS distance
               FROM code_lab.code_embeddings e
               JOIN code_lab.code_fragments f ON f.snapshot_id=e.snapshot_id AND f.fragment_id=e.fragment_id
               JOIN code_lab.code_indexes i ON i.index_key=e.index_key AND i.snapshot_id=e.snapshot_id
               WHERE i.index_key=%s AND i.state='READY' AND i.partition=%s
                 AND f.language=%s AND f.file_id=ANY(%s)
               GROUP BY f.file_id ORDER BY distance,f.file_id LIMIT %s'''
    with connection.cursor() as cursor:
        cursor.execute('SELECT state,partition FROM code_lab.code_indexes WHERE index_key=%s', (index_key,))
        if cursor.fetchone() != ('READY', partition): raise ValueError('index is incomplete or belongs to another partition')
        cursor.execute(query, (text, index_key, partition, language, gallery_ids, limit))
        return [{'fileId': file_id, 'distance': float(distance)} for file_id, distance in cursor.fetchall()]


def model_key(identity):
    required = {'checkpointSha256','tokenizerHashes','framing','pooling','mask','preprocessing','chunking','dimension','selectionSha256'}
    if not required <= identity.keys() or identity['dimension'] != 768: raise ValueError('incomplete model identity')
    sha(identity['checkpointSha256'])
    return digest(canonical(identity))


def index_key(model, snapshot, gallery, extraction):
    for item in (model,snapshot,gallery,extraction): sha(item)
    return digest(canonical([model,snapshot,gallery,extraction]))


def exact_query(connection,index,vectors,gallery_ids,language,*,partition,limit=40):
    """Exhaustive query-chunk × gallery-chunk maximum, with exact winning spans."""
    if partition not in ('train','validation'):raise ValueError('final-test scoring prohibited in this phase')
    sha(index);vector_bytes(vectors)
    if not 0<len(vectors)<=80 or not 0<len(gallery_ids)<=1000 or len(set(gallery_ids))!=len(gallery_ids):raise ValueError('bounded unique full gallery/query required')
    for file_id in gallery_ids:sha(file_id)
    if type(limit)!=int or not 1<=limit<=40:raise ValueError('file candidate budget invalid')
    preflight(connection)
    with connection.cursor() as cursor:
        cursor.execute('SELECT state,partition,expected_rows FROM code_lab.code_indexes WHERE index_key=%s',(index,));row=cursor.fetchone()
        if not row or row[:2]!=('READY',partition):raise ValueError('incomplete/wrong-partition index')
        cursor.execute('SELECT count(*) FROM code_lab.code_embeddings WHERE index_key=%s',(index,))
        if cursor.fetchone()[0]!=row[2]:raise ValueError('published index row count drift')
        cursor.execute('SELECT DISTINCT f.file_id FROM code_lab.code_fragments f JOIN code_lab.code_embeddings e ON e.snapshot_id=f.snapshot_id AND e.fragment_id=f.fragment_id WHERE e.index_key=%s AND f.language=%s AND f.file_id=ANY(%s)',(index,language,gallery_ids))
        if {r[0] for r in cursor.fetchall()}!=set(gallery_ids):raise ValueError('full gallery missing indexed eligible source')
        parameters=[]
        for ordinal,vector in enumerate(vectors):parameters.extend((ordinal,'['+','.join(format(x,'.9g') for x in vector)+']'))
        values=','.join(['(%s,%s::vector)']*len(vectors))
        sql='''WITH query_vectors(ordinal,embedding) AS (VALUES '''+values+'''),
          scored AS (SELECT f.file_id,f.fragment_id,e.chunk_ordinal,q.ordinal AS query_ordinal,
            e.source_start,e.source_end,e.token_start,e.token_end,
            e.embedding <=> q.embedding AS distance,
            row_number() OVER(PARTITION BY f.file_id ORDER BY e.embedding <=> q.embedding,q.ordinal,f.fragment_id,e.chunk_ordinal) AS rank
            FROM code_lab.code_embeddings e JOIN code_lab.code_fragments f
              ON f.snapshot_id=e.snapshot_id AND f.fragment_id=e.fragment_id
            CROSS JOIN query_vectors q
            WHERE e.index_key=%s AND f.language=%s AND f.file_id=ANY(%s))
          SELECT file_id,fragment_id,chunk_ordinal,query_ordinal,source_start,source_end,token_start,token_end,distance
          FROM scored WHERE rank=1 ORDER BY distance,file_id LIMIT %s'''
        cursor.execute(sql,(*parameters,index,language,gallery_ids,limit))
        keys=('fileId','fragmentId','chunkOrdinal','queryChunkOrdinal','sourceStart','sourceEnd','tokenStart','tokenEnd','distance')
        return [dict(zip(keys,r)) for r in cursor.fetchall()]
