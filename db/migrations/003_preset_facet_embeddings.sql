-- 2026-09-29 facet 層級向量（docs/superpowers/specs/2026-09-29-facet-vector-retrieval-design.md §4.1）：
-- 每個片段在每個有 tag 的 facet 各一列，向量算自該 facet 的英文 tag；SearchPresets 的 facet 項目與推薦的近似錨用它排序。
-- tag_key：正規化後排序去重、', ' 串接——檢索去重的鍵，也用來判斷 tag 改過沒（scripts/embed_facet_tags.py）。
-- 冪等：docker/seed.sh 每次啟動都跑；既有的開發庫手動 psql -f 一次。新建的庫 001_schema.sql 已含同樣的表。
CREATE TABLE IF NOT EXISTS preset_facet_embeddings (
    preset_id  BIGINT NOT NULL REFERENCES prompt_knowledge_presets(id) ON DELETE CASCADE,
    facet_id   TEXT   NOT NULL,
    tag_key    TEXT   NOT NULL,
    embedding  VECTOR(768) NOT NULL,
    PRIMARY KEY (preset_id, facet_id)
);
CREATE INDEX IF NOT EXISTS idx_pfe_facet ON preset_facet_embeddings (facet_id);
-- 刻意不建向量索引：單一 facet 的池最多約 3,700 筆，B-tree 縮池後精確排序就夠快；
-- 近似索引在「先過濾」的查詢上會漏筆（known-issues 已修正 #2），而且檢索要依 tag_key 去重、本來就得看完整個池。理由見設計 §7。
