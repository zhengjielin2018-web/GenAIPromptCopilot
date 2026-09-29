# facet 層級向量檢索 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** `SearchPresets` 的 facet 項目改用「該 facet 自己的向量」排序並去重，推薦在字面錨抓不到時多一條「近似錨」退路。

**Architecture:** 新子表 `preset_facet_embeddings(preset_id, facet_id, tag_key, embedding)` 由 Python 腳本從 `facet_tags` 離線算好（併發呼叫 Gemini），seed-v3 帶著它。C# 端 `PresetRepository` 多三個查詢（facet 池計數、facet 檢索去重、近似錨推薦），`KnowledgePlugin` 的 facet 項目走新查詢並接受模型給的英文 `tags`，`RecommendationService` 在字面錨不到 2 筆時改試近似錨。前端多兩段文案。查詢全部用 B-tree 縮池後精確排序，不建 HNSW（spec §7）。

**Tech Stack:** PostgreSQL 16 + pgvector、.NET 10 / Semantic Kernel 1.80、Npgsql + Pgvector、Python 3.12（psycopg 3、pgvector、google-genai、pytest）、Vue 3 + Vitest。

**Spec:** `docs/superpowers/specs/2026-09-29-facet-vector-retrieval-design.md`（實驗數字在 `docs/experiments/2026-09-29-facet-vector-text.md`）

## Global Constraints

- 註解、文件、commit 標題以外的說明用繁體中文，密度與風格照周圍程式（解釋「為什麼」、引用 spec 章節或 known-issues 編號）。
- 每個 commit 訊息結尾加 `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`（執行者若不是 Opus，改成當時 harness 給的那一行）。
- 向量型別 `VECTOR(768)`，embedding 模型 `gemini-embedding-001`，片段端 `RETRIEVAL_DOCUMENT`、查詢端 `RETRIEVAL_QUERY`，L2 正規化（spec §4、既有管線）。
- 子表**不建 HNSW**（spec §7）；`facet_id` 只建 B-tree。
- facet 項目分級門檻 高 `< 0.22`、中 `< 0.27`；維度項目與退路維持 `< 0.25`／`< 0.30`（spec §5.4）。
- 近似錨門檻預設 `0.23`（`OrchestratorOptions.RecommendationSimilarMaxDist`，spec §6.4）；近似錨候選門檻與字面錨相同 `MinAnchoredHits = 2`。
- `SearchQuery.tags` 是 `init` 屬性、不進建構子（spec §5.1）；伺服器不用它更新 `session.FacetTags`。
- 回給模型的 `SearchPresets` JSON 形狀不變（spec §5.5）。
- Python 測試從 `scripts/` 執行：`./.venv/Scripts/python.exe -m pytest tests/...`（Windows）；C# 測試 `dotnet test src/PromptCopilot.Api.Tests`（integration 測試沒有 `PC_INTEGRATION=1` 會 skip）；前端 `npm test`（Node 不在 shell PATH 上，先 `export PATH="$PATH:/c/Users/USER/AppData/Local/Microsoft/WinGet/Links"`，或照 `docs/known-issues.md` 之前的做法）。
- 不改 `scripts/pipeline/retrieval.py`、`scripts/demo.py`、`backfill_facet_tags.py`（spec §3.2）。
- 文件跟程式同一個分支（記憶規則：動流程就同步文件）。

## Review Focus

spec 沒逐條寫、但使用者一定會碰到的輸入；每一條都在對應任務裡加了測試：

1. **模型把 `tags` 填成空白或只有逗號**（`" "`、`","`）：要當成沒給，查詢句不能變成「涼鞋（）」。→ Task 7 `Blank_tags_are_treated_as_absent`。
2. **同一片段同一 facet 的 tag 只差大小寫或權重**（`Sandals`、`(sandals:1.2)`）：`tag_key` 要合併成一個、embedding 文字只留一份。→ Task 2 `test_tag_key_and_text_dedup_after_normalization`。
3. **產生腳本跑到一半有一批 Gemini 失敗**：其他批照寫、程序不中止、結尾回非零並列出失敗批；重跑只補那一批。→ Task 3 `test_failed_batch_leaves_others_written_and_is_retried_next_run`。
4. **`FacetTags` 正規化後是空的**（模型給 `"( :1.2)"`）：近似錨要跳過這個 facet，不能拿空字串去 embed。→ Task 9 `Facet_whose_tags_normalize_to_nothing_is_not_embedded_as_an_anchor`。
5. **舊版前端存下的對話沒有 `similar`／`method`／`tags`**（sessionStorage 還原）：畫面要當成 `false`／`preset`／沒有英文，不能炸。→ Task 10 `recommendationLead`／`queryLabel` 對缺欄位的測試。

---

## 檔案結構

| 檔案 | 責任 |
| :--- | :--- |
| `db/migrations/003_preset_facet_embeddings.sql`（新） | 冪等建子表與 B-tree 索引；註解說明為何不建 HNSW |
| `db/init/001_schema.sql` | 新建資料庫用的同一份 DDL |
| `scripts/pipeline/tags.py`（新） | tag 正規化、embedding 文字、`tag_key`；與 C# `TagAttribution.Normalize` 對齊 |
| `scripts/embed_facet_tags.py`（新） | 算待辦、併發 embed、主執行緒寫庫、統計 |
| `scripts/export_seed.py`、`docker/seed.sh` | seed-v3 帶子表；子表空的提醒 |
| `src/PromptCopilot.Api/Data/PresetRepository.cs` | `FacetPoolSizeAsync`、`SearchFacetAsync`、`RecommendSimilarAsync` |
| `src/PromptCopilot.Api/Plugins/Contracts.cs` | `SearchQuery.Tags` |
| `src/PromptCopilot.Api/Streaming/ToolDetails.cs` | `SearchPresetsItem.Tags`／`Method` |
| `src/PromptCopilot.Api/Plugins/KnowledgePlugin.cs` | facet 項目走子表、查詢句組法、分級門檻、退路 |
| `src/PromptCopilot.Api/Orchestration/SystemPromptBuilder.cs` | 第 1 條範例加英文 tag |
| `src/PromptCopilot.Api/Configuration/Options.cs` | `RecommendationSimilarMaxDist` |
| `src/PromptCopilot.Api/Streaming/Recommendations.cs` | `RecommendedDimension.Similar` |
| `src/PromptCopilot.Api/Orchestration/RecommendationService.cs` | 近似錨流程 |
| `src/PromptCopilot.Api/Orchestration/AgenticOrchestrator.cs` | audit 加 `similar` |
| `src/PromptCopilot.Frontend/types/api.ts`、`lib/copy.ts`、`components/RecommendationStrip.vue`、`components/ToolCallCard.vue` | 三種文案、查詢句顯示、method 標示 |
| `docs/known-issues.md`、主規格 §9、維度檢索設計 §13、`docs/SK架構說明.md`、`docs/eval-cases.md`、實驗紀錄 | 文件同步 |

---

### Task 1: 子表 DDL（migration＋schema＋seed 提醒）

**Files:**
- Create: `db/migrations/003_preset_facet_embeddings.sql`
- Modify: `db/init/001_schema.sql`（在 `idx_presets_embedding` 之後、`audit_logs` 之前插入）
- Modify: `docker/seed.sh`（`warn_unfilled` 之後加 `warn_no_facet_vectors`）
- Test: `scripts/tests/test_facet_schema.py`（新，純檔案檢查）、`scripts/tests/test_db.py`（integration 加表名）

**Interfaces:**
- Produces: 表 `preset_facet_embeddings(preset_id BIGINT FK, facet_id TEXT, tag_key TEXT, embedding VECTOR(768))`，PK `(preset_id, facet_id)`，索引 `idx_pfe_facet(facet_id)`。後面所有任務的 SQL 都依這個形狀。

- [ ] **Step 1: 寫檔案內容的失敗測試**

`scripts/tests/test_facet_schema.py`：

```python
"""子表 DDL 的契約：migration 與 001_schema.sql 要同一份、要冪等、不建 HNSW（facet 向量設計 §4.1、§7）。"""

from __future__ import annotations

import re
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
MIGRATION = ROOT / "db" / "migrations" / "003_preset_facet_embeddings.sql"
SCHEMA = ROOT / "db" / "init" / "001_schema.sql"


def _table_block(sql: str) -> str:
    m = re.search(r"CREATE TABLE IF NOT EXISTS preset_facet_embeddings \((.*?)\);", sql, re.S)
    assert m, "找不到 preset_facet_embeddings 的 CREATE TABLE IF NOT EXISTS"
    return m.group(1)


def test_migration_is_idempotent_and_has_the_agreed_columns():
    sql = MIGRATION.read_text(encoding="utf-8")
    block = _table_block(sql)
    assert "preset_id  BIGINT NOT NULL REFERENCES prompt_knowledge_presets(id) ON DELETE CASCADE" in block
    assert "facet_id   TEXT   NOT NULL" in block
    assert "tag_key    TEXT   NOT NULL" in block
    assert "embedding  VECTOR(768) NOT NULL" in block
    assert "PRIMARY KEY (preset_id, facet_id)" in block
    assert "CREATE INDEX IF NOT EXISTS idx_pfe_facet ON preset_facet_embeddings (facet_id);" in sql


def test_migration_does_not_build_hnsw_on_the_subtable():
    sql = MIGRATION.read_text(encoding="utf-8")
    assert "hnsw" not in sql.lower(), "子表刻意不建 HNSW（設計 §7）；註解也不要寫這個字，用「向量索引」「近似索引」"
    assert "設計 §7" in sql


def test_schema_carries_the_same_table_for_fresh_databases():
    schema = SCHEMA.read_text(encoding="utf-8")
    assert _table_block(schema) == _table_block(MIGRATION.read_text(encoding="utf-8"))
    assert "idx_pfe_facet" in schema
```

同時把 `scripts/tests/test_db.py` 的表名集合改成：

```python
        assert {"shared_prompt_histories", "prompt_knowledge_presets", "audit_logs", "preset_facet_embeddings"} <= names
```

- [ ] **Step 2: 跑測試確認失敗**

Run: `cd scripts && ./.venv/Scripts/python.exe -m pytest tests/test_facet_schema.py -v`
Expected: 3 個 FAIL（`FileNotFoundError` 或 assert 找不到 CREATE TABLE）

- [ ] **Step 3: 寫 migration**

`db/migrations/003_preset_facet_embeddings.sql`：

```sql
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
```

注意 `test_migration_does_not_build_hnsw_on_the_subtable` 不允許檔案裡出現 `hnsw`（連註解都不行），所以上面的註解寫「向量索引」「近似索引」。

- [ ] **Step 4: 同步 `001_schema.sql`**

在 `CREATE INDEX idx_presets_embedding …;` 之後、`-- 稽核：` 之前插入（CREATE TABLE 區塊要跟 migration **逐字相同**，測試用字串比對）：

```sql

-- facet 層級向量（2026-09-29，facet 向量設計 §4.1）：每個片段在每個有 tag 的 facet 各一列，向量算自該 facet 的英文 tag。
-- 不建向量索引：池小、要去重、精確排序才可重現；理由見設計 §7。既有資料庫用 db/migrations/003_preset_facet_embeddings.sql。
CREATE TABLE IF NOT EXISTS preset_facet_embeddings (
    preset_id  BIGINT NOT NULL REFERENCES prompt_knowledge_presets(id) ON DELETE CASCADE,
    facet_id   TEXT   NOT NULL,
    tag_key    TEXT   NOT NULL,
    embedding  VECTOR(768) NOT NULL,
    PRIMARY KEY (preset_id, facet_id)
);
CREATE INDEX IF NOT EXISTS idx_pfe_facet ON preset_facet_embeddings (facet_id);
```

- [ ] **Step 5: `seed.sh` 加子表空的提醒**

在 `warn_unfilled()` 定義之後加：

```sh
# facet 向量子表（2026-09-29）：v2 以前的種子沒有這張表的資料，SearchPresets 的 facet 項目會退回整套向量。
warn_no_facet_vectors() {
  presets=$(psql -tAc "SELECT count(*) FROM prompt_knowledge_presets")
  vectors=$(psql -tAc "SELECT count(*) FROM preset_facet_embeddings")
  if [ "$presets" -gt 0 ] && [ "$vectors" -eq 0 ]; then
    echo "seed: preset_facet_embeddings 是空的，facet 檢索會退回整套向量。跑 scripts/embed_facet_tags.py，或改用 seed-v3 以上的種子。"
  fi
}
```

並在兩處呼叫 `warn_unfilled` 的下一行各加一行 `warn_no_facet_vectors`。

- [ ] **Step 6: 跑測試確認通過**

Run: `cd scripts && ./.venv/Scripts/python.exe -m pytest tests/test_facet_schema.py -v`
Expected: 3 PASS

- [ ] **Step 7: 對開發庫套 migration 並確認表存在**

Run: `docker compose exec -T db psql -U postgres -d prompt_copilot -v ON_ERROR_STOP=1 -f - < db/migrations/003_preset_facet_embeddings.sql && docker compose exec -T db psql -U postgres -d prompt_copilot -tAc "SELECT count(*) FROM preset_facet_embeddings"`
Expected: `CREATE TABLE`、`CREATE INDEX`、然後 `0`。（容器名稱以 `docker compose ps` 為準；帳號密碼看 `.env`。）

- [ ] **Step 8: Commit**

```bash
git add db/migrations/003_preset_facet_embeddings.sql db/init/001_schema.sql docker/seed.sh scripts/tests/test_facet_schema.py scripts/tests/test_db.py
git commit -m "feat(db): preset_facet_embeddings subtable, B-tree only, with seed reminder"
```

---

### Task 2: tag 正規化、embedding 文字、`tag_key`（Python）＋ C# 對齊測試

**Files:**
- Create: `scripts/pipeline/tags.py`
- Test: `scripts/tests/test_tags.py`（新）
- Modify: `src/PromptCopilot.Api.Tests/Sessions/TagAttributionTests.cs`（加同一組案例）

