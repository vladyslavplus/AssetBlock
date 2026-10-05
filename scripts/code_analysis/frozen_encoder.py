"""Local safetensors-only frozen encoder; full state and FP32 output identities."""
import hashlib
import time
from pathlib import Path
from scripts.feasibility_pilot.paths import ensure_inside
from .contracts import canonical, digest, sha
from .representations import vector_bytes

def file_hash(path):
    h=hashlib.sha256()
    with path.open('rb') as stream:
        for block in iter(lambda:stream.read(1024*1024),b''):h.update(block)
    return h.hexdigest()

def state_hash(model):
    h=hashlib.sha256()
    for name,tensor in sorted(model.state_dict().items()):
        h.update(canonical([name,str(tensor.dtype),list(tensor.shape)]))
        h.update(tensor.detach().cpu().contiguous().reshape(-1).view(__import__('torch').uint8).numpy().tobytes())
    return h.hexdigest()

def load_frozen(model_dir,cache_root,expected_files,*,device='cpu'):
    model_dir=ensure_inside(Path(model_dir),Path(cache_root))
    required={'model.safetensors','config.json','vocab.json','merges.txt','tokenizer_config.json','special_tokens_map.json'}
    if not required<=expected_files.keys():raise ValueError('complete pinned safetensors/tokenizer identity required')
    for name,expected in expected_files.items():
        sha(expected);path=ensure_inside(model_dir/name,model_dir)
        if file_hash(path)!=expected:raise ValueError('model/tokenizer file drift')
    import torch
    from transformers import RobertaModel,RobertaTokenizerFast
    tokenizer=RobertaTokenizerFast.from_pretrained(str(model_dir),local_files_only=True,trust_remote_code=False)
    model=RobertaModel.from_pretrained(str(model_dir),local_files_only=True,trust_remote_code=False,use_safetensors=True).to(device).eval()
    if model.config.hidden_size!=768:raise ValueError('loaded encoder dimension drift')
    if model.config.is_decoder:raise ValueError('bidirectional encoder configuration required')
    for param in model.parameters():param.requires_grad_(False)
    return tokenizer,model,{'checkpointSha256':expected_files['model.safetensors'],'tokenizerHashes':{k:v for k,v in expected_files.items() if k!='model.safetensors'},'loadedFullStateSha256':state_hash(model),'dimension':768,'device':str(device),'safetensorsOnly':True,'trainableParameters':0}

def encode_chunks(model,tokenizer,chunks,*,batch_size=8):
    if not 0<len(chunks)<=12000 or type(batch_size)!=int or not 1<=batch_size<=8:raise ValueError('bounded frozen inference required')
    import torch
    if model.training or any(p.requires_grad for p in model.parameters()):raise ValueError('encoder must be fully frozen/eval')
    before=state_hash(model);output=[];device=next(model.parameters()).device;timings=[]
    if device.type=='cuda':torch.cuda.reset_peak_memory_stats(device)
    with torch.inference_mode():
        for start in range(0,len(chunks),batch_size):
            if device.type=='cuda':torch.cuda.synchronize(device)
            started=time.perf_counter()
            batch=chunks[start:start+batch_size]
            if any(not 4<len(c['inputIds'])<=256 or c.get('framingTokens')!=4 for c in batch):raise ValueError('frozen framing/token bound drift')
            length=max(len(c['inputIds']) for c in batch)
            ids=torch.tensor([c['inputIds']+[tokenizer.pad_token_id]*(length-len(c['inputIds'])) for c in batch],dtype=torch.long,device=device)
            mask=torch.tensor([[1]*len(c['inputIds'])+[0]*(length-len(c['inputIds'])) for c in batch],device=device)
            hidden=model(input_ids=ids,attention_mask=mask).last_hidden_state.float()
            pooled=(hidden*mask.unsqueeze(-1)).sum(1)/mask.sum(1).unsqueeze(-1)
            vectors=torch.nn.functional.normalize(pooled,p=2,dim=-1).cpu().tolist()
            vector_bytes(vectors);output.extend(vectors)
            if device.type=='cuda':torch.cuda.synchronize(device)
            timings.append(time.perf_counter()-started)
    after=state_hash(model)
    if before!=after:raise ValueError('frozen full-state mutation')
    raw=vector_bytes(output)
    return output,{'loadedStateBeforeSha256':before,'loadedStateAfterSha256':after,'fullStateUnchanged':True,'rows':len(output),'dimension':768,'vectorSha256':digest(raw),'vectorBytes':len(raw),'pooling':'mean over all nonpadding tokens including four framing tokens, FP32 normalized','batchSize':batch_size,'batchLatencySeconds':timings,'cudaSynchronized':device.type=='cuda','peakAllocatedVRAM':torch.cuda.max_memory_allocated(device) if device.type=='cuda' else 0,'peakReservedVRAM':torch.cuda.max_memory_reserved(device) if device.type=='cuda' else 0}


