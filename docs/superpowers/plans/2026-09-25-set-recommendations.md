# 整套組合推薦與採用 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 每次追問與每次定稿，伺服器對每個維度推薦 2–3 套知識庫裡真實存在、有圖的整套組合；使用者逐 facet 對照後採用，採用走對話重新定稿，帶進來的 tag 標 `adopted`，audit 記採用率所需的欄位。

**Architecture:** 資料端新增 `facet_tags JSONB`（tag → facet 拆分，一次性 LLM 回填腳本）。後端：模型在 `SetFacetStates` 給 covered facet 的英文 `tags` 存成 `Session.FacetTags`（推薦的錨）；`RecommendationService` 在 ask／finalized 之後由 orchestrator 呼叫，SQL 先依 `facet_tags` 過濾錨再依向量排序，另發 `recommendations` 事件，模型不知道；採用是 `POST /messages` 的 `adopt` 欄位，伺服器組一句「採用〈…〉」使用者訊息並記 `Session.Adoptions`，模型照一般流程 `FinalizePrompt`，`TagAttribution` 多第四種來源 `adopted`。前端：`recommendations` 事件掛在該輪的 ask／final 條目上，`RecommendationStrip` 畫縮圖，`AdoptDialog` 做逐 facet 對照，store 走同一條 `runTurn` 路徑送 `adopt`。量測：audit `recommendations`／`adoption` 欄位與 `scripts/adoption_report.py`。

**Tech Stack:** .NET 10 minimal API + Semantic Kernel、Npgsql + pgvector、xunit；Nuxt 3 + Pinia + vitest；Python 3.12（psycopg 3、pydantic、google-genai）、pytest；PostgreSQL 16 + pgvector；docker compose。

**Spec:** `docs/superpowers/specs/2026-09-25-set-recommendations-design.md`

## Global Constraints

- 分支：從 `master` 開 `feat/set-recommendations`（subagent-driven-development 會用 worktree）。
- 後端測試：`dotnet test src/PromptCopilot.Api.Tests --filter "FullyQualifiedName~<TestClass>"`；整包 `dotnet test src/PromptCopilot.Api.Tests`。DB／Gemini 整合測試沒設 `PC_INTEGRATION=1` 自動 Skip（`IntegrationFact`）；要跑整合測試先 `docker compose up -d db`，`PC_TEST_DB` 預設連 `localhost:5432/prompt_copilot`。
- 前端測試：Node 不在工具 shell 的 PATH 上，每次先 `export PATH="$LOCALAPPDATA/Microsoft/WinGet/Packages/OpenJS.NodeJS.22_Microsoft.Winget.Source_8wekyb3d8bbwe/node-v22.23.2-win-x64:$PATH"`（bash），再 `cd src/PromptCopilot.Frontend && npm test -- tests/<file>.test.ts`；整包 `npm test`；型別 `npx nuxi typecheck`；打包 `npm run build`。
- Python：`cd scripts && pytest tests/<file>.py -q`；整包 `pytest`（`pyproject.toml` 預設跳過 integration）；`ruff check .` 必須乾淨（line-length 120）。
- 線上欄位一律 camelCase；`SseWriter` 用 `WhenWritingNull`，可為 null 的欄位在線上整個不存在，前端型別用 `?:`。audit payload 的匿名物件屬性名要自己寫小寫（`Payload()` 沒有命名策略，既有的 `tagOrigins` 就是這樣寫的）。
- 本專案的 Api 專案**沒有** `InternalsVisibleTo`：測試要用的 helper 一律 `public static`。
- `PresetRepository`／`HistoryRepository` 為了測試 fake 不是 `sealed`，新方法要 `virtual`。
- 模型看到的 `SearchPresets` 回傳 JSON 與 on 模式的既有 system prompt 字句**逐字不變**（既有 `KnowledgePluginTests`、`SystemPromptBuilderTests` 是驗證）；本案只在 `system.md` 加第 1 條的半句與第 6 條。
- tag 正規化與字尾規則只有一份：`TagAttribution.Split`／`Normalize`／`EndsWithWord`（Task 5 改成 public），推薦與採用都用它。
- 前端純函式放 `lib/`，不用 Nuxt auto-import；元件沒有 mount 測試，靠 eval 人工驗收。
- 文件與程式同一個 commit：改契約的任務自己更新對應的 spec 段落（每個任務的 Files 已列出）。
- 中文文案用繁體；程式註解沿用現有風格（說「為什麼」，短句）。
- Commit 訊息結尾加 `Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>`。

## Review Focus

1. 模型對標成 `missing`／`waived` 的 facet 也附了 `tags`：不能存進 `FacetTags`，否則沒講的東西變成錨。→ Task 4 測試 `ApplyFacetStates_keeps_tags_only_for_covered_and_drops_them_when_state_changes`。
2. 錨 tag 含底線、括號、權重（`(long_hair:1.2)`）而資料庫 `facet_tags` 裡是 `long hair`：SQL 端要 `replace(lower(), '_', ' ')`，C# 端要先 `Normalize`，`LIKE` 的 `_`／`%` 要跳脫。→ Task 3 測試 `Recommend_anchor_matches_whole_tag_and_word_suffix`（`platform sandals` 命中 `sandals`）、`EscapeLike_escapes_wildcards`；Task 6 測試 `Anchors_come_from_facet_tags_normalized`。
3. `adopt` 的 `take` 指到這套沒有 tag 的 facet（`facet_tags` 有鍵但空陣列、或沒鍵）：要 400，不能組出「照它的（）」。→ Task 7 測試 `Rejects_take_facet_the_set_has_no_tags_for`。
4. 推薦查詢逾時或 embedding 失敗發生在 `final` 已宣告之後：不能回滾、不能發 `error`，前端要看到正常的定稿卡。→ Task 6 測試 `Recommendation_failure_does_not_roll_back_the_turn`（stub 丟例外）與 `Recommendation_timeout_is_audited_as_timeout`（stub 永不回來）。
5. 舊的 sessionStorage transcript（final 條目沒有 `recommendations`、state 沒有 `facetTags`）與舊後端（`session` 事件沒有 `text`、`dimensions` 沒有 `facetTags`）：畫面不能壞，儀表板與卡片退回現在的樣子。→ Task 10 測試 `hydrate defaults facetTags to {} and leaves old final entries without recommendations`、`session without text leaves the user entry alone`、`dimensions without facetTags resets to {}`。

---

### Task 1: 資料庫欄位 `facet_tags`、migration、seed 升級路徑

**Files:**
- Modify: `db/init/001_schema.sql`
- Create: `db/migrations/002_facet_tags.sql`
- Modify: `docker/Dockerfile.seed`
- Modify: `docker/seed.sh`

**Interfaces:**
- Produces: `prompt_knowledge_presets.facet_tags JSONB`（NULL＝尚未回填）。Task 2 回填、Task 3 讀。

- [ ] **Step 1: `001_schema.sql` 加欄位**

在 `image_url        TEXT,` 之後、`preset_embedding VECTOR(768),` 之前插入：

```sql
    facet_tags       JSONB,                              -- tag → facet 拆分（2026-09-25 整套組合推薦）：{"clothing.footwear":["sandals"]}；NULL＝尚未回填
```

- [ ] **Step 2: 建 migration**

`db/migrations/002_facet_tags.sql`：

```sql
-- 2026-09-25 整套組合推薦（docs/superpowers/specs/2026-09-25-set-recommendations-design.md §4.1）：
-- 每個 tag 歸到哪個 facet，例如 {"clothing.footwear":["sandals","platform footwear"]}。NULL＝尚未回填，推薦查詢跳過。
-- 冪等：docker/seed.sh 每次啟動都跑；既有的開發庫手動 psql -f 一次。新建的庫 001_schema.sql 已含同樣欄位。
ALTER TABLE prompt_knowledge_presets ADD COLUMN IF NOT EXISTS facet_tags JSONB;
```

- [ ] **Step 3: seed 映像帶 migrations，`seed.sh` 先跑 migration、匯入後提示未回填**

`docker/Dockerfile.seed` 在 `COPY docker/seed.sh …` 之前加一行：

```dockerfile
COPY db/migrations /migrations
```

`docker/seed.sh`：在 `count=$(psql …)` 那行**之前**插入：

```sh
# 既有資料庫升級用：migrations 都是 IF NOT EXISTS，重跑無害；新建的庫 001_schema.sql 已含同樣欄位。
for f in /migrations/*.sql; do
  [ -e "$f" ] || continue
  echo "seed: migration $(basename "$f")"
  psql -v ON_ERROR_STOP=1 -q -f "$f"
done

# 推薦查詢只看已回填 facet_tags 的片段：v1 種子沒有這欄，匯入後全部 NULL，要提醒。
warn_unfilled() {
  missing=$(psql -tAc "SELECT count(*) FROM prompt_knowledge_presets WHERE facet_tags IS NULL")
  if [ "$missing" -gt 0 ]; then
    echo "seed: 有 ${missing} 筆片段尚未拆分 facet（facet_tags 為 NULL），整套組合推薦查不到它們。跑 scripts/backfill_facet_tags.py，或改用 seed-v2 以上的種子。"
  fi
}
```

「已有資料，跳過」那段的 `exit 0` 之前加 `warn_unfilled`；檔尾 `echo "seed: 完成。"` 之前加 `warn_unfilled`。

- [ ] **Step 4: 驗證**

```bash
bash -n docker/seed.sh
docker exec -i prompt-copilot-db psql -U postgres -d prompt_copilot -v ON_ERROR_STOP=1 < db/migrations/002_facet_tags.sql
docker exec prompt-copilot-db psql -U postgres -d prompt_copilot -Atc "SELECT count(*) FILTER (WHERE facet_tags IS NULL), count(*) FROM prompt_knowledge_presets"
docker compose build seed
```

Expected：第二行印 `ALTER TABLE`；第三行 `19354|19354`（全 NULL）；build 成功。再跑一次第二行要印 `NOTICE: column "facet_tags" ... already exists, skipping`。

- [ ] **Step 5: Commit**

```bash
git add db/init/001_schema.sql db/migrations/002_facet_tags.sql docker/Dockerfile.seed docker/seed.sh
git commit -m "feat(db): facet_tags column, migration, seed.sh applies migrations and warns when unfilled"
```

---

### Task 2: 回填腳本 `scripts/backfill_facet_tags.py`

**Files:**
- Create: `scripts/backfill_facet_tags.py`
- Test: `scripts/tests/test_backfill_facet_tags.py`

**Interfaces:**
- Consumes: `pipeline.gemini_client.GeminiClient.generate_structured(prompt, schema)`、`pipeline.db.connect()`、`pipeline.facets.load_facets`。
- Produces: `split_tags(snippet) -> list[str]`、`build_prompt(rows, catalog) -> str`、`normalize(row, assignments) -> dict[str, list[str]]`、`run_backfill(conn, client, catalog, *, limit, batch_size, dry_run, log) -> dict`。row 是 `{"id", "facet_ids", "prompt_snippet"}`。

- [ ] **Step 1: 寫失敗的測試**

`scripts/tests/test_backfill_facet_tags.py`：

```python
"""回填不打網路、不連 DB：client 與 conn 都是記錄器。"""

from __future__ import annotations

import json

from backfill_facet_tags import BatchOut, build_prompt, normalize, run_backfill, split_tags
from pipeline.config import FACETS_PATH
from pipeline.facets import load_facets

CAT = load_facets(FACETS_PATH)
ROW = {"id": 41720, "facet_ids": ["clothing.upper", "clothing.footwear"],
       "prompt_snippet": "purple kimono, sandals, white socks, purple kimono, ,  tabi "}


class FakeClient:
    def __init__(self, payloads):
        self.payloads = list(payloads)
        self.prompts = []

    def generate_structured(self, prompt, schema):
        self.prompts.append(prompt)
        return schema.model_validate(self.payloads.pop(0))


class FakeResult:
    def __init__(self, rows):
        self.rows = rows

    def fetchall(self):
        return self.rows


class FakeCursor:
    def __init__(self, sink):
        self.sink = sink

    def __enter__(self):
        return self

    def __exit__(self, *exc):
        return False

    def execute(self, sql, params):
        self.sink.append((sql, params))


class FakeConn:
    """batches：每次查 pending 依序吐出的一批（tuple 列）。"""

    def __init__(self, batches):
        self.batches = list(batches)
        self.writes = []
        self.commits = 0
        self.rollbacks = 0

    def execute(self, sql, params):
        rows = self.batches.pop(0) if self.batches else []
        return FakeResult(rows[: params["limit"]])

    def cursor(self):
        return FakeCursor(self.writes)

    def commit(self):
        self.commits += 1

    def rollback(self):
        self.rollbacks += 1


def test_split_tags_strips_dedups_and_drops_empty():
    assert split_tags(ROW["prompt_snippet"]) == ["purple kimono", "sandals", "white socks", "tabi"]


def test_normalize_keeps_only_this_rows_facets_and_original_tags_and_defaults_missing_to_other():
    out = normalize(ROW, {"purple kimono": "clothing.upper", "sandals": "clothing.footwear",
                          "white socks": "clothing.lower",      # 不在這筆的 facet_ids → 丟掉
                          "kimono": "clothing.upper"})          # 不是原字 → 丟掉；tabi 沒給 → other
    assert out == {"clothing.upper": ["purple kimono"], "clothing.footwear": ["sandals"]}


def test_normalize_all_other_is_empty_dict_not_none():
    assert normalize(ROW, None) == {}
    assert normalize(ROW, {"sandals": "other"}) == {}


def test_build_prompt_lists_each_row_with_its_own_facets_and_tags():
    p = build_prompt([ROW, {"id": 7, "facet_ids": ["style.genre"], "prompt_snippet": "oil painting"}], CAT)
    assert "[id=41720]" in p and "clothing.upper, clothing.footwear" in p
    assert "purple kimono | sandals | white socks | tabi" in p
    assert "[id=7]" in p and "oil painting" in p
    assert "clothing.footwear：鞋履" in p          # facet 說明


def test_run_backfill_writes_one_json_per_row_commits_per_batch_and_stops_when_nothing_pending():
    conn = FakeConn([[(41720, ROW["facet_ids"], ROW["prompt_snippet"]), (7, ["style.genre"], "oil painting")], []])
    client = FakeClient([{"items": [
        {"id": 41720, "assignments": {"purple kimono": "clothing.upper", "sandals": "clothing.footwear"}},
    ]}])                                                     # id 7 沒回 → 全 other → {}
    stats = run_backfill(conn, client, CAT, batch_size=20, log=lambda *_: None)

    assert [(p["id"], json.loads(p["facet_tags"])) for _, p in conn.writes] == [
        (41720, {"clothing.upper": ["purple kimono"], "clothing.footwear": ["sandals"]}),
        (7, {}),
    ]
    assert all("facet_tags IS NULL" in sql for sql, _ in conn.writes)   # 兩支程式同時跑也不會互相覆蓋
    assert conn.commits == 1 and conn.rollbacks == 0
    assert stats["rows"] == 2 and stats["tags"] == 5 and stats["other"] == 3
    assert stats["facets"] == {"clothing.upper": 1, "clothing.footwear": 1}


def test_run_backfill_dry_run_writes_nothing_and_rolls_back_after_one_batch():
    conn = FakeConn([[(41720, ROW["facet_ids"], ROW["prompt_snippet"])], [(41720, ROW["facet_ids"], ROW["prompt_snippet"])]])
    client = FakeClient([{"items": [{"id": 41720, "assignments": {}}]}])
    stats = run_backfill(conn, client, CAT, dry_run=True, log=lambda *_: None)
    assert conn.writes == [] and conn.commits == 0 and conn.rollbacks == 1
    assert stats["rows"] == 1 and len(client.prompts) == 1


def test_run_backfill_limit_caps_rows_across_batches():
    rows = [(i, ["style.genre", "style.palette"], f"tag{i}") for i in range(5)]
    conn = FakeConn([rows[:2], rows[2:4], rows[4:], []])
    client = FakeClient([{"items": []}] * 3)
    stats = run_backfill(conn, client, CAT, limit=3, batch_size=2, log=lambda *_: None)
    assert stats["rows"] == 3 and len(conn.writes) == 3


def test_batch_out_schema_accepts_other():
    b = BatchOut.model_validate({"items": [{"id": 1, "assignments": {"x": "other"}}]})
    assert b.items[0].assignments == {"x": "other"}
```

- [ ] **Step 2: 跑測試確認失敗**

Run: `cd scripts && pytest tests/test_backfill_facet_tags.py -q`
Expected: FAIL，`ModuleNotFoundError: No module named 'backfill_facet_tags'`

- [ ] **Step 3: 寫腳本**

`scripts/backfill_facet_tags.py`：

```python
"""一次性回填 prompt_knowledge_presets.facet_tags：把每筆片段的 tag 歸到它 facet_ids 裡的哪個 facet
（設計 2026-09-25-set-recommendations-design.md §4.2）。整套組合推薦的對照表與「只採用這幾個 facet」靠它。

    python backfill_facet_tags.py [--limit N] [--batch-size 20] [--dry-run]

只處理 facet_tags IS NULL 的列，每批一個交易，可中斷、可重跑。全部歸不進 facet 的列寫 {}（不是 NULL），
重跑時不會再送一次。structure.py 不產這欄：語料現在沒在長，新片段進來後重跑這支即可。
"""

from __future__ import annotations

import argparse
import json
import sys
from collections import Counter
from collections.abc import Callable
from pathlib import Path

from pydantic import BaseModel, Field

sys.path.insert(0, str(Path(__file__).resolve().parent))

from pipeline.config import FACETS_PATH  # noqa: E402
from pipeline.facets import FacetCatalog, load_facets  # noqa: E402

BATCH_SIZE = 20
OTHER = "other"

PENDING_SQL = """
SELECT id, facet_ids, prompt_snippet FROM prompt_knowledge_presets
WHERE facet_tags IS NULL ORDER BY id LIMIT %(limit)s
"""
# 再檢查一次 IS NULL：兩支程式同時跑也不會互相覆蓋
UPDATE_SQL = """
UPDATE prompt_knowledge_presets SET facet_tags = %(facet_tags)s::jsonb
WHERE id = %(id)s AND facet_tags IS NULL
"""


class Assignment(BaseModel):
    id: int
    assignments: dict[str, str] = Field(description="tag（原字）→ facet id 或 other")


class BatchOut(BaseModel):
    items: list[Assignment]


PROMPT_TEMPLATE = """你是生圖提示詞知識庫的整理員。下面每一筆是一個知識庫片段：它涵蓋的 facet 清單，以及它的英文 tag 清單。
請把每個 tag 歸到「最貼切的一個 facet」；歸不進任何 facet 的填 "other"。

規則：
- 每個 tag 只歸一個 facet；facet 只能從該筆自己的清單選。
- tag 原字照抄當 key：不要改寫、不要翻譯、不要合併、不要漏掉。
- 回 JSON：{{"items":[{{"id":<id>,"assignments":{{"<tag>":"<facet id 或 other>", ...}}}}, ...]}}，每一筆都要有。

Facet 說明：
{facets}

片段：
{rows}
"""


def split_tags(snippet: str) -> list[str]:
    """以逗號拆、去頭尾空白、丟空段、去重（保留第一次出現的順序）。"""
    seen: set[str] = set()
    out: list[str] = []
    for raw in snippet.split(","):
        t = raw.strip()
        if t and t not in seen:
            seen.add(t)
            out.append(t)
    return out


def build_prompt(rows: list[dict], catalog: FacetCatalog) -> str:
    facets = "\n".join(f"- {f.id}：{f.label}（例：{f.hint}）" for f in catalog.facets.values())
    lines = []
    for r in rows:
        lines.append(f"[id={r['id']}] facets: {', '.join(r['facet_ids'])}")
        lines.append(f"  tags: {' | '.join(split_tags(r['prompt_snippet']))}")
    return PROMPT_TEMPLATE.format(facets=facets, rows="\n".join(lines))


def normalize(row: dict, assignments: dict[str, str] | None) -> dict[str, list[str]]:
    """回寫前驗證：只看輸入清單裡的原字（多出來的丟掉、缺的當 other）；facet 必須在該筆 facet_ids 內，否則當 other。"""
    allowed = set(row["facet_ids"])
    given = assignments or {}
    out: dict[str, list[str]] = {}
    for tag in split_tags(row["prompt_snippet"]):
        facet = given.get(tag, OTHER)
        if facet in allowed:
            out.setdefault(facet, []).append(tag)
    return out


def fetch_pending(conn, limit: int) -> list[dict]:
    rows = conn.execute(PENDING_SQL, {"limit": limit}).fetchall()
    return [{"id": r[0], "facet_ids": list(r[1]), "prompt_snippet": r[2]} for r in rows]


def run_backfill(conn, client, catalog: FacetCatalog, *, limit: int | None = None, batch_size: int = BATCH_SIZE,
                 dry_run: bool = False, log: Callable[..., None] = print) -> dict:
    stats: dict = {"rows": 0, "tags": 0, "other": 0, "facets": Counter()}
    remaining = limit
    while remaining is None or remaining > 0:
        n = batch_size if remaining is None else min(batch_size, remaining)
        rows = fetch_pending(conn, n)
        if not rows:
            break
        result = client.generate_structured(build_prompt(rows, catalog), BatchOut)
        by_id = {a.id: a.assignments for a in result.items}
        with conn.cursor() as cur:
            for row in rows:
                ft = normalize(row, by_id.get(row["id"]))
                total = len(split_tags(row["prompt_snippet"]))
                kept = sum(len(v) for v in ft.values())
                stats["rows"] += 1
                stats["tags"] += total
                stats["other"] += total - kept
                stats["facets"].update(ft.keys())
                if not dry_run:
                    cur.execute(UPDATE_SQL, {"id": row["id"], "facet_tags": json.dumps(ft, ensure_ascii=False)})
        if dry_run:
            conn.rollback()
            for row in rows:
                log(f"[dry-run] {row['id']}: {json.dumps(normalize(row, by_id.get(row['id'])), ensure_ascii=False)}")
            break
        conn.commit()
        if remaining is not None:
            remaining -= len(rows)
        log(f"backfill: {stats['rows']} 筆已寫入（本批 {len(rows)}）")
    return stats


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--limit", type=int, default=None, help="本次最多處理幾筆")
    ap.add_argument("--batch-size", type=int, default=BATCH_SIZE)
    ap.add_argument("--dry-run", action="store_true", help="只送第一批給 Gemini、印結果，不寫入")
    args = ap.parse_args(argv)

    from pipeline.db import connect
    from pipeline.gemini_client import default_client

    catalog = load_facets(FACETS_PATH)
    with connect() as conn:
        stats = run_backfill(conn, default_client(), catalog, limit=args.limit, batch_size=args.batch_size, dry_run=args.dry_run)
    print(f"處理 {stats['rows']} 筆、{stats['tags']} 個 tag，other {stats['other']}"
          f"（{(stats['other'] / stats['tags'] * 100) if stats['tags'] else 0:.1f}%）")
    for facet, n in sorted(stats["facets"].items()):
        print(f"  {facet:<26}{n:>7} 筆有 tag")
    return 0


if __name__ == "__main__":
    sys.exit(main())
```

- [ ] **Step 4: 跑測試確認通過、ruff 乾淨**

Run: `cd scripts && pytest tests/test_backfill_facet_tags.py -q && ruff check backfill_facet_tags.py tests/test_backfill_facet_tags.py`
Expected: 8 passed；ruff 無輸出。

- [ ] **Step 5: `scripts/README.md` 加一節**

在「## 一次性補充語料：Kisegaeningyou 服裝集」之前加：

```markdown
## 一次性回填：`facet_tags`（整套組合推薦用）

把每筆片段的 tag 歸到它 `facet_ids` 裡的哪個 facet，寫進 `prompt_knowledge_presets.facet_tags`。
整套組合推薦的對照表與「只採用這幾個 facet」靠它。只處理 `facet_tags IS NULL` 的列，可中斷、可重跑；
19k 筆約 1,000 次 Gemini 呼叫。既有資料庫先套 `db/migrations/002_facet_tags.sql`。

    python backfill_facet_tags.py --dry-run --limit 20   # 先看 20 筆拆得對不對
    python backfill_facet_tags.py                        # 全量
```

- [ ] **Step 6: Commit**

```bash
git add scripts/backfill_facet_tags.py scripts/tests/test_backfill_facet_tags.py scripts/README.md
git commit -m "feat(scripts): backfill_facet_tags splits each preset's tags by facet with one LLM pass"
```

---

### Task 3: `PresetRepository.RecommendAsync` 與 `PresetDetail.FacetTags`