**Interfaces:**
- Produces（Python）：
  - `normalize_tag(tag: str) -> str`：與 C# `TagAttribution.Normalize` 相同。
  - `embedding_text(tags: list[str]) -> str`：正規化、丟空、保序去重、`", "` 串接。
  - `tag_key(tags: list[str]) -> str`：正規化、丟空、排序去重、`", "` 串接。
  - `SHARED_CASES: list[tuple[str, str]]`：兩邊共用的正規化案例（原字 → 結果）。
- Consumes：無。

- [ ] **Step 1: 寫失敗測試**

`scripts/tests/test_tags.py`：

```python
"""tag 正規化要與 C# TagAttribution.Normalize 逐字相同：同一組 SHARED_CASES 在
src/PromptCopilot.Api.Tests/Sessions/TagAttributionTests.cs 也跑一次。改一邊就要改另一邊。"""

from __future__ import annotations

import pytest

from pipeline.tags import SHARED_CASES, embedding_text, normalize_tag, tag_key


@pytest.mark.parametrize(("raw", "expected"), SHARED_CASES)
def test_normalize_matches_the_shared_cases(raw, expected):
    assert normalize_tag(raw) == expected


def test_shared_cases_cover_the_rules_the_csharp_side_implements():
    raw = dict(SHARED_CASES)
    assert raw["(Sandals:1.2)"] == "sandals"                 # 權重＋括號
    assert raw["platform_sandals"] == "platform sandals"      # 底線
    assert raw["((tag))"] == "tag"                            # 多層括號
    assert raw["(a) (b)"] == "(a) (b)"                        # 頭尾括號但不是同一對
    assert raw["  Long   Hair "] == "long hair"               # 連續空白
    assert raw["(masterpiece:1.2)"] == "masterpiece"
    assert raw["tag:0.8"] == "tag"                            # 沒括號的權重
    assert raw["( :1.2)"] == ""                               # 剝完什麼都不剩


def test_embedding_text_keeps_order_drops_empty_and_dedups_after_normalization():
    assert embedding_text(["Sandals", "(sandals:1.2)", "white_socks", "", "( :1.2)"]) == "sandals, white socks"


def test_tag_key_ignores_order_and_duplicates():
    assert tag_key(["sunset", "golden hour"]) == tag_key(["golden hour", "sunset"]) == "golden hour, sunset"
    assert tag_key(["Sandals", "(sandals:1.2)"]) == "sandals"


def test_tag_key_and_text_dedup_after_normalization():
    # Review Focus 2：只差大小寫或權重的 tag 算同一個
    tags = ["Sandals", "(sandals:1.2)", "sandals"]
    assert tag_key(tags) == "sandals" and embedding_text(tags) == "sandals"


def test_empty_input_gives_empty_strings():
    assert embedding_text([]) == "" and tag_key([]) == ""
    assert embedding_text(["( :1.2)"]) == "" and tag_key(["( :1.2)"]) == ""
```

- [ ] **Step 2: 跑測試確認失敗**

Run: `cd scripts && ./.venv/Scripts/python.exe -m pytest tests/test_tags.py -v`
Expected: `ModuleNotFoundError: No module named 'pipeline.tags'`

- [ ] **Step 3: 實作 `scripts/pipeline/tags.py`**

```python
"""tag 正規化與 facet 向量的文字（facet 向量設計 §4.2）。

normalize_tag 必須與 C# TagAttribution.Normalize 逐字相同：小寫、底線換空白、連續空白壓成一個、
再反覆剝掉最外層成對括號與「:數字」權重。兩邊用同一組 SHARED_CASES 測。"""

from __future__ import annotations

import re

_SPACES = re.compile(r"\s+")
_WEIGHT = re.compile(r":\s*-?\d*\.?\d+$")

# (原字, 正規化結果)。C# 的 TagAttributionTests.Normalize_matches_the_shared_cases 是同一份，改這裡就要改那裡。
SHARED_CASES: list[tuple[str, str]] = [
    ("(Sandals:1.2)", "sandals"),
    ("platform_sandals", "platform sandals"),
    ("((tag))", "tag"),
    ("(a) (b)", "(a) (b)"),
    ("  Long   Hair ", "long hair"),
    ("(masterpiece:1.2)", "masterpiece"),
    ("tag:0.8", "tag"),
    ("( :1.2)", ""),
    ("sandals", "sandals"),
    ("(Long_Hair:1.2)", "long hair"),
]


def _wrapped_in_parentheses(s: str) -> bool:
    """開頭的 ( 要跟結尾的 ) 成對：「(a) (b)」頭尾都是括號，但不是同一對。"""
    if len(s) < 2 or s[0] != "(" or s[-1] != ")":
        return False
    depth = 0
    for i, ch in enumerate(s):
        if ch == "(":
            depth += 1
        elif ch == ")":
            depth -= 1
            if depth == 0:
                return i == len(s) - 1
    return False


def normalize_tag(tag: str) -> str:
    s = _SPACES.sub(" ", tag.lower().replace("_", " ")).strip()
    while True:
        before = s
        if _wrapped_in_parentheses(s):
            s = s[1:-1].strip()
        s = _WEIGHT.sub("", s).rstrip()
        if s == before:
            return s


def _normalized(tags: list[str]) -> list[str]:
    seen: set[str] = set()
    out: list[str] = []
    for t in tags:
        n = normalize_tag(t)
        if n and n not in seen:
            seen.add(n)
            out.append(n)
    return out


def embedding_text(tags: list[str]) -> str:
    """向量的文字：照原順序（片段作者的排列有語意）。"""
    return ", ".join(_normalized(tags))


def tag_key(tags: list[str]) -> str:
    """去重鍵：排序，順序不同的同一組 tag 同鍵。"""
    return ", ".join(sorted(_normalized(tags)))
```

- [ ] **Step 4: 跑 Python 測試確認通過**

Run: `cd scripts && ./.venv/Scripts/python.exe -m pytest tests/test_tags.py -v`
Expected: 全部 PASS

- [ ] **Step 5: C# 端加同一組案例**

在 `src/PromptCopilot.Api.Tests/Sessions/TagAttributionTests.cs` 類別裡加：

```csharp
    /// <summary>與 scripts/pipeline/tags.py 的 SHARED_CASES 同一份：facet 向量的文字由 Python 算、C# 查詢時比對，兩邊正規化不能有一字之差。改一邊就要改另一邊。</summary>
    [Theory]
    [InlineData("(Sandals:1.2)", "sandals")]
    [InlineData("platform_sandals", "platform sandals")]
    [InlineData("((tag))", "tag")]
    [InlineData("(a) (b)", "(a) (b)")]
    [InlineData("  Long   Hair ", "long hair")]
    [InlineData("(masterpiece:1.2)", "masterpiece")]
    [InlineData("tag:0.8", "tag")]
    [InlineData("( :1.2)", "")]
    [InlineData("sandals", "sandals")]
    [InlineData("(Long_Hair:1.2)", "long hair")]
    public void Normalize_matches_the_shared_cases(string raw, string expected) => Assert.Equal(expected, TagAttribution.Normalize(raw));
```

- [ ] **Step 6: 跑 C# 測試確認通過**

Run: `dotnet test src/PromptCopilot.Api.Tests --filter "FullyQualifiedName~TagAttributionTests"`
Expected: 全部通過（`( :1.2)` → `""` 若失敗，代表 C# 與 Python 行為不同：C# `Normalize` 對 `( :1.2)` 先剝括號得 ` :1.2` → trim → `:1.2` → 剝權重得 `""`。若 C# 實際結果不同，**以 C# 為準改 Python 與 SHARED_CASES**，並在報告裡說明。）

- [ ] **Step 7: Commit**

```bash
git add scripts/pipeline/tags.py scripts/tests/test_tags.py src/PromptCopilot.Api.Tests/Sessions/TagAttributionTests.cs
git commit -m "feat(scripts): tag normalization shared with C#, facet embedding text and tag_key"
```

---

### Task 3: `scripts/embed_facet_tags.py`（待辦計算、併發 embed、主執行緒寫庫）

**Files:**
- Create: `scripts/embed_facet_tags.py`
- Test: `scripts/tests/test_embed_facet_tags.py`（新）

**Interfaces:**
- Consumes：`pipeline.tags.embedding_text`／`tag_key`；`GeminiClient.embed_batch(texts, task_type="RETRIEVAL_DOCUMENT") -> list[list[float]]`；`pipeline.db.connect()`。
- Produces：
  - `Item = tuple[int, str, str, str]`（preset_id, facet_id, tag_key, text）
  - `Plan(upserts: list[Item], deletes: list[tuple[int, str]], unchanged: int)`
  - `make_plan(presets: list[tuple[int, dict[str, list[str]]]], existing: dict[tuple[int, str], str]) -> Plan`
  - `run(conn, client, *, workers=4, batch_size=32, limit=None, dry_run=False, log=print) -> dict`（stats：`upserted`、`deleted`、`unchanged`、`failed_batches`、`elapsed_s`）
  - CLI：`python embed_facet_tags.py [--workers 4] [--batch-size 32] [--limit N] [--dry-run]`；有失敗批回傳 1。

- [ ] **Step 1: 寫失敗測試**

`scripts/tests/test_embed_facet_tags.py`：

```python
"""不打網路、不連 DB：client 與 conn 都是記錄器。併發用真的 ThreadPoolExecutor，但 client 是假的。"""

from __future__ import annotations

import json
import threading

from embed_facet_tags import DELETE_SQL, UPSERT_SQL, Plan, make_plan, run

PRESETS = [
    (1, {"clothing.footwear": ["sandals"], "clothing.upper": ["kimono", "(kimono:1.2)"]}),
    (2, {"clothing.footwear": ["Sandals"], "clothing.lower": []}),          # lower 空陣列：不算
    (3, {}),                                                                 # {}：整筆略過
]


def test_make_plan_upserts_missing_rows_skips_unchanged_and_deletes_vanished_facets():
    existing = {(1, "clothing.footwear"): "sandals",            # 沒變
                (1, "clothing.upper"): "old kimono",            # tag 改過
                (2, "clothing.head"): "hat"}                    # facet_tags 已沒有 head → 刪
    plan = make_plan(PRESETS, existing)
    assert plan.unchanged == 1
    assert plan.deletes == [(2, "clothing.head")]
    assert plan.upserts == [
        (1, "clothing.upper", "kimono", "kimono"),               # 去重後只剩一個
        (2, "clothing.footwear", "sandals", "sandals"),
    ]


def test_make_plan_skips_facets_whose_tags_normalize_to_nothing():
    plan = make_plan([(9, {"style.genre": ["( :1.2)", ""]})], {})
    assert plan == Plan(upserts=[], deletes=[], unchanged=0)


class FakeClient:
    """記每批的文字與執行緒；fail_on：文字包含它的那一批丟例外。"""

    def __init__(self, fail_on: str | None = None):
        self.batches: list[list[str]] = []
        self.threads: set[int] = set()
        self.fail_on = fail_on
        self._lock = threading.Lock()

    def embed_batch(self, texts, *, task_type):
        assert task_type == "RETRIEVAL_DOCUMENT"
        with self._lock:
            self.batches.append(list(texts))
            self.threads.add(threading.get_ident())
        if self.fail_on and any(self.fail_on in t for t in texts):
            raise RuntimeError("boom")
        return [[float(len(t))] + [0.0] * 767 for t in texts]


class FakeResult:
    def __init__(self, rows):
        self.rows = rows

    def fetchall(self):
        return self.rows


class FakeConn:
    """presets：SELECT 片段回的列；existing：SELECT 子表回的列。寫入記在 writes（依執行順序）。"""

    def __init__(self, presets, existing):
        self.presets, self.existing = presets, existing
        self.writes: list[tuple[str, tuple]] = []
        self.commits = 0
        self.rollbacks = 0
        self.write_threads: set[int] = set()

    def execute(self, sql, params=None):
        if "FROM prompt_knowledge_presets" in sql:
            return FakeResult([(pid, json.dumps(ft)) for pid, ft in self.presets])
        if "FROM preset_facet_embeddings" in sql:
            return FakeResult(self.existing)
        self.writes.append((sql, params))
        self.write_threads.add(threading.get_ident())
        return FakeResult([])

    def cursor(self):
        conn = self

        class Cur:
            def __enter__(self):
                return self

            def __exit__(self, *exc):
                return False

            def execute(self, sql, params):
                conn.writes.append((sql, params))
                conn.write_threads.add(threading.get_ident())

            def executemany(self, sql, seq):
                for params in seq:
                    self.execute(sql, params)

        return Cur()

    def commit(self):
        self.commits += 1

    def rollback(self):
        self.rollbacks += 1


def _upserts(conn):
    return [(p[0], p[1], p[2]) for sql, p in conn.writes if sql == UPSERT_SQL]


def test_run_embeds_in_batches_across_workers_and_writes_only_from_the_main_thread():
    presets = [(i, {"style.genre": [f"tag{i}"]}) for i in range(7)]
    conn, client = FakeConn(presets, []), FakeClient()
    stats = run(conn, client, workers=3, batch_size=3, log=lambda *_: None)

    assert sorted(len(b) for b in client.batches) == [1, 3, 3]
    assert threading.get_ident() not in client.threads               # 送出在執行緒池，不在主執行緒（池會不會開到 3 條看排程，不斷言條數）
    assert conn.write_threads == {threading.get_ident()}             # 只有主執行緒寫
    assert sorted(_upserts(conn)) == [(i, "style.genre", f"tag{i}") for i in range(7)]
    assert conn.commits == 3                                         # 每批一個交易
    assert stats["upserted"] == 7 and stats["failed_batches"] == [] and stats["deleted"] == 0


def test_run_deletes_vanished_rows_first_in_their_own_transaction():
    conn = FakeConn([(1, {"style.genre": ["a"]})], [(1, "style.palette", "x")])
    stats = run(conn, FakeClient(), workers=1, log=lambda *_: None)
    assert conn.writes[0][0] == DELETE_SQL and conn.writes[0][1] == (1, "style.palette")
    assert stats["deleted"] == 1 and conn.commits == 2


def test_failed_batch_leaves_others_written_and_is_retried_next_run():
    # Review Focus 3
    presets = [(1, {"style.genre": ["good"]}), (2, {"style.genre": ["bad"]}), (3, {"style.genre": ["fine"]})]
    conn, client = FakeConn(presets, []), FakeClient(fail_on="bad")
    stats = run(conn, client, workers=2, batch_size=1, log=lambda *_: None)
    assert sorted(_upserts(conn)) == [(1, "style.genre", "good"), (3, "style.genre", "fine")]
    assert stats["failed_batches"] == [[(2, "style.genre")]]         # 失敗批裡的 (preset, facet)
    assert stats["upserted"] == 2

    # 重跑：已寫的當 existing，只剩失敗的那一筆
    conn2 = FakeConn(presets, [(1, "style.genre", "good"), (3, "style.genre", "fine")])
    stats2 = run(conn2, FakeClient(), workers=2, batch_size=1, log=lambda *_: None)
    assert _upserts(conn2) == [(2, "style.genre", "bad")] and stats2["unchanged"] == 2


def test_dry_run_plans_but_neither_embeds_nor_writes():
    conn, client = FakeConn([(1, {"style.genre": ["a"]})], [(1, "style.palette", "x")])
    stats = run(conn, client, dry_run=True, log=lambda *_: None)
    assert client.batches == [] and conn.writes == [] and conn.commits == 0
    assert stats["planned_upserts"] == 1 and stats["planned_deletes"] == 1


def test_limit_caps_the_number_of_items_embedded_this_run():
    presets = [(i, {"style.genre": [f"t{i}"]}) for i in range(5)]
    conn = FakeConn(presets, [])
    stats = run(conn, FakeClient(), limit=2, batch_size=10, log=lambda *_: None)
    assert stats["upserted"] == 2 and len(_upserts(conn)) == 2


def test_upsert_sql_updates_tag_key_and_embedding_on_conflict():
    assert "ON CONFLICT (preset_id, facet_id) DO UPDATE" in UPSERT_SQL
    assert "tag_key = EXCLUDED.tag_key" in UPSERT_SQL and "embedding = EXCLUDED.embedding" in UPSERT_SQL
```

