-- Schema 單一真實來源。EF Core 與 Python 皆不建表。
-- VECTOR 維度必須與 .env 的 EMBEDDING_DIMENSIONS 一致（目前 768 / gemini-embedding-001）。

CREATE EXTENSION IF NOT EXISTS vector;

-- HNSW 是「先搜再過濾」：帶 WHERE facet_ids && … 的查詢，索引先取全表最近的 hnsw.ef_search（預設 40）筆，
-- 之後才套 WHERE。候選池只佔全表一兩成時，近鄰常全落在池外，過濾完剩 0 筆（docs/known-issues.md 已修正 #2）。
-- pgvector 0.8+ 的 iterative scan：過濾後不足 LIMIT 筆就繼續往外搜；strict_order 讓結果仍嚴格依距離排序。
-- 設在資料庫層級，API 與 scripts/ 的每條連線都吃得到。資料庫名跟著 POSTGRES_DB 走，所以用 current_database()。
-- 只對之後建立的連線生效；既有的開發庫要手動執行一次同樣的 ALTER DATABASE。
DO $$
BEGIN
    EXECUTE format('ALTER DATABASE %I SET hnsw.iterative_scan = strict_order', current_database());
END
$$;

-- 使用者沉澱 + 管線匯入的完整 prompt；RAG 1 來源
CREATE TABLE shared_prompt_histories (
    id                  UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    source_ref          VARCHAR(64) UNIQUE,              -- 'civitai:<imageId>'；使用者紀錄為 NULL
    user_intent         TEXT NOT NULL,                   -- 繁中原始需求（匯入資料由 LLM 生成）
    positive_prompt     TEXT NOT NULL,
    negative_prompt     TEXT NOT NULL,
    subject_profile     VARCHAR(20) NOT NULL,            -- portrait | landscape | object | vehicle
    source              VARCHAR(20) NOT NULL,            -- user | civitai
    image_url           TEXT,
    completeness_scores JSONB,                           -- 六維度 + facet 四態快照
    intent_embedding    VECTOR(768),
    created_at          TIMESTAMPTZ DEFAULT NOW()
);
CREATE INDEX idx_shared_intent_embedding ON shared_prompt_histories
    USING hnsw (intent_embedding vector_cosine_ops);
CREATE INDEX idx_shared_profile ON shared_prompt_histories (subject_profile);

-- 知識包片段；RAG 2 來源
CREATE TABLE prompt_knowledge_presets (
    id               BIGSERIAL PRIMARY KEY,
    source_ref       VARCHAR(64) UNIQUE,                 -- 'civitai:<imageId>:<idx>'
    title            VARCHAR(100) NOT NULL,
    category         VARCHAR(50) NOT NULL,               -- Style | Scene | Camera | Appearance | Pose | Clothing | Combined
    description      TEXT NOT NULL,                      -- 繁中模糊敘述
    tags             TEXT[] NOT NULL,
    facet_ids        TEXT[] NOT NULL,
    prompt_snippet   TEXT NOT NULL,
    negative_snippet TEXT,
    image_url        TEXT,
    facet_tags       JSONB,                              -- tag → facet 拆分（2026-09-25 整套組合推薦）：{"clothing.footwear":["sandals"]}；NULL＝尚未回填
    preset_embedding VECTOR(768),
    created_at       TIMESTAMPTZ DEFAULT NOW()
);
CREATE INDEX idx_presets_tags      ON prompt_knowledge_presets USING gin (tags);
CREATE INDEX idx_presets_facet_ids ON prompt_knowledge_presets USING gin (facet_ids);
CREATE INDEX idx_presets_embedding ON prompt_knowledge_presets
    USING hnsw (preset_embedding vector_cosine_ops);

-- 稽核（子專案 2 開始寫入；此處先建表）
CREATE TABLE audit_logs (
    id                BIGSERIAL PRIMARY KEY,
    session_id        VARCHAR(64),
    turn_index        INT,
    event_type        VARCHAR(50) NOT NULL,
    prompt_version    VARCHAR(12),
    raw_input         TEXT,
    payload           JSONB,
    prompt_tokens     INT,
    completion_tokens INT,
    latency_ms        INT,
    created_at        TIMESTAMPTZ DEFAULT NOW()
);
CREATE INDEX idx_audit_session ON audit_logs (session_id, created_at);
