#!/bin/sh
# 首次啟動灌知識庫種子。冪等：表裡已有資料就跳過；SEED_URL 空就跳過。
# 任一步失敗以非零退出，compose 會停在這裡並印出原因，api 不會起來。
set -eu

# 既有資料庫升級用：migrations 都是 IF NOT EXISTS，重跑無害；新建的庫 001_schema.sql 已含同樣欄位。
for f in /migrations/*.sql; do
  [ -e "$f" ] || continue
  echo "seed: migration $(basename "$f")"
  psql -v ON_ERROR_STOP=1 -q -f "$f"
done

if [ -z "${SEED_URL:-}" ]; then
  echo "seed: 未設定 SEED_URL，跳過種子。知識庫是空的，要自己跑 scripts/seed_data.py。"
  exit 0
fi

# 推薦查詢只看已回填 facet_tags 的片段：v1 種子沒有這欄，匯入後全部 NULL，要提醒。
warn_unfilled() {
  missing=$(psql -tAc "SELECT count(*) FROM prompt_knowledge_presets WHERE facet_tags IS NULL")
  if [ "$missing" -gt 0 ]; then
    echo "seed: 有 ${missing} 筆片段尚未拆分 facet（facet_tags 為 NULL），整套組合推薦查不到它們。跑 scripts/backfill_facet_tags.py，或改用 seed-v2 以上的種子。"
  fi
}

count=$(psql -tAc "SELECT count(*) FROM prompt_knowledge_presets")
if [ "$count" -gt 0 ]; then
  echo "seed: 已有資料 ${count} 筆，跳過。"
  warn_unfilled
  exit 0
fi

echo "seed: 下載 ${SEED_URL} …"
if ! curl -fL --retry 3 --retry-delay 2 -o /tmp/seed.dump "$SEED_URL"; then
  echo "seed: 下載失敗。檢查網路，或在 .env 把 SEED_URL 設成空字串跳過種子。" >&2
  exit 1
fi

echo "seed: 匯入中（單一交易，失敗不留半套資料）…"
pg_restore --data-only --no-owner --single-transaction --dbname="$PGDATABASE" /tmp/seed.dump
rm -f /tmp/seed.dump

psql -tAc "SELECT 'seed: presets ' || count(*) FROM prompt_knowledge_presets
           UNION ALL SELECT 'seed: histories ' || count(*) FROM shared_prompt_histories"
warn_unfilled
echo "seed: 完成。"