- [ ] **Step 2: 跑測試確認失敗**

Run: `cd scripts && ./.venv/Scripts/python.exe -m pytest tests/test_embed_facet_tags.py -v`
Expected: `ModuleNotFoundError: No module named 'embed_facet_tags'`

- [ ] **Step 3: 實作 `scripts/embed_facet_tags.py`**

```python
"""產生／更新 preset_facet_embeddings：每個片段在每個有 tag 的 facet 各一個向量（facet 向量設計 §4.3）。

    python embed_facet_tags.py [--workers 4] [--batch-size 32] [--limit N] [--dry-run]

待辦＝子表沒有這一列、或 tag_key 跟現在的 facet_tags 算出來的不同（tag 改過）；facet_tags 已沒有的 facet 刪掉那一列。
可中斷、可重跑：每完成一批就 upsert 並提交，重跑只補沒寫進去的。Gemini 呼叫用執行緒池併發，全部共用
同一個 GeminiClient——也就共用它的 RateLimiter（總速率上限由 GEMINI_MIN_INTERVAL_S 決定）與 429／5xx 退避。
寫庫只在主執行緒：psycopg 連線不能跨執行緒共用。某一批失敗就記下來、其他批照寫，結尾回傳非零；重跑會再試那一批。
"""

from __future__ import annotations

import argparse
import json
import sys
import time
from collections.abc import Callable
from concurrent.futures import ThreadPoolExecutor, as_completed
from dataclasses import dataclass, field
from pathlib import Path

from pgvector import Vector

sys.path.insert(0, str(Path(__file__).resolve().parent))

from pipeline.tags import embedding_text, tag_key  # noqa: E402

BATCH_SIZE = 32
WORKERS = 4
Item = tuple[int, str, str, str]  # preset_id, facet_id, tag_key, text

PRESETS_SQL = "SELECT id, facet_tags::text FROM prompt_knowledge_presets WHERE facet_tags IS NOT NULL AND facet_tags <> '{}'::jsonb ORDER BY id"
EXISTING_SQL = "SELECT preset_id, facet_id, tag_key FROM preset_facet_embeddings"
UPSERT_SQL = (
    "INSERT INTO preset_facet_embeddings (preset_id, facet_id, tag_key, embedding) VALUES (%s, %s, %s, %s) "
    "ON CONFLICT (preset_id, facet_id) DO UPDATE SET tag_key = EXCLUDED.tag_key, embedding = EXCLUDED.embedding"
)
DELETE_SQL = "DELETE FROM preset_facet_embeddings WHERE preset_id = %s AND facet_id = %s"


@dataclass(frozen=True)
class Plan:
    upserts: list[Item] = field(default_factory=list)
    deletes: list[tuple[int, str]] = field(default_factory=list)
    unchanged: int = 0


def make_plan(presets: list[tuple[int, dict[str, list[str]]]], existing: dict[tuple[int, str], str]) -> Plan:
    """presets：(id, facet_tags)；existing：(preset_id, facet_id) → 子表現有的 tag_key。"""
    upserts: list[Item] = []
    unchanged = 0
    wanted: set[tuple[int, str]] = set()
    for pid, facet_tags in presets:
        for facet_id, tags in facet_tags.items():
            key = tag_key(tags)
            if not key:                      # 空陣列、或正規化後什麼都不剩：這個 facet 沒有向量可算
                continue
            wanted.add((pid, facet_id))
            if existing.get((pid, facet_id)) == key:
                unchanged += 1
            else:
                upserts.append((pid, facet_id, key, embedding_text(tags)))
    deletes = sorted(k for k in existing if k not in wanted)
    return Plan(upserts=upserts, deletes=deletes, unchanged=unchanged)


def load_plan(conn) -> Plan:
    presets = [(pid, json.loads(ft)) for pid, ft in conn.execute(PRESETS_SQL).fetchall()]
    existing = {(pid, fid): key for pid, fid, key in conn.execute(EXISTING_SQL).fetchall()}
    return make_plan(presets, existing)


def _chunks(items: list[Item], size: int) -> list[list[Item]]:
    return [items[i : i + size] for i in range(0, len(items), size)]


def run(conn, client, *, workers: int = WORKERS, batch_size: int = BATCH_SIZE, limit: int | None = None,
        dry_run: bool = False, log: Callable[..., None] = print) -> dict:
    started = time.monotonic()
    plan = load_plan(conn)
    todo = plan.upserts if limit is None else plan.upserts[:limit]
    stats: dict = {"upserted": 0, "deleted": 0, "unchanged": plan.unchanged, "failed_batches": [],
                   "planned_upserts": len(todo), "planned_deletes": len(plan.deletes), "elapsed_s": 0.0}
    log(f"embed_facet_tags: 待算 {len(todo)}（共 {len(plan.upserts)}）、刪 {len(plan.deletes)}、不變 {plan.unchanged}")
    if dry_run:
        for item in todo[:10]:
            log(f"[dry-run] {item[0]} {item[1]} key={item[2]!r} text={item[3]!r}")
        stats["elapsed_s"] = time.monotonic() - started
        return stats

    if plan.deletes:
        with conn.cursor() as cur:
            cur.executemany(DELETE_SQL, plan.deletes)
        conn.commit()
        stats["deleted"] = len(plan.deletes)

    batches = _chunks(todo, batch_size)

    def embed(batch: list[Item]) -> tuple[list[Item], list[list[float]]]:
        return batch, client.embed_batch([it[3] for it in batch], task_type="RETRIEVAL_DOCUMENT")

    # 送出在執行緒池、寫入在主執行緒：as_completed 誰先回來誰先寫，每批一個交易。
    with ThreadPoolExecutor(max_workers=max(1, workers)) as pool:
        futures = {pool.submit(embed, b): b for b in batches}
        for fut in as_completed(futures):
            batch = futures[fut]
            try:
                items, vectors = fut.result()
            except Exception as e:  # noqa: BLE001 - 一批失敗不該拖垮其他批；記下來結尾回報
                stats["failed_batches"].append([(it[0], it[1]) for it in batch])
                log(f"  批次失敗（{len(batch)} 筆）：{type(e).__name__} {e}")
                continue
            with conn.cursor() as cur:
                for (pid, fid, key, _), vec in zip(items, vectors, strict=True):
                    cur.execute(UPSERT_SQL, (pid, fid, key, Vector(vec)))
            conn.commit()
            stats["upserted"] += len(items)
            log(f"embed_facet_tags: {stats['upserted']}/{len(todo)} 已寫入")
    stats["elapsed_s"] = time.monotonic() - started
    return stats


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--workers", type=int, default=WORKERS, help="同時送 Gemini 的執行緒數")
    ap.add_argument("--batch-size", type=int, default=BATCH_SIZE, help="每次請求幾段文字（gemini_client.BATCH_SIZE 是上限）")
    ap.add_argument("--limit", type=int, default=None, help="本次最多算幾個 (片段, facet)")
    ap.add_argument("--dry-run", action="store_true", help="只算待辦、印前 10 筆，不打 Gemini、不寫入")
    args = ap.parse_args(argv)

    from pipeline.db import connect
    from pipeline.gemini_client import default_client

    with connect() as conn:
        stats = run(conn, default_client(), workers=args.workers, batch_size=args.batch_size,
                    limit=args.limit, dry_run=args.dry_run)
    print(f"完成：寫入 {stats['upserted']}、刪除 {stats['deleted']}、不變 {stats['unchanged']}，{stats['elapsed_s']:.0f} 秒")
    if stats["failed_batches"]:
        n = sum(len(b) for b in stats["failed_batches"])
        print(f"失敗 {len(stats['failed_batches'])} 批、{n} 筆，重跑會再試。第一批：{stats['failed_batches'][0][:5]}")
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
```

注意 `FakeConn.execute` 用 SQL 裡的字串分辨兩個 SELECT：`PRESETS_SQL` 含 `FROM prompt_knowledge_presets`、`EXISTING_SQL` 含 `FROM preset_facet_embeddings`；`DELETE_SQL` 走 `cursor().executemany`，不會被 `execute` 誤判。

- [ ] **Step 4: 跑測試確認通過**

Run: `cd scripts && ./.venv/Scripts/python.exe -m pytest tests/test_embed_facet_tags.py tests/test_tags.py -v`
Expected: 全部 PASS

- [ ] **Step 5: 對開發庫 dry-run 一次**

Run: `cd scripts && ./.venv/Scripts/python.exe embed_facet_tags.py --dry-run`
Expected: 印出「待算 37011（共 37011）、刪 0、不變 0」左右的數字與前 10 筆（數字以當時資料庫為準），不寫入。**不要在這個任務跑完整產生**——那是 merge 之後的事（Task 12）。

- [ ] **Step 6: Commit**

```bash
git add scripts/embed_facet_tags.py scripts/tests/test_embed_facet_tags.py
git commit -m "feat(scripts): embed_facet_tags builds preset_facet_embeddings with concurrent Gemini calls"
```

---

### Task 4: seed-v3 匯出帶子表

**Files:**
- Modify: `scripts/export_seed.py:25-31`（`TABLES`、`COUNT_SQL`）、docstring 提到「兩張知識表」的地方改「三張」
- Test: `scripts/tests/test_export_seed.py`

- [ ] **Step 1: 寫失敗測試**

在 `scripts/tests/test_export_seed.py` 加：

```python
def test_export_dumps_the_facet_embeddings_subtable_after_the_presets_it_references():
    # 子表對片段有外鍵：pg_dump --data-only 依外鍵順序輸出，但 -t 清單裡要有它才會被帶上
    from export_seed import TABLES
    assert TABLES == ("prompt_knowledge_presets", "preset_facet_embeddings", "shared_prompt_histories")


def test_count_sql_reports_the_subtable():
    from export_seed import COUNT_SQL
    assert "'facet_embeddings', count(*) FROM preset_facet_embeddings" in COUNT_SQL
```

- [ ] **Step 2: 跑測試確認失敗**

Run: `cd scripts && ./.venv/Scripts/python.exe -m pytest tests/test_export_seed.py -v`
Expected: 2 FAIL

- [ ] **Step 3: 改 `export_seed.py`**

```python
TABLES = ("prompt_knowledge_presets", "preset_facet_embeddings", "shared_prompt_histories")
```

```python
COUNT_SQL = (
    "SELECT 'presets:' || split_part(source_ref, ':', 1), count(*) FROM prompt_knowledge_presets GROUP BY 1 "
    "UNION ALL SELECT 'facet_embeddings', count(*) FROM preset_facet_embeddings "
    "UNION ALL SELECT 'histories:' || source, count(*) FROM shared_prompt_histories GROUP BY 1"
)
```

docstring 第 3 行「pg_dump 兩張知識表」改成「pg_dump 三張知識表（片段、facet 向量子表、共享紀錄）」；`main()` 的 print「串流兩張知識表」同步改成「三張」。

- [ ] **Step 4: 跑測試確認通過**

Run: `cd scripts && ./.venv/Scripts/python.exe -m pytest tests/test_export_seed.py -v`
Expected: 全部 PASS（既有測試若對 `TABLES` 的 `-t` 參數數量有斷言，照新清單更新）

- [ ] **Step 5: Commit**

```bash
git add scripts/export_seed.py scripts/tests/test_export_seed.py
git commit -m "feat(scripts): seed export carries preset_facet_embeddings"
```

---

### Task 5: `PresetRepository` 三個新查詢（含整合測試）

**Files:**
- Modify: `src/PromptCopilot.Api/Data/PresetRepository.cs`
- Test: `src/PromptCopilot.Api.Tests/Data/RepositoryIntegrationTests.cs`（integration，需 `PC_INTEGRATION=1` 與本機 DB）