**Files:**
- Modify: `src/PromptCopilot.Api/Data/PresetRepository.cs`
- Test: `src/PromptCopilot.Api.Tests/Data/PresetRepositoryTests.cs`（新，純函式）
- Test: `src/PromptCopilot.Api.Tests/Data/RepositoryIntegrationTests.cs`

**Interfaces:**
- Produces:
  - `PresetDetail(…, string? SourceUrl, IReadOnlyDictionary<string, IReadOnlyList<string>>? FacetTags = null)`（`GET /api/presets/{id}` 線上多 `facetTags`，未回填為 null → 省略）。
  - `record PresetCandidate(long Id, string Title, IReadOnlyList<string> FacetIds, IReadOnlyDictionary<string, IReadOnlyList<string>> FacetTags, string? ImageUrl, string? SourceRef, double Dist)`。
  - `virtual Task<IReadOnlyList<PresetCandidate>> RecommendAsync(float[] query, IReadOnlyList<string> dimensionFacets, IReadOnlyList<string> anchorFacets, IReadOnlyList<string> anchorTags, int take, CancellationToken ct)`：`anchorFacets` 或 `anchorTags` 為空就不加錨條件。`anchorTags` 由呼叫端先 `TagAttribution.Normalize`。
  - `static IReadOnlyDictionary<string, IReadOnlyList<string>>? ParseFacetTags(string? json)`、`static string EscapeLike(string s)`。

- [ ] **Step 1: 寫失敗的純函式測試**

`src/PromptCopilot.Api.Tests/Data/PresetRepositoryTests.cs`：

```csharp
using PromptCopilot.Api.Data;

namespace PromptCopilot.Api.Tests.Data;

public class PresetRepositoryTests
{
    [Fact]
    public void ParseFacetTags_reads_jsonb_text_and_null_stays_null()
    {
        var d = PresetRepository.ParseFacetTags("""{"clothing.footwear":["sandals","platform footwear"],"clothing.upper":[]}""")!;
        Assert.Equal(new[] { "sandals", "platform footwear" }, d["clothing.footwear"]);
        Assert.Empty(d["clothing.upper"]);
        Assert.Null(PresetRepository.ParseFacetTags(null));
        Assert.Empty(PresetRepository.ParseFacetTags("{}")!);
    }

    /// <summary>LIKE 的 % 與 _ 是萬用字元：錨 tag 已正規化（底線變空白），但保險起見兩個都跳脫。</summary>
    [Fact]
    public void EscapeLike_escapes_wildcards()
    {
        Assert.Equal(@"100\% wool\_blend", PresetRepository.EscapeLike("100% wool_blend"));
        Assert.Equal("sandals", PresetRepository.EscapeLike("sandals"));
    }
}
```

- [ ] **Step 2: 寫失敗的整合測試**

`RepositoryIntegrationTests.cs`：`InitializeAsync` 改成插兩筆（原本那筆照舊，多一筆穿搭）、`DisposeAsync` 改 `LIKE`：

```csharp
    private static readonly string Ref2 = Ref + ":1";

    public async Task InitializeAsync()
    {
        var b = new NpgsqlDataSourceBuilder(TestEnv.Db); b.UseVector(); _ds = b.Build();
        await using var cmd = _ds.CreateCommand("""
            INSERT INTO prompt_knowledge_presets (source_ref, title, category, description, tags, facet_ids, prompt_snippet, negative_snippet, preset_embedding, facet_tags)
            VALUES (@r, '測試片段', 'Style', 'd', ARRAY['x'], ARRAY['style.genre'], 'photo realism', NULL, @e, NULL),
                   (@r2, '測試穿搭', 'Clothing', 'd', ARRAY['x'], ARRAY['clothing.upper','clothing.footwear'], 'white shirt, platform sandals', NULL, @e,
                    '{"clothing.upper":["white shirt"],"clothing.footwear":["platform sandals"]}'::jsonb)
            """);
        cmd.Parameters.AddWithValue("r", Ref);
        cmd.Parameters.AddWithValue("r2", Ref2);
        cmd.Parameters.AddWithValue("e", new Pgvector.Vector(Unit(0)));
        await cmd.ExecuteNonQueryAsync();
    }
```

`DisposeAsync` 第一個 DELETE 改成 `WHERE source_ref = @r OR source_ref = @r2`（多加一個參數）。加測試：

```csharp
    [IntegrationFact]
    public async Task Recommend_requires_two_dimension_facets_and_backfilled_tags()
    {
        var repo = new PresetRepository(_ds);
        var clothing = new[] { "clothing.upper", "clothing.lower", "clothing.footwear" };
        var hits = await repo.RecommendAsync(Unit(0), clothing, Array.Empty<string>(), Array.Empty<string>(), 3, default);
        var set = Assert.Single(hits, h => h.Title == "測試穿搭");
        Assert.Equal(new[] { "platform sandals" }, set.FacetTags["clothing.footwear"]);
        Assert.Equal(Ref2, set.SourceRef);
        // 單一 facet 的片段不是「組合」，而且它沒有 facet_tags
        Assert.DoesNotContain(await repo.RecommendAsync(Unit(0), new[] { "style.genre", "style.palette" }, Array.Empty<string>(), Array.Empty<string>(), 3, default),
            h => h.Title == "測試片段");
    }

    [IntegrationFact]
    public async Task Recommend_anchor_matches_whole_tag_and_word_suffix_within_the_anchor_facets()
    {
        var repo = new PresetRepository(_ds);
        var clothing = new[] { "clothing.upper", "clothing.footwear" };
        Task<IReadOnlyList<PresetCandidate>> Q(string facet, string tag) => repo.RecommendAsync(Unit(0), clothing, new[] { facet }, new[] { tag }, 3, default);

        Assert.Contains(await Q("clothing.footwear", "sandals"), h => h.Title == "測試穿搭");            // 字尾：platform sandals ← sandals
        Assert.Contains(await Q("clothing.footwear", "platform sandals"), h => h.Title == "測試穿搭");   // 整段相等
        Assert.DoesNotContain(await Q("clothing.footwear", "boots"), h => h.Title == "測試穿搭");
        Assert.DoesNotContain(await Q("clothing.upper", "sandals"), h => h.Title == "測試穿搭");          // 錨只看指定的 facet
    }

    [IntegrationFact]
    public async Task Preset_get_returns_facet_tags_or_null()
    {
        var repo = new PresetRepository(_ds);
        var set = (await repo.RecommendAsync(Unit(0), new[] { "clothing.upper", "clothing.footwear" }, Array.Empty<string>(), Array.Empty<string>(), 3, default)).First(h => h.Title == "測試穿搭");
        Assert.Equal(new[] { "white shirt" }, (await repo.GetAsync(set.Id, default))!.FacetTags!["clothing.upper"]);
        var single = (await repo.SearchAsync(Unit(0), new[] { "style.genre" }, 5, default)).First(h => h.Title == "測試片段");
        Assert.Null((await repo.GetAsync(single.Id, default))!.FacetTags);
    }
```

- [ ] **Step 3: 跑測試確認失敗**

Run: `dotnet test src/PromptCopilot.Api.Tests --filter "FullyQualifiedName~PresetRepositoryTests"`
Expected: 編譯失敗（`ParseFacetTags`、`RecommendAsync`、`PresetCandidate` 不存在）。

- [ ] **Step 4: 實作**

`PresetRepository.cs`：

```csharp
using System.Text.Json;
using Npgsql;
using Pgvector;

namespace PromptCopilot.Api.Data;

public sealed record PresetHit(long Id, string Title, string Category, IReadOnlyList<string> FacetIds,
    string PromptSnippet, string? NegativeSnippet, string? ImageUrl, double Dist, string? SourceRef);

/// <summary>FacetTags（2026-09-25）：tag → facet 拆分，回填腳本寫的；NULL 表示尚未回填（線上省略）。</summary>
public sealed record PresetDetail(long Id, string Title, string Category, string Description, IReadOnlyList<string> Tags,
    IReadOnlyList<string> FacetIds, string PromptSnippet, string? NegativeSnippet, string? ImageUrl,
    string? SourceRef, string? SourceUrl, IReadOnlyDictionary<string, IReadOnlyList<string>>? FacetTags = null);

/// <summary>整套組合推薦的候選（設計 §4.4）。FacetTags 一定非 null：查詢只取已回填的列。</summary>
public sealed record PresetCandidate(long Id, string Title, IReadOnlyList<string> FacetIds,
    IReadOnlyDictionary<string, IReadOnlyList<string>> FacetTags, string? ImageUrl, string? SourceRef, double Dist);

public class PresetRepository(NpgsqlDataSource ds)
{
    private const string SearchSql = """ …（不動）… """;
    private const string PoolSql = "…（不動）…";
    private const string GetSql = """
        SELECT id, title, category, description, tags, facet_ids, prompt_snippet, negative_snippet, image_url, source_ref, facet_tags::text
        FROM prompt_knowledge_presets WHERE id = @id
        """;
    // 設計 §4.4：GIN 過濾該維度、至少涵蓋維度裡 2 個 facet（一個 facet 是單品不是組合）、只取已回填的列；
    // 有錨時先過濾再排序（涼鞋 20 筆、穿著 2,357 筆，先取向量前 N 再過濾會漏掉大半）。
    private const string RecommendSql = """
        SELECT id, title, facet_ids, facet_tags::text, image_url, source_ref, preset_embedding <=> @q AS dist
        FROM prompt_knowledge_presets
        WHERE facet_ids && @facets
          AND facet_tags IS NOT NULL
          AND cardinality(ARRAY(SELECT unnest(facet_ids) INTERSECT SELECT unnest(@facets))) >= 2
        """;
    // 錨：該維度任一 covered facet 底下有 tag 整段相等或以空白為界的字尾相符（與 TagAttribution.EndsWithWord 同義）。
    // 資料庫的 tag 可能帶底線，錨已正規化成空白，所以比對前 replace。
    private const string AnchorClause = """
          AND EXISTS (
            SELECT 1
            FROM jsonb_each(facet_tags) AS ft(facet_id, tags), jsonb_array_elements_text(ft.tags) AS t(tag)
            WHERE ft.facet_id = ANY(@anchorFacets)
              AND (replace(lower(t.tag), '_', ' ') = ANY(@anchorTags) OR replace(lower(t.tag), '_', ' ') LIKE ANY(@anchorSuffixes))
          )
        """;
    private const string RecommendTail = "\n        ORDER BY dist\n        LIMIT @take";

    // SearchAsync、PoolSizeAsync 不動

    public virtual async Task<IReadOnlyList<PresetCandidate>> RecommendAsync(float[] query, IReadOnlyList<string> dimensionFacets,
        IReadOnlyList<string> anchorFacets, IReadOnlyList<string> anchorTags, int take, CancellationToken ct)
    {
        var anchored = anchorFacets.Count > 0 && anchorTags.Count > 0;
        await using var cmd = ds.CreateCommand(RecommendSql + (anchored ? AnchorClause : "") + RecommendTail);
        cmd.Parameters.AddWithValue("q", new Vector(query));
        cmd.Parameters.AddWithValue("facets", dimensionFacets.ToArray());
        cmd.Parameters.AddWithValue("take", take);
        if (anchored)
        {
            cmd.Parameters.AddWithValue("anchorFacets", anchorFacets.ToArray());
            cmd.Parameters.AddWithValue("anchorTags", anchorTags.ToArray());
            cmd.Parameters.AddWithValue("anchorSuffixes", anchorTags.Select(t => "% " + EscapeLike(t)).ToArray());
        }
        var list = new List<PresetCandidate>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new PresetCandidate(r.GetInt64(0), r.GetString(1), r.GetFieldValue<string[]>(2), ParseFacetTags(r.GetString(3))!,
                r.IsDBNull(4) ? null : r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5), r.GetDouble(6)));
        return list;
    }

    public virtual async Task<PresetDetail?> GetAsync(long id, CancellationToken ct)
    {
        // …前面不動…
        return new PresetDetail(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetFieldValue<string[]>(4),
            r.GetFieldValue<string[]>(5), r.GetString(6), r.IsDBNull(7) ? null : r.GetString(7), r.IsDBNull(8) ? null : r.GetString(8),
            sourceRef, SourceAttribution.UrlFor(sourceRef), ParseFacetTags(r.IsDBNull(10) ? null : r.GetString(10)));
    }

    /// <summary>jsonb 以 ::text 讀回來再自己解：Npgsql 對 jsonb 的動態型別對應在不同版本行為不一，字串最穩。</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>>? ParseFacetTags(string? json)
    {
        if (json is null) return null;
        var d = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(json) ?? new();
        return d.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value);
    }

    /// <summary>LIKE 的 % 與 _ 是萬用字元；PostgreSQL 預設的跳脫字元是反斜線。</summary>
    public static string EscapeLike(string s) => s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
}
```

- [ ] **Step 5: 跑測試**

Run: `dotnet test src/PromptCopilot.Api.Tests --filter "FullyQualifiedName~PresetRepositoryTests"`
Expected: 2 passed。

Run（要 DB）: `PC_INTEGRATION=1 dotnet test src/PromptCopilot.Api.Tests --filter "FullyQualifiedName~RepositoryIntegrationTests"`
Expected: 全部 passed（含既有 4 條）。整包 `dotnet test src/PromptCopilot.Api.Tests` 也要綠：`EndpointTests.FakePresets` 用 11 個位置參數建 `PresetDetail`，第 12 個有預設值，不用改。

- [ ] **Step 6: 主規格 §7 資料模型與 §10.1 同步**

`docs/superpowers/specs/2026-09-21-genai-prompt-copilot-design.md`：§7 的 `prompt_knowledge_presets` 欄位表（找 `negative_snippet` 那列）之後加一列 `| facet_tags | JSONB | tag → facet 拆分（2026-09-25 整套組合推薦）；NULL＝尚未回填，由 scripts/backfill_facet_tags.py 填 |`；§10.1 `GET /api/presets/{id}` 那列末尾加「；`facetTags`（2026-09-25，未回填時省略）」。

- [ ] **Step 7: Commit**

```bash
git add src/PromptCopilot.Api/Data/PresetRepository.cs src/PromptCopilot.Api.Tests/Data/PresetRepositoryTests.cs src/PromptCopilot.Api.Tests/Data/RepositoryIntegrationTests.cs docs/superpowers/specs/2026-09-21-genai-prompt-copilot-design.md
git commit -m "feat(api): PresetRepository.RecommendAsync filters by facet_tags anchors before vector order; GetAsync returns facetTags"
```

---

### Task 4: 模型給 covered facet 的英文 tag：`FacetStateEntry.Tags`、`Session.FacetTags`、`dimensions` 事件、GET dto

**Files:**
- Modify: `src/PromptCopilot.Api/Plugins/SessionPlugin.cs`
- Modify: `src/PromptCopilot.Api/Sessions/Session.cs`
- Modify: `src/PromptCopilot.Api/Orchestration/TurnContext.cs`
- Modify: `src/PromptCopilot.Api/Streaming/AgentEvent.cs`
- Modify: `src/PromptCopilot.Api/Endpoints/SessionEndpoints.cs`（`SessionSnapshotDto`）
- Modify: `src/PromptCopilot.Api/Prompts/system.md`
- Test: `src/PromptCopilot.Api.Tests/Sessions/SessionTests.cs`
- Test: `src/PromptCopilot.Api.Tests/Plugins/SessionPluginTests.cs`（新）
- Test: `src/PromptCopilot.Api.Tests/Plugins/ContractsTests.cs`
- Test: `src/PromptCopilot.Api.Tests/Orchestration/SystemPromptBuilderTests.cs`
- Test: `src/PromptCopilot.Api.Tests/Endpoints/EndpointTests.cs`
- Modify: `docs/superpowers/specs/2026-09-21-genai-prompt-copilot-design.md`（§4 工具表 `SetFacetStates` 列、`FacetStateEntry` 型別那段、§10.1 GET 列、§10.2 `dimensions` 列）

**Interfaces:**
- Produces:
  - `FacetStateEntry(string FacetId, string State, string? Note = null, string? Tags = null)`（線上 `tags`）。
  - `Dictionary<string, string> Session.FacetTags`（facetId → 模型給的英文 tag 原字串，逗號分隔）；`Session.ApplyFacetStates(updates, catalog, IReadOnlyDictionary<string, string>? tags = null)`。
  - `DimensionsEvent(string? Profile, IReadOnlyDictionary<string, string> FacetStates, IReadOnlyDictionary<string, string>? FacetTags = null)`（線上 `facetTags`）。
  - `SessionSnapshotDto(…, string Retrieval, IReadOnlyDictionary<string, string> FacetTags)`（線上 `facetTags`）。
  - `SessionSnapshot(…, int TurnIndex, Dictionary<string, string> FacetTags)`。

- [ ] **Step 1: 寫失敗的測試**

`SessionTests.cs` 加：

```csharp
    /// <summary>設計 §5.5：只有 covered 的 facet 有 tags；狀態改成別的就移除；不適用的 facet 一起被丟掉。</summary>
    [Fact]
    public void ApplyFacetStates_keeps_tags_only_for_covered_and_drops_them_when_state_changes()
    {
        var s = New(); s.ApplyProfile("portrait", Catalog);
        s.ApplyFacetStates(
            new Dictionary<string, FacetState> { ["clothing.footwear"] = FacetState.Covered, ["clothing.upper"] = FacetState.Missing, ["scene.season"] = FacetState.Covered },
            Catalog,
            new Dictionary<string, string> { ["clothing.footwear"] = " sandals ", ["clothing.upper"] = "shirt", ["scene.season"] = "autumn" });
        Assert.Equal(new Dictionary<string, string> { ["clothing.footwear"] = "sandals" }, s.FacetTags);   // missing 的不存；portrait 沒有 scene.season

        s.ApplyFacetStates(new Dictionary<string, FacetState> { ["clothing.footwear"] = FacetState.Waived }, Catalog);
        Assert.Empty(s.FacetTags);
    }

    [Fact]
    public void ApplyProfile_clears_facet_tags()
    {
        var s = New(); s.ApplyProfile("portrait", Catalog);
        s.ApplyFacetStates(new Dictionary<string, FacetState> { ["pose.gaze"] = FacetState.Covered }, Catalog, new Dictionary<string, string> { ["pose.gaze"] = "looking at viewer" });
        s.ApplyProfile("landscape", Catalog);
        Assert.Empty(s.FacetTags);
    }
```

`Snapshot_restore_reverts_everything_including_history_and_ledger` 在 `s.RecordFinalize(...)` 之前加一行 `s.ApplyFacetStates(new Dictionary<string, FacetState> { ["scene.season"] = FacetState.Covered }, Catalog, new Dictionary<string, string> { ["scene.season"] = "autumn" });`（那時 profile 已是 landscape），最後多一句 `Assert.Empty(s.FacetTags);`。

`src/PromptCopilot.Api.Tests/Plugins/SessionPluginTests.cs`（新檔）：

```csharp
using System.Threading.Channels;
using PromptCopilot.Api.Configuration;
using PromptCopilot.Api.Orchestration;
using PromptCopilot.Api.Plugins;
using PromptCopilot.Api.Safety;
using PromptCopilot.Api.Sessions;
using PromptCopilot.Api.Streaming;
using PromptCopilot.Api.Tests.Configuration;

namespace PromptCopilot.Api.Tests.Plugins;

public class SessionPluginTests
{
    private static readonly FacetCatalog Catalog = FacetCatalogTests.Real();

    private static (SessionPlugin plugin, Session s, ChannelReader<AgentEvent> events) Make()
    {
        var s = new Session("s"); s.ApplyProfile("portrait", Catalog);
        var ch = Channel.CreateUnbounded<AgentEvent>();
        var turn = new TurnContext(s, 1, GuardResult.Ok(false), ToolNames.Always, ch.Writer);
        return (new SessionPlugin(turn, Catalog), s, ch.Reader);
    }

    /// <summary>設計 §5.5：模型標 covered 時附的英文 tag 存進 session，dimensions 事件帶給前端。</summary>
    [Fact]
    public void Apply_stores_tags_of_covered_facets_and_emits_them_in_dimensions()
    {
        var (p, s, events) = Make();
        var r = p.SetFacetStates(new[]
        {
            new FacetStateEntry("clothing.footwear", "covered", Tags: "sandals"),
            new FacetStateEntry("clothing.upper", "missing", Tags: "shirt"),
            new FacetStateEntry("pose.gaze", "covered"),
        });
        Assert.Equal("ok：套用 3 筆", r);
        Assert.Equal("sandals", s.FacetTags["clothing.footwear"]);
        Assert.False(s.FacetTags.ContainsKey("clothing.upper"));
        Assert.False(s.FacetTags.ContainsKey("pose.gaze"));
        Assert.True(events.TryRead(out var e));
        var d = Assert.IsType<DimensionsEvent>(e);
        Assert.Equal("sandals", d.FacetTags!["clothing.footwear"]);
        Assert.Equal("covered", d.FacetStates["pose.gaze"]);
    }

    [Fact]
    public void Apply_without_tags_leaves_existing_tags_alone_until_state_changes()
    {
        var (p, s, _) = Make();
        p.SetFacetStates(new[] { new FacetStateEntry("clothing.footwear", "covered", Tags: "sandals") });
        p.SetFacetStates(new[] { new FacetStateEntry("clothing.footwear", "covered") });     // 沒帶 tags：保留
        Assert.Equal("sandals", s.FacetTags["clothing.footwear"]);
        p.SetFacetStates(new[] { new FacetStateEntry("clothing.footwear", "missing") });
        Assert.Empty(s.FacetTags);
    }
}
```

`ContractsTests.cs` 加：

```csharp
    /// <summary>設計 §5.5：tags 可省略（舊形狀照收），有就是逗號分隔的英文。</summary>
    [Fact]
    public void FacetStateEntry_tags_round_trips_and_may_be_omitted()
    {
        var parsed = JsonSerializer.Deserialize<FacetStateEntry[]>("""[{"facetId":"clothing.footwear","state":"covered","tags":"sandals"},{"facetId":"pose.gaze","state":"missing"}]""")!;
        Assert.Equal("sandals", parsed[0].Tags);
        Assert.Null(parsed[1].Tags); Assert.Null(parsed[1].Note);
        Assert.Equal("""{"facetId":"a","state":"covered","note":null,"tags":"x, y"}""", JsonSerializer.Serialize(new FacetStateEntry("a", "covered", Tags: "x, y")));
    }
```

`SystemPromptBuilderTests.cs` 加：

```csharp
    /// <summary>設計 §5.5：第 1 條要模型把 covered facet 的英文 tag 一起給，推薦的錨從這裡來。</summary>
    [Fact]
    public void Flow_rule_asks_for_english_tags_on_covered_facets()
    {
        var (prompt, _) = Make().Build(new Session("s"), ToolNames.Always);
        Assert.Contains("標 `covered`，並在 `tags` 附上那一項的英文 tag（例：涼鞋 → `sandals`）", prompt);
    }
```

`EndpointTests.cs` 加：

```csharp
    [Fact]
    public async Task Get_session_includes_facet_tags()
    {
        var store = _factory.Services.GetRequiredService<SessionStore>();
        var catalog = _factory.Services.GetRequiredService<PromptCopilot.Api.Configuration.FacetCatalog>();
        var s = store.Create(); s.ApplyProfile("portrait", catalog);
        s.ApplyFacetStates(new Dictionary<string, FacetState> { ["clothing.footwear"] = FacetState.Covered }, catalog, new Dictionary<string, string> { ["clothing.footwear"] = "sandals" });
        var body = await (await _client.GetAsync($"/api/sessions/{s.Id}")).Content.ReadAsStringAsync();
        Assert.Contains("\"facetTags\":{\"clothing.footwear\":\"sandals\"}", body);
    }
```

- [ ] **Step 2: 跑測試確認失敗**

Run: `dotnet test src/PromptCopilot.Api.Tests --filter "FullyQualifiedName~SessionTests|FullyQualifiedName~SessionPluginTests|FullyQualifiedName~ContractsTests"`
Expected: 編譯失敗（`Tags`、`FacetTags` 不存在）。

- [ ] **Step 3: 實作**

`SessionPlugin.cs`：

```csharp
/// <summary>Tags（2026-09-25，設計 §5.5）：facet 標 covered 時附使用者那一項的英文 tag（逗號分隔），是整套組合推薦的錨。其他狀態忽略。</summary>
public sealed record FacetStateEntry(
    [property: JsonPropertyName("facetId")] string FacetId,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("note")] string? Note = null,
    [property: JsonPropertyName("tags")] string? Tags = null);
```

`SetFacetStates` 的 `[Description]` 改成：