def independently_accepted(drafts):
    if not isinstance(drafts, dict):
        return False
    return (drafts.get("reviewerApproval") is True and bool(drafts.get("reviewer"))
            and drafts.get("status") in {"REVIEWED", "ACCEPTED", "INDEPENDENTLY_ACCEPTED"})


QUERY_BINDING_FIELDS = ("derivationId", "outputSha256", "parentFileId", "parentSha256",
                        "transformation", "partition", "language")


def accepted_query_bindings(accepted_records):
    bound = {}
    for row in accepted_records or []:
        if not independently_accepted(row):
            raise ValueError("query-only embeddings require independently accepted records; encoder not invoked")
        missing = [name for name in QUERY_BINDING_FIELDS if not row.get(name)]
        if missing:
            raise ValueError("accepted query record missing bindings; encoder not invoked")
        derivation_id = row["derivationId"]
        sha(derivation_id)
        sha(row["outputSha256"])
        sha(row["parentFileId"])
        sha(row["parentSha256"])
        if derivation_id in bound:
            raise ValueError("accepted query record identity duplicated; encoder not invoked")
        bound[derivation_id] = row
    if not bound:
        raise ValueError("query-only embeddings require accepted records; encoder not invoked")
    return bound


def require_accepted_query_sources(bindings, sources):
    if not isinstance(sources, dict) or not sources:
        raise ValueError("query-only embeddings require bound source bytes; encoder not invoked")
    extra = set(sources) - set(bindings)
    missing = set(bindings) - set(sources)
    if extra or missing:
        raise ValueError("accepted query source membership drift; encoder not invoked")
    for derivation_id, raw in sources.items():
        if not isinstance(raw, (bytes, bytearray)):
            raise ValueError("accepted query source bytes missing; encoder not invoked")
        if digest(raw) != bindings[derivation_id]["outputSha256"]:
            raise ValueError("accepted query source bytes drifted; encoder not invoked")


def require_chunk_query_binding(chunk, record):
    if chunk.get("galleryRow"):
        raise ValueError("query-only encoder refuses gallery rows")
    if chunk.get("queryId") != record["derivationId"]:
        raise ValueError("unaccepted query; encoder not invoked")
    if chunk.get("stratum") != record["transformation"]:
        raise ValueError("query stratum does not match accepted record; encoder not invoked")
    if chunk.get("partition") != record["partition"]:
        raise ValueError("query partition does not match accepted record; encoder not invoked")
    if chunk.get("language") != record["language"]:
        raise ValueError("query language does not match accepted record; encoder not invoked")
    if chunk.get("parentFileId") != record["parentFileId"]:
        raise ValueError("query parent does not match accepted record; encoder not invoked")
    digest_fields = {chunk.get("querySha256"), chunk.get("sourceSha256")} - {None}
    if not digest_fields or digest_fields != {record["outputSha256"]}:
        raise ValueError("query source digest does not match accepted record; encoder not invoked")
    if chunk.get("parentSha256") not in (None, record["parentSha256"]):
        raise ValueError("query parent hash does not match accepted record; encoder not invoked")


def encode_query_only(model, tokenizer, chunks, *, accepted_drafts, accepted_records, sources, batch_size=1):
    if not independently_accepted(accepted_drafts):
        raise ValueError("query-only embeddings require independently accepted drafts; encoder not invoked")
    if batch_size != 1:
        raise ValueError("query-only microbatch1 required")
    bindings = accepted_query_bindings(accepted_records)
    require_accepted_query_sources(bindings, sources)
    if not chunks:
        raise ValueError("query-only embeddings require query chunks; encoder not invoked")
    for chunk in chunks:
        record = bindings.get(chunk.get("queryId"))
        if record is None:
            raise ValueError("unaccepted query; encoder not invoked")
        require_chunk_query_binding(chunk, record)
    return encode_chunks(model, tokenizer, chunks, batch_size=1)


def confirm_query_embedding_bindings(queries, *, accepted_records, sources):
    bindings = accepted_query_bindings(accepted_records)
    extra_sources = set(sources) - set(bindings)
    if extra_sources:
        raise ValueError("accepted query source membership drift; encoder not invoked")
    used = {}
    for query in queries:
        record = bindings.get(query.get("queryId"))
        if record is None:
            raise ValueError("unaccepted query; encoder not invoked")
        raw = sources.get(record["derivationId"])
        require_accepted_query_sources({record["derivationId"]: record}, {record["derivationId"]: raw})
        require_chunk_query_binding({
            "queryId": query.get("queryId"),
            "stratum": query.get("stratum"),
            "partition": query.get("partition"),
            "language": query.get("language"),
            "parentFileId": query.get("parentFileId"),
            "parentSha256": query.get("parentSha256"),
            "querySha256": query.get("querySha256"),
            "sourceSha256": query.get("querySha256"),
            "galleryRow": False,
        }, record)
        used[record["derivationId"]] = record
    unused = set(bindings) - set(used)
    if unused:
        raise ValueError("accepted query unused during confirmed reuse")
    return used