**Interfaces:**
- Produces（都是 `public virtual`，測試用 fake 覆寫）：
  - `Task<long> FacetPoolSizeAsync(string facetId, CancellationToken ct)`：子表裡該 facet 的列數。
  - `Task<IReadOnlyList<PresetHit>> SearchFacetAsync(float[] query, string facetId, int k, CancellationToken ct)`：依 `tag_key` 去重後取前 k，`Dist` 是 facet 距離。
  - `Task<IReadOnlyList<PresetCandidate>> RecommendSimilarAsync(float[] anchor, string facetId, IReadOnlyList<string> dimensionFacets, double maxDist, int take, CancellationToken ct)`：組合條件同 `SetFilter`，facet 距離 ≤ maxDist，不去重。

- [ ] **Step 1: 寫失敗的整合測試**

`RepositoryIntegrationTests.InitializeAsync` 在 INSERT 之後加子表列（需要 preset id，所以先 `RETURNING`）。把整個 `InitializeAsync` 改成：

```csharp
    public async Task InitializeAsync()
    {
        var b = new NpgsqlDataSourceBuilder(TestEnv.Db); b.UseVector(); _ds = b.Build();
        await using var cmd = _ds.CreateCommand("""
            INSERT INTO prompt_knowledge_presets (source_ref, title, category, description, tags, facet_ids, prompt_snippet, negative_snippet, preset_embedding, facet_tags)
            VALUES (@r, '測試片段', 'Style', 'd', ARRAY['x'], ARRAY['style.genre'], 'photo realism', NULL, @e, NULL),
                   (@r2, '測試穿搭', 'Clothing', 'd', ARRAY['x'], ARRAY['clothing.upper','clothing.lower','clothing.footwear'], 'white shirt, platform sandals', NULL, @e,
                    '{"clothing.upper":["white shirt"],"clothing.lower":[],"clothing.footwear":["platform sandals"]}'::jsonb),
                   (@r3, '測試穿搭二', 'Clothing', 'd', ARRAY['x'], ARRAY['clothing.upper','clothing.footwear'], 'black shirt, platform sandals', NULL, @e,
                    '{"clothing.upper":["black shirt"],"clothing.footwear":["platform sandals"]}'::jsonb),
                   (@r4, '測試單品', 'Clothing', 'd', ARRAY['x'], ARRAY['clothing.footwear'], 'sneakers', NULL, @e,
                    '{"clothing.footwear":["sneakers"]}'::jsonb)
            RETURNING id
            """);
        cmd.Parameters.AddWithValue("r", Ref); cmd.Parameters.AddWithValue("r2", Ref2);
        cmd.Parameters.AddWithValue("r3", Ref3); cmd.Parameters.AddWithValue("r4", Ref4);
        cmd.Parameters.AddWithValue("e", new Pgvector.Vector(Unit(0)));
        var ids = new List<long>();
        await using (var r = await cmd.ExecuteReaderAsync()) while (await r.ReadAsync()) ids.Add(r.GetInt64(0));
        // 子表：穿搭與穿搭二的鞋履 tag_key 相同（platform sandals）、向量也相同 → SearchFacetAsync 去重只留一筆；單品的 sneakers 較遠
        await using var sub = _ds.CreateCommand("""
            INSERT INTO preset_facet_embeddings (preset_id, facet_id, tag_key, embedding) VALUES
              (@p2, 'clothing.footwear', 'platform sandals', @near),
              (@p3, 'clothing.footwear', 'platform sandals', @near),
              (@p4, 'clothing.footwear', 'sneakers', @far),
              (@p2, 'clothing.upper', 'white shirt', @far)
            """);
        sub.Parameters.AddWithValue("p2", ids[1]); sub.Parameters.AddWithValue("p3", ids[2]); sub.Parameters.AddWithValue("p4", ids[3]);
        sub.Parameters.AddWithValue("near", new Pgvector.Vector(Unit(1)));
        sub.Parameters.AddWithValue("far", new Pgvector.Vector(Unit(2)));
        await sub.ExecuteNonQueryAsync();
    }
```

在類別頂端加 `private static readonly string Ref3 = Ref + ":2"; private static readonly string Ref4 = Ref + ":3";`，`DisposeAsync` 的 DELETE 改成 `WHERE source_ref IN (@r, @r2, @r3, @r4)`（子表靠 `ON DELETE CASCADE` 一起刪）。

加三個測試：

```csharp
    [IntegrationFact]
    public async Task Facet_search_ranks_by_the_facet_vector_and_keeps_one_row_per_tag_key()
    {
        var repo = new PresetRepository(_ds);
        Assert.Equal(3, await repo.FacetPoolSizeAsync("clothing.footwear", default));          // 列數，不是去重後的組合數
        Assert.Equal(0, await repo.FacetPoolSizeAsync("nope.facet", default));

        var hits = await repo.SearchFacetAsync(Unit(1), "clothing.footwear", 5, default);
        Assert.Equal(2, hits.Count);                                                             // platform sandals 兩筆去重成一筆＋sneakers
        Assert.Equal("sneakers", hits[1].PromptSnippet);
        Assert.True(hits[0].Dist < 1e-6 && hits[1].Dist > 0.5);                                  // Dist 是 facet 距離
        Assert.Contains(hits[0].Title, new[] { "測試穿搭", "測試穿搭二" });
        Assert.Equal(Ref4, hits[1].SourceRef);
    }

    [IntegrationFact]
    public async Task Similar_recommendation_respects_the_distance_cap_and_the_set_filter()
    {
        var repo = new PresetRepository(_ds);
        var clothing = new[] { "clothing.upper", "clothing.lower", "clothing.footwear" };
        // 錨＝Unit(1)：platform sandals 距離 0、sneakers 距離 1。單品（測試單品）只有 1 個 facet 有 tag，不算組合。
        var hits = await repo.RecommendSimilarAsync(Unit(1), "clothing.footwear", clothing, 0.23, 3, default);
        Assert.Equal(2, hits.Count);                                                             // 不去重：兩套 platform sandals 都在
        Assert.All(hits, h => Assert.Equal(new[] { "platform sandals" }, h.FacetTags["clothing.footwear"]));
        Assert.DoesNotContain(hits, h => h.Title == "測試單品");
        // 錨換成 Unit(2)（sneakers 的方向）：platform sandals 距離 1 > 0.23 被門檻擋掉；sneakers 那筆是單品、不算組合 → 空
        Assert.Empty(await repo.RecommendSimilarAsync(Unit(2), "clothing.footwear", clothing, 0.23, 3, default));
    }

    [IntegrationFact]
    public async Task Facet_search_returns_fewer_rows_than_k_when_there_are_fewer_tag_keys()
    {
        var repo = new PresetRepository(_ds);
        var hits = await repo.SearchFacetAsync(Unit(1), "clothing.upper", 5, default);
        Assert.Single(hits);                                                                     // 只有 white shirt 一組
    }
```

- [ ] **Step 2: 跑整合測試確認失敗（編譯錯誤）**

Run: `PC_INTEGRATION=1 dotnet test src/PromptCopilot.Api.Tests --filter "FullyQualifiedName~RepositoryIntegrationTests"`
Expected: 編譯失敗，`FacetPoolSizeAsync`／`SearchFacetAsync`／`RecommendSimilarAsync` 不存在。（本機沒有 DB 的話至少要看到編譯錯誤；`TestEnv.Db` 的連線字串看 `src/PromptCopilot.Api.Tests/IntegrationFact.cs`。）

- [ ] **Step 3: 實作三個查詢**

在 `PresetRepository` 的常數區加：

```csharp
    // facet 層級向量（facet 向量設計 §5.3）：子表以 facet_id 的 B-tree 縮池，之後精確算距離。
    // 不建 HNSW：池最多約 3,700 筆，而且要依 tag_key 去重（同一組 tag 的片段向量一模一樣，不去重前 5 名常是 5 筆 sandals）——去重要看完整個池，近似索引幫不上（設計 §7）。
    private const string FacetPoolSql = "SELECT count(*) FROM preset_facet_embeddings WHERE facet_id = @facet";
    private const string SearchFacetSql = """
        WITH d AS (
            SELECT DISTINCT ON (tag_key) preset_id, embedding <=> @q AS dist
            FROM preset_facet_embeddings
            WHERE facet_id = @facet
            ORDER BY tag_key, dist
        )
        SELECT p.id, p.title, p.category, p.facet_ids, p.prompt_snippet, p.negative_snippet, p.image_url, d.dist, p.source_ref
        FROM d JOIN prompt_knowledge_presets p ON p.id = d.preset_id
        ORDER BY d.dist
        LIMIT @k
        """;
    // 近似錨（設計 §6.3）：字面錨不到 2 筆時，拿該 facet 的錨去比同一個 facet 的向量，門檻內、且仍是「組合」的列。不去重：整套穿搭不同才是要給使用者比的。
    private const string RecommendSimilarSql = $"""
        SELECT p.id, p.title, p.facet_ids, p.facet_tags::text, p.image_url, p.source_ref, e.embedding <=> @a AS dist
        FROM preset_facet_embeddings e
        JOIN prompt_knowledge_presets p ON p.id = e.preset_id
        WHERE e.facet_id = @facet
          AND e.embedding <=> @a <= @maxDist
          AND {SetFilter}
        ORDER BY dist
        LIMIT @take
        """;
```

`SetFilter` 在 JOIN 之後不會有欄位歧義：它用到的 `facet_ids`、`facet_tags`、`preset_embedding` 只有 `prompt_knowledge_presets` 有，`preset_facet_embeddings` 的欄位是 `preset_id`、`facet_id`、`tag_key`、`embedding`。整合測試會證明這條 SQL 能跑。

方法：

```csharp
    public virtual async Task<long> FacetPoolSizeAsync(string facetId, CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand(FacetPoolSql);
        cmd.Parameters.AddWithValue("facet", facetId);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public virtual async Task<IReadOnlyList<PresetHit>> SearchFacetAsync(float[] query, string facetId, int k, CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand(SearchFacetSql);
        cmd.Parameters.AddWithValue("q", new Vector(query));
        cmd.Parameters.AddWithValue("facet", facetId);
        cmd.Parameters.AddWithValue("k", k);
        return await ReadHitsAsync(cmd, ct);
    }

    public virtual async Task<IReadOnlyList<PresetCandidate>> RecommendSimilarAsync(float[] anchor, string facetId, IReadOnlyList<string> dimensionFacets,
        double maxDist, int take, CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand(RecommendSimilarSql);
        cmd.Parameters.AddWithValue("a", new Vector(anchor));
        cmd.Parameters.AddWithValue("facet", facetId);
        cmd.Parameters.AddWithValue("facets", dimensionFacets.ToArray());
        cmd.Parameters.AddWithValue("maxDist", maxDist);
        cmd.Parameters.AddWithValue("take", take);
        return await ReadCandidatesAsync(cmd, ct);
    }
```

把 `SearchAsync` 與 `RecommendAsync` 讀 reader 的迴圈抽成兩個私有方法 `ReadHitsAsync(NpgsqlCommand, CancellationToken)`／`ReadCandidatesAsync(...)`，三個舊方法與兩個新方法共用（欄位順序：hits 9 欄、candidates 7 欄，與現有 SQL 相同）。

最後把檔案開頭 `SearchSql` 上面的註解「與 scripts/pipeline/retrieval.py 的 PRESETS_SQL 一致」改成「維度項目與舊資料庫退路用；與 scripts/pipeline/retrieval.py 的 PRESETS_SQL 一致。facet 項目走 SearchFacetSql（2026-09-29）」。

- [ ] **Step 4: 跑整合測試與全部單元測試**

Run: `PC_INTEGRATION=1 dotnet test src/PromptCopilot.Api.Tests --filter "FullyQualifiedName~RepositoryIntegrationTests"`，再 `dotnet test src/PromptCopilot.Api.Tests`
Expected: 整合測試全過（沒有 DB 就至少編譯通過、其餘 skip）；單元測試全部通過（`FakePresets` 繼承 `PresetRepository(null!)`，新方法有預設實作不會被呼叫）。

- [ ] **Step 5: Commit**

```bash
git add src/PromptCopilot.Api/Data/PresetRepository.cs src/PromptCopilot.Api.Tests/Data/RepositoryIntegrationTests.cs
git commit -m "feat(api): facet-vector search, facet pool count and similar-anchor recommendation queries"
```

---

### Task 6: `SearchQuery.Tags` 與檢索細節欄位

**Files:**
- Modify: `src/PromptCopilot.Api/Plugins/Contracts.cs:21-36`
- Modify: `src/PromptCopilot.Api/Streaming/ToolDetails.cs:8-11`
- Test: `src/PromptCopilot.Api.Tests/Llm/GeminiToolDeclarationTests.cs`（既有：確認 `tags` 不是 required）、`src/PromptCopilot.Api.Tests/Streaming/SseWriterTests.cs`

**Interfaces:**
- Produces：`SearchQuery.Tags` (`string?`, JSON `tags`)；`SearchPresetsItem(..., IReadOnlyList<SearchPresetsHit> Hits, string? Tags = null, string Method = "preset")`，JSON `tags`／`method`；常數 `SearchPresetsItem.MethodFacet = "facet"`、`MethodPreset = "preset"`。

- [ ] **Step 1: 寫失敗測試**

`GeminiToolDeclarationTests.SearchPresets_declaration_and_binding_through_the_google_connector` 已經驗 `queries.items` 的 schema：`required` 只有 `query`，`dimension`／`facetId` 是 `nullable` 的 string。把那個 `foreach (var optional in new[] { "dimension", "facetId" })` 改成 `new[] { "dimension", "facetId", "tags" }`，並在迴圈上方的註解補一句「tags 同理（2026-09-29）：它是 init 屬性，不進建構子，否則 SK 會把它列成 required」。`Assert.Equal(new[] { "query" }, required)` 不動——`tags` 若不小心進了建構子，這一行就會紅。

`SseWriterTests` 那個序列化 `SearchPresetsItem` 的測試（約 L74）多驗新欄位：把第一個 item 改成 `new SearchPresetsItem("clothing", "clothing.footwear", "鞋履", "拖鞋", true, 300, 5, null, hits, "slippers", SearchPresetsItem.MethodFacet)`，斷言輸出含 `"tags":"slippers"` 與 `"method":"facet"`；第二個（error）item 不給新參數，斷言輸出含 `"method":"preset"`、不含 `"tags"`（null 省略）。