```
"更新 facet 狀態。使用者第一次描述題材時，先用它把已描述的 facet 標 covered，再依流程檢索或追問；covered 的 facet 附 tags：使用者那一項的英文 tag（例：涼鞋 → sandals；銀色雙馬尾 → silver hair, twintails）。另外用於 waived（使用者明說不要指定）與單一項目的委託（note 記「使用者委託此項」，狀態維持 missing）。"
```

`Apply`：

```csharp
        var parsed = new Dictionary<string, FacetState>();
        var tags = new Dictionary<string, string>();
        foreach (var u in updates)
        {
            if (!FacetStateParser.TryParse(u.State, out var st)) { turn.Rejections.Add($"facet {u.FacetId} 的狀態 '{u.State}' 無法解析，略過"); continue; }
            parsed[u.FacetId] = st;
            if (!string.IsNullOrWhiteSpace(u.Note)) turn.Session.FacetNotes[u.FacetId] = u.Note!;
            if (!string.IsNullOrWhiteSpace(u.Tags)) tags[u.FacetId] = u.Tags!;
        }
        turn.Session.ApplyFacetStates(parsed, catalog, tags);
```

`Session.cs`：

```csharp
public sealed record SessionSnapshot(
    SessionStatus Status, string? Profile, int AskCount, int DiscussStreak, bool AutoFill,
    Dictionary<string, FacetState> FacetStates, Dictionary<string, string> FacetNotes, int HistoryCount, PresetLedger Ledger, FinalPrompt? LastFinal, int TurnIndex,
    Dictionary<string, string> FacetTags);

    /// <summary>模型標 covered 時給的英文 tag（設計 §5.5），整套組合推薦的錨。只有 covered 的 facet 有；狀態改成別的就移除；隨 profile 重設。</summary>
    public Dictionary<string, string> FacetTags { get; private set; } = new();

    public void ApplyProfile(string profile, FacetCatalog catalog)
    {
        Profile = profile;
        FacetStates = catalog.IdsForProfile(profile).ToDictionary(id => id, _ => FacetState.Missing);
        FacetNotes = new();
        FacetTags = new();
    }

    /// <summary>只收 profile 適用的 facet；其餘丟掉（不信 LLM 自述）。tags 只在該 facet 這次標 covered 時存；非 covered 一律移除舊的。</summary>
    public void ApplyFacetStates(IReadOnlyDictionary<string, FacetState> updates, FacetCatalog catalog, IReadOnlyDictionary<string, string>? tags = null)
    {
        if (Profile is null) return;
        var applicable = catalog.IdsForProfile(Profile);
        foreach (var (id, state) in updates)
        {
            if (!applicable.Contains(id)) continue;
            FacetStates[id] = state;
            if (state != FacetState.Covered) { FacetTags.Remove(id); continue; }
            if (tags is not null && tags.TryGetValue(id, out var t) && !string.IsNullOrWhiteSpace(t)) FacetTags[id] = t.Trim();
        }
    }
```

`Snapshot()` 末尾多傳 `new Dictionary<string, string>(FacetTags)`；`Restore` 加 `FacetTags = new Dictionary<string, string>(s.FacetTags);`。

`AgentEvent.cs`：

```csharp
/// <summary>FacetTags（2026-09-25）：模型給 covered facet 的英文 tag，儀表板 chip 的 title 顯示。</summary>
public sealed record DimensionsEvent(string? Profile, IReadOnlyDictionary<string, string> FacetStates, IReadOnlyDictionary<string, string>? FacetTags = null) : AgentEvent("dimensions");
```

`TurnContext.DimensionsSnapshot()`：

```csharp
    public DimensionsEvent DimensionsSnapshot() =>
        new(Session.Profile, Session.FacetStates.ToDictionary(kv => kv.Key, kv => FacetStateParser.ToWire(kv.Value)), new Dictionary<string, string>(Session.FacetTags));
```

`SessionEndpoints.cs`：`SessionSnapshotDto` 末尾加 `IReadOnlyDictionary<string, string> FacetTags`；GET 的建構呼叫末尾加 `new Dictionary<string, string>(s.FacetTags)`；GET 的 `WithDescription` 第一段「這段對話是否使用知識庫（`retrieval`）」之後加「、模型給每個已涵蓋 facet 的英文 tag（`facetTags`）」。

`system.md` 第 1 條：把「把使用者這句話已經描述到的 facet 標 `covered`；沒講的維持 `missing`，不要猜。」改成「把使用者這句話已經描述到的 facet 標 `covered`，並在 `tags` 附上那一項的英文 tag（例：涼鞋 → `sandals`）；沒講的維持 `missing`，不要猜。」

- [ ] **Step 4: 跑測試**

Run: `dotnet test src/PromptCopilot.Api.Tests`
Expected: 全部 passed（`Flow_rule_asks_for_every_missing_dimension_and_marks_covered_before_search` 檢查的「先 `SetFacetStates`」字串仍在）。

- [ ] **Step 5: 主規格同步**

`2026-09-21-genai-prompt-copilot-design.md`：
- §4 工具表 `SessionPlugin.SetFacetStates` 列的說明末尾加「；covered 的 facet 附 `tags`（英文，2026-09-25，整套組合推薦的錨）」。
- 「`FacetStateEntry = { facetId: string, state: FacetState, note?: string }`」改成「`FacetStateEntry = { facetId: string, state: FacetState, note?: string, tags?: string }`」，同段末尾加一句「`tags`（2026-09-25）是模型把使用者那一項翻成的英文 tag，只在 covered 時存進 `Session.FacetTags`，見 `2026-09-25-set-recommendations-design.md` §5.5。」
- §10.2 `dimensions` 列的 `data:` 改成 `{ profile, facetStates: {facetId: state}, facetTags?: {facetId: "sandals"} }`。
- §10.1 `GET /api/sessions/{id}` 列的括號內加「、facetTags」。

- [ ] **Step 6: Commit**

```bash
git add src/PromptCopilot.Api src/PromptCopilot.Api.Tests docs/superpowers/specs/2026-09-21-genai-prompt-copilot-design.md
git commit -m "feat(api): model supplies english tags for covered facets; Session.FacetTags rides on dimensions and GET session"
```

---

### Task 5: `Adoption`、`Session.Adoptions`、`TagAttribution` 的 `adopted` 來源

**Files:**
- Create: `src/PromptCopilot.Api/Sessions/Adoption.cs`
- Modify: `src/PromptCopilot.Api/Sessions/Session.cs`
- Modify: `src/PromptCopilot.Api/Sessions/TagAttribution.cs`
- Modify: `src/PromptCopilot.Api/Plugins/DialogPlugin.cs`（`FinalizePrompt` 傳 `S.Adoptions`）
- Modify: `src/PromptCopilot.Api/Orchestration/AgenticOrchestrator.cs`（`TagOrigins` 多 `adopted`）
- Test: `src/PromptCopilot.Api.Tests/Sessions/TagAttributionTests.cs`
- Test: `src/PromptCopilot.Api.Tests/Sessions/SessionTests.cs`
- Test: `src/PromptCopilot.Api.Tests/Orchestration/AgenticOrchestratorTests.cs`（既有 `tagOrigins` 斷言）
- Modify: `docs/superpowers/specs/2026-09-21-genai-prompt-copilot-design.md`（§9 tag 來源那條）

**Interfaces:**
- Produces:
  - `record Adoption(int TurnIndex, long PresetId, string Title, string? SourceRef, string Dimension, IReadOnlyDictionary<string, IReadOnlyList<string>> Taken, IReadOnlyList<string> Kept, IReadOnlyList<string> Filled, IReadOnlyList<string> Replaced)`。
  - `List<Adoption> Session.Adoptions`；`Session.RecordAdoption(Adoption a, LedgerEntry preset)`；`SessionSnapshot(…, Dictionary<string,string> FacetTags, List<Adoption> Adoptions)`。
  - `TagAttribution.Adopted = "adopted"`；`Attribute(string prompt, PresetLedger ledger, bool negative, IReadOnlyList<Adoption>? adoptions = null)`；`public static` 的 `Split`、`Normalize`、`EndsWithWord`。
  - audit `tagOrigins` 形狀 `{"rag":n,"adopted":n,"llm":n,"base":n}`。

- [ ] **Step 1: 寫失敗的測試**

`TagAttributionTests.cs` 加：

```csharp
    private static Adoption Adopt(long id, string title, params (string facet, string tags)[] taken) =>
        new(2, id, title, "civitai:1:0", "clothing", taken.ToDictionary(t => t.facet, t => (IReadOnlyList<string>)t.tags.Split(", ")),
            Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>());

    /// <summary>設計 §6.5：優先序 base → adopted → rag → llm。</summary>
    [Fact]
    public void Adopted_wins_over_rag_and_loses_to_base()
    {
        var ledger = Ledger((5, "涼鞋套裝", "sandals, masterpiece, white socks", null));
        var r = TagAttribution.Attribute("masterpiece, sandals, white socks, 1girl", ledger, negative: false,
            new[] { Adopt(41720, "和風女僕", ("clothing.footwear", "sandals")) });
        Assert.Equal(new[] { "base", "adopted", "rag", "llm" }, r.Select(x => x.Origin));
        Assert.Equal(new long[] { 41720 }, r[1].PresetIds);
        Assert.Equal("和風女僕", r[1].PresetTitle);
        Assert.Equal("civitai:1:0", r[1].SourceRef);
    }

    [Fact]
    public void Adopted_uses_the_same_word_suffix_rule_both_ways()
    {
        var a = new[] { Adopt(1, "t", ("clothing.footwear", "platform sandals"), ("clothing.upper", "shirt")) };
        var r = TagAttribution.Attribute("sandals, white shirt, (Shirt:1.1)", Ledger(), negative: false, a);
        Assert.All(r, x => Assert.Equal("adopted", x.Origin));
    }

    [Fact]
    public void Latest_adoption_wins_when_two_sets_brought_the_same_tag()
    {
        var a = new[] { Adopt(1, "first", ("clothing.upper", "shirt")), Adopt(2, "second", ("clothing.upper", "shirt")) };
        Assert.Equal(new long[] { 2 }, Assert.Single(TagAttribution.Attribute("shirt", Ledger(), negative: false, a)).PresetIds);
    }

    [Fact]
    public void Adoptions_do_not_apply_to_the_negative_prompt()
    {
        var a = new[] { Adopt(1, "t", ("clothing.upper", "shirt")) };
        Assert.Equal("llm", Assert.Single(TagAttribution.Attribute("shirt", Ledger(), negative: true, a)).Origin);
    }

    [Fact]
    public void Split_normalize_and_endswithword_are_public_and_shared()
    {
        Assert.Equal(new[] { "a", "b c" }, TagAttribution.Split(" a ,, b c "));
        Assert.Equal("long hair", TagAttribution.Normalize("(Long_Hair:1.2)"));
        Assert.True(TagAttribution.EndsWithWord("platform sandals", "sandals"));
        Assert.False(TagAttribution.EndsWithWord("laptop", "top"));
    }
```

（`Ledger()` 已接受 params，不帶參數就是空 ledger。）

`SessionTests.cs` 加：

```csharp
    private static Adoption SomeAdoption(int turn = 3) =>
        new(turn, 41720, "和風女僕", "civitai:1:0", "clothing",
            new Dictionary<string, IReadOnlyList<string>> { ["clothing.upper"] = new[] { "purple kimono" } },
            new[] { "clothing.footwear" }, new[] { "clothing.upper" }, Array.Empty<string>());

    /// <summary>設計 §6.3：採用寫進 ledger，定稿 chip 能開抽屜、offered 區段會列它。</summary>
    [Fact]
    public void RecordAdoption_adds_to_list_ledger_and_offered()
    {
        var s = New(); s.ApplyProfile("portrait", Catalog);
        s.RecordAdoption(SomeAdoption(), new LedgerEntry { Id = 41720, Title = "和風女僕", PromptSnippet = "purple kimono, sandals", FacetIds = new[] { "clothing.upper", "clothing.footwear" } });
        Assert.Single(s.Adoptions);
        var e = s.Ledger.Get(41720)!;
        Assert.Equal("clothing", Assert.Single(e.Hits).Dimension);
        Assert.Equal("採用", Assert.Single(e.OfferedAs).Label);
        Assert.Equal(3, e.OfferedAs[0].TurnIndex);
    }
```

`Snapshot_restore_reverts_everything_including_history_and_ledger`：在 `s.RecordFinalize(...)` 之前加 `s.RecordAdoption(SomeAdoption(), new LedgerEntry { Id = 41720, Title = "t", PromptSnippet = "p", FacetIds = Array.Empty<string>() });`，最後加 `Assert.Empty(s.Adoptions); Assert.False(s.Ledger.Contains(41720));`。

`AgenticOrchestratorTests.cs`：`Budget_exhausted_forces_finalize_with_only_that_tool` 的斷言改成 `Assert.Contains("""tagOrigins":{"rag":0,"adopted":0,"llm":1,"base":1}""", completed.PayloadJson!);`。

- [ ] **Step 2: 跑測試確認失敗**

Run: `dotnet test src/PromptCopilot.Api.Tests --filter "FullyQualifiedName~TagAttributionTests|FullyQualifiedName~SessionTests"`
Expected: 編譯失敗（`Adoption` 不存在）。

- [ ] **Step 3: 實作**

`src/PromptCopilot.Api/Sessions/Adoption.cs`：

```csharp
namespace PromptCopilot.Api.Sessions;

/// <summary>使用者採用了推薦的一套組合（設計 §6.3）。Taken：照它的 facet → 該 facet 的 tag（資料庫原字）；Kept：保留我的。
/// Filled／Replaced 是採用前的狀態分類（missing／notApplicable → Filled；covered／waived → Replaced），給量測的擴充率與取代率用。
/// Title／SourceRef 讓 adopted chip 不用再查 ledger。</summary>
public sealed record Adoption(int TurnIndex, long PresetId, string Title, string? SourceRef, string Dimension,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Taken, IReadOnlyList<string> Kept,
    IReadOnlyList<string> Filled, IReadOnlyList<string> Replaced);
```

`Session.cs`：

```csharp
public sealed record SessionSnapshot(
    SessionStatus Status, string? Profile, int AskCount, int DiscussStreak, bool AutoFill,
    Dictionary<string, FacetState> FacetStates, Dictionary<string, string> FacetNotes, int HistoryCount, PresetLedger Ledger, FinalPrompt? LastFinal, int TurnIndex,
    Dictionary<string, string> FacetTags, List<Adoption> Adoptions);

    /// <summary>採用過的組合，依採用先後（設計 §6.3）。定稿時 TagAttribution 用它標 adopted。</summary>
    public List<Adoption> Adoptions { get; private set; } = new();

    /// <summary>採用寫進 ledger：定稿 chip 能開抽屜、system prompt 的 offered 區段會列它。dist 0、grounded true：
    /// 使用者親手選的，當然可借入。</summary>
    public void RecordAdoption(Adoption a, LedgerEntry preset)
    {
        Adoptions.Add(a);
        Ledger.Record(preset, new LedgerHit(a.Dimension, 0, true));
        Ledger.MarkOffered(preset.Id, new OfferedRef(a.TurnIndex, a.Dimension, "採用"));
    }
```

`Snapshot()` 末尾多傳 `new List<Adoption>(Adoptions)`；`Restore` 加 `Adoptions = new List<Adoption>(s.Adoptions);`。

`TagAttribution.cs`：

```csharp
    public const string Rag = "rag";
    public const string Llm = "llm";
    public const string Base = "base";
    /// <summary>採用組合帶進來的（設計 §6.5）：優先序 base → adopted → rag → llm。只看 positive：採用的都是正向 tag。</summary>
    public const string Adopted = "adopted";

    public static IReadOnlyList<TagSource> Attribute(string prompt, PresetLedger ledger, bool negative, IReadOnlyList<Adoption>? adoptions = null)
    {
        // …Split、index、snippets 不動…
        var baseWords = negative ? BaseNegative : BasePositive;
        return tags.Select(tag =>
        {
            var key = Normalize(tag);
            if (baseWords.Contains(key)) return new TagSource(tag, Base, Array.Empty<long>(), null);
            if (!negative && adoptions is { Count: > 0 } && AdoptedBy(key, adoptions) is { } a)
                return new TagSource(tag, Adopted, new[] { a.PresetId }, a.Title, a.SourceRef);
            // …rag／llm 不動…
        }).ToList();
    }

    /// <summary>最近一次採用優先：同一個 tag 被兩套都帶進來時，使用者最後選的那套才是它的出處。</summary>
    private static Adoption? AdoptedBy(string key, IReadOnlyList<Adoption> adoptions)
    {
        for (var i = adoptions.Count - 1; i >= 0; i--)
            foreach (var taken in adoptions[i].Taken.Values)
                foreach (var raw in taken)
                {
                    var t = Normalize(raw);
                    if (t.Length > 0 && (t == key || EndsWithWord(t, key) || EndsWithWord(key, t))) return adoptions[i];
                }
        return null;
    }

    public static bool EndsWithWord(string longer, string suffix) => …（原本的，改 public）
    public static List<string> Split(string? text) => …（改 public）
    public static string Normalize(string tag) …（改 public）
```

`DialogPlugin.FinalizePrompt`：`TagAttribution.Attribute(positive, S.Ledger, negative: false, S.Adoptions)`（negative 那個不傳）。

`AgenticOrchestrator.TagOrigins`：

```csharp
        return new
        {
            rag = s.Count(x => x.Origin == TagAttribution.Rag),
            adopted = s.Count(x => x.Origin == TagAttribution.Adopted),
            llm = s.Count(x => x.Origin == TagAttribution.Llm),
            @base = s.Count(x => x.Origin == TagAttribution.Base),
        };
```

- [ ] **Step 4: 跑測試**

Run: `dotnet test src/PromptCopilot.Api.Tests`
Expected: 全部 passed。

- [ ] **Step 5: 主規格 §9 同步**

「**定稿 tag 來源標示（2026-09-25 實作）。**」那條末尾加：「2026-09-25 起多第四種 `adopted`：使用者採用推薦組合帶進來的 tag（`Session.Adoptions` 的 `Taken`，同一套字尾規則，最近一次採用優先），優先序 base → adopted → rag → llm；只標 positive。見 `2026-09-25-set-recommendations-design.md` §6.5。」

- [ ] **Step 6: Commit**

```bash
git add src/PromptCopilot.Api src/PromptCopilot.Api.Tests docs/superpowers/specs/2026-09-21-genai-prompt-copilot-design.md
git commit -m "feat(api): Adoption record on the session and a fourth tag origin 'adopted' between base and rag"
```

---

### Task 6: `RecommendationService`、`recommendations` 事件、orchestrator 接線、audit

**Files:**
- Create: `src/PromptCopilot.Api/Streaming/Recommendations.cs`
- Create: `src/PromptCopilot.Api/Orchestration/RecommendationService.cs`
- Modify: `src/PromptCopilot.Api/Configuration/FacetCatalog.cs`（`DimensionsOf`）
- Modify: `src/PromptCopilot.Api/Configuration/Options.cs`（`RecommendationTake`、`RecommendationTimeoutSeconds`）
- Modify: `src/PromptCopilot.Api/Orchestration/AgenticOrchestrator.cs`
- Modify: `src/PromptCopilot.Api/Program.cs`
- Test: `src/PromptCopilot.Api.Tests/Orchestration/RecommendationServiceTests.cs`（新）
- Test: `src/PromptCopilot.Api.Tests/Orchestration/AgenticOrchestratorTests.cs`
- Test: `src/PromptCopilot.Api.Tests/Configuration/FacetCatalogTests.cs`
- Modify: `docs/superpowers/specs/2026-09-21-genai-prompt-copilot-design.md`（§10.2 事件表加 `recommendations` 列）

**Interfaces:**
- Consumes: Task 3 `PresetRepository.RecommendAsync`、`PresetCandidate`；Task 4 `Session.FacetTags`；Task 5 `TagAttribution.Split/Normalize/EndsWithWord`。
- Produces:
  - `record RecommendedFacet(string FacetId, string Label, string State, IReadOnlyList<string> Tags)`
  - `record RecommendedSet(long PresetId, string Title, string? ImageUrl, string? SourceRef, double Dist, IReadOnlyList<RecommendedFacet> Facets)`
  - `record RecommendedDimension(string Dimension, string Label, bool Anchored, IReadOnlyList<string> AnchorTags, IReadOnlyList<RecommendedSet> Sets)`
  - `record RecommendationsEvent(int TurnIndex, IReadOnlyList<RecommendedDimension> Dimensions) : AgentEvent("recommendations")`
  - `interface IRecommendationService { Task<RecommendationsEvent?> BuildAsync(Session s, TurnOutcome outcome, int turnIndex, CancellationToken ct); }`
  - `FacetCatalog.DimensionsOf(string profile) -> IReadOnlyList<string>`（依 facets.yaml 順序、只列該 profile 有 facet 的維度）。
  - `OrchestratorOptions.RecommendationTake = 3`、`RecommendationTimeoutSeconds = 20`。
  - `AgenticOrchestrator` 建構子多一個 `IRecommendationService recommendations` 參數（放在 `kernelFactory` 之前）。
  - audit `Turn_Completed` payload 多 `recommendations: { dimensions: [{ dimension, anchored, presetIds }] }`；新事件 `Recommendation_Failed`（payload `{ stage: "recommend", errorClass }`）。

- [ ] **Step 1: 寫失敗的測試**

`FacetCatalogTests.cs` 加：

```csharp
    [Fact]
    public void DimensionsOf_lists_only_dimensions_the_profile_has_in_yaml_order()
    {
        var c = Real();
        Assert.Equal(new[] { "style", "scene", "camera", "appearance", "pose", "clothing" }, c.DimensionsOf("portrait"));
        Assert.Equal(new[] { "style", "scene", "camera" }, c.DimensionsOf("landscape"));
    }
```

`src/PromptCopilot.Api.Tests/Orchestration/RecommendationServiceTests.cs`（新檔）：

