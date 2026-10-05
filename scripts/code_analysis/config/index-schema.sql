-- Explicit setup for the dedicated experimental database only. Never called by inference.
DO $$ BEGIN
  IF current_database() <> 'assetblock_code_lab' OR current_user <> 'assetblock_code_index' THEN
    RAISE EXCEPTION 'refusing unknown/product database or role';
  END IF;
END $$;
CREATE EXTENSION vector VERSION '0.8.6';
CREATE SCHEMA code_lab;
CREATE TABLE code_lab.sandbox_marker (
  marker text PRIMARY KEY CHECK (marker = 'assetblock-code-index-sandbox-v1')
);
INSERT INTO code_lab.sandbox_marker VALUES ('assetblock-code-index-sandbox-v1');
CREATE TABLE code_lab.corpus_snapshots (
  snapshot_id text PRIMARY KEY CHECK (snapshot_id ~ '^[0-9a-f]{64}$'),
  manifest_sha256 text NOT NULL CHECK (manifest_sha256 ~ '^[0-9a-f]{64}$'),
  rights_sha256 text NOT NULL CHECK (rights_sha256 ~ '^[0-9a-f]{64}$'),
  split_sha256 text NOT NULL CHECK (split_sha256 ~ '^[0-9a-f]{64}$'),
  state text NOT NULL CHECK (state IN ('BUILDING','FROZEN'))
);
CREATE TABLE code_lab.code_fragments (
  snapshot_id text NOT NULL REFERENCES code_lab.corpus_snapshots(snapshot_id),
  fragment_id text NOT NULL CHECK (fragment_id ~ '^[0-9a-f]{64}$'),
  file_id text NOT NULL CHECK (file_id ~ '^[0-9a-f]{64}$'),
  source_sha256 text NOT NULL CHECK (source_sha256 ~ '^[0-9a-f]{64}$'),
  fragment_sha256 text NOT NULL CHECK (fragment_sha256 ~ '^[0-9a-f]{64}$'),
  language text NOT NULL CHECK (language IN ('javascript','typescript','python','java','csharp')),
  dialect text NOT NULL,
  start_byte integer NOT NULL CHECK (start_byte >= 0),
  end_byte integer NOT NULL CHECK (end_byte > start_byte),
  provenance_ref text NOT NULL,
  PRIMARY KEY (snapshot_id,fragment_id)
);
CREATE TABLE code_lab.code_indexes (
  index_key text PRIMARY KEY CHECK (index_key ~ '^[0-9a-f]{64}$'),
  snapshot_id text NOT NULL REFERENCES code_lab.corpus_snapshots(snapshot_id),
  model_key text NOT NULL CHECK (model_key ~ '^[0-9a-f]{64}$'),
  gallery_sha256 text NOT NULL CHECK (gallery_sha256 ~ '^[0-9a-f]{64}$'),
  representation_sha256 text NOT NULL CHECK (representation_sha256 ~ '^[0-9a-f]{64}$'),
  partition text NOT NULL CHECK (partition IN ('train','validation','final-test')),
  dimension integer NOT NULL CHECK (dimension = 768),
  expected_rows integer NOT NULL CHECK (expected_rows > 0 AND expected_rows <= 12000),
  vector_sha256 text NOT NULL CHECK (vector_sha256 ~ '^[0-9a-f]{64}$'),
  state text NOT NULL CHECK (state IN ('BUILDING','READY','FAILED')),
  UNIQUE (index_key,snapshot_id)
);
CREATE TABLE code_lab.code_embeddings (
  index_key text NOT NULL,
  snapshot_id text NOT NULL,
  fragment_id text NOT NULL,
  chunk_ordinal integer NOT NULL CHECK (chunk_ordinal >= 0 AND chunk_ordinal < 16),
  token_start integer NOT NULL CHECK (token_start >= 0),
  token_end integer NOT NULL CHECK (token_end > token_start),
  source_start integer CHECK (source_start >= 0),
  source_end integer,
  representation_sha256 text NOT NULL CHECK (representation_sha256 ~ '^[0-9a-f]{64}$'),
  embedding vector(768) NOT NULL CHECK (vector_dims(embedding)=768 AND vector_norm(embedding) BETWEEN 0.99999 AND 1.00001),
  CHECK ((source_start IS NULL AND source_end IS NULL) OR (source_start IS NOT NULL AND source_end IS NOT NULL AND source_end > source_start)),
  PRIMARY KEY (index_key,fragment_id,chunk_ordinal),
  FOREIGN KEY (snapshot_id,fragment_id) REFERENCES code_lab.code_fragments(snapshot_id,fragment_id),
  FOREIGN KEY (index_key,snapshot_id) REFERENCES code_lab.code_indexes(index_key,snapshot_id)
);
CREATE INDEX code_fragment_file_filter ON code_lab.code_fragments(snapshot_id,language,dialect,file_id);
CREATE INDEX code_index_identity_filter ON code_lab.code_indexes(snapshot_id,model_key,partition,state);