- [ ] **Step 2: 跑測試確認失敗**

Run: `dotnet test src/PromptCopilot.Api.Tests --filter "FullyQualifiedName~GeminiToolDeclarationTests|FullyQualifiedName~SseWriterTests"`
Expected: 編譯失敗（`Tags`／`MethodFacet` 不存在）

- [ ] **Step 3: 實作**

`Contracts.cs` 的 `SearchQuery`：

```csharp
public sealed record SearchQuery
{
    public SearchQuery(string? dimension, string query, string? facetId = null, string? tags = null)
    {
        Dimension = dimension; Query = query; FacetId = facetId; Tags = tags;
    }

    // 反序列化與 SK 產 schema 都走這個建構子。SK 把「沒有預設值的建構子參數」一律列進 required（不看 nullable），
    // 寫成多參數的 positional record 時 dimension 會變成必填；這裡只讓 query 當建構子參數，其餘是可省略的 init 屬性。
    [JsonConstructor]
    private SearchQuery(string query) => Query = query;

    [JsonPropertyName("dimension")] public string? Dimension { get; init; }
    [JsonPropertyName("query")] public string Query { get; init; }
    [JsonPropertyName("facetId")] public string? FacetId { get; init; }
    /// <summary>facet 項目專用（2026-09-29，facet 向量設計 §5.1）：使用者對這個 facet 的描述翻成的英文 SD tag，逗號分隔，寫法同 SetFacetStates 的 tags。
    /// 查詢句會組成「原話（tags）」；維度項目帶了也忽略。伺服器不拿它更新 session.FacetTags。</summary>
    [JsonPropertyName("tags")] public string? Tags { get; init; }
}
```

`ToolDetails.cs`：

```csharp
/// <summary>一個查詢項目。驗證失敗的項目 Error 有值、Hits 空、PoolSize 與 K 為 0、Label 是模型送的原始 facetId 或 dimension。
/// Tags／Method（2026-09-29）：facet 項目這次用了什麼英文（沒有就 null）、走的是 facet 向量（facet）還是整套向量（preset：維度項目，或子表沒資料的退路）。</summary>
public sealed record SearchPresetsItem(
    string Dimension, string? FacetId, string Label, string Query,
    bool Grounded, long PoolSize, int K, string? Error,
    IReadOnlyList<SearchPresetsHit> Hits, string? Tags = null, string Method = SearchPresetsItem.MethodPreset)
{
    public const string MethodFacet = "facet";
    public const string MethodPreset = "preset";
}
```

- [ ] **Step 4: 跑測試確認通過**

Run: `dotnet test src/PromptCopilot.Api.Tests`
Expected: 全部通過

- [ ] **Step 5: Commit**

```bash
git add src/PromptCopilot.Api/Plugins/Contracts.cs src/PromptCopilot.Api/Streaming/ToolDetails.cs src/PromptCopilot.Api.Tests/Llm/GeminiToolDeclarationTests.cs src/PromptCopilot.Api.Tests/Streaming/SseWriterTests.cs
git commit -m "feat(api): optional tags on SearchPresets items; detail carries tags and method"
```

---

### Task 7: `KnowledgePlugin` facet 項目走子表

**Files:**
- Modify: `src/PromptCopilot.Api/Plugins/KnowledgePlugin.cs`
- Test: `src/PromptCopilot.Api.Tests/Plugins/KnowledgePluginTests.cs`

**Interfaces:**
- Consumes：Task 5 的 `FacetPoolSizeAsync`／`SearchFacetAsync`；Task 6 的 `SearchQuery.Tags`、`SearchPresetsItem.Tags`／`Method`。
- Produces：`KnowledgePlugin.FacetHighMax = 0.22`、`FacetMidMax = 0.27`；`static string Band(double dist, bool facetVector)`（舊的 `Band(double)` 保留給維度項目）；`static string QueryText(string query, string? tags)`；`static string? CleanTags(string? tags)`（空白／只有逗號 → null）。

- [ ] **Step 1: 擴充 `FakePresets` 並寫失敗測試**

`KnowledgePluginTests.FakePresets` 加：

```csharp
        public List<(string facet, int k, float queryIndex)> FacetSearches { get; } = new();
        public List<string> FacetPoolCalls { get; } = new();
        public Dictionary<string, IReadOnlyList<PresetHit>> FacetHits { get; } = new();
        public Dictionary<string, long> FacetPools { get; } = new();                 // 沒設的 facet 視為 0 → 退路

        public override Task<long> FacetPoolSizeAsync(string facetId, CancellationToken ct)
        {
            FacetPoolCalls.Add(facetId);
            return Task.FromResult(FacetPools.GetValueOrDefault(facetId, 0L));
        }
        public override Task<IReadOnlyList<PresetHit>> SearchFacetAsync(float[] query, string facetId, int k, CancellationToken ct)
        {
            FacetSearches.Add((facetId, k, query[0]));
            return Task.FromResult(FacetHits.GetValueOrDefault(facetId, Array.Empty<PresetHit>()));
        }
```

加 helper `private static SearchQuery F(string facetId, string query, string? tags) => new(null, query, facetId, tags);`（保留既有兩參數版）。加測試：

```csharp
    private static SearchPresetsDetail Detail(ChannelReader<AgentEvent> events) =>
        (SearchPresetsDetail)Drain(events).OfType<ToolResultEvent>().Single(e => e.Name == ToolNames.SearchPresets).Detail!;

    [Fact]
    public async Task Facet_item_with_tags_embeds_the_bilingual_query_and_searches_the_facet_subtable()
    {
        var (p, _, _, embed, presets, events) = Make(covered: new[] { "clothing.footwear" });
        presets.FacetPools["clothing.footwear"] = 338;
        presets.FacetHits["clothing.footwear"] = new[] { Hit(1, "涼鞋", "clothing.footwear", 0.20), Hit(2, "靴", "clothing.footwear", 0.25) };

        var r = await p.SearchPresetsAsync(new[] { F("clothing.footwear", "涼鞋", "sandals") }, default);

        Assert.Equal(new[] { "涼鞋（sandals）" }, Assert.Single(embed.Calls));
        var fs = Assert.Single(presets.FacetSearches);
        Assert.Equal(("clothing.footwear", KnowledgePlugin.KCovered, 0f), fs);
        Assert.Empty(presets.Searches); Assert.Empty(presets.PoolCalls);                       // 沒走整套向量、沒數 facet_ids 池
        var it = Assert.Single(Detail(events).Items);
        Assert.Equal(338, it.PoolSize); Assert.Equal("sandals", it.Tags); Assert.Equal(SearchPresetsItem.MethodFacet, it.Method);
        Assert.Equal("涼鞋", it.Query);                                                          // detail 的 query 仍是原話，英文另放 tags
        Assert.Equal(new[] { "高", "中" }, it.Hits.Select(h => h.Band));                          // facet 門檻 0.22／0.27
        var result = Results(r)[0];
        Assert.Equal(338, result.GetProperty("poolSize").GetInt64());
        Assert.False(result.TryGetProperty("tags", out _)); Assert.False(result.TryGetProperty("method", out _));   // 回給模型的形狀不變
    }

    [Fact]
    public async Task Facet_item_without_tags_embeds_the_plain_query()
    {
        var (p, _, _, embed, presets, _) = Make();
        presets.FacetPools["clothing.footwear"] = 1;
        await p.SearchPresetsAsync(new[] { F("clothing.footwear", "涼鞋") }, default);
        Assert.Equal(new[] { "涼鞋" }, Assert.Single(embed.Calls));
    }

    [Fact]
    public async Task Blank_tags_are_treated_as_absent()
    {
        // Review Focus 1：模型填了空白或只有逗號，查詢句不能變成「涼鞋（）」
        var (p, _, _, embed, presets, events) = Make();
        presets.FacetPools["clothing.footwear"] = 1;
        await p.SearchPresetsAsync(new[] { F("clothing.footwear", "涼鞋", " , "), F("clothing.head", "草帽", "  ") }, default);
        Assert.Equal(new[] { "涼鞋", "草帽" }, Assert.Single(embed.Calls));
        Assert.All(Detail(events).Items, it => Assert.Null(it.Tags));
    }

    [Fact]
    public async Task Dimension_item_ignores_tags_and_keeps_the_old_path_and_thresholds()
    {
        var (p, _, _, embed, presets, events) = Make();
        presets.Pools["style.genre"] = 4455;
        presets.Hits["style.genre"] = new[] { Hit(1, "寫實", "style.genre", 0.23) };            // 0.23：facet 門檻是「中」，維度門檻是「高」
        await p.SearchPresetsAsync(new[] { new SearchQuery("style", "寫實攝影", tags: "photorealistic") }, default);
        Assert.Equal(new[] { "寫實攝影" }, Assert.Single(embed.Calls));
        Assert.Empty(presets.FacetSearches); Assert.Empty(presets.FacetPoolCalls);
        var it = Assert.Single(Detail(events).Items);
        Assert.Null(it.Tags); Assert.Equal(SearchPresetsItem.MethodPreset, it.Method); Assert.Equal("高", it.Hits[0].Band);
    }

    [Fact]
    public async Task Facet_item_falls_back_to_the_whole_preset_vector_when_the_subtable_has_no_rows()
    {
        var (p, _, _, _, presets, events) = Make();
        presets.Pools["clothing.footwear"] = 396;                                                // FacetPools 沒設 → 0
        presets.Hits["clothing.footwear"] = new[] { Hit(1, "涼鞋", "clothing.footwear", 0.26) };
        await p.SearchPresetsAsync(new[] { F("clothing.footwear", "涼鞋", "sandals") }, default);
        Assert.Equal(new[] { "clothing.footwear" }, presets.FacetPoolCalls);                     // 池計數本身就是退路判斷
        Assert.Empty(presets.FacetSearches);
        Assert.Equal(("clothing.footwear", KnowledgePlugin.KMissing, 0f), Assert.Single(presets.Searches));
        var it = Assert.Single(Detail(events).Items);
        Assert.Equal(396, it.PoolSize); Assert.Equal(SearchPresetsItem.MethodPreset, it.Method); Assert.Equal("sandals", it.Tags);
        Assert.Equal("中", it.Hits[0].Band);                                                       // 退路用維度門檻 0.25／0.30
    }

    [Fact]
    public async Task Facet_pool_is_counted_once_per_facet_and_separately_from_the_dimension_pool()
    {
        var (p, _, _, _, presets, _) = Make();
        presets.FacetPools["clothing.footwear"] = 338; presets.Pools["clothing.head"] = 700;
        await p.SearchPresetsAsync(new[] { F("clothing.footwear", "涼鞋"), F("clothing.footwear", "拖鞋"), new SearchQuery("clothing", "夏日穿搭") }, default);
        Assert.Equal(new[] { "clothing.footwear" }, presets.FacetPoolCalls);                     // 同 facet 兩項只數一次
        Assert.Equal(new[] { "clothing.head" }, presets.PoolCalls);                              // 維度項目照舊數 facet_ids 池
    }

    [Theory]
    [InlineData(0.219, true, "高")] [InlineData(0.22, true, "中")] [InlineData(0.269, true, "中")] [InlineData(0.27, true, "低")]
    [InlineData(0.249, false, "高")] [InlineData(0.25, false, "中")] [InlineData(0.30, false, "低")]
    public void Band_uses_the_facet_thresholds_only_for_facet_vectors(double dist, bool facet, string expected) =>
        Assert.Equal(expected, KnowledgePlugin.Band(dist, facet));
```

另外改 `src/PromptCopilot.Api.Tests/Llm/GeminiToolDeclarationTests.cs`：
- `Pre` 加 `public override Task<long> FacetPoolSizeAsync(string f, CancellationToken ct) => Task.FromResult(0L);`（它的 `ds` 是 null，不覆寫會 NRE；回 0 走退路，不用再覆寫 `SearchFacetAsync`）。
- 罐頭回覆的第二項改成 `{"facetId":"clothing.footwear","query":"拖鞋","tags":"slippers"}`，最後的斷言改成 `Assert.Equal(new[] { "寫實攝影", "拖鞋（slippers）" }, call);`——證明 connector 把 `tags` 綁進 `SearchQuery`、查詢句是雙語。

- [ ] **Step 2: 跑測試確認失敗**

Run: `dotnet test src/PromptCopilot.Api.Tests --filter "FullyQualifiedName~KnowledgePluginTests|FullyQualifiedName~GeminiToolDeclarationTests"`
Expected: 編譯失敗（`FacetHighMax`、`Band(double,bool)`、`SearchFacetAsync` 覆寫等）

- [ ] **Step 3: 實作**

`KnowledgePlugin` 常數與 helper：

```csharp
    // 與 scripts/pipeline/retrieval.py 一致（維度項目、以及子表沒資料時的退路）
    public const int KCovered = 5;
    public const int KMissing = 3;
    public const double HighMax = 0.25;
    public const double MidMax = 0.30;
    // facet 項目走 facet 向量（facet 向量設計 §5.4）：距離分布不同，門檻另訂。實驗 V4d 前 10 名正解中位數 0.221、非正解 0.278。
    public const double FacetHighMax = 0.22;
    public const double FacetMidMax = 0.27;

    public static string Band(double dist) => Band(dist, facetVector: false);
    public static string Band(double dist, bool facetVector)
    {
        var (high, mid) = facetVector ? (FacetHighMax, FacetMidMax) : (HighMax, MidMax);
        return dist < high ? "高" : dist < mid ? "中" : "低";
    }

    /// <summary>facet 項目的查詢句（設計 §5.2）：「原話（英文 tag）」。實驗：只用中文 73、帶英文 81（滿分 85）；帶著原話是為了翻譯偏掉時還有東西撐著。</summary>
    public static string QueryText(string query, string? tags) => tags is null ? query : $"{query}（{tags}）";

    /// <summary>模型填的 tags 去頭尾空白；只有空白或逗號的當沒給。</summary>
    public static string? CleanTags(string? tags)
    {
        if (tags is null) return null;
        var t = string.Join(", ", TagAttribution.Split(tags));
        return t.Length == 0 ? null : t;
    }
```