```csharp
using Microsoft.SemanticKernel.ChatCompletion;
using PromptCopilot.Api.Configuration;
using PromptCopilot.Api.Data;
using PromptCopilot.Api.Llm;
using PromptCopilot.Api.Orchestration;
using PromptCopilot.Api.Plugins;
using PromptCopilot.Api.Sessions;
using PromptCopilot.Api.Tests.Configuration;

namespace PromptCopilot.Api.Tests.Orchestration;

public class RecommendationServiceTests
{
    private static readonly FacetCatalog Catalog = FacetCatalogTests.Real();

    private sealed class FakeEmbeddings : IEmbeddingClient
    {
        public List<string> Texts { get; } = new();
        public Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, string taskType, CancellationToken ct)
        {
            Texts.AddRange(texts);
            return Task.FromResult<IReadOnlyList<float[]>>(texts.Select(_ => new float[768]).ToList());
        }
    }

    /// <summary>記每次呼叫的參數；回什麼由 Script 決定（key：維度第一個 facet + 是否帶錨）。</summary>
    private sealed class FakePresets() : PresetRepository(null!)
    {
        public List<(string firstFacet, IReadOnlyList<string> anchorFacets, IReadOnlyList<string> anchorTags, int take)> Calls { get; } = new();
        public Dictionary<(string firstFacet, bool anchored), IReadOnlyList<PresetCandidate>> Script { get; } = new();
        public override Task<IReadOnlyList<PresetCandidate>> RecommendAsync(float[] query, IReadOnlyList<string> dimensionFacets,
            IReadOnlyList<string> anchorFacets, IReadOnlyList<string> anchorTags, int take, CancellationToken ct)
        {
            Calls.Add((dimensionFacets[0], anchorFacets, anchorTags, take));
            return Task.FromResult(Script.GetValueOrDefault((dimensionFacets[0], anchorFacets.Count > 0), Array.Empty<PresetCandidate>()));
        }
    }

    private static PresetCandidate Set(long id, string title, params (string facet, string tags)[] facetTags) =>
        new(id, title, facetTags.Select(f => f.facet).ToList(),
            facetTags.ToDictionary(f => f.facet, f => (IReadOnlyList<string>)f.tags.Split(", ")), "https://img", "civitai:1:0", 0.21);

    private static (RecommendationService svc, Session s, FakeEmbeddings embed, FakePresets presets) Make(params (string facet, string tags)[] covered)
    {
        var s = new Session("s"); s.ApplyProfile("portrait", Catalog);
        s.ChatHistory.AddSystemMessage("sys");
        s.ChatHistory.AddUserMessage("一個少女穿涼鞋");
        s.ApplyFacetStates(covered.ToDictionary(c => c.facet, _ => FacetState.Covered), Catalog, covered.ToDictionary(c => c.facet, c => c.tags));
        var embed = new FakeEmbeddings(); var presets = new FakePresets();
        return (new RecommendationService(Catalog, embed, presets, new OrchestratorOptions()), s, embed, presets);
    }

    private static AskOutcome Ask(params string[] dims) =>
        new("p", dims.Select(d => new AskItem(d, "q", new[] { Catalog.FacetsOf("portrait", d)[0] }, new[] { new OptionItem("a", "t", null), new OptionItem("b", "t", null) })).ToList());
    private static FinalizedOutcome Finalized(string positive) => new(new FinalPrompt(positive, "lowres", "t", "i"));

    [Fact]
    public async Task Ask_outcome_queries_only_the_asked_dimensions_in_ask_order()
    {
        var (svc, s, _, presets) = Make();
        presets.Script[("scene.location", false)] = new[] { Set(1, "雨夜", ("scene.location", "city street"), ("scene.weather", "rain")) };
        var e = await svc.BuildAsync(s, Ask("scene", "style"), 3, default);
        Assert.Equal(new[] { "scene.location", "style.genre" }, presets.Calls.Select(c => c.firstFacet));
        Assert.Equal("scene", Assert.Single(e!.Dimensions).Dimension);     // style 沒命中就不列
        Assert.Equal(3, e.TurnIndex);
    }

    [Fact]
    public async Task Finalized_outcome_queries_every_dimension_of_the_profile()
    {
        var (svc, s, _, presets) = Make();
        await svc.BuildAsync(s, Finalized("1girl"), 2, default);
        Assert.Equal(6, presets.Calls.Count);
        Assert.Equal("clothing.head", presets.Calls[5].firstFacet);
    }

    /// <summary>設計 §5.3：錨＝該維度 covered facet 的 FacetTags，正規化、去重；沒 covered 的維度不帶錨。</summary>
    [Fact]
    public async Task Anchors_come_from_facet_tags_normalized()
    {
        var (svc, s, _, presets) = Make(("clothing.footwear", "Sandals, platform_footwear"), ("clothing.upper", "(white shirt:1.2)"));
        await svc.BuildAsync(s, Ask("clothing", "style"), 1, default);
        var clothing = presets.Calls.Single(c => c.firstFacet == "clothing.head");
        Assert.Equal(new[] { "clothing.upper", "clothing.footwear" }, clothing.anchorFacets);       // facets.yaml 順序
        Assert.Equal(new[] { "white shirt", "sandals", "platform footwear" }, clothing.anchorTags);
        Assert.Equal(3, clothing.take);
        Assert.Empty(presets.Calls.Single(c => c.firstFacet == "style.genre").anchorTags);
    }

    [Fact]
    public async Task Finalized_adds_positive_tags_to_the_anchors_of_dimensions_with_covered_facets()
    {
        var (svc, s, _, presets) = Make(("clothing.footwear", "sandals"));
        await svc.BuildAsync(s, Finalized("masterpiece, 1girl, sandals, white socks"), 1, default);
        var clothing = presets.Calls.First(c => c.firstFacet == "clothing.head");
        Assert.Equal(new[] { "sandals", "masterpiece", "1girl", "white socks" }, clothing.anchorTags);
        Assert.Empty(presets.Calls.First(c => c.firstFacet == "style.genre").anchorTags);       // style 沒有 covered
    }

    [Fact]
    public async Task Anchored_query_with_two_hits_is_reported_anchored_with_the_matched_anchor_tags_only()
    {
        var (svc, s, _, presets) = Make(("clothing.footwear", "sandals, socks"));
        presets.Script[("clothing.head", true)] = new[]
        {
            Set(1, "夏日", ("clothing.upper", "front-tie top"), ("clothing.footwear", "platform sandals")),
            Set(2, "海邊", ("clothing.lower", "short shorts"), ("clothing.footwear", "sandals")),
        };
        var e = await svc.BuildAsync(s, Ask("clothing"), 1, default);
        var d = Assert.Single(e!.Dimensions);
        Assert.True(d.Anchored);
        Assert.Equal(new[] { "sandals" }, d.AnchorTags);                                  // socks 沒有任何候選命中
        Assert.Equal("人物穿著", d.Label);
        Assert.Single(presets.Calls);                                                     // 沒退回第二次查詢
        var set = d.Sets[0];
        Assert.Equal(6, set.Facets.Count);                                                // 該維度全部 facet，順序照 yaml
        Assert.Equal(new[] { "clothing.head", "clothing.upper", "clothing.lower", "clothing.footwear", "clothing.material", "clothing.accessories" }, set.Facets.Select(f => f.FacetId));
        Assert.Equal("covered", set.Facets[3].State); Assert.Equal(new[] { "platform sandals" }, set.Facets[3].Tags);
        Assert.Equal("missing", set.Facets[0].State); Assert.Empty(set.Facets[0].Tags);
        Assert.Equal("鞋履", set.Facets[3].Label);
        Assert.Equal(0.21, set.Dist); Assert.Equal("https://img", set.ImageUrl); Assert.Equal("civitai:1:0", set.SourceRef);
    }

    [Fact]
    public async Task Anchored_query_with_fewer_than_two_hits_falls_back_to_an_unanchored_query()
    {
        var (svc, s, _, presets) = Make(("clothing.footwear", "sandals"));
        presets.Script[("clothing.head", true)] = new[] { Set(1, "只有一套", ("clothing.footwear", "sandals"), ("clothing.upper", "x")) };
        presets.Script[("clothing.head", false)] = new[] { Set(2, "最接近", ("clothing.upper", "shirt"), ("clothing.lower", "jeans")), Set(3, "b", ("clothing.upper", "a"), ("clothing.lower", "b")) };
        var e = await svc.BuildAsync(s, Ask("clothing"), 1, default);
        var d = Assert.Single(e!.Dimensions);
        Assert.False(d.Anchored); Assert.Empty(d.AnchorTags);
        Assert.Equal(new long[] { 2, 3 }, d.Sets.Select(x => x.PresetId));
        Assert.Equal(2, presets.Calls.Count);
        Assert.Empty(presets.Calls[1].anchorFacets);
    }

    [Fact]
    public async Task No_covered_facets_means_one_unanchored_query()
    {
        var (svc, s, _, presets) = Make();
        presets.Script[("style.genre", false)] = new[] { Set(9, "油畫", ("style.genre", "oil painting"), ("style.palette", "muted")) };
        var e = await svc.BuildAsync(s, Ask("style"), 1, default);
        Assert.False(Assert.Single(e!.Dimensions).Anchored);
        Assert.Single(presets.Calls);
    }

    [Fact]
    public async Task Returns_null_when_no_dimension_has_candidates()
    {
        var (svc, s, _, _) = Make();
        Assert.Null(await svc.BuildAsync(s, Ask("style"), 1, default));
    }

    /// <summary>設計 §5.3：查詢向量＝使用者原話串接的最後 500 字，採用句不算；一輪只嵌入一次。</summary>
    [Fact]
    public async Task Query_text_joins_user_messages_skips_adoption_sentences_and_embeds_once()
    {
        var (svc, s, embed, presets) = Make();
        s.ChatHistory.AddAssistantMessage("好的");
        s.ChatHistory.AddUserMessage("採用〈和風女僕〉（知識庫 #41720）：上半身照它的（purple kimono）。");
        s.ChatHistory.AddUserMessage(new string('黃', 600) + "昏街頭");
        presets.Script[("style.genre", false)] = new[] { Set(9, "x", ("style.genre", "a"), ("style.palette", "b")) };
        await svc.BuildAsync(s, Finalized("1girl"), 1, default);
        var q = Assert.Single(embed.Texts);
        Assert.Equal(500, q.Length);
        Assert.EndsWith("昏街頭", q);
        Assert.DoesNotContain("採用〈", q);
        Assert.Equal("一個少女穿涼鞋\n" + new string('黃', 600) + "昏街頭", RecommendationService.JoinedUserText(s.ChatHistory));
    }

    [Fact]
    public async Task Facet_tags_for_facets_outside_the_dimension_are_ignored()
    {
        var (svc, s, _, presets) = Make();
        presets.Script[("style.genre", false)] = new[] { Set(9, "x", ("style.genre", "oil painting"), ("style.palette", "muted"), ("scene.weather", "rain")) };
        var e = await svc.BuildAsync(s, Ask("style"), 1, default);
        var set = Assert.Single(Assert.Single(e!.Dimensions).Sets);
        Assert.DoesNotContain(set.Facets, f => f.FacetId == "scene.weather");
        Assert.Equal(4, set.Facets.Count);
    }

    [Fact]
    public async Task Without_profile_returns_null_and_touches_nothing()
    {
        var s = new Session("s");
        var embed = new FakeEmbeddings(); var presets = new FakePresets();
        Assert.Null(await new RecommendationService(Catalog, embed, presets, new OrchestratorOptions()).BuildAsync(s, Ask("style"), 1, default));
        Assert.Empty(embed.Texts); Assert.Empty(presets.Calls);
    }
}
```

`AgenticOrchestratorTests.cs`：`Harness` 加：

```csharp
        /// <summary>預設不推薦：既有測試不該因為推薦而多出事件。</summary>
        public IRecommendationService Recommendations { get; set; } = new StubRecommendations(_ => Task.FromResult<RecommendationsEvent?>(null));
```

`Build()` 的 `new AgenticOrchestrator(...)` 在 `NullLogger<AgenticOrchestrator>.Instance` 之後、`kernelFactory:` 之前加 `Recommendations,`。類別內加：

```csharp
    internal sealed class StubRecommendations(Func<TurnOutcome, Task<RecommendationsEvent?>> impl) : IRecommendationService
    {
        public int Calls { get; private set; }
        public Task<RecommendationsEvent?> BuildAsync(Session s, TurnOutcome outcome, int turnIndex, CancellationToken ct) { Calls++; return impl(outcome); }
    }

    private static RecommendationsEvent SomeRecommendations(int turnIndex) => new(turnIndex, new[]
    {
        new RecommendedDimension("style", "風格", false, Array.Empty<string>(), new[]
        {
            new RecommendedSet(7, "油畫", null, null, 0.2, new[] { new RecommendedFacet("style.genre", "藝術流派／媒材", "missing", new[] { "oil painting" }) }),
        }),
    });

    /// <summary>設計 §5.1：推薦事件跟在 final 與 dimensions 之後；audit 記推薦了哪些 preset。</summary>
    [Fact]
    public async Task Recommendations_event_follows_final_and_is_audited()
    {
        var h = new Harness();
        h.Recommendations = new StubRecommendations(_ => Task.FromResult<RecommendationsEvent?>(SomeRecommendations(1)));
        h.Chat.ThenAsync(async (hist, k) =>
        {
            await Invoke(hist, k!, "Session", "SetProfile", new { profile = "portrait" });
            return new[] { await Invoke(hist, k!, "Dialog", "AskUser", AskArgs()) };
        });
        var events = await h.RunAsync("一個銀髮少女");
        var kinds = events.Select(e => e.Type).ToList();
        Assert.True(kinds.IndexOf("final") < kinds.LastIndexOf("dimensions") && kinds.LastIndexOf("dimensions") < kinds.IndexOf("recommendations"));
        Assert.Equal(7, Assert.Single(events.OfType<RecommendationsEvent>()).Dimensions[0].Sets[0].PresetId);
        var completed = Assert.Single(h.Audit.Entries, a => a.EventType == "Turn_Completed");
        Assert.Contains("""recommendations":{"dimensions":[{"dimension":"style","anchored":false,"presetIds":[7]}]}""", completed.PayloadJson!);
    }

    /// <summary>設計 §9：推薦是附加的。final 已宣告，推薦炸了不能回滾、不能發 error。</summary>
    [Fact]
    public async Task Recommendation_failure_does_not_roll_back_the_turn()
    {
        var h = new Harness();
        h.Recommendations = new StubRecommendations(_ => throw new InvalidOperationException("db down"));
        h.Chat.ThenAsync(async (hist, k) =>
        {
            await Invoke(hist, k!, "Session", "SetProfile", new { profile = "portrait" });
            return new[] { await Invoke(hist, k!, "Dialog", "AskUser", AskArgs()) };
        });
        var events = await h.RunAsync("一個銀髮少女");
        Assert.Single(events.OfType<FinalEvent>());
        Assert.Empty(events.OfType<ErrorEvent>());
        Assert.Empty(events.OfType<RecommendationsEvent>());
        Assert.Equal(1, h.Session.AskCount);
        var failed = Assert.Single(h.Audit.Entries, a => a.EventType == "Recommendation_Failed");
        Assert.Contains("\"errorClass\":\"InvalidOperationException\"", failed.PayloadJson!);
        Assert.Contains(h.Audit.Entries, a => a.EventType == "Turn_Completed");
    }

    [Fact]
    public async Task Recommendation_timeout_is_audited_as_timeout()
    {
        var h = new Harness();
        h.Options.RecommendationTimeoutSeconds = 0;        // CancellationTokenSource(0)：token 立刻取消
        h.Recommendations = new ObservingRecommendations();  // 會觀察 ct 的 stub，永遠等到被取消
        h.Chat.ThenAsync(async (hist, k) =>
        {
            await Invoke(hist, k!, "Session", "SetProfile", new { profile = "portrait" });
            return new[] { await Invoke(hist, k!, "Dialog", "AskUser", AskArgs()) };
        });
        var events = await h.RunAsync("一個銀髮少女");
        Assert.Single(events.OfType<FinalEvent>());
        Assert.Contains("\"errorClass\":\"Timeout\"", Assert.Single(h.Audit.Entries, a => a.EventType == "Recommendation_Failed").PayloadJson!);
    }

    private sealed class ObservingRecommendations : IRecommendationService
    {
        public async Task<RecommendationsEvent?> BuildAsync(Session s, TurnOutcome outcome, int turnIndex, CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            return null;
        }
    }

    [Fact]
    public async Task Retrieval_off_never_calls_recommendations()
    {
        var h = new Harness();
        var stub = new StubRecommendations(_ => Task.FromResult<RecommendationsEvent?>(SomeRecommendations(1)));
        h.Recommendations = stub;
        var off = new Session("off", retrievalEnabled: false);
        h.Chat.ThenAsync(async (hist, k) =>
        {
            await Invoke(hist, k!, "Session", "SetProfile", new { profile = "portrait" });
            return new[] { await Invoke(hist, k!, "Dialog", "AskUser", AskArgs()) };
        });
        h.GuardChat.Then(FakeChatCompletion.Text(OkVerdict)); h.ClassifierChat.Then(FakeChatCompletion.Text(OkVerdict));
        var events = new List<AgentEvent>();
        await foreach (var e in h.Build().RunTurnAsync(off, "一個銀髮少女", default)) events.Add(e);
        Assert.Equal(0, stub.Calls);
        Assert.Empty(events.OfType<RecommendationsEvent>());
    }
```

- [ ] **Step 2: 跑測試確認失敗**

Run: `dotnet test src/PromptCopilot.Api.Tests --filter "FullyQualifiedName~RecommendationServiceTests|FullyQualifiedName~FacetCatalogTests"`
Expected: 編譯失敗。

- [ ] **Step 3: 實作**

`Options.cs` 的 `OrchestratorOptions` 加：

```csharp
    /// <summary>整套組合推薦：每個維度幾套（設計 §5.3）。</summary>
    public int RecommendationTake { get; set; } = 3;
    /// <summary>推薦自己的逾時：它在 final 宣告之後才跑，不能掛在整輪的 token 上（那會走回滾）。</summary>
    public int RecommendationTimeoutSeconds { get; set; } = 20;
```

`FacetCatalog.cs` 加：

```csharp
    /// <summary>該 profile 有 facet 的維度，依 facets.yaml 順序。</summary>
    public IReadOnlyList<string> DimensionsOf(string profile) => Dimensions.Where(d => FacetsOf(profile, d).Count > 0).ToList();
```

`src/PromptCopilot.Api/Streaming/Recommendations.cs`：

```csharp
namespace PromptCopilot.Api.Streaming;

/// <summary>整套組合推薦事件（設計 §5.4）。跟在 final 與 dimensions 之後；模型看不到這些。
/// Facets 列該維度對本 profile 的全部 facet（yaml 順序），State 是本輪結束時的四態，Tags 取自 facet_tags（沒有就空）。</summary>
public sealed record RecommendedFacet(string FacetId, string Label, string State, IReadOnlyList<string> Tags);
public sealed record RecommendedSet(long PresetId, string Title, string? ImageUrl, string? SourceRef, double Dist, IReadOnlyList<RecommendedFacet> Facets);
/// <summary>Anchored：候選是先用「含使用者講的元素」過濾的；AnchorTags 是實際命中的錨。false 時是純向量排序的「最接近你描述的組合」。</summary>
public sealed record RecommendedDimension(string Dimension, string Label, bool Anchored, IReadOnlyList<string> AnchorTags, IReadOnlyList<RecommendedSet> Sets);
public sealed record RecommendationsEvent(int TurnIndex, IReadOnlyList<RecommendedDimension> Dimensions) : AgentEvent("recommendations");
```

`src/PromptCopilot.Api/Orchestration/RecommendationService.cs`：

```csharp
using Microsoft.SemanticKernel.ChatCompletion;
using PromptCopilot.Api.Configuration;
using PromptCopilot.Api.Data;
using PromptCopilot.Api.Llm;
using PromptCopilot.Api.Plugins;
using PromptCopilot.Api.Sessions;
using PromptCopilot.Api.Streaming;

namespace PromptCopilot.Api.Orchestration;

public interface IRecommendationService
{
    /// <summary>沒有任何維度有候選（或 profile 未設）時回 null，不發事件。</summary>
    Task<RecommendationsEvent?> BuildAsync(Session s, TurnOutcome outcome, int turnIndex, CancellationToken ct);
}

/// <summary>整套組合推薦（設計 §5）。由伺服器產生、模型不知道：推薦系統要「每次都在、每次一樣」。
/// 追問時只查被問的維度，定稿時查本 profile 全部維度；每個維度：錨（covered facet 的 FacetTags＋定稿 positive）
/// 有就先過濾再向量排序，命中不到 2 筆退回純向量。</summary>
public sealed class RecommendationService(FacetCatalog catalog, IEmbeddingClient embed, PresetRepository presets, OrchestratorOptions options) : IRecommendationService
{
    public const int QueryChars = 500;
    public const int MinAnchoredHits = 2;
    /// <summary>伺服器組的採用句開頭（AdoptionComposer 也用這個字串）。</summary>
    public const string AdoptionPrefix = "採用〈";

    public async Task<RecommendationsEvent?> BuildAsync(Session s, TurnOutcome outcome, int turnIndex, CancellationToken ct)
    {
        if (s.Profile is null) return null;
        var profile = s.Profile;
        IReadOnlyList<string> dimensions = outcome switch
        {
            AskOutcome a => a.Asks.Select(x => x.Dimension).Distinct().Where(d => catalog.FacetsOf(profile, d).Count > 0).ToList(),
            FinalizedOutcome => catalog.DimensionsOf(profile),
            _ => Array.Empty<string>(),
        };
        if (dimensions.Count == 0) return null;
        var query = QueryText(s.ChatHistory);
        if (query.Length == 0) return null;
        var vec = (await embed.EmbedAsync(new[] { query }, GeminiEmbeddingClient.RetrievalQuery, ct))[0];
        var finalTags = outcome is FinalizedOutcome f
            ? TagAttribution.Split(f.Final.Positive).Select(TagAttribution.Normalize).Where(t => t.Length > 0).ToList()
            : new List<string>();

        var result = new List<RecommendedDimension>();
        foreach (var dim in dimensions)
        {
            var facets = catalog.FacetsOf(profile, dim);
            var covered = facets.Where(x => s.FacetStates.GetValueOrDefault(x) == FacetState.Covered).ToList();
            var anchors = AnchorTags(s, covered, finalTags);
            IReadOnlyList<PresetCandidate> hits = Array.Empty<PresetCandidate>();
            var anchored = false;
            if (anchors.Count > 0)
            {
                hits = await presets.RecommendAsync(vec, facets, covered, anchors, options.RecommendationTake, ct);
                anchored = hits.Count >= MinAnchoredHits;
            }
            if (!anchored) hits = await presets.RecommendAsync(vec, facets, Array.Empty<string>(), Array.Empty<string>(), options.RecommendationTake, ct);
            if (hits.Count == 0) continue;
            var matched = anchored ? MatchedAnchors(hits, covered, anchors) : Array.Empty<string>();
            var sets = hits.Select(h => new RecommendedSet(h.Id, h.Title, h.ImageUrl, h.SourceRef, Math.Round(h.Dist, 3),
                facets.Select(x => new RecommendedFacet(x, catalog.Facets[x].Label,
                    FacetStateParser.ToWire(s.FacetStates.GetValueOrDefault(x, FacetState.Missing)),
                    h.FacetTags.GetValueOrDefault(x) ?? Array.Empty<string>())).ToList())).ToList();
            result.Add(new RecommendedDimension(dim, catalog.DimensionLabel(dim, profile), anchored, matched, sets));
        }
        return result.Count == 0 ? null : new RecommendationsEvent(turnIndex, result);
    }

    /// <summary>本 session 使用者講過的原話依序串接；伺服器組的採用句不算（那不是描述）。</summary>
    public static string JoinedUserText(ChatHistory history) =>
        string.Join("\n", history
            .Where(m => m.Role == AuthorRole.User && !string.IsNullOrWhiteSpace(m.Content) && !m.Content!.TrimStart().StartsWith(AdoptionPrefix, StringComparison.Ordinal))
            .Select(m => m.Content!.Trim()));

    /// <summary>查詢向量的來源：最後 500 字。定稿前後同一個查法，跟片段的中文 embedding 同語言。</summary>
    public static string QueryText(ChatHistory history)
    {
        var joined = JoinedUserText(history);
        return joined.Length <= QueryChars ? joined : joined[^QueryChars..];
    }

    /// <summary>該維度 covered facet 的錨：模型給的 FacetTags，定稿時再加 positive 的全部 tag。全部正規化、去重、保序。</summary>
    public static IReadOnlyList<string> AnchorTags(Session s, IReadOnlyList<string> covered, IReadOnlyList<string> finalTags)
    {
        if (covered.Count == 0) return Array.Empty<string>();
        var set = new List<string>();
        foreach (var f in covered)
            if (s.FacetTags.TryGetValue(f, out var raw))
                foreach (var t in TagAttribution.Split(raw).Select(TagAttribution.Normalize))
                    if (t.Length > 0 && !set.Contains(t)) set.Add(t);
        foreach (var t in finalTags) if (!set.Contains(t)) set.Add(t);
        return set;
    }

    private static IReadOnlyList<string> MatchedAnchors(IReadOnlyList<PresetCandidate> hits, IReadOnlyList<string> covered, IReadOnlyList<string> anchors) =>
        anchors.Where(a => hits.Any(h => covered.Any(f => (h.FacetTags.GetValueOrDefault(f) ?? Array.Empty<string>())
            .Select(TagAttribution.Normalize).Any(t => t == a || TagAttribution.EndsWithWord(t, a) || TagAttribution.EndsWithWord(a, t))))).ToList();
}
```

`AgenticOrchestrator.cs`：建構子在 `ILogger<AgenticOrchestrator> logger,` 之後加 `IRecommendationService recommendations,`。`ExecuteAsync` 的 `writer.TryWrite(dimensions);` 之後：

```csharp
            var recommended = await TryRecommendAsync(session, turn.Outcome, turnIndex, version, text);
            if (recommended is not null) writer.TryWrite(recommended);
```

`Turn_Completed` 的 `Payload(...)` 在 `("tagOrigins", …)` 之後加：

```csharp
                    ("recommendations", recommended is null ? null : (object)new
                    {
                        dimensions = recommended.Dimensions.Select(d => new { dimension = d.Dimension, anchored = d.Anchored, presetIds = d.Sets.Select(x => x.PresetId).ToArray() }).ToArray(),
                    })
```

加方法：

```csharp
    /// <summary>推薦是附加的（設計 §5.1、§9）：final 已宣告出去，推薦失敗或逾時只記 audit，不回滾、不發 error。
    /// 用自己的逾時，不掛在整輪的 token 上——整輪的 token 取消會走回滾路徑。</summary>
    private async Task<RecommendationsEvent?> TryRecommendAsync(Session session, TurnOutcome outcome, int turnIndex, string version, string text)
    {
        if (!session.RetrievalEnabled || outcome is not (AskOutcome or FinalizedOutcome)) return null;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(options.RecommendationTimeoutSeconds));
        try { return await recommendations.BuildAsync(session, outcome, turnIndex, cts.Token); }
        catch (Exception e)
        {
            logger.LogWarning(e, "recommendations failed for session {SessionId} turn {TurnIndex}", session.Id, turnIndex);
            await TryAuditAsync(new AuditEntry(session.Id, turnIndex, "Recommendation_Failed", version, text,
                Payload(("stage", "recommend"), ("errorClass", e is OperationCanceledException ? "Timeout" : e.GetType().Name))));
            return null;
        }
    }
```

