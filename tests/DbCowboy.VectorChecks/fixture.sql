-- Fixture for tests/DbCowboy.VectorChecks and tests/DbCowboy.ConnectionChecks.
-- Shape: words (labels, definitions, tags) and word_embeddings keyed by word id, model, revision,
-- pipeline and sense, with a declared FK to words and 384-dimension vectors in several embedding spaces.
CREATE EXTENSION IF NOT EXISTS vector;
SELECT setseed(0.42);
CREATE TABLE words (
    id uuid PRIMARY KEY,
    word text NOT NULL,
    definition text,
    parts_of_speech text[],
    tags text[]
);
INSERT INTO words
SELECT md5('word' || g)::uuid, 'word' || lpad(g::text, 4, '0') || (ARRAY['alpha','beta','gamma','delta'])[1 + g % 4],
       'Definition of word number ' || g, ARRAY[(ARRAY['noun','verb','adjective'])[1 + g % 3]], ARRAY['tag' || (g % 7), 'set' || (g % 3)]
FROM generate_series(1, 1500) g;

CREATE TABLE word_embeddings (
    id uuid NOT NULL REFERENCES words(id),
    model text NOT NULL,
    model_revision text NOT NULL,
    pipeline text NOT NULL,
    sense_index int NOT NULL DEFAULT 0,
    sense_text text,
    truncated boolean NOT NULL DEFAULT false,
    embedding vector(384),
    PRIMARY KEY (id, model, model_revision, pipeline, sense_index)
);
-- A random but clustered space: each word gets a base direction; pipelines perturb it.
CREATE TEMP TABLE base AS
SELECT w.id, (SELECT array_agg((random() - 0.5)::real) FROM generate_series(1, 384) d WHERE w.id IS NOT NULL) AS v FROM words w;
INSERT INTO word_embeddings (id, model, model_revision, pipeline, sense_index, sense_text, truncated, embedding)
SELECT b.id, s.model, s.revision, s.pipeline, sense, 'sense ' || sense, (random() < 0.1),
       CASE WHEN s.pipeline = 'raw-v2' AND random() < 0.05 THEN NULL
            ELSE (SELECT array_agg(x + (random() - 0.5) * s.noise)::vector FROM unnest(b.v) x) END
FROM base b
CROSS JOIN (VALUES ('sentence-transformers/all-MiniLM-L6-v2', 'c9745ed1', 'clean-v1', 0.05, 1500),
                   ('sentence-transformers/all-MiniLM-L6-v2', 'c9745ed1', 'raw-v2', 0.2, 900),
                   ('BAAI/bge-small-en-v1.5', '5c38ec7c', 'clean-v1', 0.3, 600)) s(model, revision, pipeline, noise, take)
CROSS JOIN LATERAL generate_series(0, CASE WHEN ('x' || substr(b.id::text, 1, 2))::bit(8)::int % 5 = 0 THEN 1 ELSE 0 END) sense
WHERE (SELECT count(*) FROM base b2 WHERE b2.id <= b.id) <= s.take;
CREATE INDEX word_embeddings_hnsw ON word_embeddings USING hnsw (embedding vector_cosine_ops);
ANALYZE;