`SearchPresetsAsync` 的改動（照現有結構，只列差異）：

1. `valid` 的 tuple 多一個 `string? tags`：facet 項目 `CleanTags(q.Tags)`，維度項目 `null`。
2. embedding：`await embed.EmbedAsync(valid.Select(v => QueryText(v.query, v.tags)).ToList(), GeminiEmbeddingClient.RetrievalQuery, ct)`。
3. 迴圈裡每個項目：

```csharp
            var (index, dimension, facetId, query, tags, facetIds) = valid[vi];
            var isGrounded = grounded.Contains(dimension);
            var k = isGrounded ? KCovered : KMissing;
            long pool; IReadOnlyList<PresetHit> hits; var method = SearchPresetsItem.MethodPreset;
            // facet 項目先看子表有沒有這個 facet 的向量：池計數同時就是退路判斷（設計 §5.3），0 筆就走整套向量。
            var facetPool = facetId is null ? 0L : await CachedAsync(pools, "facet:" + facetId, () => presets.FacetPoolSizeAsync(facetId, ct));
            if (facetId is not null && facetPool > 0)
            {
                pool = facetPool; method = SearchPresetsItem.MethodFacet;
                hits = await presets.SearchFacetAsync(vectors[vi], facetId, k, ct);
            }
            else
            {
                pool = await CachedAsync(pools, string.Join(",", facetIds), () => presets.PoolSizeAsync(facetIds, ct));
                hits = await presets.SearchAsync(vectors[vi], facetIds, k, ct);
            }
            var facetVector = method == SearchPresetsItem.MethodFacet;
            // … ledger.Record 不變 …
                detailHits.Add(new SearchPresetsHit(h.Id, h.Title, Band(h.Dist, facetVector), Math.Round(h.Dist, 3), isGrounded, …));
            // …
            items[index] = new SearchPresetsItem(dimension, facetId, label, query, isGrounded, pool, k, null, detailHits, tags, method);
```

加私有 helper：

```csharp
    private static async Task<long> CachedAsync(Dictionary<string, long> cache, string key, Func<Task<long>> compute)
    {
        if (!cache.TryGetValue(key, out var v)) cache[key] = v = await compute();
        return v;
    }
```

`ModelResult` 不動（回給模型的 JSON 不變）。

- [ ] **Step 4: 跑測試確認通過**

Run: `dotnet test src/PromptCopilot.Api.Tests`
Expected: 全部通過（既有 `Batch_embeds_once_and_counts_each_dimension_pool_once` 等仍過：維度項目路徑沒變）

- [ ] **Step 5: Commit**

```bash
git add src/PromptCopilot.Api/Plugins/KnowledgePlugin.cs src/PromptCopilot.Api.Tests/Plugins/KnowledgePluginTests.cs src/PromptCopilot.Api.Tests/Llm/GeminiToolDeclarationTests.cs
git commit -m "feat(api): SearchPresets facet items rank by facet vectors with bilingual queries and their own bands"
```

---

### Task 8: system prompt 第 1 條範例＋工具說明

**Files:**
- Modify: `src/PromptCopilot.Api/Orchestration/SystemPromptBuilder.cs:12-13`
- Modify: `src/PromptCopilot.Api/Plugins/KnowledgePlugin.cs:27-32`（`ItemsHelp` 與 `[Description]`）
- Test: `src/PromptCopilot.Api.Tests/Orchestration/SystemPromptBuilderTests.cs`

- [ ] **Step 1: 寫失敗測試**

在 `SystemPromptBuilderTests` 加：

```csharp
    /// <summary>2026-09-29 facet 向量：facet 項目要附英文 tag，查詢句才會是「原話（英文）」（設計 §5.1）。</summary>
    [Fact]
    public void Flow_rule_asks_for_english_tags_on_each_facet_item()
    {
        var (prompt, _) = Make().Build(new Session("s"), ToolNames.Always);
        Assert.Contains("`clothing.footwear`＋「拖鞋」＋`slippers`", prompt);
        Assert.Contains("`appearance.hair`＋「銀色雙馬尾」＋`silver hair, twintails`", prompt);
        Assert.Contains("寫法跟 `SetFacetStates` 的 `tags` 一樣", prompt);
    }
```

- [ ] **Step 2: 跑測試確認失敗**

Run: `dotnet test src/PromptCopilot.Api.Tests --filter "FullyQualifiedName~SystemPromptBuilderTests"`
Expected: 1 FAIL

- [ ] **Step 3: 改常數與工具說明**

`SystemPromptBuilder.RetrievalStepOn`：

```csharp
    internal const string RetrievalStepOn =
        "再**用一次 `SearchPresets`**：使用者講到的每個 facet 各一項，用 `facetId` 加上他描述那一項的原話，並附上翻成英文 SD tag 的 `tags`（例：`clothing.footwear`＋「拖鞋」＋`slippers`、`appearance.hair`＋「銀色雙馬尾」＋`silver hair, twintails`；寫法跟 `SetFacetStates` 的 `tags` 一樣）；使用者沒講的維度每個用 `dimension` 給兩個對比方向的項目（例：「寫實攝影」與「日系動漫插畫」）。不要把整句描述丟給一個維度，也不要一個項目一次呼叫。需要風格參考時呼叫 `SearchSimilarPrompts`。";
```

`KnowledgePlugin.ItemsHelp`：

```csharp
    private const string ItemsHelp = "每項：query（該項專屬的繁中查詢語句）加上 facetId（單一 facet，例如 clothing.footwear）或 dimension（整個維度，style | scene | camera | appearance | pose | clothing）。使用者講到的每個 facet 各一項用 facetId 與他的原話，並附 tags（該描述翻成的英文 SD tag，逗號分隔，寫法同 SetFacetStates 的 tags）；使用者沒講的維度用 dimension 給兩個對比方向。最多 24 項。";
```

既有測試 `Flow_rule_asks_for_one_batched_SearchPresets_call_with_one_item_per_stated_facet` 斷言的字串「使用者講到的每個 facet 各一項，用 `facetId` 加上他描述那一項的原話」仍在新句子裡，不用改。

- [ ] **Step 4: 跑測試確認通過**

Run: `dotnet test src/PromptCopilot.Api.Tests`
Expected: 全部通過

- [ ] **Step 5: Commit**

```bash
git add src/PromptCopilot.Api/Orchestration/SystemPromptBuilder.cs src/PromptCopilot.Api/Plugins/KnowledgePlugin.cs src/PromptCopilot.Api.Tests/Orchestration/SystemPromptBuilderTests.cs
git commit -m "feat(api): system prompt and tool help ask for english tags on facet items"
```

---

### Task 9: 推薦的近似錨

**Files:**
- Modify: `src/PromptCopilot.Api/Configuration/Options.cs:36-39`
- Modify: `src/PromptCopilot.Api/Streaming/Recommendations.cs:7-8`
- Modify: `src/PromptCopilot.Api/Orchestration/RecommendationService.cs`
- Modify: `src/PromptCopilot.Api/Orchestration/AgenticOrchestrator.cs:189`
- Modify: `src/PromptCopilot.Api/appsettings.json:5`
- Test: `src/PromptCopilot.Api.Tests/Orchestration/RecommendationServiceTests.cs`、`AgenticOrchestratorTests.cs:681`

**Interfaces:**
- Consumes：Task 5 的 `FacetPoolSizeAsync`、`RecommendSimilarAsync`。
- Produces：`OrchestratorOptions.RecommendationSimilarMaxDist` (double, 0.23)；`RecommendedDimension(..., IReadOnlyList<RecommendedSet> Sets, bool Similar = false)`（JSON `similar`）；`RecommendationService.SimilarAnchorText(Session s, string facetId) -> string?`（public static）。

- [ ] **Step 1: 擴充 `FakePresets`、寫失敗測試**

`RecommendationServiceTests.FakePresets` 加：

```csharp
        public List<(string facet, double maxDist, int take)> SimilarCalls { get; } = new();
        public Dictionary<string, IReadOnlyList<PresetCandidate>> SimilarScript { get; } = new();     // key：facetId
        public Dictionary<string, long> FacetPools { get; } = new();                                    // 沒設 → 0 → 跳過近似
        public override Task<long> FacetPoolSizeAsync(string facetId, CancellationToken ct) => Task.FromResult(FacetPools.GetValueOrDefault(facetId, 0L));
        public override Task<IReadOnlyList<PresetCandidate>> RecommendSimilarAsync(float[] anchor, string facetId, IReadOnlyList<string> dimensionFacets, double maxDist, int take, CancellationToken ct)
        {
            SimilarCalls.Add((facetId, maxDist, take));
            return Task.FromResult(SimilarScript.GetValueOrDefault(facetId, Array.Empty<PresetCandidate>()));
        }
```

`Set` helper 加可選距離：`private static PresetCandidate Set(long id, string title, params (string facet, string tags)[] facetTags) => Set(id, title, 0.21, facetTags);` 與 `Set(long id, string title, double dist, params (string facet, string tags)[] facetTags)`。

測試：

```csharp
    [Fact]
    public async Task Literal_anchor_short_of_two_hits_tries_the_similar_anchor_and_reports_it_as_similar()
    {
        var (svc, s, embed, presets) = Make(("clothing.footwear", "slippers"));
        presets.FacetPools["clothing.footwear"] = 338;
        presets.Script[("clothing.head", true)] = new[] { Set(1, "只有一套", ("clothing.footwear", "slippers"), ("clothing.upper", "x")) };
        presets.SimilarScript["clothing.footwear"] = new[]
        {
            Set(2, "涼鞋一", 0.18, ("clothing.footwear", "sandals"), ("clothing.upper", "a")),
            Set(3, "涼鞋二", 0.21, ("clothing.footwear", "sandals"), ("clothing.lower", "b")),
        };
        var e = await svc.BuildAsync(s, Ask("clothing"), 1, default);
        var d = Assert.Single(e!.Dimensions);
        Assert.False(d.Anchored); Assert.True(d.Similar);
        Assert.Equal(new[] { "slippers" }, d.AnchorTags);                                        // 有貢獻的 facet 的 FacetTags
        Assert.Equal(new long[] { 2, 3 }, d.Sets.Select(x => x.PresetId));
        Assert.Equal(0.18, d.Sets[0].Dist);                                                      // 近似路上的 Dist 是 facet 距離
        var call = Assert.Single(presets.SimilarCalls);
        Assert.Equal(("clothing.footwear", 0.23, 3), call);
        Assert.Single(presets.Calls);                                                            // 沒退到無錨
        Assert.Equal(new[] { "一個少女穿涼鞋", "slippers" }, embed.Texts);                        // 錨向量與查詢向量同一次 embed
    }

    [Fact]
    public async Task Similar_anchor_short_of_two_hits_falls_back_to_unanchored()
    {
        var (svc, s, _, presets) = Make(("clothing.footwear", "slippers"));
        presets.FacetPools["clothing.footwear"] = 338;
        presets.SimilarScript["clothing.footwear"] = new[] { Set(2, "只一套", 0.2, ("clothing.footwear", "sandals"), ("clothing.upper", "a")) };
        presets.Script[("clothing.head", false)] = new[] { Set(8, "a", ("clothing.upper", "x"), ("clothing.lower", "y")), Set(9, "b", ("clothing.upper", "x"), ("clothing.lower", "y")) };
        var d = Assert.Single((await svc.BuildAsync(s, Ask("clothing"), 1, default))!.Dimensions);
        Assert.False(d.Anchored); Assert.False(d.Similar); Assert.Empty(d.AnchorTags);
        Assert.Equal(new long[] { 8, 9 }, d.Sets.Select(x => x.PresetId));
        Assert.Equal(2, presets.Calls.Count);
    }

    [Fact]
    public async Task Similar_anchor_is_skipped_when_the_subtable_has_no_rows_for_the_facet()
    {
        var (svc, s, _, presets) = Make(("clothing.footwear", "slippers"));                     // FacetPools 沒設 → 0
        presets.Script[("clothing.head", false)] = new[] { Set(8, "a", ("clothing.upper", "x"), ("clothing.lower", "y")) };
        await svc.BuildAsync(s, Ask("clothing"), 1, default);
        Assert.Empty(presets.SimilarCalls);
    }

    [Fact]
    public async Task Anchored_result_never_runs_the_similar_query()
    {
        var (svc, s, _, presets) = Make(("clothing.footwear", "sandals"));
        presets.FacetPools["clothing.footwear"] = 338;
        presets.Script[("clothing.head", true)] = new[] { Set(1, "a", ("clothing.footwear", "sandals"), ("clothing.upper", "x")), Set(2, "b", ("clothing.footwear", "sandals"), ("clothing.lower", "y")) };
        var d = Assert.Single((await svc.BuildAsync(s, Ask("clothing"), 1, default))!.Dimensions);
        Assert.True(d.Anchored); Assert.False(d.Similar); Assert.Empty(presets.SimilarCalls);
    }

    [Fact]
    public async Task Similar_results_from_several_facets_merge_by_min_distance_and_credit_only_contributing_facets()
    {
        var (svc, s, _, presets) = Make(("clothing.footwear", "slippers"), ("clothing.head", "beret"));
        presets.FacetPools["clothing.footwear"] = 338; presets.FacetPools["clothing.head"] = 662;
        presets.SimilarScript["clothing.footwear"] = new[] { Set(2, "x", 0.20, ("clothing.footwear", "sandals"), ("clothing.upper", "a")), Set(3, "y", 0.22, ("clothing.footwear", "sandals"), ("clothing.upper", "a")) };
        presets.SimilarScript["clothing.head"] = new[] { Set(2, "x", 0.10, ("clothing.head", "cap"), ("clothing.upper", "a")), Set(4, "z", 0.15, ("clothing.head", "cap"), ("clothing.upper", "a")) };
        var d = Assert.Single((await svc.BuildAsync(s, Ask("clothing"), 1, default))!.Dimensions);
        Assert.True(d.Similar);
        Assert.Equal(new long[] { 2, 4, 3 }, d.Sets.Select(x => x.PresetId));                    // 2 取兩邊較小的 0.10
        Assert.Equal(0.10, d.Sets[0].Dist);
        Assert.Equal(new[] { "beret", "slippers" }, d.AnchorTags);                               // 兩個 facet 都有進前 3；順序照 yaml（head 在 footwear 前）
    }

    [Fact]
    public async Task Facet_whose_tags_normalize_to_nothing_is_not_embedded_as_an_anchor()
    {
        // Review Focus 4
        var (svc, s, embed, presets) = Make(("clothing.footwear", "( :1.2)"));
        presets.FacetPools["clothing.footwear"] = 338;
        presets.Script[("clothing.head", false)] = new[] { Set(8, "a", ("clothing.upper", "x"), ("clothing.lower", "y")) };
        await svc.BuildAsync(s, Ask("clothing"), 1, default);
        Assert.Equal(new[] { "一個少女穿涼鞋" }, embed.Texts);
        Assert.Empty(presets.SimilarCalls);
        Assert.Null(RecommendationService.SimilarAnchorText(s, "clothing.footwear"));
        Assert.Null(RecommendationService.SimilarAnchorText(s, "clothing.head"));                 // 沒有 FacetTags
    }

    [Fact]
    public void Similar_anchor_text_normalizes_dedups_and_joins_the_facet_tags()
    {
        var s = new Session("s"); s.ApplyProfile("portrait", Catalog);
        s.ApplyFacetStates(new Dictionary<string, FacetState> { ["clothing.footwear"] = FacetState.Covered }, Catalog,
            new Dictionary<string, string> { ["clothing.footwear"] = "(Slippers:1.2), flip_flops, slippers" });
        Assert.Equal("slippers, flip flops", RecommendationService.SimilarAnchorText(s, "clothing.footwear"));
    }
```