`Program.cs`：`services.AddSingleton<AgentKernelFactory>();` 之後加 `services.AddSingleton<IRecommendationService, RecommendationService>();`；`new AgenticOrchestrator(...)` 在 `sp.GetRequiredService<ILogger<AgenticOrchestrator>>(),` 之後加 `sp.GetRequiredService<IRecommendationService>(),`。

- [ ] **Step 4: 跑測試**

Run: `dotnet test src/PromptCopilot.Api.Tests`
Expected: 全部 passed（含 `EndpointTests`：`Program` 的 DI 要能組出 `RecommendationService`）。

- [ ] **Step 5: 主規格 §10.2 同步**

事件表 `dimensions` 列之後加一列：

```
| `recommendations` | `{ turnIndex, dimensions: [{ dimension, label, anchored, anchorTags, sets: [{ presetId, title, imageUrl?, sourceRef?, dist, facets: [{ facetId, label, state, tags }] }] }] }` | 整套組合推薦（2026-09-25）：追問時只有被問的維度、定稿時全部維度，跟在 `final`＋`dimensions` 之後；掛在該輪的追問卡／定稿卡下方。`retrieval: off` 的對話沒有。見 `2026-09-25-set-recommendations-design.md` §5 |
```

「成功的一輪以 `final` + `dimensions` 收尾」改成「成功的一輪以 `final` + `dimensions`（+ `recommendations`，有的話）收尾」；`SessionEndpoints` 的 `WithDescription` 事件表也加同一列、同一句。

- [ ] **Step 6: Commit**

```bash
git add src/PromptCopilot.Api src/PromptCopilot.Api.Tests docs/superpowers/specs/2026-09-21-genai-prompt-copilot-design.md
git commit -m "feat(api): RecommendationService emits per-dimension set recommendations after ask/finalize; audited, never rolls back"
```

---

### Task 7: `AdoptionComposer`：驗證 `adopt` 請求、組採用句、算 Filled／Replaced

**Files:**
- Create: `src/PromptCopilot.Api/Sessions/AdoptionComposer.cs`
- Test: `src/PromptCopilot.Api.Tests/Sessions/AdoptionComposerTests.cs`（新）

**Interfaces:**
- Consumes: Task 3 `PresetDetail.FacetTags`；Task 5 `Adoption`。
- Produces:
  - `record AdoptRequest(long PresetId, string Dimension, IReadOnlyList<string>? Take)`（線上 `{ presetId, dimension, take }`）。
  - `class AdoptValidationException(string message) : Exception`。
  - `record ComposedAdoption(string Text, Adoption Adoption, LedgerEntry Preset)`。
  - `AdoptionComposer.Compose(AdoptRequest req, PresetDetail preset, Session s, FacetCatalog catalog, int turnIndex) -> ComposedAdoption`（丟 `AdoptValidationException` 的都是 400 的情況；retrieval off 與 profile 未設由端點先擋成 409）。
  - `AdoptionComposer.Prefix = "採用〈"`（與 `RecommendationService.AdoptionPrefix` 同值；Task 8 把兩處合成引用同一個常數）。

- [ ] **Step 1: 寫失敗的測試**

`src/PromptCopilot.Api.Tests/Sessions/AdoptionComposerTests.cs`：

```csharp
using PromptCopilot.Api.Configuration;
using PromptCopilot.Api.Data;
using PromptCopilot.Api.Sessions;
using PromptCopilot.Api.Tests.Configuration;

namespace PromptCopilot.Api.Tests.Sessions;

public class AdoptionComposerTests
{
    private static readonly FacetCatalog Catalog = FacetCatalogTests.Real();

    private static PresetDetail Preset(IReadOnlyDictionary<string, IReadOnlyList<string>>? facetTags) =>
        new(41720, "和風女僕紫和服", "Clothing", "d", new[] { "kimono" }, new[] { "clothing.head", "clothing.upper", "clothing.footwear" },
            "detached sleeves, purple kimono, maid headdress, sandals", null, "https://img", "civitai:9:0", "https://civitai.com/images/9", facetTags);

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> Tags = new Dictionary<string, IReadOnlyList<string>>
    {
        ["clothing.head"] = new[] { "maid headdress" },
        ["clothing.upper"] = new[] { "purple kimono", "detached sleeves" },
        ["clothing.footwear"] = new[] { "sandals" },
        ["clothing.lower"] = Array.Empty<string>(),
    };

    private static Session Sess(params (string facet, FacetState state)[] states)
    {
        var s = new Session("s"); s.ApplyProfile("portrait", Catalog);
        s.ApplyFacetStates(states.ToDictionary(x => x.facet, x => x.state), Catalog);
        return s;
    }

    /// <summary>設計 §6.2：照它的依 facets.yaml 順序、tags 以「, 」相接；保留我的是該維度其餘 facet（notApplicable 除外）。</summary>
    [Fact]
    public void Composes_the_sentence_in_yaml_order_with_kept_facets()
    {
        var s = Sess(("clothing.footwear", FacetState.Covered), ("clothing.material", FacetState.NotApplicable));
        var c = AdoptionComposer.Compose(new AdoptRequest(41720, "clothing", new[] { "clothing.upper", "clothing.head" }), Preset(Tags), s, Catalog, 4);
        Assert.Equal("採用〈和風女僕紫和服〉（知識庫 #41720）：頭部配件照它的（maid headdress）、上半身照它的（purple kimono, detached sleeves）；下半身、鞋履、配件飾品保留我的。", c.Text);
        Assert.Equal(4, c.Adoption.TurnIndex);
        Assert.Equal(new[] { "clothing.head", "clothing.upper" }, c.Adoption.Taken.Keys);
        Assert.Equal(new[] { "clothing.lower", "clothing.footwear", "clothing.accessories" }, c.Adoption.Kept);
        Assert.Equal("civitai:9:0", c.Adoption.SourceRef);
        Assert.Equal(41720, c.Preset.Id); Assert.Equal("detached sleeves, purple kimono, maid headdress, sandals", c.Preset.PromptSnippet);
        Assert.Equal("https://img", c.Preset.ImageUrl);
    }

    [Fact]
    public void Omits_the_kept_clause_when_nothing_is_kept()
    {
        var s = Sess(("clothing.lower", FacetState.NotApplicable), ("clothing.material", FacetState.NotApplicable), ("clothing.accessories", FacetState.NotApplicable));
        var c = AdoptionComposer.Compose(new AdoptRequest(41720, "clothing", new[] { "clothing.head", "clothing.upper", "clothing.footwear" }), Preset(Tags), s, Catalog, 1);
        Assert.EndsWith("鞋履照它的（sandals）。", c.Text);
        Assert.DoesNotContain("保留我的", c.Text);
        Assert.Empty(c.Adoption.Kept);
    }

    /// <summary>量測用：Filled＝原本 missing／notApplicable；Replaced＝原本 covered／waived。</summary>
    [Fact]
    public void Classifies_taken_facets_into_filled_and_replaced_by_their_previous_state()
    {
        var s = Sess(("clothing.footwear", FacetState.Covered), ("clothing.head", FacetState.Waived));
        var c = AdoptionComposer.Compose(new AdoptRequest(41720, "clothing", new[] { "clothing.head", "clothing.upper", "clothing.footwear" }), Preset(Tags), s, Catalog, 1);
        Assert.Equal(new[] { "clothing.upper" }, c.Adoption.Filled);
        Assert.Equal(new[] { "clothing.head", "clothing.footwear" }, c.Adoption.Replaced);
    }

    [Fact]
    public void Dedups_take()
    {
        var c = AdoptionComposer.Compose(new AdoptRequest(41720, "clothing", new[] { "clothing.upper", "clothing.upper" }), Preset(Tags), Sess(), Catalog, 1);
        Assert.Single(c.Adoption.Taken);
    }

    [Theory]
    [InlineData("clothing", new[] { "clothing.lower" }, "這套沒有 下半身 的 tag")]          // 有鍵但空
    [InlineData("clothing", new[] { "clothing.accessories" }, "這套沒有 配件飾品 的 tag")]  // 沒鍵
    [InlineData("clothing", new[] { "scene.weather" }, "facet scene.weather 不屬於維度 clothing")]
    [InlineData("clothing", new string[0], "take 不可為空")]
    [InlineData("nope", new[] { "clothing.upper" }, "維度 nope 對 portrait 不適用或不存在")]
    public void Rejects_take_facet_the_set_has_no_tags_for_and_other_bad_requests(string dimension, string[] take, string message)
    {
        var e = Assert.Throws<AdoptValidationException>(() => AdoptionComposer.Compose(new AdoptRequest(41720, dimension, take), Preset(Tags), Sess(), Catalog, 1));
        Assert.StartsWith(message, e.Message);
    }

    [Fact]
    public void Rejects_null_take_preset_without_facet_tags_and_session_without_profile()
    {
        Assert.Throws<AdoptValidationException>(() => AdoptionComposer.Compose(new AdoptRequest(41720, "clothing", null), Preset(Tags), Sess(), Catalog, 1));
        Assert.Contains("尚未拆分", Assert.Throws<AdoptValidationException>(() => AdoptionComposer.Compose(new AdoptRequest(41720, "clothing", new[] { "clothing.upper" }), Preset(null), Sess(), Catalog, 1)).Message);
        Assert.Contains("題材", Assert.Throws<AdoptValidationException>(() => AdoptionComposer.Compose(new AdoptRequest(41720, "clothing", new[] { "clothing.upper" }), Preset(Tags), new Session("x"), Catalog, 1)).Message);
    }
}
```

- [ ] **Step 2: 跑測試確認失敗**

Run: `dotnet test src/PromptCopilot.Api.Tests --filter "FullyQualifiedName~AdoptionComposerTests"`
Expected: 編譯失敗。

- [ ] **Step 3: 實作**

`src/PromptCopilot.Api/Sessions/AdoptionComposer.cs`：

```csharp
using PromptCopilot.Api.Configuration;
using PromptCopilot.Api.Data;

namespace PromptCopilot.Api.Sessions;

/// <summary>POST /messages 的 adopt 欄位（設計 §6.1）。Take：照它的 facet；該維度其餘 facet 視為保留我的。</summary>
public sealed record AdoptRequest(long PresetId, string Dimension, IReadOnlyList<string>? Take);

/// <summary>請求本身不成立（端點回 400）。retrieval off 與 profile 未設是 409，端點先擋。</summary>
public sealed class AdoptValidationException(string message) : Exception(message);

public sealed record ComposedAdoption(string Text, Adoption Adoption, LedgerEntry Preset);

/// <summary>採用句由伺服器組（設計 §6.2）：模型每次看到的形狀一致、tag 一定是資料庫的原字。純函式，不動 session。</summary>
public static class AdoptionComposer
{
    public const string Prefix = "採用〈";

    public static ComposedAdoption Compose(AdoptRequest req, PresetDetail preset, Session s, FacetCatalog catalog, int turnIndex)
    {
        if (s.Profile is null) throw new AdoptValidationException("尚未判定題材，還不能採用組合");
        if (preset.FacetTags is null) throw new AdoptValidationException("這筆片段尚未拆分 facet，無法採用");
        var facets = catalog.FacetsOf(s.Profile, req.Dimension);
        if (facets.Count == 0) throw new AdoptValidationException($"維度 {req.Dimension} 對 {s.Profile} 不適用或不存在");
        var take = (req.Take ?? Array.Empty<string>()).Distinct().ToList();
        if (take.Count == 0) throw new AdoptValidationException("take 不可為空：至少一個 facet 照它的");
        foreach (var f in take)
        {
            if (!facets.Contains(f)) throw new AdoptValidationException($"facet {f} 不屬於維度 {req.Dimension}");
            if ((preset.FacetTags.GetValueOrDefault(f)?.Count ?? 0) == 0) throw new AdoptValidationException($"這套沒有 {catalog.Facets[f].Label} 的 tag");
        }

        var ordered = facets.Where(take.Contains).ToList();                                   // 依 facets.yaml 順序
        var taken = ordered.ToDictionary(f => f, f => preset.FacetTags[f]);
        FacetState StateOf(string f) => s.FacetStates.GetValueOrDefault(f, FacetState.Missing);
        var kept = facets.Where(f => !take.Contains(f) && StateOf(f) != FacetState.NotApplicable).ToList();
        var filled = ordered.Where(f => StateOf(f) is FacetState.Missing or FacetState.NotApplicable).ToList();
        var replaced = ordered.Where(f => !filled.Contains(f)).ToList();

        var takeText = string.Join("、", ordered.Select(f => $"{catalog.Facets[f].Label}照它的（{string.Join(", ", taken[f])}）"));
        var keptText = kept.Count == 0 ? "" : $"；{string.Join("、", kept.Select(f => catalog.Facets[f].Label))}保留我的";
        var text = $"{Prefix}{preset.Title}〉（知識庫 #{preset.Id}）：{takeText}{keptText}。";

        var adoption = new Adoption(turnIndex, preset.Id, preset.Title, preset.SourceRef, req.Dimension, taken, kept, filled, replaced);
        var entry = new LedgerEntry
        {
            Id = preset.Id, Title = preset.Title, PromptSnippet = preset.PromptSnippet, NegativeSnippet = preset.NegativeSnippet,
            FacetIds = preset.FacetIds, ImageUrl = preset.ImageUrl, SourceRef = preset.SourceRef,
        };
        return new ComposedAdoption(text, adoption, entry);
    }
}
```

`RecommendationService.AdoptionPrefix` 改成 `public const string AdoptionPrefix = AdoptionComposer.Prefix;`。

- [ ] **Step 4: 跑測試**

Run: `dotnet test src/PromptCopilot.Api.Tests --filter "FullyQualifiedName~AdoptionComposerTests"`
Expected: 全部 passed。

- [ ] **Step 5: Commit**

```bash
git add src/PromptCopilot.Api/Sessions/AdoptionComposer.cs src/PromptCopilot.Api/Orchestration/RecommendationService.cs src/PromptCopilot.Api.Tests/Sessions/AdoptionComposerTests.cs
git commit -m "feat(api): AdoptionComposer validates an adopt request and composes the server-side adoption sentence"
```

---

### Task 8: `TurnInput`：orchestrator 收採用、`SessionEvent.Text`、audit `adoption`、prompt 第 6 條

**Files:**
- Modify: `src/PromptCopilot.Api/Orchestration/IPromptOrchestrator.cs`
- Modify: `src/PromptCopilot.Api/Orchestration/StateMachineOrchestrator.cs`
- Modify: `src/PromptCopilot.Api/Orchestration/AgenticOrchestrator.cs`
- Modify: `src/PromptCopilot.Api/Streaming/AgentEvent.cs`（`SessionEvent`）
- Modify: `src/PromptCopilot.Api/Endpoints/SessionEndpoints.cs`（只改呼叫 `RunTurnAsync` 那行，`adopt` 欄位在 Task 9）
- Modify: `src/PromptCopilot.Api/Prompts/system.md`
- Test: `src/PromptCopilot.Api.Tests/Orchestration/AgenticOrchestratorTests.cs`
- Test: `src/PromptCopilot.Api.Tests/Orchestration/SystemPromptBuilderTests.cs`
- Test: `src/PromptCopilot.Api.Tests/Endpoints/EndpointTests.cs`（`FakeOrchestrator` 改簽名）

**Interfaces:**
- Consumes: Task 5 `Adoption`、`Session.RecordAdoption`；Task 7 `ComposedAdoption`。
- Produces:
  - `record TurnInput(string Text, Adoption? Adoption = null, LedgerEntry? AdoptedPreset = null)`；`IPromptOrchestrator.RunTurnAsync(Session session, TurnInput input, CancellationToken ct)`。
  - `SessionEvent(string SessionId, int TurnIndex, string Status, string? Text = null)`：只有採用輪帶 `text`（線上省略 null）。
  - audit `Turn_Completed` payload 多 `adoption: { presetId, dimension, take, filled, replaced }`。
  - `system.md`「## 流程」第 6 條。

- [ ] **Step 1: 寫失敗的測試**

`AgenticOrchestratorTests.cs`：`Harness` 加一個多載，原本的 `RunAsync(string)` 改成呼叫它：

```csharp
        public Task<List<AgentEvent>> RunAsync(string text, CancellationToken ct = default) => RunAsync(new TurnInput(text), ct);

        public async Task<List<AgentEvent>> RunAsync(TurnInput input, CancellationToken ct = default)
        {
            GuardChat.Then(FakeChatCompletion.Text(OkVerdict));
            ClassifierChat.Then(FakeChatCompletion.Text(OkVerdict));
            var events = new List<AgentEvent>();
            await foreach (var e in Build().RunTurnAsync(Session, input, ct)) events.Add(e);
            return events;
        }
```

既有直接呼叫 `RunTurnAsync(h.Session, "x", default)`／`RunTurnAsync(off, "一個銀髮少女", default)` 的測試改成 `new TurnInput("x")`。加：

```csharp
    private static TurnInput AdoptInput() => new(
        "採用〈和風女僕〉（知識庫 #41720）：上半身照它的（purple kimono, detached sleeves）；鞋履保留我的。",
        new Adoption(0, 41720, "和風女僕", "civitai:9:0", "clothing",
            new Dictionary<string, IReadOnlyList<string>> { ["clothing.upper"] = new[] { "purple kimono", "detached sleeves" } },
            new[] { "clothing.footwear" }, new[] { "clothing.upper" }, Array.Empty<string>()),
        new LedgerEntry { Id = 41720, Title = "和風女僕", PromptSnippet = "purple kimono, detached sleeves, sandals", FacetIds = new[] { "clothing.upper", "clothing.footwear" }, SourceRef = "civitai:9:0" });

    /// <summary>設計 §6.3／§8：採用輪的 session 事件帶伺服器組的句子；記帳、寫 ledger；定稿 tag 標 adopted；audit 記 adoption。</summary>
    [Fact]
    public async Task Adoption_turn_records_adoption_marks_ledger_and_audits()
    {
        var h = new Harness();
        h.Session.ApplyProfile("portrait", Catalog);
        h.Session.RecordFinalize(new FinalPrompt("1girl", "lowres", "t", "i"));   // 採用發生在定稿之後；Finalized 時不掛 AskUser，定稿閘門不會擋
        h.Chat.ThenAsync(async (hist, k) =>
        {
            Assert.StartsWith("採用〈", hist.Last(m => m.Role == AuthorRole.User).Content!);
            return new[] { await Invoke(hist, k!, "Dialog", "FinalizePrompt", new
            {
                positivePrompt = "masterpiece, 1girl, purple kimono, detached sleeves, sandals", negativePrompt = "lowres", tips = "t", intentSummary = "和服少女",
                facetStates = new[] { new { facetId = "clothing.upper", state = "covered", tags = "purple kimono, detached sleeves" } },
            }) };
        });
        var events = await h.RunAsync(AdoptInput());

        Assert.Equal(AdoptInput().Text, Assert.Single(events.OfType<SessionEvent>()).Text);
        var a = Assert.Single(h.Session.Adoptions);
        Assert.Equal(1, a.TurnIndex);                                                   // orchestrator 補上真正的輪次
        Assert.Equal("採用", Assert.Single(h.Session.Ledger.Get(41720)!.OfferedAs).Label);
        var final = Assert.Single(events.OfType<FinalEvent>());
        Assert.Equal(new[] { "base", "llm", "adopted", "adopted", "rag" }, final.PositiveSources!.Select(x => x.Origin));   // sandals 在 ledger 片段裡
        var completed = Assert.Single(h.Audit.Entries, a2 => a2.EventType == "Turn_Completed");
        Assert.Contains("""adoption":{"presetId":41720,"dimension":"clothing","take":["clothing.upper"],"filled":["clothing.upper"],"replaced":[]}""", completed.PayloadJson!);
        Assert.Contains("""tagOrigins":{"rag":1,"adopted":2,"llm":1,"base":1}""", completed.PayloadJson!);
        Assert.StartsWith("採用〈", completed.RawInput!);
    }

    [Fact]
    public async Task Ordinary_turn_session_event_has_no_text_and_no_adoption_in_audit()
    {
        var h = new Harness();
        h.Chat.ThenAsync(async (hist, k) =>
        {
            await Invoke(hist, k!, "Session", "SetProfile", new { profile = "portrait" });
            return new[] { await Invoke(hist, k!, "Dialog", "AskUser", AskArgs()) };
        });
        var events = await h.RunAsync("一個銀髮少女");
        Assert.Null(Assert.Single(events.OfType<SessionEvent>()).Text);
        Assert.DoesNotContain("adoption", Assert.Single(h.Audit.Entries, a => a.EventType == "Turn_Completed").PayloadJson!);
    }

    /// <summary>設計 §9：採用那一輪失敗要連 Adoptions 與 ledger 一起回滾。</summary>
    [Fact]
    public async Task Failed_adoption_turn_rolls_back_adoptions_and_ledger()
    {
        var h = new Harness();
        h.Session.ApplyProfile("portrait", Catalog);
        h.Session.RecordFinalize(new FinalPrompt("1girl", "lowres", "t", "i"));
        h.Chat.Throw(new InvalidOperationException("boom"));
        var events = await h.RunAsync(AdoptInput());
        Assert.Single(events.OfType<ErrorEvent>());
        Assert.Empty(h.Session.Adoptions);
        Assert.False(h.Session.Ledger.Contains(41720));
    }
```

`SystemPromptBuilderTests.cs` 加：

```csharp
    /// <summary>設計 §6.4：採用句的處理規則。</summary>
    [Fact]
    public void Flow_rule_tells_the_model_how_to_handle_an_adoption_message()
    {
        var (prompt, _) = Make().Build(new Session("s"), ToolNames.Always);
        Assert.Contains("6. 使用者訊息以「採用〈」開頭時", prompt);
        Assert.Contains("然後直接 `FinalizePrompt`，不要追問", prompt);
        // off 模式也要有：規則無害，而且 off 的 session 根本不會收到採用句
        Assert.Contains("6. 使用者訊息以「採用〈」開頭時", Make().Build(new Session("s", retrievalEnabled: false), ToolsWithoutSearch).Prompt);
    }
```

`EndpointTests.FakeOrchestrator`：

```csharp
        public async IAsyncEnumerable<AgentEvent> RunTurnAsync(Session session, TurnInput input, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            yield return new SessionEvent(session.Id, 1, "Collecting", input.Adoption is null ? null : input.Text);
            await Task.Delay(10, ct);
            if (input.Text == ThrowTrigger) throw new InvalidOperationException("boom");
            yield return new FinalEvent("message", Message: $"echo: {input.Text}");
        }
```

- [ ] **Step 2: 跑測試確認失敗**

Run: `dotnet test src/PromptCopilot.Api.Tests --filter "FullyQualifiedName~AgenticOrchestratorTests"`
Expected: 編譯失敗（`TurnInput` 不存在）。

- [ ] **Step 3: 實作**

`IPromptOrchestrator.cs`：

```csharp
/// <summary>一輪的輸入：使用者原文；或伺服器組好的採用句加上要記帳的採用與片段（設計 §6）。</summary>
public sealed record TurnInput(string Text, Adoption? Adoption = null, LedgerEntry? AdoptedPreset = null);

public interface IPromptOrchestrator
{
    IAsyncEnumerable<AgentEvent> RunTurnAsync(Session session, TurnInput input, CancellationToken ct);
}
```

`StateMachineOrchestrator.RunTurnAsync` 簽名同步改成 `TurnInput input`。

`AgentEvent.cs`：

```csharp
/// <summary>Text（2026-09-25）：採用輪才有，是伺服器組的「採用〈…〉」句；前端拿它換掉使用者泡泡的暫代文字。</summary>
public sealed record SessionEvent(string SessionId, int TurnIndex, string Status, string? Text = null) : AgentEvent("session");
```

`AgenticOrchestrator.cs`：
- `RunTurnAsync(Session session, TurnInput input, CancellationToken ct)`，內部 `ExecuteAsync(session, input, channel.Writer, ct)`。
- `ExecuteAsync(Session session, TurnInput input, ChannelWriter<AgentEvent> writer, CancellationToken ct)` 開頭：

