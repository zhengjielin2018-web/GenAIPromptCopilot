-- 2026-09-25 整套組合推薦（docs/superpowers/specs/2026-09-25-set-recommendations-design.md §4.1）：
-- 每個 tag 歸到哪個 facet，例如 {"clothing.footwear":["sandals","platform footwear"]}。NULL＝尚未回填，推薦查詢跳過。
-- 冪等：docker/seed.sh 每次啟動都跑；既有的開發庫手動 psql -f 一次。新建的庫 001_schema.sql 已含同樣欄位。
ALTER TABLE prompt_knowledge_presets ADD COLUMN IF NOT EXISTS facet_tags JSONB;