`AgenticOrchestratorTests.cs:681` 的字串改成含 `similar`：

```csharp
        Assert.Contains("""recommendations":{"dimensions":[{"dimension":"style","anchored":false,"similar":false,"presetIds":[7]}]}""", completed.PayloadJson!);
```

- [ ] **Step 2: 跑測試確認失敗**

Run: `dotnet test src/PromptCopilot.Api.Tests --filter "FullyQualifiedName~RecommendationServiceTests|FullyQualifiedName~AgenticOrchestratorTests"`
Expected: 編譯失敗（`Similar`、`RecommendSimilarAsync` 覆寫等）

- [ ] **Step 3: 實作**

`Options.cs` 在 `RecommendationTake` 之後加：

```csharp
    /// <summary>推薦的近似錨（facet 向量設計 §6.4）：字面錨不到 2 筆時，facet 向量距離在這個門檻內的才算「接近你講的」。
    /// 實驗（英文比英文）正解距離中位數 0.19、非正解 0.27。</summary>
    public double RecommendationSimilarMaxDist { get; set; } = 0.23;
```

`appsettings.json` 的 `Orchestrator` 物件加 `"RecommendationSimilarMaxDist": 0.23`。

`Recommendations.cs`：

```csharp
/// <summary>Anchored：候選是先用「含使用者講的元素」字面過濾的；Similar（2026-09-29）：字面抓不到、改用 facet 向量離錨夠近的（facet 向量設計 §6）；兩者不會同時為 true。
/// AnchorTags 是實際命中（anchored）或有貢獻（similar）的錨。兩者都 false 時是純向量排序的「最接近你描述的組合」。</summary>
public sealed record RecommendedDimension(string Dimension, string Label, bool Anchored, IReadOnlyList<string> AnchorTags, IReadOnlyList<RecommendedSet> Sets, bool Similar = false);
```

`RecommendationService.BuildAsync`：

```csharp
        // 近似錨的文字先算好，跟使用者原話一起 embed（設計 §6.2）：哪個維度走到近似錨都不會多一次呼叫；沒用到的向量丟掉無妨。
        var anchorTexts = new List<(string dim, string facet, string text)>();
        foreach (var dim in dimensions)
            foreach (var f in catalog.FacetsOf(profile, dim))
                if (s.FacetStates.GetValueOrDefault(f, FacetState.Missing) == FacetState.Covered && SimilarAnchorText(s, f) is { } t)
                    anchorTexts.Add((dim, f, t));
        var vectors = await embed.EmbedAsync(new[] { query }.Concat(anchorTexts.Select(a => a.text)).ToList(), GeminiEmbeddingClient.RetrievalQuery, ct);
        var vec = vectors[0];
        var anchorVec = anchorTexts.Select((a, i) => (a, v: vectors[i + 1])).ToDictionary(x => (x.a.dim, x.a.facet), x => x.v);
```

維度迴圈裡，在 `if (!anchored) hits = await presets.RecommendAsync(vec, facets, [], [], take, ct);` 之前插入：

```csharp
            var similar = false;
            IReadOnlyList<string> similarTags = Array.Empty<string>();
            if (!anchored)
            {
                (hits, similarTags) = await SimilarAsync(s, dim, covered, facets, anchorVec, ct);
                similar = hits.Count >= MinAnchoredHits;
                if (!similar) hits = Array.Empty<PresetCandidate>();
            }
            if (!anchored && !similar) hits = await presets.RecommendAsync(vec, facets, Array.Empty<string>(), Array.Empty<string>(), options.RecommendationTake, ct);
            if (hits.Count == 0) continue;
            var matched = anchored ? MatchedAnchors(hits, covered, anchors) : similar ? similarTags : Array.Empty<string>();
            // … sets 同現有 …
            result.Add(new RecommendedDimension(dim, catalog.DimensionLabel(dim, profile), anchored, matched, sets, similar));
```

新方法：

```csharp
    /// <summary>近似錨（設計 §6.1）：每個 covered 且有錨向量的 facet 各查一次，同一片段取最小距離，依距離取前 take；
    /// 回傳的 tags 是「有貢獻」的 facet 的 FacetTags（至少一筆進了前 take）。子表沒有這個 facet 的列就跳過它。</summary>
    private async Task<(IReadOnlyList<PresetCandidate> hits, IReadOnlyList<string> tags)> SimilarAsync(Session s, string dim, IReadOnlyList<string> covered,
        IReadOnlyList<string> facets, IReadOnlyDictionary<(string, string), float[]> anchorVec, CancellationToken ct)
    {
        var best = new Dictionary<long, (PresetCandidate c, string facet)>();
        foreach (var f in covered)
        {
            if (!anchorVec.TryGetValue((dim, f), out var av)) continue;
            if (await presets.FacetPoolSizeAsync(f, ct) == 0) continue;
            foreach (var h in await presets.RecommendSimilarAsync(av, f, facets, options.RecommendationSimilarMaxDist, options.RecommendationTake, ct))
                if (!best.TryGetValue(h.Id, out var cur) || h.Dist < cur.c.Dist) best[h.Id] = (h, f);
        }
        var top = best.Values.OrderBy(x => x.c.Dist).Take(options.RecommendationTake).ToList();
        var contributing = covered.Where(f => top.Any(x => x.facet == f)).ToList();
        var tags = new List<string>();
        foreach (var f in contributing)
            foreach (var t in TagAttribution.Split(s.FacetTags.GetValueOrDefault(f)).Select(TagAttribution.Normalize))
                if (t.Length > 0 && !tags.Contains(t)) tags.Add(t);
        return (top.Select(x => x.c).ToList(), tags);
    }

    /// <summary>該 facet 的錨文字：FacetTags 正規化、丟空、去重、保序、", " 串接；沒有可用的 tag 回 null（不拿空字串去 embed）。</summary>
    public static string? SimilarAnchorText(Session s, string facetId)
    {
        if (!s.FacetTags.TryGetValue(facetId, out var raw)) return null;
        var parts = TagAttribution.Split(raw).Select(TagAttribution.Normalize).Where(t => t.Length > 0).Distinct().ToList();
        return parts.Count == 0 ? null : string.Join(", ", parts);
    }
```

類別上方的 `<summary>` 補一句「字面錨不到 2 筆先試近似錨（facet 向量離錨 ≤ RecommendationSimilarMaxDist），仍不到 2 筆才退回純向量」。

`AgenticOrchestrator.cs:189`：

```csharp
                        dimensions = recommended.Dimensions.Select(d => new { dimension = d.Dimension, anchored = d.Anchored, similar = d.Similar, presetIds = d.Sets.Select(x => x.PresetId).ToArray() }).ToArray(),
```

- [ ] **Step 4: 跑測試確認通過**

Run: `dotnet test src/PromptCopilot.Api.Tests`
Expected: 全部通過。既有 `Anchored_query_with_fewer_than_two_hits_falls_back_to_an_unanchored_query` 仍過：它的 `FacetPools` 沒設 → 近似跳過 → 退回無錨，`Calls.Count == 2` 不變。

- [ ] **Step 5: Commit**

```bash
git add src/PromptCopilot.Api/Configuration/Options.cs src/PromptCopilot.Api/appsettings.json src/PromptCopilot.Api/Streaming/Recommendations.cs src/PromptCopilot.Api/Orchestration/RecommendationService.cs src/PromptCopilot.Api/Orchestration/AgenticOrchestrator.cs src/PromptCopilot.Api.Tests/Orchestration/RecommendationServiceTests.cs src/PromptCopilot.Api.Tests/Orchestration/AgenticOrchestratorTests.cs
git commit -m "feat(api): recommendations fall back to a similar anchor on facet vectors before going unanchored"
```

---

### Task 10: 前端：三種推薦文案、查詢句顯示、method 標示

**Files:**
- Modify: `src/PromptCopilot.Frontend/types/api.ts:14-17,44`
- Modify: `src/PromptCopilot.Frontend/lib/copy.ts`
- Modify: `src/PromptCopilot.Frontend/components/RecommendationStrip.vue:8`
- Modify: `src/PromptCopilot.Frontend/components/ToolCallCard.vue:21-23`
- Test: `src/PromptCopilot.Frontend/tests/copy.test.ts`

**Interfaces:**
- Produces：`recommendationLead(d: { anchored: boolean; similar?: boolean; anchorTags: string[] }): string`；`queryLabel(it: { query: string; tags?: string | null }): string`；`methodLabel(it: { facetId?: string | null; method?: 'facet' | 'preset' }): string | null`。

- [ ] **Step 1: 寫失敗測試**

`tests/copy.test.ts` 加：

```ts
import { methodLabel, queryLabel, recommendationLead } from '../lib/copy'

describe('recommendationLead', () => {
  it('字面命中、近似命中、純向量三種文案', () => {
    expect(recommendationLead({ anchored: true, similar: false, anchorTags: ['sandals'] })).toBe('含你講的 sandals')
    expect(recommendationLead({ anchored: false, similar: true, anchorTags: ['slippers', 'beret'] })).toBe('接近你講的 slippers, beret')
    expect(recommendationLead({ anchored: false, similar: false, anchorTags: [] })).toBe('最接近你描述的組合')
  })
  it('舊後端／舊 sessionStorage 沒有 similar 欄位時當成 false', () => {
    // Review Focus 5
    expect(recommendationLead({ anchored: false, anchorTags: [] })).toBe('最接近你描述的組合')
  })
})

describe('queryLabel / methodLabel', () => {
  it('有英文 tag 就併在原話後面，沒有就只有原話；舊資料沒有 tags 欄位也一樣', () => {
    expect(queryLabel({ query: '涼鞋', tags: 'sandals' })).toBe('涼鞋（sandals）')
    expect(queryLabel({ query: '涼鞋', tags: null })).toBe('涼鞋')
    expect(queryLabel({ query: '涼鞋' })).toBe('涼鞋')
  })
  it('只有 facet 項目標示走哪種向量；缺 method 的舊資料當整套向量', () => {
    expect(methodLabel({ facetId: 'clothing.footwear', method: 'facet' })).toBe('facet 向量')
    expect(methodLabel({ facetId: 'clothing.footwear', method: 'preset' })).toBe('整套向量')
    expect(methodLabel({ facetId: 'clothing.footwear' })).toBe('整套向量')
    expect(methodLabel({ facetId: null, method: 'preset' })).toBeNull()
  })
})
```

- [ ] **Step 2: 跑測試確認失敗**

Run: `cd src/PromptCopilot.Frontend && npm test -- copy`
Expected: FAIL（找不到 export）

- [ ] **Step 3: 實作**

`types/api.ts`：

```ts
export interface SearchPresetsItem {
  dimension: string; facetId?: string | null; label: string; query: string
  grounded: boolean; poolSize: number; k: number; error?: string | null; hits: SearchPresetsHit[]
  /** facet 項目這次用的英文 tag（2026-09-29）；沒有、或舊資料沒這欄就當沒有 */
  tags?: string | null
  /** facet：facet 向量子表；preset：整套向量（維度項目，或子表沒資料的退路）。舊資料沒這欄當 preset */
  method?: 'facet' | 'preset'
}
```

```ts
/** similar（2026-09-29）：字面錨抓不到、改用 facet 向量離錨夠近的組合；與 anchored 不會同時為 true。舊資料沒這欄當 false */
export interface RecommendedDimension { dimension: string; label: string; anchored: boolean; similar?: boolean; anchorTags: string[]; sets: RecommendedSet[] }
```

`lib/copy.ts` 加：

```ts
/** 推薦區塊每個維度的說明（facet 向量設計 §6.5）。 */
export function recommendationLead(d: { anchored: boolean; similar?: boolean; anchorTags: string[] }): string {
  if (d.anchored) return `含你講的 ${d.anchorTags.join(', ')}`
  if (d.similar) return `接近你講的 ${d.anchorTags.join(', ')}`
  return '最接近你描述的組合'
}

/** 工具卡的查詢句：facet 項目帶英文時顯示「原話（英文）」，跟送去 embedding 的字串同形。 */
export function queryLabel(it: { query: string; tags?: string | null }): string {
  return it.tags ? `${it.query}（${it.tags}）` : it.query
}

/** 只有 facet 項目標示走哪種向量；維度項目本來就只有整套向量，不標。 */
export function methodLabel(it: { facetId?: string | null; method?: 'facet' | 'preset' }): string | null {
  if (!it.facetId) return null
  return it.method === 'facet' ? 'facet 向量' : '整套向量'
}
```