```csharp
        var text = input.Text;
        var turnIndex = session.TurnIndex + 1;
        writer.TryWrite(new SessionEvent(session.Id, turnIndex, session.Status.ToString(), input.Adoption is null ? null : text));
```

- `stage = "setup"` 那段，在 `session.TurnIndex = turnIndex;` 之後加：

```csharp
            // 採用：快照已取，這裡記的帳失敗時會一起回滾（設計 §9）。TurnIndex 由這裡補，端點不知道輪次。
            if (input.Adoption is { } adoption) session.RecordAdoption(adoption with { TurnIndex = turnIndex }, input.AdoptedPreset!);
```

- `Turn_Completed` 的 `Payload(...)` 在 `("recommendations", …)` 之後加：

```csharp
                    ("adoption", input.Adoption is null ? null : (object)new
                    {
                        presetId = input.Adoption.PresetId, dimension = input.Adoption.Dimension,
                        take = input.Adoption.Taken.Keys.ToArray(), filled = input.Adoption.Filled, replaced = input.Adoption.Replaced,
                    })
```

`SessionEndpoints.cs` 的 `/messages`：`orchestrator.RunTurnAsync(s, req.Text.Trim(), …)` 改成 `orchestrator.RunTurnAsync(s, new TurnInput(req.Text.Trim()), …)`（Task 9 再換成完整的判斷）。

`system.md`「## 流程」第 5 條之後加：

```
6. 使用者訊息以「採用〈」開頭時，那是他從推薦的組合裡挑了一套：「照它的」facet 寫入括號內的 tag（原字，不改寫）、狀態設 `covered`、`tags` 填同樣的字、note 記「採用知識庫 #編號」；「保留我的」facet 維持原狀。然後直接 `FinalizePrompt`，不要追問。
```

- [ ] **Step 4: 跑測試**

Run: `dotnet test src/PromptCopilot.Api.Tests`
Expected: 全部 passed。

- [ ] **Step 5: 主規格 §10.2 同步**

`session` 列的 `data:` 改成 `{ sessionId, turnIndex, status, text? }`，說明加「`text`（2026-09-25）只在採用輪出現，是伺服器組的採用句，前端用它換掉使用者泡泡」。

- [ ] **Step 6: Commit**

```bash
git add src/PromptCopilot.Api src/PromptCopilot.Api.Tests docs/superpowers/specs/2026-09-21-genai-prompt-copilot-design.md
git commit -m "feat(api): TurnInput carries an adoption through the orchestrator; session event echoes the composed sentence; prompt rule 6"
```

---

### Task 9: `POST /api/sessions/{id}/messages` 的 `adopt` 欄位

**Files:**
- Modify: `src/PromptCopilot.Api/Endpoints/SessionEndpoints.cs`
- Test: `src/PromptCopilot.Api.Tests/Endpoints/EndpointTests.cs`
- Modify: `docs/superpowers/specs/2026-09-21-genai-prompt-copilot-design.md`（§10.1 `/messages` 列）

**Interfaces:**
- Consumes: Task 7 `AdoptRequest`、`AdoptionComposer.Compose`、`AdoptValidationException`；Task 8 `TurnInput`；Task 3 `PresetRepository.GetAsync`。
- Produces: `MessageRequest(string? Text, AdoptRequest? Adopt)`。body `{ "adopt": { "presetId": 41720, "dimension": "clothing", "take": ["clothing.upper"] } }` 時 `text` 忽略。回應：`400`（preset 不存在／未拆分 facet／`take` 不合法／兩個欄位都沒給）、`409`（`retrieval: off`、尚未判定題材、上一輪還在跑）、其餘同一般訊息。

- [ ] **Step 1: 寫失敗的測試**

`EndpointTests.FakePresets` 改成：

```csharp
    /// <summary>不打 DB。id 1 是一筆已拆分 facet 的 civitai preset；id 2 尚未拆分；其他都不存在。</summary>
    private sealed class FakePresets() : PresetRepository(null!)
    {
        public override Task<PresetDetail?> GetAsync(long id, CancellationToken ct) => Task.FromResult(id switch
        {
            1 => new PresetDetail(1, "霓虹雨夜街頭", "Scene", "昏暗雨夜的賽博龐克街道", ["neon", "rain"], ["scene.location", "scene.weather"],
                "neon city street, rain, night", null, null, "civitai:12345:0", SourceAttribution.UrlFor("civitai:12345:0"),
                new Dictionary<string, IReadOnlyList<string>> { ["scene.location"] = new[] { "neon city street" }, ["scene.weather"] = new[] { "rain" } }),
            2 => new PresetDetail(2, "未拆分", "Scene", "d", ["x"], ["scene.location"], "x", null, null, null, null),
            _ => null,
        });
    }
```

加測試：

```csharp
    private Session PortraitSession(bool retrieval = true)
    {
        var store = _factory.Services.GetRequiredService<SessionStore>();
        var s = store.Create(retrieval);
        s.ApplyProfile("portrait", _factory.Services.GetRequiredService<PromptCopilot.Api.Configuration.FacetCatalog>());
        return s;
    }

    /// <summary>設計 §6.1／§6.2：伺服器組句、串流第一個事件帶那句。</summary>
    [Fact]
    public async Task Adopt_composes_the_user_sentence_and_streams_it()
    {
        var s = PortraitSession();
        var r = await _client.PostAsJsonAsync($"/api/sessions/{s.Id}/messages", new { adopt = new { presetId = 1, dimension = "scene", take = new[] { "scene.location" } } });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var body = await r.Content.ReadAsStringAsync();
        Assert.Contains("\"text\":\"採用〈霓虹雨夜街頭〉（知識庫 #1）：地點類型照它的（neon city street）；前景元素、中景／主體周邊、背景與遠景、光源與時間、天氣氛圍保留我的。\"", body);
        Assert.Contains("echo: 採用〈霓虹雨夜街頭〉", body);
    }

    [Theory]
    [InlineData("""{"adopt":{"presetId":99,"dimension":"scene","take":["scene.location"]}}""", HttpStatusCode.BadRequest, "找不到")]
    [InlineData("""{"adopt":{"presetId":2,"dimension":"scene","take":["scene.location"]}}""", HttpStatusCode.BadRequest, "尚未拆分")]
    [InlineData("""{"adopt":{"presetId":1,"dimension":"scene","take":[]}}""", HttpStatusCode.BadRequest, "take 不可為空")]
    [InlineData("""{"adopt":{"presetId":1,"dimension":"scene","take":["clothing.upper"]}}""", HttpStatusCode.BadRequest, "不屬於維度")]
    [InlineData("""{}""", HttpStatusCode.BadRequest, "text 不可為空")]
    public async Task Adopt_rejects_bad_requests(string json, HttpStatusCode status, string message)
    {
        var s = PortraitSession();
        var r = await _client.PostAsync($"/api/sessions/{s.Id}/messages", new StringContent(json, System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(status, r.StatusCode);
        Assert.Contains(message, (await r.Content.ReadFromJsonAsync<Dictionary<string, string>>())!["error"]);
    }

    [Fact]
    public async Task Adopt_is_409_when_retrieval_is_off_or_profile_is_unset()
    {
        var off = PortraitSession(retrieval: false);
        var r1 = await _client.PostAsJsonAsync($"/api/sessions/{off.Id}/messages", new { adopt = new { presetId = 1, dimension = "scene", take = new[] { "scene.location" } } });
        Assert.Equal(HttpStatusCode.Conflict, r1.StatusCode);
        var fresh = _factory.Services.GetRequiredService<SessionStore>().Create();
        var r2 = await _client.PostAsJsonAsync($"/api/sessions/{fresh.Id}/messages", new { adopt = new { presetId = 1, dimension = "scene", take = new[] { "scene.location" } } });
        Assert.Equal(HttpStatusCode.Conflict, r2.StatusCode);
        Assert.Contains("題材", (await r2.Content.ReadFromJsonAsync<Dictionary<string, string>>())!["error"]);
    }
```

- [ ] **Step 2: 跑測試確認失敗**

Run: `dotnet test src/PromptCopilot.Api.Tests --filter "FullyQualifiedName~EndpointTests"`
Expected: 新測試 FAIL（`adopt` 被忽略，回 400 `text 不可為空`）。

- [ ] **Step 3: 實作**

`SessionEndpoints.cs`：

```csharp
/// <summary>Adopt（2026-09-25）：採用推薦的一套組合；有它時 Text 忽略（設計 §6.1）。</summary>
public sealed record MessageRequest(string? Text, AdoptRequest? Adopt = null);
```

`/messages` 端點改成：

```csharp
        g.MapPost("/{id}/messages", async (string id, MessageRequest req, SessionStore store, IPromptOrchestrator orchestrator,
            PresetRepository presets, FacetCatalog catalog, HttpContext http) =>
        {
            var s = store.TryGet(id);
            if (s is null) return Results.NotFound(new ErrorBody("session 不存在或已過期"));
            if (req.Adopt is null && string.IsNullOrWhiteSpace(req.Text)) return Results.BadRequest(new ErrorBody("text 不可為空"));
            if (!await s.Lock.WaitAsync(0)) return Results.Conflict(new ErrorBody("這個 session 還有一輪在跑"));
            try
            {
                TurnInput input;
                if (req.Adopt is { } adopt)
                {
                    // 拿著鎖再讀 session：沒鎖時讀到的可能是上一輪回滾中的半途狀態
                    if (!s.RetrievalEnabled) return Results.Conflict(new ErrorBody("這段對話沒有知識庫，沒有組合可以採用"));
                    if (s.Profile is null) return Results.Conflict(new ErrorBody("尚未判定題材，還不能採用組合"));
                    var preset = await presets.GetAsync(adopt.PresetId, http.RequestAborted);
                    if (preset is null) return Results.BadRequest(new ErrorBody($"找不到 preset #{adopt.PresetId}"));
                    try
                    {
                        var c = AdoptionComposer.Compose(adopt, preset, s, catalog, s.TurnIndex + 1);
                        input = new TurnInput(c.Text, c.Adoption, c.Preset);
                    }
                    catch (AdoptValidationException e) { return Results.BadRequest(new ErrorBody(e.Message)); }
                }
                else input = new TurnInput(req.Text!.Trim());

                await SseWriter.WriteAsync(http.Response, orchestrator.RunTurnAsync(s, input, http.RequestAborted), http.RequestAborted);
                return Results.Empty;
            }
            // …catch 與 finally 不動…
        })
```

`WithDescription` 的 body 說明改成：

```
body：`{"text": "一個銀髮少女站在雨夜的霓虹街頭"}`；或採用推薦的一套組合 `{"adopt": {"presetId": 41720, "dimension": "clothing", "take": ["clothing.upper", "clothing.head"]}}`（`take` 是「照它的」facet，其餘該維度的 facet 保留使用者原本的；有 `adopt` 時 `text` 忽略，伺服器會組一句「採用〈標題〉（知識庫 #id）：…照它的（tags）；…保留我的。」當使用者訊息，`session` 事件的 `text` 帶回這句）。回應是 `text/event-stream`，…
```

狀態碼清單加：

```
- `400`：`text` 是空白且沒有 `adopt`；`adopt` 的 preset 不存在、尚未拆分 facet、`take` 為空或含不屬於該維度／這套沒有 tag 的 facet
- `409`：同一個 session 上一輪還沒跑完；`adopt` 但這段對話 `retrieval: off` 或尚未判定題材
```

（原本的 `400`／`409` 兩行以這兩行取代。）

- [ ] **Step 4: 跑測試**

Run: `dotnet test src/PromptCopilot.Api.Tests`
Expected: 全部 passed（`Api_documents_every_status_code` 類的 OpenAPI 測試若檢查 `/messages` 的狀態碼集合 `200,400,404,409`，集合不變）。

- [ ] **Step 5: 主規格 §10.1 同步**

`/messages` 列改成：「body `{ text }` 或 `{ adopt: { presetId, dimension, take } }`（2026-09-25 採用推薦組合，伺服器組句）；回 `text/event-stream`；同一 session 已有一輪在跑 → `409`；`adopt` 但 `retrieval: off` 或未判定題材 → `409`；`adopt` 不合法 → `400`」。

- [ ] **Step 6: Commit**

```bash
git add src/PromptCopilot.Api/Endpoints/SessionEndpoints.cs src/PromptCopilot.Api.Tests/Endpoints/EndpointTests.cs docs/superpowers/specs/2026-09-21-genai-prompt-copilot-design.md
git commit -m "feat(api): POST /messages accepts adopt {presetId, dimension, take}; 400 on bad take, 409 when retrieval is off"
```

---

### Task 10: 前端型別與 reducer：`recommendations` 事件、`session.text`、`facetTags`、`adopted`

**Files:**
- Modify: `src/PromptCopilot.Frontend/types/api.ts`
- Modify: `src/PromptCopilot.Frontend/lib/reducer.ts`
- Test: `src/PromptCopilot.Frontend/tests/reducer.test.ts`

**Interfaces:**
- Produces（`types/api.ts`）：
  - `RecommendedFacet { facetId; label; state: FacetState; tags: string[] }`、`RecommendedSet { presetId; title; imageUrl?; sourceRef?; dist; facets: RecommendedFacet[] }`、`RecommendedDimension { dimension; label; anchored; anchorTags: string[]; sets: RecommendedSet[] }`、`Recommendations { turnIndex; dimensions: RecommendedDimension[] }`。
  - `AgentEvent` 多 `{ type: 'recommendations' } & Recommendations`；`session` 多 `text?: string | null`；`dimensions` 多 `facetTags?: Record<string, string>`；`AGENT_EVENT_TYPES` 多 `'recommendations'`。
  - `TagSource.origin: 'rag' | 'llm' | 'base' | 'adopted'`。
  - `AdoptRequest { presetId: number; dimension: string; take: string[] }`。
  - `SessionSnapshotDto.facetTags?: Record<string, string>`；`PresetDetail.facetTags?: Record<string, string[]> | null`。
- Produces（`lib/reducer.ts`）：`FinalEntry.recommendations?: Recommendations | null`；`ChatState.facetTags: Record<string, string>`。

- [ ] **Step 1: 寫失敗的測試**

`tests/reducer.test.ts` 頂部的 fixture 旁加：

```ts
const recs: AgentEvent = { type: 'recommendations', turnIndex: 1, dimensions: [{ dimension: 'style', label: '風格', anchored: false, anchorTags: [], sets: [
  { presetId: 7, title: '油畫', imageUrl: 'https://img', sourceRef: 'civitai:1:0', dist: 0.2, facets: [{ facetId: 'style.genre', label: '藝術流派', state: 'missing', tags: ['oil painting'] }] },
] }] }
```

在 `describe('applyEvent', …)` 裡加：

```ts
  // 設計 §7.1：推薦掛在該輪的追問卡／定稿卡上，重載後跟著 transcript 一起回來
  it('recommendations attaches to the final entry of its turn', () => {
    let s = applyEvent(started(), ask)
    s = applyEvent(s, recs)
    const entry = s.transcript.at(-1) as any
    expect(entry.kind).toBe('final')
    expect(entry.recommendations.dimensions[0].sets[0].presetId).toBe(7)
  })

  it('recommendations for a turn without a final entry is ignored', () => {
    const s = applyEvent(started(), { ...recs, turnIndex: 9 } as AgentEvent)
    expect(s.transcript.some(e => e.kind === 'final')).toBe(false)
  })

  // 採用輪：送出當下泡泡是暫代字，session 事件帶伺服器組的那句
  it('session with text replaces the pending user entry text', () => {
    let s = beginTurn({ ...initialState(), sessionId: 's1' }, '採用〈油畫〉…')
    s = applyEvent(s, { type: 'session', sessionId: 's1', turnIndex: 2, status: 'Finalized', text: '採用〈油畫〉（知識庫 #7）：藝術流派照它的（oil painting）。' })
    expect(s.transcript.at(-1)).toEqual({ kind: 'user', text: '採用〈油畫〉（知識庫 #7）：藝術流派照它的（oil painting）。' })
  })

  it('session without text leaves the user entry alone', () => {
    const s = started()
    expect(s.transcript.at(-1)).toEqual({ kind: 'user', text: '一個女生' })
  })

  it('dimensions carries facetTags; missing key resets to {}', () => {
    let s = applyEvent(started(), { ...dims, facetTags: { 'style.genre': 'oil painting' } } as AgentEvent)
    expect(s.facetTags).toEqual({ 'style.genre': 'oil painting' })
    s = applyEvent(s, dims)
    expect(s.facetTags).toEqual({})
  })
```

在 `describe('hydrate', …)`（既有）裡加：

```ts
  it('takes facetTags from the dto and defaults to {} for an old backend; old final entries without recommendations survive', () => {
    const dto = { sessionId: 's1', status: 'Collecting', profile: 'portrait', turnIndex: 1, askCount: 0, askLimit: 2, facetStates: {}, lastFinal: null } as SessionSnapshotDto
    const old: Entry[] = [{ kind: 'user', text: 'x' }, { kind: 'final', turnIndex: 1, data: { kind: 'ask', preamble: 'p', asks: [] } }]
    expect(hydrate(initialState(), dto, old).facetTags).toEqual({})
    expect((hydrate(initialState(), dto, old).transcript[1] as any).recommendations).toBeUndefined()
    expect(hydrate(initialState(), { ...dto, facetTags: { 'style.genre': 'anime' } }, old).facetTags).toEqual({ 'style.genre': 'anime' })
  })
```

- [ ] **Step 2: 跑測試確認失敗**

Run: `cd src/PromptCopilot.Frontend && npm test -- tests/reducer.test.ts`
Expected: 型別錯誤或 FAIL（`recommendations` 不是合法事件、`facetTags` 不存在）。

- [ ] **Step 3: 實作**

`types/api.ts`：

```ts
/** 定稿 tag 的來源…（原註解）。adopted（2026-09-25）：使用者採用推薦組合帶進來的，presetIds 是那套的 id。 */
export interface TagSource { tag: string; origin: 'rag' | 'llm' | 'base' | 'adopted'; presetIds: number[]; presetTitle?: string | null; sourceRef?: string | null }

/** 整套組合推薦（2026-09-25，設計 §5.4）：跟在 final 之後，掛在該輪的追問卡／定稿卡上。facets 列該維度全部 facet，state 是本輪結束時的四態。 */
export interface RecommendedFacet { facetId: string; label: string; state: FacetState; tags: string[] }
export interface RecommendedSet { presetId: number; title: string; imageUrl?: string | null; sourceRef?: string | null; dist: number; facets: RecommendedFacet[] }
export interface RecommendedDimension { dimension: string; label: string; anchored: boolean; anchorTags: string[]; sets: RecommendedSet[] }
export interface Recommendations { turnIndex: number; dimensions: RecommendedDimension[] }

/** POST /api/sessions/{id}/messages 的 adopt：照它的 facet 清單，其餘該維度 facet 保留使用者原本的。 */
export interface AdoptRequest { presetId: number; dimension: string; take: string[] }

export type AgentEvent =
  | { type: 'session'; sessionId: string; turnIndex: number; status: SessionStatus; text?: string | null }
  | { type: 'tool_call'; callId: string; name: string; argsSummary: string }
  | { type: 'tool_result'; callId: string; name: string; summary: string; presets?: PresetRef[]; detail?: ToolDetail }
  | { type: 'dimensions'; profile?: string | null; facetStates: Record<string, FacetState>; facetTags?: Record<string, string> }
  | { type: 'token'; text: string }
  | ({ type: 'final' } & FinalData)
  | ({ type: 'recommendations' } & Recommendations)
  | { type: 'blocked'; reason: string; message: string }
  | { type: 'error'; code: string; message: string }

export const AGENT_EVENT_TYPES = ['session', 'tool_call', 'tool_result', 'dimensions', 'token', 'final', 'recommendations', 'blocked', 'error'] as const satisfies readonly AgentEventType[]
```

`SessionSnapshotDto` 加 `/** 模型給每個已涵蓋 facet 的英文 tag（2026-09-25）；舊後端沒有 */ facetTags?: Record<string, string>`；`PresetDetail` 加 `/** tag → facet 拆分（2026-09-25）；尚未回填時省略 */ facetTags?: Record<string, string[]> | null`。

`lib/reducer.ts`：

```ts
/** recommendations 是 2026-09-25 加的：舊條目沒有這個鍵。 */
export interface FinalEntry { kind: 'final'; turnIndex: number; data: FinalData; recommendations?: Recommendations | null }

export interface ChatState {
  …
  /** 模型給每個已涵蓋 facet 的英文 tag；儀表板 chip 的 title 顯示 */
  facetTags: Record<string, string>
  …
}
```

`initialState()` 加 `facetTags: {}`；`hydrate` 的回傳加 `facetTags: { ...(dto.facetTags ?? {}) }`。`applyEvent`：

```ts
    case 'session': {
      const next = { ...state, sessionId: ev.sessionId, turnIndex: ev.turnIndex, status: ev.status, highlighted: [] }
      // 採用輪：泡泡先放暫代字，這裡換成伺服器組的那句（只換這一輪還在等的那則）
      if (!ev.text || !state.pending) return next
      const idx = findLastIndex(state.transcript, e => e.kind === 'user')
      if (idx < 0) return next
      const transcript = state.transcript.slice()
      transcript[idx] = { kind: 'user', text: ev.text }
      return { ...next, transcript }
    }

    case 'dimensions':
      return { ...state, profile: ev.profile ?? null, facetStates: { ...ev.facetStates }, facetTags: { ...(ev.facetTags ?? {}) } }

    case 'recommendations': {
      const idx = findLastIndex(state.transcript, e => e.kind === 'final' && e.turnIndex === ev.turnIndex)
      if (idx < 0) return state
      const { type: _t, ...recs } = ev
      const transcript = state.transcript.slice()
      transcript[idx] = { ...(transcript[idx] as FinalEntry), recommendations: recs as Recommendations }
      return { ...state, transcript }
    }
```

`import` 補 `Recommendations`。

- [ ] **Step 4: 跑測試與型別**

Run: `cd src/PromptCopilot.Frontend && npm test -- tests/reducer.test.ts && npx nuxi typecheck`
Expected: 全部 passed；typecheck 只會在 `PromptBlock.vue` 抱怨 `SWATCH`／`TEXT` 少了 `adopted` 鍵（`Record<TagSource['origin'], string>`）——那是 Task 13 的事，這裡先在兩個表各加一行 `adopted: ''` 佔位讓型別過，Task 13 填真正的樣式。

- [ ] **Step 5: Commit**

```bash
git add src/PromptCopilot.Frontend/types/api.ts src/PromptCopilot.Frontend/lib/reducer.ts src/PromptCopilot.Frontend/tests/reducer.test.ts src/PromptCopilot.Frontend/components/PromptBlock.vue
git commit -m "feat(frontend): recommendations event lands on its turn's card; session text replaces the adoption bubble; facetTags in state"
```

---

### Task 11: 前端純函式：`lib/adopt.ts`、`lib/trace.ts`、`lib/dashboard.ts`

**Files:**
- Create: `src/PromptCopilot.Frontend/lib/adopt.ts`
- Modify: `src/PromptCopilot.Frontend/lib/trace.ts`
- Modify: `src/PromptCopilot.Frontend/lib/dashboard.ts`
- Test: `src/PromptCopilot.Frontend/tests/adopt.test.ts`（新）
- Test: `src/PromptCopilot.Frontend/tests/trace.test.ts`
- Test: `src/PromptCopilot.Frontend/tests/dashboard.test.ts`

**Interfaces:**
- Produces（`lib/adopt.ts`）：
  - `type AdoptChoice = 'mine' | 'set'`；`interface AdoptRow { facetId; label; state: FacetState; setTags: string[]; available: boolean; choice: AdoptChoice }`
  - `adoptRows(set: RecommendedSet): AdoptRow[]`、`setChoice(rows, facetId, choice): AdoptRow[]`、`takeAll(rows): AdoptRow[]`、`adoptPayload(presetId, dimension, rows): AdoptRequest | null`、`latestRecommendableTurn(transcript: Entry[]): number | null`、`adoptPlaceholder(title): string`、`mineLabel(state: FacetState): string`。
- Produces（`lib/trace.ts`）：`Contribution.origin: 'rag' | 'adopted'`；`ContributionSummary.counts` 多 `adopted`。
- Produces（`lib/dashboard.ts`）：`dashboardRows(catalog, profile, facetStates, highlighted, facetTags = {})`；`DashboardChip.tags: string | null`。

- [ ] **Step 1: 寫失敗的測試**

`tests/adopt.test.ts`：

```ts
import { describe, it, expect } from 'vitest'
import { adoptRows, setChoice, takeAll, adoptPayload, latestRecommendableTurn, adoptPlaceholder, mineLabel } from '../lib/adopt'
import type { Entry } from '../lib/reducer'
import type { RecommendedSet } from '../types/api'

const SET: RecommendedSet = { presetId: 41720, title: '和風女僕', dist: 0.2, facets: [
  { facetId: 'clothing.head', label: '頭部配件', state: 'missing', tags: ['maid headdress'] },
  { facetId: 'clothing.upper', label: '上半身', state: 'covered', tags: ['purple kimono'] },
  { facetId: 'clothing.lower', label: '下半身', state: 'missing', tags: [] },
  { facetId: 'clothing.footwear', label: '鞋履', state: 'waived', tags: ['sandals'] },
  { facetId: 'clothing.material', label: '材質', state: 'notApplicable', tags: ['silk'] },
] }

describe('adoptRows', () => {
  // 設計 §7.2：missing→照它的、covered／waived→留我的、這套沒有的停用、notApplicable 不列
  it('defaults by state, disables rows the set has nothing for, hides notApplicable', () => {
    expect(adoptRows(SET)).toEqual([
      { facetId: 'clothing.head', label: '頭部配件', state: 'missing', setTags: ['maid headdress'], available: true, choice: 'set' },
      { facetId: 'clothing.upper', label: '上半身', state: 'covered', setTags: ['purple kimono'], available: true, choice: 'mine' },
      { facetId: 'clothing.lower', label: '下半身', state: 'missing', setTags: [], available: false, choice: 'mine' },
      { facetId: 'clothing.footwear', label: '鞋履', state: 'waived', setTags: ['sandals'], available: true, choice: 'mine' },
    ])
  })

  it('setChoice only changes available rows; takeAll switches every available row', () => {
    const rows = adoptRows(SET)
    expect(setChoice(rows, 'clothing.upper', 'set')[1].choice).toBe('set')
    expect(setChoice(rows, 'clothing.lower', 'set')[2].choice).toBe('mine')
    expect(takeAll(rows).map(r => r.choice)).toEqual(['set', 'set', 'mine', 'set'])
  })

  it('adoptPayload lists the set rows and is null when nothing is taken', () => {
    expect(adoptPayload(41720, 'clothing', adoptRows(SET))).toEqual({ presetId: 41720, dimension: 'clothing', take: ['clothing.head'] })
    expect(adoptPayload(41720, 'clothing', adoptRows(SET).map(r => ({ ...r, choice: 'mine' as const })))).toBeNull()
  })

  it('mineLabel and adoptPlaceholder', () => {
    expect([mineLabel('covered'), mineLabel('missing'), mineLabel('waived'), mineLabel('notApplicable')]).toEqual(['保留你講的', '空白', '不指定', '不適用'])
    expect(adoptPlaceholder('和風女僕')).toBe('採用〈和風女僕〉…')
  })
})

describe('latestRecommendableTurn', () => {
  const ask = (t: number): Entry => ({ kind: 'final', turnIndex: t, data: { kind: 'ask', preamble: 'p', asks: [] } })
  const fin = (t: number): Entry => ({ kind: 'final', turnIndex: t, data: { kind: 'finalized', positive: 'p', negative: 'n', tips: 't', intentSummary: 'i' } })
  const msg = (t: number): Entry => ({ kind: 'final', turnIndex: t, data: { kind: 'message', message: 'm' } })

  // 只有最新一張追問卡或定稿卡可以採用；中間的討論氣泡不算新結果
  it('returns the latest ask or finalized turn, skipping message entries', () => {
    expect(latestRecommendableTurn([ask(1), fin(2), msg(3)])).toBe(2)
    expect(latestRecommendableTurn([fin(2), ask(3)])).toBe(3)
    expect(latestRecommendableTurn([{ kind: 'user', text: 'x' }, msg(1)])).toBeNull()
  })
})
```

`tests/trace.test.ts`：既有兩個 `contributions` 期望值改成含 `adopted: 0` 與 `origin: 'rag'`：

```ts
    expect(c.counts).toEqual({ rag: 2, adopted: 0, llm: 1, base: 1 })
    expect(c.byPreset).toEqual([
      { presetId: 9, title: '霓虹雨夜', sourceRef: 'civitai:1:0', origin: 'rag', tags: ['neon lights', '-blurry'] },
      { presetId: 3, title: '夏日涼鞋', sourceRef: null, origin: 'rag', tags: ['sandals'] },
    ])
```

（第二個測試同理加 `origin: 'rag'`；`handles empty sources` 的 counts 加 `adopted: 0`。）加：

```ts
  // 採用的組合另外列，同一筆 preset 既是採用來源又是 rag 命中時分成兩列
  it('lists adopted tags under their own origin, separately from rag hits of the same preset', () => {
    const c = contributions([
      { tag: 'purple kimono', origin: 'adopted', presetIds: [41720], presetTitle: '和風女僕', sourceRef: 'civitai:9:0' },
      { tag: 'sandals', origin: 'rag', presetIds: [41720], presetTitle: '和風女僕', sourceRef: 'civitai:9:0' },
      { tag: 'maid headdress', origin: 'adopted', presetIds: [41720], presetTitle: '和風女僕' },
    ], [])
    expect(c.counts).toEqual({ rag: 1, adopted: 2, llm: 0, base: 0 })
    expect(c.byPreset).toEqual([
      { presetId: 41720, title: '和風女僕', sourceRef: 'civitai:9:0', origin: 'adopted', tags: ['purple kimono', 'maid headdress'] },
      { presetId: 41720, title: '和風女僕', sourceRef: 'civitai:9:0', origin: 'rag', tags: ['sandals'] },
    ])
  })
```

`tests/dashboard.test.ts` 加：

```ts
  it('carries the model-supplied tags onto the chip and null when there are none', () => {
    const rows = dashboardRows(catalog, 'portrait', { 'style.genre': 'covered' }, [], { 'style.genre': 'oil painting' })
    expect(rows[0].chips[0].tags).toBe('oil painting')
    expect(rows[1].chips[0].tags).toBeNull()
  })
```

- [ ] **Step 2: 跑測試確認失敗**

Run: `cd src/PromptCopilot.Frontend && npm test -- tests/adopt.test.ts tests/trace.test.ts tests/dashboard.test.ts`
Expected: FAIL（模組不存在、期望值不符）。

- [ ] **Step 3: 實作**

`lib/adopt.ts`：

```ts
import type { AdoptRequest, FacetState, RecommendedSet } from '../types/api'
import type { Entry } from './reducer'

export type AdoptChoice = 'mine' | 'set'
export interface AdoptRow { facetId: string; label: string; state: FacetState; setTags: string[]; available: boolean; choice: AdoptChoice }

/** 對照表的列（設計 §7.2）：notApplicable 不列；預設 missing→照它的、covered／waived→留我的；這套沒有的列停用、固定留我的。 */
export function adoptRows(set: RecommendedSet): AdoptRow[] {
  return set.facets.filter(f => f.state !== 'notApplicable').map(f => {
    const available = f.tags.length > 0
    return { facetId: f.facetId, label: f.label, state: f.state, setTags: f.tags, available, choice: available && f.state === 'missing' ? 'set' : 'mine' }
  })
}

export function setChoice(rows: AdoptRow[], facetId: string, choice: AdoptChoice): AdoptRow[] {
  return rows.map(r => (r.facetId === facetId && r.available ? { ...r, choice } : r))
}

/** 「整套照它的」：全部可用的列切到照它的。 */
export function takeAll(rows: AdoptRow[]): AdoptRow[] {
  return rows.map(r => (r.available ? { ...r, choice: 'set' } : r))
}

export function adoptPayload(presetId: number, dimension: string, rows: AdoptRow[]): AdoptRequest | null {
  const take = rows.filter(r => r.choice === 'set').map(r => r.facetId)
  return take.length ? { presetId, dimension, take } : null
}

/** 只有最新一張追問卡或定稿卡上的推薦可以採用：舊卡的狀態已失效。中間的討論氣泡不改狀態，不算新結果。 */
export function latestRecommendableTurn(transcript: Entry[]): number | null {
  for (let i = transcript.length - 1; i >= 0; i--) {
    const e = transcript[i]
    if (e.kind === 'final' && (e.data.kind === 'ask' || e.data.kind === 'finalized')) return e.turnIndex
  }
  return null
}

/** 送出當下的使用者泡泡文字；session 事件到了會換成伺服器組的那句。 */
export function adoptPlaceholder(title: string): string { return `採用〈${title}〉…` }

/** 對照表「你的」欄：只講狀態，不猜使用者的原話（伺服器沒有逐 facet 的原話）。 */
export function mineLabel(state: FacetState): string {
  return { covered: '保留你講的', missing: '空白', waived: '不指定', notApplicable: '不適用' }[state]
}
```

`lib/trace.ts`：

```ts
export interface Contribution { presetId: number; title: string; sourceRef: string | null; origin: 'rag' | 'adopted'; tags: string[] }
export interface ContributionSummary { counts: { rag: number; adopted: number; llm: number; base: number }; byPreset: Contribution[] }

export function contributions(positive: TagSource[], negative: TagSource[]): ContributionSummary {
  const counts = { rag: 0, adopted: 0, llm: 0, base: 0 }
  for (const t of positive) if (t.origin in counts) counts[t.origin] += 1
  // 鍵含 origin：同一筆 preset 既是採用來源又是 rag 命中時分成兩列，讀的人才分得出哪些是採用帶進來的
  const groups = new Map<string, Contribution>()
  const add = (t: TagSource, label: string) => {
    if ((t.origin !== 'rag' && t.origin !== 'adopted') || t.presetIds.length === 0) return
    const id = t.presetIds[0]
    const key = `${t.origin}:${id}`
    const g = groups.get(key) ?? { presetId: id, title: t.presetTitle ?? String(id), sourceRef: t.sourceRef ?? null, origin: t.origin, tags: [] }
    g.tags.push(label)
    groups.set(key, g)
  }
  for (const t of positive) add(t, t.tag)
  for (const t of negative) add(t, `-${t.tag}`)
  const byPreset = [...groups.values()].sort((a, b) => b.tags.length - a.tags.length || a.presetId - b.presetId)
  return { counts, byPreset }
}
```

（`retrievalSummary` 的 `borrowed` 照舊只算 `rag`。）

`lib/dashboard.ts`：`DashboardChip` 加 `tags: string | null`；簽名加第五個參數 `facetTags: Record<string, string> = {}`；兩處建 chip 的物件加 `tags: facetTags[id] ?? null`。

- [ ] **Step 4: 跑測試**

Run: `cd src/PromptCopilot.Frontend && npm test`
Expected: 全部 passed。

- [ ] **Step 5: Commit**

```bash
git add src/PromptCopilot.Frontend/lib src/PromptCopilot.Frontend/tests
git commit -m "feat(frontend): adopt rows/payload helpers, adopted contributions in trace, facet tags on dashboard chips"
```

---

### Task 12: 前端 store 與 API：`adopt` 走同一條 `runTurn`

**Files:**
- Modify: `src/PromptCopilot.Frontend/composables/useApi.ts`
- Modify: `src/PromptCopilot.Frontend/stores/session.ts`

**Interfaces:**
- Consumes: Task 10 `AdoptRequest`、`RecommendedSet`；Task 11 `latestRecommendableTurn`、`adoptPlaceholder`。
- Produces:
  - `useApi().openStream(id, body: TurnBody, signal)`，`type TurnBody = { text: string } | { adopt: AdoptRequest }`。
  - store：`adoptTarget: Ref<{ set: RecommendedSet; dimension: string; turnIndex: number } | null>`、`latestRecommendableTurn: ComputedRef<number | null>`、`openAdopt(set, dimension, turnIndex)`、`closeAdopt()`、`adopt(req: AdoptRequest, title: string): Promise<void>`。`send()` 行為不變。

（store 沒有單元測試，與專案既有做法一致；邏輯都在 Task 11 的 lib 裡。驗收靠 Task 15 的 eval。）

- [ ] **Step 1: `useApi.ts`**

```ts
import type { AdoptRequest, FacetCatalog, PresetDetail, RetrievalMode, SessionCreated, SessionSnapshotDto } from '../types/api'

export type TurnBody = { text: string } | { adopt: AdoptRequest }

  /** 不檢查 status：404／409／400 的處理在 store。body 是一般訊息或採用（設計 §6.1）。 */
  function openStream(id: string, body: TurnBody, signal: AbortSignal): Promise<Response> {
    return fetch(`${base}/api/sessions/${encodeURIComponent(id)}/messages`, {
      method: 'POST', headers: { 'content-type': 'application/json', accept: 'text/event-stream' },
      body: JSON.stringify(body), signal,
    })
  }
```

- [ ] **Step 2: `stores/session.ts`**

import 加 `import { latestRecommendableTurn as latestRecommendableTurnOf, adoptPlaceholder } from '../lib/adopt'`、`type AdoptRequest, type RecommendedSet` 與 `type TurnBody`（從 `../composables/useApi` 匯入型別）。

state 加：

```ts
  /** 對照表正在看的那套；null 表示關閉。 */
  const adoptTarget = ref<{ set: RecommendedSet; dimension: string; turnIndex: number } | null>(null)
  /** 只有最新一張追問卡／定稿卡上的推薦可以採用。 */
  const latestRecommendableTurn = computed<number | null>(() => latestRecommendableTurnOf(state.value.transcript))
```

`send()` 拆成兩層；`runTurn` 是原本 `send` 的內容，差別只在「顯示的文字」與「送出的 body」分開：

```ts
  async function send() {
    const text = draft.value.trim()
    if (!text) return
    draft.value = ''; chips.value = []; draftDirty.value = false
    await runTurn(text, { text })
  }

  /** 一輪：display 是使用者泡泡先顯示的字（採用時是暫代字，session 事件會換成伺服器組的那句），body 是真正送出的內容。 */
  async function runTurn(display: string, body: TurnBody) {
    if (busy.value || !state.value.sessionId) return
    busy.value = true
    notice.value = null
    state.value = beginTurn(state.value, display)
    // 送出當下先存一次：輪次中重載時，原文經 draft 回到輸入框（採用沒有原文可回，存空字串）
    persist({ draft: 'text' in body ? body.text : '' })
    const ctl = new AbortController()
    try {
      const r = await api.openStream(state.value.sessionId!, body, ctl.signal)
      if (r.status === 404) {
        await newSession()
        notice.value = 'text' in body ? EXPIRED_KEPT_TEXT : '上次的對話已過期，已開新對話。'
        if ('text' in body) { draft.value = body.text; draftDirty.value = true }
        return
      }
      if (!r.ok || !r.body) {
        let msg = r.status === 409 ? '這個對話還有一輪在跑，等它結束再送。' : `送出失敗（HTTP ${r.status}）。`
        // 採用被拒（400／409）帶有理由：直接顯示
        if ('adopt' in body) { try { msg = (await r.json()).error ?? msg } catch { /* 沒 body 就用預設字 */ } }
        state.value = failHttp(state.value, `http_${r.status}`, msg)
        return
      }
      for await (const frame of readSse(r.body)) {
        // …與現在完全相同…
      }
    } catch (e) {
      console.warn('stream aborted', e)
    } finally {
      state.value = endTurn(state.value)
      persist()
      busy.value = false
    }
  }

  function openAdopt(set: RecommendedSet, dimension: string, turnIndex: number) {
    if (busy.value || turnIndex !== latestRecommendableTurn.value) return
    adoptTarget.value = { set, dimension, turnIndex }
  }
  function closeAdopt() { adoptTarget.value = null }

  /** 確定採用：關對照表、走一般的一輪。泡泡先顯示「採用〈標題〉…」。 */
  async function adopt(req: AdoptRequest, title: string) {
    closeAdopt()
    await runTurn(adoptPlaceholder(title), { adopt: req })
  }
```

`return` 多 `adoptTarget, latestRecommendableTurn, openAdopt, closeAdopt, adopt`。

注意 `readSse` 迴圈裡的未知事件檢查用 `AGENT_EVENT_TYPES`（Task 10 已含 `recommendations`），不用改。

- [ ] **Step 3: 型別檢查**

Run: `cd src/PromptCopilot.Frontend && npx nuxi typecheck && npm test`
Expected: 通過。

- [ ] **Step 4: Commit**

```bash
git add src/PromptCopilot.Frontend/composables/useApi.ts src/PromptCopilot.Frontend/stores/session.ts
git commit -m "feat(frontend): store runs an adoption through the same turn path as a message; openStream takes a TurnBody"
```

---

### Task 13: 前端元件：`RecommendationStrip`、`AdoptDialog`、`adopted` chip、儀表板 tag

**Files:**
- Create: `src/PromptCopilot.Frontend/components/RecommendationStrip.vue`
- Create: `src/PromptCopilot.Frontend/components/AdoptDialog.vue`
- Modify: `src/PromptCopilot.Frontend/components/AskCard.vue`
- Modify: `src/PromptCopilot.Frontend/components/FinalCard.vue`
- Modify: `src/PromptCopilot.Frontend/components/ChatStream.vue`
- Modify: `src/PromptCopilot.Frontend/components/PromptBlock.vue`
- Modify: `src/PromptCopilot.Frontend/components/Dashboard.vue`
- Modify: `src/PromptCopilot.Frontend/app.vue`

**Interfaces:**
- Consumes: Task 10 型別；Task 11 `adoptRows`／`setChoice`／`takeAll`／`adoptPayload`／`mineLabel`；Task 12 store 的 `adoptTarget`／`openAdopt`／`closeAdopt`／`adopt`／`latestRecommendableTurn`。
- Produces: `<RecommendationStrip :recs :turn-index />`、`<AdoptDialog />`（掛在 `app.vue`）。

- [ ] **Step 1: `RecommendationStrip.vue`**

```vue
<template>
  <section data-section="recommendations" class="mt-4 border-t border-rule pt-3">
    <h4 class="text-xs font-bold">參考組合</h4>
    <p class="mt-0.5 text-[11px] text-muted">知識庫裡真實存在、有圖的整套設定。圖片來自來源網站，著作權屬原作者，點圖看出處。</p>
    <div v-for="d in recs.dimensions" :key="d.dimension" class="mt-2.5">
      <p class="flex flex-wrap items-baseline gap-x-2 text-xs">
        <span class="font-medium">{{ d.label }}</span>
        <span class="text-[11px] text-muted">{{ d.anchored ? `含你講的 ${d.anchorTags.join(', ')}` : '最接近你描述的組合' }}</span>
      </p>
      <ul class="mt-1.5 flex gap-2 overflow-x-auto pb-1">
        <li v-for="set in d.sets" :key="set.presetId" class="w-28 shrink-0">
          <button type="button" class="block w-full text-left" :title="set.title" @click="s.openDrawer(set.presetId)">
            <span v-if="set.imageUrl && !broken.has(set.presetId)" class="relative block h-28 w-28">
              <img :src="set.imageUrl" :alt="set.title" class="h-28 w-28 rounded-[3px] object-cover" loading="lazy"
                   referrerpolicy="no-referrer" @error="broken.add(set.presetId)">
              <span v-if="sourceName(set.sourceRef)"
                    class="absolute bottom-0.5 right-0.5 rounded-[2px] bg-ink/70 px-1 text-[9px] leading-4 text-paper">{{ sourceName(set.sourceRef) }}</span>
            </span>
            <div v-else class="flex h-28 w-28 items-center justify-center rounded-[3px] border border-dashed border-rule text-xs text-muted">無圖</div>
            <span class="mt-1 block truncate text-[11px] text-ink">{{ set.title }}</span>
          </button>
          <button type="button" :disabled="!adoptable" :title="adoptable ? '逐項選擇要照它的' : '已有新的結果，這張卡的推薦不能再採用'"
                  class="mt-1 w-full rounded-md border border-ink/80 px-2 py-1 text-[11px] font-medium hover:bg-ink hover:text-paper disabled:border-rule disabled:text-muted disabled:hover:bg-transparent"
                  @click="s.openAdopt(set, d.dimension, turnIndex)">採用</button>
        </li>
      </ul>
    </div>
  </section>
</template>

<script setup lang="ts">
import type { Recommendations } from '../types/api'
import { sourceName } from '../lib/copy'
/** recs：該輪的 recommendations 事件；turnIndex：卡片的輪次。只有最新一張追問卡／定稿卡可以採用（舊卡的狀態已失效）。 */
const props = defineProps<{ recs: Recommendations; turnIndex: number }>()
const s = useSessionStore()
const broken = reactive(new Set<number>())
const adoptable = computed(() => s.latestRecommendableTurn === props.turnIndex && !s.busy)
</script>
```

- [ ] **Step 2: `AdoptDialog.vue`**

```vue
<template>
  <div v-if="t" class="fixed inset-0 z-30 flex items-end justify-center bg-ink/40 p-4 md:items-center" @click.self="s.closeAdopt()">
    <section role="dialog" aria-label="採用這套組合" class="max-h-[90vh] w-full max-w-2xl overflow-y-auto rounded-lg border border-rule bg-surface p-5 shadow-xl">
      <header class="flex items-start gap-4">
        <img v-if="t.set.imageUrl && !imgFailed" :src="t.set.imageUrl" :alt="t.set.title" referrerpolicy="no-referrer"
             class="h-32 w-32 shrink-0 rounded-[4px] object-cover" @error="imgFailed = true">
        <div v-else class="flex h-32 w-32 shrink-0 items-center justify-center rounded-[4px] border border-dashed border-rule text-xs text-muted">無圖</div>
        <div class="min-w-0 flex-1">
          <h2 class="text-base font-bold leading-snug">{{ t.set.title }}</h2>
          <p class="mt-1 text-xs text-muted">{{ notice.text }}</p>
          <button type="button" class="mt-1 text-xs underline underline-offset-2 hover:text-cyan" @click="s.openDrawer(t.set.presetId)">看完整片段</button>
        </div>
        <button type="button" class="rounded px-1.5 text-sm text-muted hover:text-ink" @click="s.closeAdopt()">關閉</button>
      </header>

      <p class="mt-4 text-xs text-muted">每一項各自決定：留你原本的，還是照這套。沒講過的項目預設照這套。</p>
      <table class="mt-2 w-full text-xs">
        <thead class="text-left text-[11px] text-muted">
          <tr><th class="py-1 pr-2 font-medium">項目</th><th class="py-1 pr-2 font-medium">你的</th><th class="py-1 pr-2 font-medium">這套</th><th class="py-1 font-medium">採用</th></tr>
        </thead>
        <tbody>
          <tr v-for="r in rows" :key="r.facetId" class="border-t border-rule/60 align-top" :class="{ 'opacity-50': !r.available }">
            <td class="py-2 pr-2 font-medium">{{ r.label }}</td>
            <td class="py-2 pr-2 text-muted">{{ mineLabel(r.state) }}</td>
            <td class="py-2 pr-2">
              <span v-if="!r.available" class="text-muted">這套沒有</span>
              <span v-for="tag in r.setTags" :key="tag" class="mr-1 inline-block rounded-[3px] border border-rule bg-paper px-1.5 font-mono text-[11px] leading-5">{{ tag }}</span>
            </td>
            <td class="py-2">
              <span class="inline-flex overflow-hidden rounded-md border border-rule text-[11px]">
                <button type="button" class="px-2 py-1" :class="r.choice === 'mine' ? 'bg-ink text-paper' : 'hover:bg-paper'" :disabled="!r.available"
                        :aria-pressed="r.choice === 'mine'" @click="rows = setChoice(rows, r.facetId, 'mine')">留我的</button>
                <button type="button" class="border-l border-rule px-2 py-1" :class="r.choice === 'set' ? 'bg-ink text-paper' : 'hover:bg-paper'" :disabled="!r.available"
                        :aria-pressed="r.choice === 'set'" @click="rows = setChoice(rows, r.facetId, 'set')">照它的</button>
              </span>
            </td>
          </tr>
        </tbody>
      </table>

      <footer class="mt-4 flex flex-wrap items-center gap-3">
        <button type="button" :disabled="!payload"
                class="rounded-md bg-ink px-3.5 py-1.5 text-sm font-medium text-paper hover:bg-ink/85 disabled:bg-rule disabled:text-muted"
                @click="confirm">確定採用</button>
        <button type="button" class="rounded-md border border-rule px-3 py-1.5 text-sm hover:bg-paper" @click="rows = takeAll(rows)">全部照它的</button>
        <button type="button" class="ml-auto text-sm text-muted hover:text-ink" @click="s.closeAdopt()">取消</button>
      </footer>
    </section>
  </div>
</template>

<script setup lang="ts">
import { adoptRows, setChoice, takeAll, adoptPayload, mineLabel, type AdoptRow } from '../lib/adopt'
import { sourceNotice } from '../lib/copy'
const s = useSessionStore()
const t = computed(() => s.adoptTarget)
const rows = ref<AdoptRow[]>([])
const imgFailed = ref(false)
const notice = computed(() => sourceNotice(t.value?.set.sourceRef))
watch(t, (v) => { rows.value = v ? adoptRows(v.set) : []; imgFailed.value = false }, { immediate: true })
const payload = computed(() => (t.value ? adoptPayload(t.value.set.presetId, t.value.dimension, rows.value) : null))
function confirm() { if (t.value && payload.value) s.adopt(payload.value, t.value.set.title) }
function onKey(e: KeyboardEvent) { if (e.key === 'Escape' && t.value) s.closeAdopt() }
onMounted(() => window.addEventListener('keydown', onKey))
onBeforeUnmount(() => window.removeEventListener('keydown', onKey))
</script>
```