`RecommendationStrip.vue:8`：

```vue
        <span class="text-[11px] text-muted">{{ recommendationLead(d) }}</span>
```

並在 `<script setup>` 的 import 改成 `import { recommendationLead, sourceName } from '../lib/copy'`。

`ToolCallCard.vue:21-23`：

```vue
                <span class="truncate">{{ queryLabel(it) }}</span>
                <span v-if="methodLabel(it)" class="shrink-0 text-[10px] text-muted">{{ methodLabel(it) }}</span>
                <span class="ml-auto shrink-0 tabular-nums">池 {{ it.poolSize }} → {{ it.hits.length }}</span>
```

並把該檔第 67 行 `import { sourceName } from '../lib/copy'` 改成 `import { methodLabel, queryLabel, sourceName } from '../lib/copy'`。

- [ ] **Step 4: 跑前端測試與型別檢查**

Run: `cd src/PromptCopilot.Frontend && npm test && npx vue-tsc --noEmit`（若專案沒有 `vue-tsc`，用 `package.json` 裡既有的 typecheck／build script）
Expected: 全部通過、無型別錯誤

- [ ] **Step 5: Commit**

```bash
git add src/PromptCopilot.Frontend/types/api.ts src/PromptCopilot.Frontend/lib/copy.ts src/PromptCopilot.Frontend/components/RecommendationStrip.vue src/PromptCopilot.Frontend/components/ToolCallCard.vue src/PromptCopilot.Frontend/tests/copy.test.ts
git commit -m "feat(frontend): similar-anchor lead, bilingual query label and vector method on the tool card"
```

---

### Task 11: 文件同步

**Files:**
- Modify: `docs/known-issues.md`（表格第 10 列、§5 的 facet 檢索那一條、§10 的同義詞那一條 → 「已修正」新節）
- Modify: `docs/superpowers/specs/2026-09-21-genai-prompt-copilot-design.md` §9（表格 `SearchPresets` 列、推薦第 4 步）
- Modify: `docs/superpowers/specs/2026-09-22-dimension-scoped-retrieval-design.md` §13.1／§13.2
- Modify: `docs/SK架構說明.md`（若有列 `SearchPresets` 參數）
- Modify: `docs/eval-cases.md`（新節，案例先寫、結果欄留給 Task 12）

- [ ] **Step 1: known-issues**

1. 表格第 10 列的問題欄把「同義詞抓不到、」拿掉。
2. §5 刪掉「**facet 檢索「過濾準、排序不準」在穿著上看得到**」整條；§10 刪掉「**同義詞抓不到**」整條（§10 開頭的兩行括號註記後面加一行「（2026-09-29：同義詞抓不到已由 facet 向量的近似錨處理，見已修正 #12。）」）。
3. 「已修正」最後加：

```markdown
### 12. facet 檢索「過濾準、排序不準」與推薦的同義詞（facet 層級向量）

**現象**：R2 驗收「涼鞋」查 `clothing.footwear` 前 5 名只有 2 筆 sandals（知識庫有 18 筆）；推薦的錨 `slippers` 對不上 `sandals`，退回無錨。原本分別記在 §5 與 §10。

**根因**：facet 項目過濾後仍拿整套片段向量（`title。description。全部 tag`）排序，穿著片段被上下身主導；錨比對只做字面。

**修正**（分支 `feat/facet-vector-retrieval`，commit `<hash>`）：子表 `preset_facet_embeddings` 存每個片段每個 facet 的向量（`scripts/embed_facet_tags.py` 從 `facet_tags` 算，seed-v3 帶著）；`SearchPresets` facet 項目改比 facet 向量、依 `tag_key` 去重、查詢句「原話（模型給的英文 tags）」、分級門檻 0.22／0.27；推薦字面錨不到 2 筆先試近似錨（facet 距離 ≤ 0.23），前端標「接近你講的 …」。不建 HNSW（設計 §7）。設計：`docs/superpowers/specs/2026-09-29-facet-vector-retrieval-design.md`；實驗：`docs/experiments/2026-09-29-facet-vector-text.md`。

**驗收**：單元測試（`KnowledgePluginTests`、`RecommendationServiceTests`、`test_embed_facet_tags.py`、`test_tags.py`）、整合測試 `RepositoryIntegrationTests`。離線重跑與線上驗收見實驗紀錄與 `docs/eval-cases.md` 2026-09-29 facet 向量一節。
```

`<hash>` 在 Task 12 之後補（照 #8 的做法，另一個 docs commit）。

- [ ] **Step 2: 主規格 §9**

表格 `SearchPresets` 列改成：

```markdown
| `SearchPresets` | **分維度**：一次呼叫帶多個項目；維度項目 `facetIds` 過濾後以整套向量排序；facet 項目（2026-09-29）以該 facet 自己的向量排序、依 tag 組合去重，查詢句「原話（英文 tag）」 | 維度：`WHERE facet_ids && $facets ORDER BY preset_embedding <=> $vec LIMIT k`；facet：`preset_facet_embeddings WHERE facet_id = $f`，`DISTINCT ON (tag_key)` 後依 `embedding <=> $vec` |
```

「不設距離門檻；tool result 帶相似度分級」那一條後面加：「facet 項目的門檻另訂 `<0.22` 高、`<0.27` 中（facet 向量的距離分布不同，見 [facet 向量設計](2026-09-29-facet-vector-retrieval-design.md) §5.4）；子表沒有該 facet 的列時退回整套向量與原門檻，detail 的 `method` 標 `preset`。」

「整套組合推薦」第 4 步「不足 2 筆就整批丟掉，改成不過濾」改成「不足 2 筆先試**近似錨**（2026-09-29：該 facet 的 `FacetTags` 向量對子表同一 facet 的向量，距離 ≤ 0.23 且仍是組合的列，各 covered facet 合併取最小距離；≥ 2 筆顯示「接近你講的 …」），仍不足就改成不過濾、依距離取 3 筆（HNSW），不跟前面的結果合併。」

- [ ] **Step 3: 維度檢索設計 §13**

§13.1 第一條「片段檢索的 SQL 形狀」後面加「（維度項目；C# 的 facet 項目 2026-09-29 起改走 `preset_facet_embeddings`，見 [facet 向量設計](2026-09-29-facet-vector-retrieval-design.md)）」；第二條分級門檻後加「（facet 項目另有 0.22／0.27）」。§13.2 表格加一列：

```markdown
| facet 項目的向量 | 沒有 facet 項目 | facet 項目以該 facet 的英文 tag 向量排序、依 tag 組合去重、查詢句帶模型給的英文 tag；Python 端沒有對應實作 |
```

- [ ] **Step 4: SK架構說明、eval-cases**

`docs/SK架構說明.md` 用 grep 找 `SearchPresets` 的參數描述（`queries`、`facetId`）；有就補「`tags`（選填，facet 項目的英文 tag，2026-09-29）」，沒有就不動。

`docs/eval-cases.md` 末尾加新節，結果欄留空：

```markdown
## 2026-09-29 facet 層級向量（待 merge 後跑）

前置：開發庫套 `db/migrations/003`、跑 `scripts/embed_facet_tags.py` 到 `preset_facet_embeddings` 有約 37k 列；用本分支建 compose。Claude 以 Playwright 驅動 headless Edge 跑，實際呼叫 Gemini。

| # | 操作 | 應該看到 | 結果 |
| :--- | :--- | :--- | :--- |
| F1 | 送「一個銀髮少女穿涼鞋站在雨夜街頭」，開「顯示檢索細節」 | 鞋履項目的查詢句顯示「涼鞋（sandals）」、標「facet 向量」、池約 338；前 5 名的鞋履 tag 都是 sandal 類且互不相同 | |
| F2 | 多送 5 句不同描述（含穿著、髮型、場景、風格），查 audit `Tool_Invoked` 的 `SearchPresets` args | facet 項目帶 `tags` 的比例；低於一半就要回頭加強工具說明 | |
| F3 | 送「一個女生穿拖鞋在海邊」，等追問／定稿的推薦 | 穿著維度若字面錨 `slippers` 命中不到 2 筆，出現「接近你講的 slippers」；audit `recommendations.dimensions[]` 有 `similar:true`；看前 3 套的鞋履 tag 是否合理（門檻 0.23） | |
| F4 | 對一個 `preset_facet_embeddings` 為空的庫（或暫時 `TRUNCATE` 後還原）送 F1 那句 | 鞋履項目標「整套向量」、池 396、照常回結果 | |
| F5 | `EXPLAIN ANALYZE` `SearchFacetSql`，`facet_id = 'scene.location'` | Execution Time 個位數毫秒級 | |
```

- [ ] **Step 5: Commit**

```bash
git add docs/known-issues.md docs/superpowers/specs/2026-09-21-genai-prompt-copilot-design.md docs/superpowers/specs/2026-09-22-dimension-scoped-retrieval-design.md docs/SK架構說明.md docs/eval-cases.md
git commit -m "docs: facet vector retrieval — known-issues #12, retrieval spec sections, eval cases F1-F5"
```

---

### Task 12: merge 後的資料產生、驗收、seed-v3（不由子代理執行）

這個任務在使用者說 merge、分支進 master 之後由 Claude 主 session 跑，結果補進文件後再 commit；`SEED_URL` 改成 v3 要等 Release 上傳完成。

- [ ] **Step 1: 開發庫產生向量**

Run: `cd scripts && ./.venv/Scripts/python.exe embed_facet_tags.py --workers 4`
Expected: 約 37,011 筆寫入、0 失敗；記下秒數。失敗批就重跑一次。

- [ ] **Step 2: 離線重跑實驗（驗收 1）**

在 scratchpad 寫一次性腳本：對實驗紀錄結果表的 17 題，查詢句 `f"{中文}（{英文 tag}）"` 用 `RETRIEVAL_QUERY` embed，對子表跑 `SearchFacetSql`（逐字相同，`LIMIT 10`），依實驗紀錄的正解 regex 對 `facet_tags -> facet` 計 p5／p10／div5。把結果表補進 `docs/experiments/2026-09-29-facet-vector-text.md` 新節「驗收：正式路徑重跑」，與 V4d 對照並解釋差異（剝權重、`tag_key` 不看順序）。

- [ ] **Step 3: 查詢速度（驗收 2、F5）**

Run（psql）：`EXPLAIN ANALYZE` 帶入任一 768 維向量、`facet_id = 'scene.location'`、`LIMIT 5`。把 Execution Time 記進 eval-cases F5 與 spec §7 第 1 點。

- [ ] **Step 4: 線上驗收 F1–F4**

用本分支建 compose（`docker compose up -d --build api frontend`），Playwright + headless Edge 跑 F1–F4，結果寫進 eval-cases 的結果欄；F2 的 `tags` 填寫率從 `audit_logs` 算。F3 若門檻不合理，調 `RecommendationSimilarMaxDist` 並記錄。

- [ ] **Step 5: 補文件與 commit**

known-issues #12 的 `<hash>` 換成 merge commit；eval-cases、實驗紀錄補結果。

```bash
git add docs/known-issues.md docs/eval-cases.md docs/experiments/2026-09-29-facet-vector-text.md docs/superpowers/specs/2026-09-29-facet-vector-retrieval-design.md
git commit -m "docs: record facet vector acceptance (offline rerun, query timing, F1-F4)"
```

- [ ] **Step 6: seed-v3**

Run: `cd scripts && ./.venv/Scripts/python.exe export_seed.py --version 3`
Expected: 印出三張表的筆數（`facet_embeddings` 約 37k）、檔案約 210MB、以及 `gh release create seed-v3 …` 指令。**使用者**執行那行指令上傳。

- [ ] **Step 7: 上傳完成後改 compose 預設，並用全新 volume 驗證**

`docker-compose.yml` 的 `SEED_URL` 預設改成 `…/releases/download/seed-v3/prompt_copilot_seed_v3.dump`。用另一個 project name 起一組全新 volume（`docker compose -p pcseedcheck up -d db seed`，容器名衝突時照 compose 的 `-p` 隔離），確認 seed log 印出三張表筆數、沒有「preset_facet_embeddings 是空的」提醒；驗完 `docker compose -p pcseedcheck down -v`。

```bash
git add docker-compose.yml
git commit -m "chore(compose): default SEED_URL points at seed-v3 (carries preset_facet_embeddings)"
```

---

## 自我檢查（寫完計畫後對照 spec）

- **spec 覆蓋**：§4.1→Task 1；§4.2→Task 2；§4.3→Task 3；§4.4→Task 1（seed.sh）、4、12；§5.1→Task 6、8；§5.2–5.5→Task 7、6；§6→Task 9；§7→Task 1 註解、5 註解、11 文件；§8→各任務測試；§9→Task 12（案例在 Task 11 先寫）；§10→Task 11；§11→Task 12；§12 風險→Task 7（不填 tags 的退路）、Task 9（門檻可設定）。
- **型別一致**：`FacetPoolSizeAsync(string, CancellationToken)`、`SearchFacetAsync(float[], string, int, CancellationToken)`、`RecommendSimilarAsync(float[], string, IReadOnlyList<string>, double, int, CancellationToken)` 在 Task 5 定義、Task 7／9 的 fake 與呼叫同簽名；`SearchPresetsItem` 的兩個新參數順序 `(…, Hits, Tags, Method)` 在 Task 6 定義、Task 7 使用；`RecommendedDimension.Similar` 是最後一個參數、有預設值，Task 9 與 Task 10 的 JSON 名 `similar` 一致；Python `Item` 四元組順序 (preset_id, facet_id, tag_key, text) 在 Task 3 的 `make_plan`、`run`、測試一致。
- **Review Focus** 五條各有測試：1→Task 7、2→Task 2、3→Task 3、4→Task 9、5→Task 10。