- [ ] **Step 3: 掛到卡片與 app**

`ChatStream.vue`：

```vue
          <AskCard v-if="e.data.kind === 'ask'" :data="e.data" :turn-index="e.turnIndex" :recommendations="e.recommendations ?? null" />
          …
          <FinalCard v-else-if="e.data.kind === 'finalized'" :data="e.data" :turn-index="e.turnIndex" :recommendations="e.recommendations ?? null" />
```

`AskCard.vue`：props 改成 `defineProps<{ data: Extract<FinalData, { kind: 'ask' }>; turnIndex: number; recommendations?: Recommendations | null }>()`（import `Recommendations`）；在最後那行 `<p class="mt-4 text-[11px] text-muted">選項會填進…</p>` 之後加：

```vue
    <RecommendationStrip v-if="recommendations" :recs="recommendations" :turn-index="turnIndex" />
```

`FinalCard.vue`：props 加 `recommendations?: Recommendations | null`；在「檢索貢獻」`<section>` 之後、`<footer>` 之前加同一行。「檢索貢獻」區塊改成：

```vue
      <p class="mt-1.5 text-xs tabular-nums text-muted">rag {{ trace.counts.rag }}・adopted {{ trace.counts.adopted }}・llm {{ trace.counts.llm }}・base {{ trace.counts.base }}</p>
      <ul v-if="trace.byPreset.length" class="mt-1.5 flex flex-col gap-1 text-xs">
        <li v-for="p in trace.byPreset" :key="`${p.origin}:${p.presetId}`" class="flex items-baseline gap-2">
          <span class="shrink-0 text-[10px] text-muted">{{ p.origin === 'adopted' ? '採用' : '借用' }}</span>
          <button type="button" class="shrink-0 font-medium hover:text-cyan" @click="s.openDrawer(p.presetId)">{{ p.title }}</button>
          <span class="text-muted">→</span>
          <span class="font-mono text-[11px]">{{ p.tags.join(', ') }}</span>
        </li>
      </ul>
```

`app.vue`：`<PresetDrawer />` 之後加 `<AdoptDialog />`。

- [ ] **Step 4: `PromptBlock.vue` 的 `adopted` chip**

```ts
const SWATCH: Record<TagSource['origin'], string> = {
  rag: 'border-cyan bg-cyan-wash',
  adopted: 'border-magenta bg-magenta-wash',
  llm: 'border-rule bg-surface',
  base: 'border-rule/60 bg-paper',
}
const TEXT: Record<TagSource['origin'], string> = { rag: 'text-ink', adopted: 'text-ink', llm: 'text-ink', base: 'text-muted' }
const LEGEND = [
  { origin: 'rag', label: '知識庫片段' },
  { origin: 'adopted', label: '採用的組合' },
  { origin: 'llm', label: '模型生成' },
  { origin: 'base', label: '基礎詞' },
] as const
/** 可點的 chip：rag 與 adopted 都指向一筆 preset。 */
const clickable = (t: TagSource) => (t.origin === 'rag' || t.origin === 'adopted') && t.presetIds.length > 0
function chipTitle(t: TagSource) {
  const name = sourceName(t.sourceRef)
  const who = `〈${t.presetTitle ?? `片段 #${t.presetIds[0]}`}〉${name ? `（${name}）` : ''}`
  return t.origin === 'adopted' ? `採用${who}帶進來的` : `來自${who}`
}
```

模板：`v-if="t.origin === 'rag' && t.presetIds.length"` 改成 `v-if="clickable(t)"`，`:title="ragTitle(t)"` 改成 `:title="chipTitle(t)"`，hover 色 adopted 用 `hover:bg-magenta`：`:class="[CHIP, look(t), t.origin === 'adopted' ? 'hover:bg-magenta hover:text-paper' : 'hover:bg-cyan hover:text-paper']"`。刪掉 `ragTitle` 與 Task 10 放的 `adopted: ''` 佔位。

- [ ] **Step 5: `Dashboard.vue`**

`rows` 改成 `dashboardRows(s.catalog, s.state.profile, s.state.facetStates, s.state.highlighted, s.state.facetTags)`；chip 的 `:title` 改成 `` `${c.label}：${stateLabel(c.state)}${c.tags ? `（${c.tags}）` : ''}` ``。

- [ ] **Step 6: 型別、測試、打包**

Run: `cd src/PromptCopilot.Frontend && npx nuxi typecheck && npm test && npm run build`
Expected: 全部通過。

- [ ] **Step 7: 手動煙霧測試（需要 API 與知識庫；`facet_tags` 尚未回填時推薦區塊不會出現，只看不壞）**

依 `manual-tests/README.md` 起 API 與 `npm run dev`，送「一個少女穿涼鞋」：追問卡照常、儀表板鞋履 chip 的 title 含模型給的英文 tag、定稿 chip 圖例多「採用的組合」。完整驗收在 Task 16。

- [ ] **Step 8: Commit**

```bash
git add src/PromptCopilot.Frontend
git commit -m "feat(frontend): recommendation strip on ask/final cards, per-facet adopt dialog, adopted chip, facet tags on dashboard"
```

---

### Task 14: `scripts/adoption_report.py`：採用率、擴充率、取代率

**Files:**
- Create: `scripts/adoption_report.py`
- Test: `scripts/tests/test_adoption_report.py`
- Modify: `scripts/README.md`

**Interfaces:**
- Consumes: audit `Turn_Completed` payload（Task 6 `recommendations`、Task 8 `adoption`、Task 5 `tagOrigins.adopted`）。
- Produces: `@dataclass Turn(session_id: str, turn_index: int, payload: dict)`；`build_report(turns: list[Turn]) -> str`（Markdown）；`fetch_turns(conn, since: str | None) -> list[Turn]`。

- [ ] **Step 1: 寫失敗的測試**

`scripts/tests/test_adoption_report.py`：

```python
from adoption_report import Turn, build_report

REC_STYLE = {"dimensions": [{"dimension": "style", "anchored": False, "presetIds": [7, 8]}]}
REC_CLOTHING = {"dimensions": [{"dimension": "clothing", "anchored": True, "presetIds": [1, 2, 3]}]}
ADOPT_CLOTHING = {"presetId": 1, "dimension": "clothing", "take": ["clothing.upper", "clothing.head"],
                  "filled": ["clothing.upper"], "replaced": ["clothing.head"]}


def fin(session, turn, rec=None, adoption=None, origins=None):
    p = {"outcome": "FinalizedOutcome"}
    if rec is not None:
        p["recommendations"] = rec
    if adoption is not None:
        p["adoption"] = adoption
    if origins is not None:
        p["tagOrigins"] = origins
    return Turn(session, turn, p)


def ask(session, turn, rec=None):
    p = {"outcome": "AskOutcome"}
    if rec is not None:
        p["recommendations"] = rec
    return Turn(session, turn, p)


def test_adoption_rate_counts_only_the_very_next_turn_of_the_same_session():
    turns = [
        fin("a", 1, rec=REC_CLOTHING), fin("a", 2, adoption=ADOPT_CLOTHING),                     # 採用
        fin("b", 1, rec=REC_STYLE), fin("b", 2),                                                # 沒採用
        fin("c", 1, rec=REC_STYLE), fin("d", 2, adoption=ADOPT_CLOTHING),                       # 別的 session 的採用不算
    ]
    text = build_report(turns)
    assert "定稿輪採用率：1/3（33.3%）" in text


def test_ask_turns_are_reported_separately():
    turns = [ask("a", 1, rec=REC_CLOTHING), fin("a", 2, adoption=ADOPT_CLOTHING), ask("b", 1, rec=REC_STYLE), fin("b", 2)]
    text = build_report(turns)
    assert "追問輪採用率：1/2（50.0%）" in text
    assert "定稿輪採用率：0/0" in text


def test_filled_replaced_averages_dimension_counts_and_anchored_split():
    turns = [
        fin("a", 1, rec=REC_CLOTHING), fin("a", 2, adoption=ADOPT_CLOTHING),
        fin("b", 1, rec=REC_STYLE), fin("b", 2, adoption={"presetId": 7, "dimension": "style", "take": ["style.genre"], "filled": ["style.genre"], "replaced": []}),
    ]
    text = build_report(turns)
    assert "平均補上 1.0 個 facet、換掉 0.5 個 facet" in text
    assert "clothing：1" in text and "style：1" in text
    assert "採用時該維度有錨：1/2" in text


def test_adopted_tag_share_over_finalized_turns():
    turns = [fin("a", 1, origins={"rag": 2, "adopted": 2, "llm": 4, "base": 2}), fin("a", 2, origins={"rag": 0, "adopted": 0, "llm": 5, "base": 3})]
    assert "adopted tag 佔定稿 tag：2/18（11.1%）" in build_report(turns)


def test_no_adoptions_prints_the_notice_but_still_counts_recommendations():
    text = build_report([fin("a", 1, rec=REC_STYLE)])
    assert "尚無採用紀錄" in text
    assert "有推薦的定稿輪：1" in text


def test_empty_input():
    assert "沒有 Turn_Completed 紀錄" in build_report([])
```

- [ ] **Step 2: 跑測試確認失敗**

Run: `cd scripts && pytest tests/test_adoption_report.py -q`
Expected: FAIL（模組不存在）。

- [ ] **Step 3: 寫腳本**

`scripts/adoption_report.py`：

```python
"""量測整套組合推薦的採用率（設計 2026-09-25-set-recommendations-design.md §8）。
讀 audit_logs 的 Turn_Completed，輸出 Markdown 到 stdout。

    python adoption_report.py [--since 2026-09-25]

採用率：有推薦的那一輪，同 session 的下一輪是採用的比例（定稿輪與追問輪分開算）。
擴充率／取代率：採用時補上的（原本 missing）與換掉的（原本 covered／waived）facet 數。
"""

from __future__ import annotations

import argparse
import json
import sys
from collections import Counter
from dataclasses import dataclass
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

SQL = """
SELECT session_id, turn_index, payload
FROM audit_logs
WHERE event_type = 'Turn_Completed' AND session_id IS NOT NULL AND payload IS NOT NULL
  AND (%(since)s::date IS NULL OR created_at >= %(since)s::date)
ORDER BY session_id, turn_index
"""


@dataclass
class Turn:
    session_id: str
    turn_index: int
    payload: dict


def fetch_turns(conn, since: str | None) -> list[Turn]:
    rows = conn.execute(SQL, {"since": since}).fetchall()
    return [Turn(r[0], r[1], r[2] if isinstance(r[2], dict) else json.loads(r[2])) for r in rows]


def _pct(n: int, d: int) -> str:
    return f"{n}/{d}（{(n / d * 100) if d else 0:.1f}%）"


def build_report(turns: list[Turn]) -> str:
    if not turns:
        return "沒有 Turn_Completed 紀錄。"
    by_key = {(t.session_id, t.turn_index): t for t in turns}
    adoptions = [t.payload["adoption"] for t in turns if "adoption" in t.payload]

    # 有推薦的輪：下一輪是不是採用
    rec_final = rec_ask = adopted_final = adopted_ask = 0
    anchored_hit = anchored_total = 0
    for t in turns:
        rec = t.payload.get("recommendations")
        if not rec:
            continue
        is_final = t.payload.get("outcome") == "FinalizedOutcome"
        nxt = by_key.get((t.session_id, t.turn_index + 1))
        adoption = nxt.payload.get("adoption") if nxt else None
        if is_final:
            rec_final += 1
            adopted_final += bool(adoption)
        else:
            rec_ask += 1
            adopted_ask += bool(adoption)
        if adoption:
            anchored_total += 1
            dim = next((d for d in rec.get("dimensions", []) if d.get("dimension") == adoption.get("dimension")), None)
            anchored_hit += bool(dim and dim.get("anchored"))

    origins = [t.payload["tagOrigins"] for t in turns if isinstance(t.payload.get("tagOrigins"), dict)]
    adopted_tags = sum(o.get("adopted", 0) for o in origins)
    all_tags = sum(sum(o.values()) for o in origins)

    lines = ["# 整套組合推薦：採用率", ""]
    lines.append(f"- 有推薦的定稿輪：{rec_final}；定稿輪採用率：{_pct(adopted_final, rec_final)}")
    lines.append(f"- 有推薦的追問輪：{rec_ask}；追問輪採用率：{_pct(adopted_ask, rec_ask)}")
    lines.append(f"- adopted tag 佔定稿 tag：{_pct(adopted_tags, all_tags)}")
    if not adoptions:
        lines += ["", "尚無採用紀錄。"]
        return "\n".join(lines)
    filled = sum(len(a.get("filled", [])) for a in adoptions) / len(adoptions)
    replaced = sum(len(a.get("replaced", [])) for a in adoptions) / len(adoptions)
    lines.append(f"- 採用 {len(adoptions)} 次；平均補上 {filled:.1f} 個 facet、換掉 {replaced:.1f} 個 facet")
    lines.append(f"- 採用時該維度有錨：{_pct(anchored_hit, anchored_total)}")
    lines += ["", "## 各維度採用次數", ""]
    for dim, n in sorted(Counter(a.get("dimension", "?") for a in adoptions).items()):
        lines.append(f"- {dim}：{n}")
    return "\n".join(lines)


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--since", default=None, help="只算這一天（含）之後的紀錄，YYYY-MM-DD")
    args = ap.parse_args(argv)
    from pipeline.db import connect

    with connect() as conn:
        turns = fetch_turns(conn, args.since)
    print(build_report(turns))
    return 0


if __name__ == "__main__":
    sys.exit(main())
```

- [ ] **Step 4: 跑測試、ruff**

Run: `cd scripts && pytest tests/test_adoption_report.py -q && ruff check .`
Expected: 6 passed；ruff 乾淨。

- [ ] **Step 5: `scripts/README.md`**

在 Task 2 加的那節之後加：

```markdown
## 量測：整套組合推薦的採用率

    python adoption_report.py [--since 2026-09-25]

讀 `audit_logs` 的 `Turn_Completed`（`recommendations`、`adoption`、`tagOrigins.adopted`），印 Markdown：定稿輪／追問輪採用率、
平均補上與換掉的 facet 數、各維度採用次數、adopted tag 佔比。這是決定要不要做離線 A/B 之前要看的數字。
```

- [ ] **Step 6: Commit**

```bash
git add scripts/adoption_report.py scripts/tests/test_adoption_report.py scripts/README.md
git commit -m "feat(scripts): adoption_report computes adoption, expansion and replacement rates from audit"
```

---

### Task 15: 文件同步與 eval 案例

**Files:**
- Modify: `docs/單輪流程說明.md`
- Modify: `docs/eval-cases.md`
- Modify: `docs/known-issues.md`
- Modify: `README.md`
- Modify: `docs/superpowers/specs/2026-09-21-genai-prompt-copilot-design.md`（§12.3）

- [ ] **Step 1: `docs/單輪流程說明.md`**

在「## 8. 最後的畫面」段落的結尾（「每個選項都標了來源片段…」那句之後、`---` 之前）加：

```markdown
### 參考組合與採用（C# 端，2026-09-25 起）

上面是單輪 demo 的畫面。C# 端從 2026-09-25 起多一層**伺服器端的推薦**，模型不知道它存在：

1. 每次追問（被問的維度）與每次定稿（全部維度），`RecommendationService` 對每個維度找 2–3 套「整套」片段（涵蓋該維度 ≥ 2 個 facet、已拆分 `facet_tags`）。使用者講過的元素當錨：模型在 `SetFacetStates` 把 facet 標 covered 時順便給的英文 `tags`（涼鞋 → `sandals`），定稿時再加上定稿 positive 的 tag。SQL 先篩「該維度 covered facet 底下含錨 tag」的片段，再依使用者整段描述的向量排序；命中不到 2 筆就退回純向量排序，卡片上會說「最接近你描述的組合」而不是「含你講的 sandals」。
2. 結果以 `recommendations` 事件跟在 `final` 後面，掛在該輪的追問卡／定稿卡下方：每個維度一列縮圖。
3. 使用者按「採用」打開對照表：一列一個 facet，「留我的」或「照它的」（沒講過的預設照它的）。確定後前端送 `{ adopt: { presetId, dimension, take } }`，**伺服器**組一句「採用〈標題〉（知識庫 #id）：上半身照它的（purple kimono）；鞋履保留我的。」當使用者訊息，模型照 system.md 第 6 條把「照它的」facet 標 covered 並 `FinalizePrompt`。
4. 定稿時伺服器把採用帶進來的 tag 標第四種來源 `adopted`（優先序 base → adopted → rag → llm）。audit 記推薦了什麼、採用了什麼、補上／換掉幾個 facet；`scripts/adoption_report.py` 算採用率。

設計：`docs/superpowers/specs/2026-09-25-set-recommendations-design.md`。
```

- [ ] **Step 2: `docs/eval-cases.md`**

檔尾加：

```markdown
## 2026-09-25 整套組合推薦與採用（待跑）

設計：`docs/superpowers/specs/2026-09-25-set-recommendations-design.md`。瀏覽器驗收，需要 API、知識庫，且 `facet_tags` 已回填（`scripts/backfill_facet_tags.py` 或 seed-v2）。

| # | 操作 | 應該看到 | 結果 |
| :--- | :--- | :--- | :--- |
| S1 | 新對話，送「一個少女穿涼鞋」 | 儀表板鞋履 chip 的 title 含模型給的英文 tag（如 `sandals`）；追問卡底下「參考組合」只有被問的維度；穿著那列副標「含你講的 sandals」（`anchored=true`）、3 張縮圖有來源標籤；縮圖點開抽屜 | ⏳ |
| S2 | 回「直接給我」定稿 | 定稿卡底下每個適用維度都有一列參考組合；穿著列 `anchored=true`；沒錨的維度副標「最接近你描述的組合」 | ⏳ |
| S3 | 在穿著列按一套的「採用」：上半身切「照它的」、鞋履維持「留我的」、確定 | 對照表：missing 的列預設「照它的」、covered 的預設「留我的」、這套沒有的列停用；確定後使用者泡泡先是「採用〈標題〉…」再變成伺服器組的整句；新定稿卡上半身 tag 是洋紅色 `adopted` chip（點開抽屜）、鞋履仍是原詞；「檢索貢獻」多「採用」那列；audit `Turn_Completed` 有 `adoption.filled` 含 `clothing.upper`、`tagOrigins.adopted ≥ 1` | ⏳ |
| S4 | `retrieval: off` 的新對話跑到定稿；再用 curl 對它送 `{"adopt":{"presetId":1,"dimension":"clothing","take":["clothing.upper"]}}` | 沒有任何「參考組合」區塊；curl 回 `409` | ⏳ |
| S5 | 在 S3 的對話重新整理 | 兩張定稿卡與參考組合都回來；只有最新一張的「採用」可按，舊的停用並提示；`adopted` chip 仍在 | ⏳ |
| S6 | 跑 `python scripts/adoption_report.py --since <今天>` | 定稿輪採用率 ≥ 1 次、各維度採用次數有 clothing | ⏳ |
```

- [ ] **Step 3: `docs/known-issues.md`**

在「## 6. 子專案 4 全分支審查留下的小項目」之前加：

```markdown
## 9. 整套組合推薦的已知限制（2026-09-25）

- **錨靠模型翻譯**：追問階段的錨是模型在 `SetFacetStates` 給的英文 `tags`，翻錯或沒給就退回無錨（「最接近你描述的組合」），不報錯。定稿後多了 positive 的 tag 當錨，會好一些。
- **同義詞抓不到**：錨比對是整段相等或空白為界的字尾（`platform sandals` ↔ `sandals`），`slippers` 對 `sandals` 不會命中。後續的 facet 向量案（子表 `preset_facet_embeddings`）用「該 facet 向量最近的」補這個缺口，排在本案之後。
- **採用那一輪失敗後的重試是純文字**：失敗條目的「重試」把伺服器組的採用句填回輸入框，重送時走一般訊息，模型仍會照第 6 條定稿，但 `Adoption` 沒記帳、tag 不會標 `adopted`。要重新採用請再按一次卡片上的「採用」。
```

- [ ] **Step 4: `README.md`**

第一段「…逐個 tag 標示來源（知識庫片段或模型生成）。」改成「…逐個 tag 標示來源（知識庫片段、採用的組合或模型生成）。每次追問與定稿另外推薦知識庫裡真實存在、有圖的整套組合，使用者可逐項採用。」；「## 文件」的子專案設計列表加「[整套組合推薦](docs/superpowers/specs/2026-09-25-set-recommendations-design.md)」（放在「前端與 SSE」之後）。

- [ ] **Step 5: 主規格 §12.3**

23 條之後加「24. 整套組合推薦與採用 → `docs/eval-cases.md` S1–S6（2026-09-25）」。

- [ ] **Step 6: Commit**

```bash
git add docs README.md
git commit -m "docs: flow doc, eval cases S1-S6, known limitations and README for set recommendations"
```

---

### Task 16: 回填、seed v2、驗收

**Files:**
- Modify: `docker-compose.yml`（`SEED_URL` 預設值）
- Modify: `docs/eval-cases.md`（填結果）

這個任務是手動操作與驗收，沒有 TDD；每步有可檢查的輸出。要 `.env` 裡的 `GEMINI_API_KEY` 與跑著的 `db`。

- [ ] **Step 1: 回填**

```bash
cd scripts
python backfill_facet_tags.py --dry-run --limit 20        # 人眼看 20 筆拆得對不對（tag 有沒有被歸錯 facet）
python backfill_facet_tags.py                             # 全量，約 1,000 次呼叫；中斷可重跑
docker exec prompt-copilot-db psql -U postgres -d prompt_copilot -Atc "SELECT count(*) FILTER (WHERE facet_tags IS NULL), count(*) FILTER (WHERE facet_tags = '{}'), count(*) FROM prompt_knowledge_presets"
```

Expected：最後一行第一個數字是 0；`{}` 的比例要小（幾百筆以內），大了就抽幾筆看 prompt 有沒有問題。腳本末尾的 `other` 佔比也記下來（合理範圍 5–20%）。

- [ ] **Step 2: 整合測試**

```bash
PC_INTEGRATION=1 dotnet test src/PromptCopilot.Api.Tests --filter "Category=Integration"
```

Expected: 全部 passed。

- [ ] **Step 3: 瀏覽器驗收 S1–S6**

依 `manual-tests/README.md` 起 API 與前端，跑 `docs/eval-cases.md` 的 S1–S6，把結果欄從 ⏳ 改成 ✅／❌ 加一句觀察。❌ 的回到對應任務修。

- [ ] **Step 4: seed v2**

```bash
python scripts/export_seed.py --version 2
# 依印出的 gh 指令上傳 Release seed-v2
```

`docker-compose.yml` 的 `SEED_URL` 預設值改成 `…/releases/download/seed-v2/prompt_copilot_seed_v2.dump`。用一個新的 volume 名稱（或 `docker compose down -v` 後）跑 `docker compose up -d --build`，看 seed log 有「migration 002_facet_tags.sql」、沒有「尚未拆分 facet」的提示。

- [ ] **Step 5: Commit**

```bash
git add docker-compose.yml docs/eval-cases.md
git commit -m "chore: seed-v2 with facet_tags; record S1-S6 acceptance results"
```

---

## 執行順序與依賴

- Task 1 → 2 → 3 是資料層，可先做；Task 2 的全量回填（Task 16 Step 1）可以在後端開發時背景跑。
- Task 4、5 互不依賴；Task 6 依賴 3、4、5；Task 7 依賴 3、5；Task 8 依賴 5、7；Task 9 依賴 7、8。
- Task 10 → 11 → 12 → 13 是前端，依賴後端契約（Task 4、6、8、9 定的線上形狀），可在 Task 9 完成後平行進行。
- Task 14 只依賴 audit 形狀（Task 6、8）。Task 15 收尾文件；Task 16 最後。
