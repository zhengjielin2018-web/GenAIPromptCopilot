# 子專案 1：資料地基 — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 建好 pgvector 資料庫與一條分階段、可重跑的 Python 管線，把 Civitai 公開資料清洗、結構化、向量化後寫入兩張表，並用一支查詢腳本以繁中模糊敘述驗證檢索品質。

**Architecture:** 管線分五個階段（fetch → clean → structure → embed → load），每階段讀前一階段的 jsonl、寫自己的 jsonl，可獨立重跑與斷點續傳。Gemini 與 Civitai 的 HTTP 呼叫各自包在可注入假物件的 client 類別裡，讓每階段的邏輯都能在不打網路的情況下用 pytest 測。DB schema 以 SQL 檔為單一真實來源，由 docker-compose 自動執行。

**Tech Stack:** Python 3.12+、httpx、google-genai、pydantic v2、psycopg 3 + pgvector、PyYAML、python-dotenv、pytest、ruff；PostgreSQL 16 + pgvector（Docker）；Gemini `gemini-3.5-flash-lite`（結構化；原訂的 `gemini-2.5-flash-lite` 已對新使用者停用，見下方修正紀錄 R18）與 `gemini-embedding-001`（768 維）。

**Spec:** [docs/superpowers/specs/2026-09-21-genai-prompt-copilot-design.md](../specs/2026-09-21-genai-prompt-copilot-design.md) — 本計畫實作 §2.1 第 1 項、§6.3、§7、§8、§9、§14 第 1 列。

---

## ⚠️ 執行期修正紀錄（Corrections applied during execution）

> **本計畫已完整執行完畢。下列項目是執行過程中發現「計畫本身寫錯」的地方。**
> 各任務的程式碼區塊**保留原樣未改寫**，以保存當初的判斷紀錄；但若你要直接照抄某段程式碼，
> 請先看這張表。以 repo 內的實際程式碼為準，計畫文件只是當初的論證。

| # | 影響任務 | 計畫寫錯的地方 | 實際採用的作法 |
| :--- | :--- | :--- | :--- |
| R2 | Task 1 | `line-length = 100`，但計畫自己提供的程式碼有約 18 行超過 100 字元 | 改為 `line-length = 120` |
| R3 | Task 1 | `config.py` 匯入了從未使用的 `Field`（ruff F401） | 只匯入 `BaseModel` |
| R5/R8 | Task 1 | `Settings` 用 `load_dotenv()`，會把 `.env` 洩進全域 `os.environ`；模組載入時就執行，導致所有測試互相污染 | 改用 `dotenv_values()`，永不寫入 `os.environ`；優先序仍為 kwargs > os.environ > .env > 預設值 |
| R12 | Task 2、Task 10 | 測試寫 `len(vector) == 768`，但 pgvector 的 `Vector` 沒有 `__len__` 也沒有 `__iter__` | 改用 `.dimensions()` 與 `.to_list()`；`to_numpy()` 不可用（未安裝 numpy） |
| **R13** | **Task 5** | **`iter_images` 每頁只算一次 `next_cursor` 並附在該頁每一筆上；`run_fetch` 在頁中途停止時存下的是「整頁之後」的游標，導致該頁未寫入的資料永久遺失。實測 `--max-items 20` 搭配預設 `limit=200`，抓了 200 筆只寫 20 筆，游標卻跳過全部 200 筆。** | **改為 yield「抓這一頁所用的游標」，早停時一律記 `done: False`；續跑會重抓該頁，由既有去重邏輯跳過已寫入的部分。另補上頁中途停止的測試** |
| R14/R15 | Task 6 | NSFW 關鍵詞清單漏掉性暗示形容詞；實測 3/12 筆通過過濾的資料含 `cleavage, extremely sexy, seductive`。且斷詞方式讓 `underwear_only`（底線）與 `half-naked`（連字號）繞過過濾 | 新增 `sexy / seductive / cleavage / busty / skimpy / scantily / voluptuous / lewd / suggestive / provocative`（刻意**不**加解剖學名詞如 `breasts`，那是一般動漫標籤）；比對前把非英數字元正規化成空白 |
| **R18** | **Task 7** | **`gemini-2.5-flash-lite` 已對新使用者停用，實際呼叫回 404。它仍會出現在 API 自己的 `models.list()` 裡，所以「列表裡有」不等於「可以用」** | **改用 `gemini-3.5-flash-lite`（釘死版本，不用 `-latest` 別名，以維持 eval 可重現）** |
| R17/R19 | Task 8 | 只靠 prompt 指示 LLM 別把 `score_9` 這類分數標籤當成片段。實測負向詞那側洩漏率 12.5%，而正向側「0/16」在 n=5 下幾乎不具統計意義 | 改為 `structure.py` 內的確定性過濾 `_strip_boilerplate`，正負兩側都套用。實測在 859 筆 preset 上洩漏 0 筆 |
| R24 | Task 8 | 同一個過濾沒套用到 history 的 `positive_prompt`／`negative_prompt` | 一併套用（embedding 建在 `user_intent` 上，所以修補不需重跑 LLM） |
| R4 | Task 11 | `query_check.py` 用了沒有佔位符的 f-string（ruff F541） | 改成一般字串 |
| R20/R21 | Task 10 | GIN 查詢的斷言沒限定測試資料列，資料庫一有真實資料就會失敗；且 preset 的 `ON CONFLICT DO UPDATE` 分支完全沒有測試 | 改為斷言「測試列存在於結果集合中」；補上三個 upsert 測試 |
| R22 | Task 11 | 最終跑 `--max-items 3000`，約等於 1500 次即時 LLM 呼叫，很可能耗盡 Gemini 免費額度 | 改跑 `--max-items 400`（產出 258 histories／859 presets）。各階段皆可續跑，要擴充直接用更大的數字重跑同一指令 |

完整的判斷理由與證據保存在執行紀錄 `.superpowers/sdd/2026-09-21-subproject-1-data-foundation/progress.md`（該目錄未納入版控）。

---

## Global Constraints

- Python 3.12+；PostgreSQL 16 + pgvector（`pgvector/pgvector:pg16`）
- 向量維度 **768**；embedding 模型 `gemini-embedding-001`（`task_type`：文件用 `RETRIEVAL_DOCUMENT`、查詢用 `RETRIEVAL_QUERY`）。此決定寫入 `.env.example` 與 `db/init/001_schema.sql`，兩處必須一致；換模型必須 `embed.py --reindex`
- 結構化模型 `gemini-3.5-flash-lite`（可由 `.env` 覆寫）
- NSFW 過濾為必要步驟：Civitai `nsfw=None` + `nsfwLevel == "None"` + 自建關鍵詞清單，三層皆做
- `description`、`user_intent`、`title` 為繁體中文；`prompt_snippet`、`negative_snippet`、`positive_prompt`、`negative_prompt`、`tags` 為英文
- Schema 只在 `db/init/001_schema.sql` 定義；Python 不建表
- 金鑰只在 `.env`（git ignore）；`scripts/data/` 全部 git ignore
- 每階段可 `--from <stage>` 重跑；`embed` 與 `structure` 支援斷點續傳
- 所有 CLI 從 `scripts/` 目錄執行：`python -m pipeline.<stage>`
- 每個任務結束都 commit；commit 訊息結尾附 `Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>`

### 與 spec 的兩處小偏離（已決定）

1. 兩張表各加 `source_ref VARCHAR(64) UNIQUE`（如 `civitai:12345`、`civitai:12345:0`），讓 `load.py` 能 upsert 而非重複插入。使用者沉澱的紀錄此欄為 NULL。
2. 增加 `scripts/data/embedded/` 目錄存放向量化結果，embed 階段的斷點續傳靠它。

---

## File Structure

```text
GenAIPromptCopilot/
├─ .env.example                              # 所有環境變數與預設值（Task 1）
├─ docker-compose.yml                        # db 服務（Task 2）
├─ db/init/001_schema.sql                    # schema（Task 2）
├─ src/PromptCopilot.Api/Configuration/
│  └─ facets.yaml                            # 六維度 facet 定義；Python 與之後的 C# 共用（Task 3）
└─ scripts/
   ├─ requirements.txt                       # 相依套件（Task 1）
   ├─ pyproject.toml                         # pytest + ruff 設定（Task 1）
   ├─ README.md                              # 管線使用說明（Task 11）
   ├─ seed_data.py                           # 串起五階段的入口（Task 11）
   ├─ query_check.py                         # 驗收用查詢腳本（Task 11）
   ├─ pipeline/
   │  ├─ __init__.py
   │  ├─ config.py                           # 讀 .env、路徑常數（Task 1）
   │  ├─ jsonl.py                            # jsonl 讀寫（Task 4）
   │  ├─ db.py                               # psycopg 連線 + register_vector（Task 2）
   │  ├─ facets.py                           # 載入 facets.yaml，提供合法 facet id 集合（Task 3）
   │  ├─ ratelimit.py                        # RateLimiter + retry（Task 4）
   │  ├─ civitai_client.py                   # Civitai HTTP client，cursor 分頁（Task 5）
   │  ├─ fetch_civitai.py                    # 階段 1 CLI（Task 5）
   │  ├─ nsfw_filter.py                      # 關鍵詞過濾（Task 6）
   │  ├─ clean.py                            # 階段 2（Task 6）
   │  ├─ gemini_client.py                    # 結構化生成 + 批次 embedding 包裝（Task 7）
   │  ├─ structure.py                        # 階段 3（Task 8）
   │  ├─ embed.py                            # 階段 4（Task 9）
   │  └─ load.py                             # 階段 5（Task 10）
   ├─ tests/
   │  ├─ conftest.py
   │  ├─ test_smoke.py                       # Task 1
   │  ├─ test_db.py                          # Task 2（integration）
   │  ├─ test_facets.py                      # Task 3
   │  ├─ test_jsonl.py / test_ratelimit.py   # Task 4
   │  ├─ test_civitai_client.py / test_fetch.py   # Task 5
   │  ├─ test_clean.py                       # Task 6
   │  ├─ test_gemini_client.py               # Task 7
   │  ├─ test_structure.py                   # Task 8
   │  ├─ test_embed.py                       # Task 9
   │  └─ test_load.py                        # Task 10（integration）
   └─ data/                                  # git ignore
      ├─ raw/      images.jsonl, state.json
      ├─ clean/    records.jsonl
      ├─ structured/ histories.jsonl, presets.jsonl
      └─ embedded/   histories.jsonl, presets.jsonl
```

### 各階段資料契約

**raw/images.jsonl** — Civitai `items[]` 原樣一行一筆（不改欄位）。

**clean/records.jsonl**
```json
{"source_id": 12345, "prompt": "1girl, ...", "negative_prompt": "lowres, ...", "image_url": "https://...", "width": 832, "height": 1216, "base_model": "Illustrious", "like_count": 321, "prompt_hash": "9f2a..."}
```

**structured/histories.jsonl**
```json
{"source_ref": "civitai:12345", "user_intent": "雨夜霓虹街道上的黑髮少女，低角度仰拍", "positive_prompt": "...", "negative_prompt": "...", "subject_profile": "portrait", "image_url": "https://..."}
```

**structured/presets.jsonl**
```json
{"source_ref": "civitai:12345:0", "title": "賽博龐克雨夜街道", "category": "Scene", "description": "昏暗潮濕的城市夜景，霓虹招牌倒映在積水路面", "tags": ["cyberpunk", "rainy night", "neon"], "facet_ids": ["scene.location", "scene.weather", "scene.lighting"], "prompt_snippet": "cyberpunk city street at night, rain, neon signs reflecting on wet pavement", "negative_snippet": null, "image_url": "https://..."}
```

**embedded/*.jsonl** — 同上再加 `"embedding": [768 floats]`。

---

### Task 1: 環境前置與專案骨架

**Files:**
- Create: `.env.example`
- Create: `scripts/requirements.txt`
- Create: `scripts/pyproject.toml`
- Create: `scripts/pipeline/__init__.py`
- Create: `scripts/pipeline/config.py`
- Create: `scripts/tests/conftest.py`
- Create: `scripts/tests/test_smoke.py`
- Modify: `.gitignore`（確認 `scripts/data/`、`.venv/`、`.env` 已在內）

**Interfaces:**
- Produces: `pipeline.config.Settings`（pydantic model，欄位見下）與 `pipeline.config.settings`（模組層單例）、`pipeline.config.REPO_ROOT / DATA_DIR / RAW_DIR / CLEAN_DIR / STRUCTURED_DIR / EMBEDDED_DIR / FACETS_PATH`（`pathlib.Path`）

- [ ] **Step 1: 安裝 Python 3.12+ 與 Docker Desktop（本機目前兩者皆未安裝）**

PowerShell（系統管理員不需要）：
```powershell
winget install --id Python.Python.3.12 -e
winget install --id Docker.DockerDesktop -e
```
安裝後**重開終端機**。Docker Desktop 第一次需手動啟動並完成初始設定（WSL 2 後端）。

- [ ] **Step 2: 驗證安裝**

Run: `python --version` → Expected: `Python 3.12.x`（不是 Microsoft Store 的提示訊息）
Run: `docker compose version` → Expected: `Docker Compose version v2.x`

- [ ] **Step 3: 建立 venv 與相依清單**

`scripts/requirements.txt`:
```text
httpx>=0.27
google-genai>=1.0
pydantic>=2.7
psycopg[binary]>=3.2
pgvector>=0.3
PyYAML>=6.0
python-dotenv>=1.0
pytest>=8.0
ruff>=0.5
```

PowerShell，在 repo 根目錄：
```powershell
cd scripts
python -m venv .venv
.\.venv\Scripts\Activate.ps1
python -m pip install --upgrade pip
pip install -r requirements.txt
```

- [ ] **Step 4: pytest 與 ruff 設定**

`scripts/pyproject.toml`:
```toml
[tool.pytest.ini_options]
testpaths = ["tests"]
markers = ["integration: 需要本機 PostgreSQL 或真實 API 金鑰"]
addopts = "-m 'not integration'"

[tool.ruff]
line-length = 100
target-version = "py312"

[tool.ruff.lint]
select = ["E", "F", "I", "UP", "B"]
```

- [ ] **Step 5: 寫 `.env.example`**

```dotenv
# ---- PostgreSQL（docker-compose 與管線共用）----
POSTGRES_USER=postgres
POSTGRES_PASSWORD=change_me
POSTGRES_DB=prompt_copilot
POSTGRES_HOST=localhost
POSTGRES_PORT=5432

# ---- Gemini ----
GEMINI_API_KEY=
GEMINI_STRUCTURE_MODEL=gemini-2.5-flash-lite
GEMINI_EMBEDDING_MODEL=gemini-embedding-001
# 換 embedding 模型或維度 = 整個向量庫必須 `python -m pipeline.embed --reindex` 後重新 load，
# 且 db/init/001_schema.sql 的 VECTOR(768) 必須同步修改
EMBEDDING_DIMENSIONS=768

# ---- 呼叫節流（秒）----
CIVITAI_MIN_INTERVAL_S=1.0
GEMINI_MIN_INTERVAL_S=0.5
```

複製為 `.env` 並填入金鑰：`Copy-Item .env.example .env`（在 repo 根目錄）。

- [ ] **Step 6: 寫失敗的 smoke test**

`scripts/tests/conftest.py`:
```python
import sys
from pathlib import Path

# 讓 tests 能 import pipeline.*（從 scripts/ 執行 pytest 時）
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
```

`scripts/tests/test_smoke.py`:
```python
from pipeline.config import DATA_DIR, EMBEDDED_DIR, RAW_DIR, REPO_ROOT, Settings


def test_repo_root_contains_docs():
    assert (REPO_ROOT / "docs").is_dir()


def test_data_dirs_are_under_scripts_data():
    assert RAW_DIR == DATA_DIR / "raw"
    assert EMBEDDED_DIR == DATA_DIR / "embedded"


def test_settings_defaults_without_env(monkeypatch):
    monkeypatch.delenv("GEMINI_API_KEY", raising=False)
    s = Settings(_env_file=None)
    assert s.embedding_dimensions == 768
    assert s.gemini_embedding_model == "gemini-embedding-001"
    assert s.postgres_dsn.startswith("postgresql://")
```

- [ ] **Step 7: 跑測試確認失敗**

Run（在 `scripts/`）: `python -m pytest tests/test_smoke.py -v`
Expected: FAIL — `ModuleNotFoundError: No module named 'pipeline'`

- [ ] **Step 8: 實作 config.py**

`scripts/pipeline/__init__.py`: 空檔案。

`scripts/pipeline/config.py`:
```python
"""環境變數與路徑常數。所有階段都從這裡取設定，不各自讀 os.environ。"""

from __future__ import annotations

import os
from pathlib import Path

from dotenv import load_dotenv
from pydantic import BaseModel, Field

REPO_ROOT = Path(__file__).resolve().parents[2]
SCRIPTS_DIR = REPO_ROOT / "scripts"
DATA_DIR = SCRIPTS_DIR / "data"
RAW_DIR = DATA_DIR / "raw"
CLEAN_DIR = DATA_DIR / "clean"
STRUCTURED_DIR = DATA_DIR / "structured"
EMBEDDED_DIR = DATA_DIR / "embedded"
FACETS_PATH = REPO_ROOT / "src" / "PromptCopilot.Api" / "Configuration" / "facets.yaml"


class Settings(BaseModel):
    postgres_user: str = "postgres"
    postgres_password: str = "postgres"
    postgres_db: str = "prompt_copilot"
    postgres_host: str = "localhost"
    postgres_port: int = 5432

    gemini_api_key: str = ""
    gemini_structure_model: str = "gemini-2.5-flash-lite"
    gemini_embedding_model: str = "gemini-embedding-001"
    embedding_dimensions: int = 768

    civitai_min_interval_s: float = 1.0
    gemini_min_interval_s: float = 0.5

    def __init__(self, _env_file: str | Path | None = REPO_ROOT / ".env", **overrides):
        if _env_file is not None:
            load_dotenv(_env_file, override=False)
        values = {
            name: os.environ[name.upper()]
            for name in type(self).model_fields
            if name.upper() in os.environ
        }
        values.update(overrides)
        super().__init__(**values)

    @property
    def postgres_dsn(self) -> str:
        return (
            f"postgresql://{self.postgres_user}:{self.postgres_password}"
            f"@{self.postgres_host}:{self.postgres_port}/{self.postgres_db}"
        )


settings = Settings()
```

- [ ] **Step 9: 跑測試確認通過**

Run: `python -m pytest tests/test_smoke.py -v`
Expected: 3 passed

- [ ] **Step 10: ruff 檢查**

Run: `ruff check .`
Expected: `All checks passed!`

- [ ] **Step 11: 確認 .gitignore 並 commit**

確認 `.gitignore` 內含 `.env`、`.venv/`、`scripts/data/`、`__pycache__/`（brainstorming 階段建立的 `.gitignore` 已含這些；若缺則補上）。

```bash
git add .env.example scripts/requirements.txt scripts/pyproject.toml scripts/pipeline scripts/tests .gitignore
git commit -m "chore(pipeline): project skeleton, settings, smoke test

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 2: 資料庫 — docker-compose、schema、連線模組

**Files:**
- Create: `docker-compose.yml`
- Create: `db/init/001_schema.sql`
- Create: `scripts/pipeline/db.py`
- Create: `scripts/tests/test_db.py`

**Interfaces:**
- Consumes: `pipeline.config.settings.postgres_dsn`
- Produces: `pipeline.db.connect() -> psycopg.Connection`（已 `register_vector`，`autocommit=False`）、`pipeline.db.db_available() -> bool`

- [ ] **Step 1: 寫 docker-compose.yml**

```yaml
services:
  db:
    image: pgvector/pgvector:pg16
    container_name: prompt-copilot-db
    environment:
      POSTGRES_USER: ${POSTGRES_USER:-postgres}
      POSTGRES_PASSWORD: ${POSTGRES_PASSWORD:-postgres}
      POSTGRES_DB: ${POSTGRES_DB:-prompt_copilot}
    ports:
      - "${POSTGRES_PORT:-5432}:5432"
    volumes:
      - pgdata:/var/lib/postgresql/data
      - ./db/init:/docker-entrypoint-initdb.d:ro
    healthcheck:
      test: ["CMD-SHELL", "pg_isready -U ${POSTGRES_USER:-postgres} -d ${POSTGRES_DB:-prompt_copilot}"]
      interval: 5s
      timeout: 3s
      retries: 12

volumes:
  pgdata:
```

- [ ] **Step 2: 寫 schema**

`db/init/001_schema.sql`:
```sql
-- Schema 單一真實來源。EF Core 與 Python 皆不建表。
-- VECTOR 維度必須與 .env 的 EMBEDDING_DIMENSIONS 一致（目前 768 / gemini-embedding-001）。

CREATE EXTENSION IF NOT EXISTS vector;

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
```

- [ ] **Step 3: 啟動資料庫並驗證 schema**

在 repo 根目錄（`.env` 已存在）：
```powershell
docker compose up -d db
docker compose ps
```
Expected: `prompt-copilot-db` 狀態 `healthy`（可能需等 10 秒）。

```powershell
docker exec prompt-copilot-db psql -U postgres -d prompt_copilot -c "\dt" -c "\dx"
```
Expected: 三張表列出；`vector` 出現在 extension 清單。

若表不存在：init 只在 volume 第一次建立時執行，`docker compose down -v` 再 `up -d db`。

- [ ] **Step 4: 寫失敗的連線測試**

`scripts/tests/test_db.py`:
```python
import pytest

from pipeline import db


@pytest.mark.integration
def test_connect_registers_vector_and_tables_exist():
    if not db.db_available():
        pytest.skip("PostgreSQL 未啟動")
    with db.connect() as conn:
        names = {
            r[0]
            for r in conn.execute(
                "SELECT tablename FROM pg_tables WHERE schemaname = 'public'"
            ).fetchall()
        }
        assert {"shared_prompt_histories", "prompt_knowledge_presets", "audit_logs"} <= names
        dim = conn.execute(
            "SELECT atttypmod FROM pg_attribute "
            "WHERE attrelid = 'prompt_knowledge_presets'::regclass AND attname = 'preset_embedding'"
        ).fetchone()[0]
        assert dim == 768
```

- [ ] **Step 5: 跑測試確認失敗**

Run: `python -m pytest tests/test_db.py -m integration -v`
Expected: FAIL — `AttributeError: module 'pipeline.db' has no attribute 'db_available'`（或 ImportError）

- [ ] **Step 6: 實作 db.py**

`scripts/pipeline/db.py`:
```python
"""psycopg 3 連線工廠。所有 DB 存取都經由 connect()，確保 vector 型別已註冊。"""

from __future__ import annotations

import psycopg
from pgvector.psycopg import register_vector

from pipeline.config import settings


def connect() -> psycopg.Connection:
    conn = psycopg.connect(settings.postgres_dsn)
    register_vector(conn)
    return conn


def db_available() -> bool:
    try:
        with psycopg.connect(settings.postgres_dsn, connect_timeout=2):
            return True
    except psycopg.OperationalError:
        return False
```

- [ ] **Step 7: 跑測試確認通過**

Run: `python -m pytest tests/test_db.py -m integration -v`
Expected: 1 passed

Run: `python -m pytest -v`（預設排除 integration）
Expected: smoke 3 passed，`test_db` 顯示 deselected

- [ ] **Step 8: Commit**

```bash
git add docker-compose.yml db/init/001_schema.sql scripts/pipeline/db.py scripts/tests/test_db.py
git commit -m "feat(db): pgvector compose service, schema, psycopg connection helper

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 3: facets.yaml 與載入器

**Files:**
- Create: `src/PromptCopilot.Api/Configuration/facets.yaml`
- Create: `scripts/pipeline/facets.py`
- Create: `scripts/tests/test_facets.py`

**Interfaces:**
- Produces: `pipeline.facets.load_facets(path=FACETS_PATH) -> FacetCatalog`；`FacetCatalog.all_ids: frozenset[str]`、`FacetCatalog.dimension_of(facet_id) -> str`、`FacetCatalog.ids_for_profile(profile) -> frozenset[str]`、`FacetCatalog.prompt_listing() -> str`（給 LLM 看的清單文字）

- [ ] **Step 1: 寫 facets.yaml（spec §5.2、§5.3、§5.6 的完整內容）**

`src/PromptCopilot.Api/Configuration/facets.yaml`:
```yaml
# 六維度 facet 定義。Python 管線與 C# API 共用此檔；前端由 GET /api/config/facets 取得。
dimensions:
  - key: style
    label: 風格
    facets:
      - { id: style.genre,     label: 藝術流派／媒材,       hint: "anime, oil painting, photorealistic, watercolor" }
      - { id: style.reference, label: 參照畫師或作品,       hint: "Makoto Shinkai style, by Greg Rutkowski" }
      - { id: style.render,    label: 渲染引擎／技術風格詞, hint: "octane render, cel shading, film grain（不含 masterpiece 等基礎畫質詞）" }
      - { id: style.palette,   label: 色調傾向,             hint: "vibrant colors, muted palette, monochrome" }
  - key: scene
    label: 場景
    facets:
      - { id: scene.location,   label: 地點類型,       hint: "city street, forest, bedroom, space station" }
      - { id: scene.foreground, label: 前景元素,       hint: "flower petals in foreground, rain drops on lens" }
      - { id: scene.midground,  label: 中景／主體周邊, hint: "surrounded by market stalls, standing beside a motorcycle" }
      - { id: scene.background, label: 背景與遠景,     hint: "mountains in the distance, skyline, blurred crowd" }
      - { id: scene.lighting,   label: 光源與時間,     hint: "golden hour, neon lights, backlit, overcast noon" }
      - { id: scene.weather,    label: 天氣氛圍,       hint: "rain, fog, snow, clear sky" }
      - { id: scene.season,     label: 季節,           hint: "autumn leaves, cherry blossoms, snow-covered" }
  - key: camera
    label: 鏡頭
    facets:
      - { id: camera.shot,        label: 景別,     hint: "close-up, upper body, full body, wide shot" }
      - { id: camera.angle,       label: 視角高度, hint: "low angle, eye level, from above, bird's-eye view" }
      - { id: camera.focal,       label: 焦段,     hint: "85mm, wide-angle lens, telephoto" }
      - { id: camera.dof,         label: 景深,     hint: "shallow depth of field, bokeh, everything in focus" }
      - { id: camera.composition, label: 構圖,     hint: "rule of thirds, centered, dutch angle, symmetrical" }
  - key: appearance
    label: 人物樣貌
    facets:
      - { id: appearance.age_gender, label: 年齡與性別, hint: "1girl, young woman, elderly man" }
      - { id: appearance.face,       label: 臉部特徵,   hint: "sharp jawline, freckles, blue eyes" }
      - { id: appearance.hair,       label: 髮型髮色,   hint: "long black hair, messy silver bob, twin tails" }
      - { id: appearance.body,       label: 膚色體型,   hint: "pale skin, athletic build, tan" }
      - { id: appearance.expression, label: 表情,       hint: "smiling, tired eyes, smirk, crying" }
      - { id: appearance.material,   label: 材質與工藝, hint: "brushed steel, hand-carved wood, worn leather（object/vehicle 用）" }
      - { id: appearance.wear,       label: 磨損與使用痕跡, hint: "scratched paint, rust, patina（object/vehicle 用）" }
      - { id: appearance.scale,      label: 尺度參照,   hint: "towering over buildings, palm-sized（object/vehicle 用）" }
  - key: pose
    label: 人物動作
    facets:
      - { id: pose.main,         label: 主要動作／姿勢,         hint: "sitting on a bench, running, leaning against wall" }
      - { id: pose.limbs,        label: 肢體細節,               hint: "hand on hip, head tilted, arms crossed" }
      - { id: pose.gaze,         label: 視線方向,               hint: "looking at viewer, looking away, eyes closed" }
      - { id: pose.interaction,  label: 與環境或道具的互動,     hint: "holding umbrella, touching water, riding bicycle" }
      - { id: pose.motion,       label: 動態感,                 hint: "motion blur, hair flowing in wind, frozen mid-jump" }
      - { id: pose.motion_state, label: 運動狀態,               hint: "parked, speeding, drifting（vehicle 用）" }
      - { id: pose.terrain,      label: 與地形的互動,           hint: "kicking up dust, splashing through puddle（vehicle 用）" }
  - key: clothing
    label: 人物穿著
    facets:
      - { id: clothing.head,        label: 頭部配件,   hint: "beret, cat ears headband, glasses" }
      - { id: clothing.upper,       label: 上半身,     hint: "oversized hoodie, leather jacket, kimono" }
      - { id: clothing.lower,       label: 下半身,     hint: "pleated skirt, cargo pants, torn jeans" }
      - { id: clothing.footwear,    label: 鞋履,       hint: "combat boots, sneakers, barefoot" }
      - { id: clothing.material,    label: 材質與磨損, hint: "worn denim, glossy latex, frayed hem" }
      - { id: clothing.accessories, label: 配件飾品,   hint: "silver necklace, backpack, wristwatch" }

profiles:
  portrait:
    dimensions:
      style:      [style.genre, style.reference, style.render, style.palette]
      scene:      [scene.location, scene.foreground, scene.midground, scene.background, scene.lighting, scene.weather]
      camera:     [camera.shot, camera.angle, camera.focal, camera.dof, camera.composition]
      appearance: [appearance.age_gender, appearance.face, appearance.hair, appearance.body, appearance.expression]
      pose:       [pose.main, pose.limbs, pose.gaze, pose.interaction, pose.motion]
      clothing:   [clothing.head, clothing.upper, clothing.lower, clothing.footwear, clothing.material, clothing.accessories]
  landscape:
    dimensions:
      style:  [style.genre, style.reference, style.render, style.palette]
      scene:  [scene.location, scene.foreground, scene.midground, scene.background, scene.lighting, scene.weather, scene.season]
      camera: [camera.shot, camera.angle, camera.focal, camera.dof, camera.composition]
  object:
    labels: { appearance: 主體外觀 }
    dimensions:
      style:      [style.genre, style.reference, style.render, style.palette]
      scene:      [scene.location, scene.foreground, scene.midground, scene.background, scene.lighting, scene.weather]
      camera:     [camera.shot, camera.angle, camera.focal, camera.dof, camera.composition]
      appearance: [appearance.material, appearance.wear, appearance.scale]
  vehicle:
    labels: { appearance: 主體外觀, pose: 運動狀態 }
    dimensions:
      style:      [style.genre, style.reference, style.render, style.palette]
      scene:      [scene.location, scene.foreground, scene.midground, scene.background, scene.lighting, scene.weather]
      camera:     [camera.shot, camera.angle, camera.focal, camera.dof, camera.composition]
      appearance: [appearance.material, appearance.wear, appearance.scale]
      pose:       [pose.motion_state, pose.terrain]
```

- [ ] **Step 2: 寫失敗測試**

`scripts/tests/test_facets.py`:
```python
from pipeline.config import FACETS_PATH
from pipeline.facets import load_facets


def test_loads_all_six_dimensions():
    cat = load_facets(FACETS_PATH)
    assert set(cat.dimensions) == {"style", "scene", "camera", "appearance", "pose", "clothing"}


def test_all_ids_include_pose_and_footwear():
    cat = load_facets(FACETS_PATH)
    assert "pose.main" in cat.all_ids
    assert "clothing.footwear" in cat.all_ids
    assert cat.dimension_of("clothing.footwear") == "clothing"


def test_landscape_profile_has_no_person_dimensions():
    cat = load_facets(FACETS_PATH)
    ids = cat.ids_for_profile("landscape")
    assert "scene.season" in ids
    assert not any(i.startswith(("appearance.", "pose.", "clothing.")) for i in ids)


def test_every_profile_id_exists_in_dimensions():
    cat = load_facets(FACETS_PATH)
    for profile in ("portrait", "landscape", "object", "vehicle"):
        assert cat.ids_for_profile(profile) <= cat.all_ids


def test_prompt_listing_mentions_ids_and_hints():
    listing = load_facets(FACETS_PATH).prompt_listing()
    assert "scene.weather" in listing
    assert "rain, fog" in listing
```

- [ ] **Step 3: 跑測試確認失敗**

Run: `python -m pytest tests/test_facets.py -v`
Expected: FAIL — `ModuleNotFoundError: No module named 'pipeline.facets'`

- [ ] **Step 4: 實作 facets.py**

`scripts/pipeline/facets.py`:
```python
"""載入 facets.yaml。管線用它驗證 LLM 回傳的 facet_ids，並產生給 LLM 看的清單文字。"""

from __future__ import annotations

from dataclasses import dataclass, field
from pathlib import Path

import yaml


@dataclass(frozen=True)
class Facet:
    id: str
    label: str
    hint: str
    dimension: str


@dataclass
class FacetCatalog:
    dimensions: dict[str, str] = field(default_factory=dict)  # key -> label
    facets: dict[str, Facet] = field(default_factory=dict)  # id -> Facet
    profiles: dict[str, dict[str, list[str]]] = field(default_factory=dict)  # profile -> dim -> ids

    @property
    def all_ids(self) -> frozenset[str]:
        return frozenset(self.facets)

    def dimension_of(self, facet_id: str) -> str:
        return self.facets[facet_id].dimension

    def ids_for_profile(self, profile: str) -> frozenset[str]:
        dims = self.profiles[profile]
        return frozenset(i for ids in dims.values() for i in ids)

    def prompt_listing(self) -> str:
        lines: list[str] = []
        for key, label in self.dimensions.items():
            lines.append(f"[{key}] {label}")
            for f in self.facets.values():
                if f.dimension == key:
                    lines.append(f"  - {f.id}：{f.label}（例：{f.hint}）")
        return "\n".join(lines)


def load_facets(path: Path) -> FacetCatalog:
    raw = yaml.safe_load(path.read_text(encoding="utf-8"))
    cat = FacetCatalog()
    for dim in raw["dimensions"]:
        cat.dimensions[dim["key"]] = dim["label"]
        for f in dim["facets"]:
            cat.facets[f["id"]] = Facet(
                id=f["id"], label=f["label"], hint=f["hint"], dimension=dim["key"]
            )
    for name, body in raw["profiles"].items():
        cat.profiles[name] = {k: list(v) for k, v in body["dimensions"].items()}
    unknown = {i for dims in cat.profiles.values() for ids in dims.values() for i in ids} - cat.all_ids
    if unknown:
        raise ValueError(f"facets.yaml profiles 引用了不存在的 facet id: {sorted(unknown)}")
    return cat
```

- [ ] **Step 5: 跑測試確認通過**

Run: `python -m pytest tests/test_facets.py -v`
Expected: 5 passed

- [ ] **Step 6: Commit**

```bash
git add src/PromptCopilot.Api/Configuration/facets.yaml scripts/pipeline/facets.py scripts/tests/test_facets.py
git commit -m "feat(facets): six-dimension facet catalog and loader

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 4: 共用基礎 — jsonl 讀寫、RateLimiter、retry

**Files:**
- Create: `scripts/pipeline/jsonl.py`
- Create: `scripts/pipeline/ratelimit.py`
- Create: `scripts/tests/test_jsonl.py`
- Create: `scripts/tests/test_ratelimit.py`

**Interfaces:**
- Produces:
  - `pipeline.jsonl.read_jsonl(path) -> Iterator[dict]`（檔案不存在回空）
  - `pipeline.jsonl.append_jsonl(path, record: dict) -> None`（自動建目錄，UTF-8，`ensure_ascii=False`）
  - `pipeline.jsonl.write_jsonl(path, records: Iterable[dict]) -> int`（覆寫，回筆數）
  - `pipeline.jsonl.existing_keys(path, key: str) -> set`（讀既有檔案的某欄位集合，供斷點續傳）
  - `pipeline.ratelimit.RateLimiter(min_interval_s, sleep=time.sleep, now=time.monotonic)`，方法 `wait()`
  - `pipeline.ratelimit.retry(fn, *, attempts=5, base_delay_s=2.0, should_retry: Callable[[Exception], bool], sleep=time.sleep)`

- [ ] **Step 1: 寫失敗測試**

`scripts/tests/test_jsonl.py`:
```python
from pipeline.jsonl import append_jsonl, existing_keys, read_jsonl, write_jsonl


def test_roundtrip_preserves_unicode(tmp_path):
    p = tmp_path / "a" / "b.jsonl"
    append_jsonl(p, {"id": 1, "text": "雨夜"})
    append_jsonl(p, {"id": 2, "text": "霓虹"})
    assert list(read_jsonl(p)) == [{"id": 1, "text": "雨夜"}, {"id": 2, "text": "霓虹"}]
    assert "雨夜" in p.read_text(encoding="utf-8")  # 不是 \uXXXX


def test_read_missing_file_yields_nothing(tmp_path):
    assert list(read_jsonl(tmp_path / "nope.jsonl")) == []


def test_write_overwrites_and_returns_count(tmp_path):
    p = tmp_path / "x.jsonl"
    append_jsonl(p, {"id": 9})
    n = write_jsonl(p, [{"id": 1}, {"id": 2}])
    assert n == 2
    assert [r["id"] for r in read_jsonl(p)] == [1, 2]


def test_existing_keys(tmp_path):
    p = tmp_path / "x.jsonl"
    write_jsonl(p, [{"k": "a"}, {"k": "b"}, {"other": 1}])
    assert existing_keys(p, "k") == {"a", "b"}
```

`scripts/tests/test_ratelimit.py`:
```python
import pytest

from pipeline.ratelimit import RateLimiter, retry


def test_rate_limiter_sleeps_only_when_called_too_soon():
    clock = [100.0]
    slept: list[float] = []
    rl = RateLimiter(1.0, sleep=slept.append, now=lambda: clock[0])
    rl.wait()  # 第一次不等
    clock[0] += 0.3
    rl.wait()  # 太快 → 等 0.7
    clock[0] += 5
    rl.wait()  # 夠久 → 不等
    assert [round(s, 3) for s in slept] == [0.7]


def test_retry_succeeds_after_transient_failures():
    calls = {"n": 0}
    slept: list[float] = []

    def flaky():
        calls["n"] += 1
        if calls["n"] < 3:
            raise TimeoutError("boom")
        return "ok"

    out = retry(flaky, attempts=5, base_delay_s=1.0,
                should_retry=lambda e: isinstance(e, TimeoutError), sleep=slept.append)
    assert out == "ok"
    assert calls["n"] == 3
    assert slept == [1.0, 2.0]  # 指數退避


def test_retry_gives_up_and_reraises():
    def always():
        raise TimeoutError("boom")

    with pytest.raises(TimeoutError):
        retry(always, attempts=3, base_delay_s=0.0,
              should_retry=lambda e: True, sleep=lambda s: None)


def test_retry_does_not_retry_non_matching_errors():
    calls = {"n": 0}

    def bad():
        calls["n"] += 1
        raise ValueError("nope")

    with pytest.raises(ValueError):
        retry(bad, attempts=5, base_delay_s=0.0,
              should_retry=lambda e: isinstance(e, TimeoutError), sleep=lambda s: None)
    assert calls["n"] == 1
```

- [ ] **Step 2: 跑測試確認失敗**

Run: `python -m pytest tests/test_jsonl.py tests/test_ratelimit.py -v`
Expected: FAIL — ModuleNotFoundError（兩個模組都不存在）

- [ ] **Step 3: 實作**

`scripts/pipeline/jsonl.py`:
```python
"""jsonl 讀寫。所有階段的落地格式統一經過這裡。"""

from __future__ import annotations

import json
from collections.abc import Iterable, Iterator
from pathlib import Path


def read_jsonl(path: Path) -> Iterator[dict]:
    if not path.exists():
        return
    with path.open(encoding="utf-8") as fh:
        for line in fh:
            line = line.strip()
            if line:
                yield json.loads(line)


def append_jsonl(path: Path, record: dict) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("a", encoding="utf-8") as fh:
        fh.write(json.dumps(record, ensure_ascii=False) + "\n")


def write_jsonl(path: Path, records: Iterable[dict]) -> int:
    path.parent.mkdir(parents=True, exist_ok=True)
    n = 0
    with path.open("w", encoding="utf-8") as fh:
        for r in records:
            fh.write(json.dumps(r, ensure_ascii=False) + "\n")
            n += 1
    return n


def existing_keys(path: Path, key: str) -> set:
    return {r[key] for r in read_jsonl(path) if key in r}
```

`scripts/pipeline/ratelimit.py`:
```python
"""最小間隔節流與指數退避重試。時間函式可注入，測試不用真的 sleep。"""

from __future__ import annotations

import time
from collections.abc import Callable
from typing import TypeVar

T = TypeVar("T")


class RateLimiter:
    def __init__(
        self,
        min_interval_s: float,
        sleep: Callable[[float], None] = time.sleep,
        now: Callable[[], float] = time.monotonic,
    ):
        self._min = min_interval_s
        self._sleep = sleep
        self._now = now
        self._last: float | None = None

    def wait(self) -> None:
        if self._last is not None:
            elapsed = self._now() - self._last
            if elapsed < self._min:
                self._sleep(self._min - elapsed)
        self._last = self._now()


def retry(
    fn: Callable[[], T],
    *,
    attempts: int = 5,
    base_delay_s: float = 2.0,
    should_retry: Callable[[Exception], bool],
    sleep: Callable[[float], None] = time.sleep,
) -> T:
    for i in range(attempts):
        try:
            return fn()
        except Exception as e:  # noqa: BLE001 - 由 should_retry 決定
            if i == attempts - 1 or not should_retry(e):
                raise
            sleep(base_delay_s * (2**i))
    raise AssertionError("unreachable")
```

- [ ] **Step 4: 跑測試確認通過**

Run: `python -m pytest tests/test_jsonl.py tests/test_ratelimit.py -v`
Expected: 8 passed

- [ ] **Step 5: Commit**

```bash
git add scripts/pipeline/jsonl.py scripts/pipeline/ratelimit.py scripts/tests/test_jsonl.py scripts/tests/test_ratelimit.py
git commit -m "feat(pipeline): jsonl helpers, rate limiter, retry

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 5: 階段 1 — Civitai client 與 fetch

**Files:**
- Create: `scripts/pipeline/civitai_client.py`
- Create: `scripts/pipeline/fetch_civitai.py`
- Create: `scripts/tests/test_civitai_client.py`
- Create: `scripts/tests/test_fetch.py`

**Interfaces:**
- Consumes: `RateLimiter`、`retry`、`append_jsonl`、`read_jsonl`、`RAW_DIR`、`settings.civitai_min_interval_s`
- Produces:
  - `pipeline.civitai_client.CivitaiClient(http: httpx.Client, limiter: RateLimiter, sleep=time.sleep)`，方法 `iter_images(*, limit=200, cursor: str | None = None, base_models: list[str] | None = None) -> Iterator[tuple[dict, str | None]]`（每次 yield `(item, next_cursor_after_this_page)`）
  - `pipeline.fetch_civitai.run_fetch(client, *, max_items: int, out_path: Path, state_path: Path) -> int`（回本次新增筆數）
  - 檔案：`data/raw/images.jsonl`、`data/raw/state.json`（`{"cursor": str|null, "fetched": int, "done": bool}`）

- [ ] **Step 1: 寫失敗測試 — client**

`scripts/tests/test_civitai_client.py`:
```python
import httpx

from pipeline.civitai_client import CivitaiClient
from pipeline.ratelimit import RateLimiter


def _page(ids, next_cursor):
    return {
        "items": [{"id": i, "nsfwLevel": "None", "meta": {"prompt": f"p{i}"}} for i in ids],
        "metadata": {"nextCursor": next_cursor} if next_cursor else {},
    }


def _client(handler):
    http = httpx.Client(transport=httpx.MockTransport(handler), base_url="https://civitai.com")
    return CivitaiClient(http, RateLimiter(0, sleep=lambda s: None), sleep=lambda s: None)


def test_iter_images_follows_cursor_and_sends_required_params():
    seen_params = []

    def handler(req: httpx.Request) -> httpx.Response:
        seen_params.append(dict(req.url.params))
        cursor = req.url.params.get("cursor")
        if cursor is None:
            return httpx.Response(200, json=_page([1, 2], "c2"))
        return httpx.Response(200, json=_page([3], None))

    items = list(_client(handler).iter_images(limit=2))
    assert [it["id"] for it, _ in items] == [1, 2, 3]
    assert [c for _, c in items] == ["c2", "c2", None]
    p = seen_params[0]
    assert p["nsfw"] == "None" and p["withMeta"] == "true" and p["type"] == "image"
    assert p["sort"] == "Most Reactions" and p["limit"] == "2"
    assert seen_params[1]["cursor"] == "c2"


def test_iter_images_retries_on_429_then_succeeds():
    calls = {"n": 0}

    def handler(req: httpx.Request) -> httpx.Response:
        calls["n"] += 1
        if calls["n"] == 1:
            return httpx.Response(429, json={"error": "slow down"})
        return httpx.Response(200, json=_page([7], None))

    items = list(_client(handler).iter_images())
    assert [it["id"] for it, _ in items] == [7]
    assert calls["n"] == 2


def test_iter_images_passes_base_models_csv():
    captured = {}

    def handler(req: httpx.Request) -> httpx.Response:
        captured.update(dict(req.url.params))
        return httpx.Response(200, json=_page([], None))

    list(_client(handler).iter_images(base_models=["Illustrious", "SDXL 1.0"]))
    assert captured["baseModels"] == "Illustrious,SDXL 1.0"
```

- [ ] **Step 2: 寫失敗測試 — fetch 階段（含斷點續傳）**

`scripts/tests/test_fetch.py`:
```python
import json

from pipeline.fetch_civitai import run_fetch
from pipeline.jsonl import read_jsonl


class FakeClient:
    """模擬 CivitaiClient.iter_images：依 cursor 回不同批次。"""

    def __init__(self, pages):
        self.pages = pages  # {cursor_or_None: (items, next_cursor)}
        self.calls = []

    def iter_images(self, *, limit=200, cursor=None, base_models=None):
        self.calls.append(cursor)
        while True:
            items, nxt = self.pages[cursor]
            for it in items:
                yield it, nxt
            if nxt is None:
                return
            cursor = nxt


def test_fetch_writes_raw_and_state(tmp_path):
    client = FakeClient({None: ([{"id": 1}, {"id": 2}], "c2"), "c2": ([{"id": 3}], None)})
    out, state = tmp_path / "images.jsonl", tmp_path / "state.json"
    n = run_fetch(client, max_items=10, out_path=out, state_path=state)
    assert n == 3
    assert [r["id"] for r in read_jsonl(out)] == [1, 2, 3]
    assert json.loads(state.read_text())["done"] is True


def test_fetch_stops_at_max_items_and_records_cursor(tmp_path):
    client = FakeClient({None: ([{"id": 1}, {"id": 2}], "c2"), "c2": ([{"id": 3}], None)})
    out, state = tmp_path / "images.jsonl", tmp_path / "state.json"
    n = run_fetch(client, max_items=2, out_path=out, state_path=state)
    assert n == 2
    s = json.loads(state.read_text())
    assert s == {"cursor": "c2", "fetched": 2, "done": False}


def test_fetch_resumes_from_state_and_skips_duplicates(tmp_path):
    client = FakeClient({None: ([{"id": 1}, {"id": 2}], "c2"), "c2": ([{"id": 2}, {"id": 3}], None)})
    out, state = tmp_path / "images.jsonl", tmp_path / "state.json"
    run_fetch(client, max_items=2, out_path=out, state_path=state)
    n = run_fetch(client, max_items=10, out_path=out, state_path=state)
    assert client.calls[-1] == "c2"  # 從 state 的 cursor 續跑
    assert n == 1  # id=2 重複被略過
    assert [r["id"] for r in read_jsonl(out)] == [1, 2, 3]


def test_fetch_noop_when_done(tmp_path):
    client = FakeClient({None: ([{"id": 1}], None)})
    out, state = tmp_path / "images.jsonl", tmp_path / "state.json"
    run_fetch(client, max_items=10, out_path=out, state_path=state)
    assert run_fetch(client, max_items=10, out_path=out, state_path=state) == 0
    assert client.calls == [None]
```

- [ ] **Step 3: 跑測試確認失敗**

Run: `python -m pytest tests/test_civitai_client.py tests/test_fetch.py -v`
Expected: FAIL — ModuleNotFoundError

- [ ] **Step 4: 實作 civitai_client.py**

`scripts/pipeline/civitai_client.py`:
```python
"""Civitai 公開 REST API：GET /api/v1/images，cursor 分頁。
匿名呼叫被平台壓在公開瀏覽等級，再加 nsfw=None 只取純 SFW。"""

from __future__ import annotations

import time
from collections.abc import Callable, Iterator

import httpx

from pipeline.ratelimit import RateLimiter, retry

BASE_URL = "https://civitai.com"
IMAGES_PATH = "/api/v1/images"


def _is_transient(e: Exception) -> bool:
    if isinstance(e, httpx.HTTPStatusError):
        return e.response.status_code in (429, 500, 502, 503, 504)
    return isinstance(e, (httpx.TimeoutException, httpx.TransportError))


class CivitaiClient:
    def __init__(
        self,
        http: httpx.Client,
        limiter: RateLimiter,
        sleep: Callable[[float], None] = time.sleep,
    ):
        self._http = http
        self._limiter = limiter
        self._sleep = sleep

    def _get_page(self, params: dict) -> dict:
        def call() -> dict:
            self._limiter.wait()
            r = self._http.get(IMAGES_PATH, params=params, timeout=30)
            r.raise_for_status()
            return r.json()

        return retry(call, attempts=6, base_delay_s=3.0, should_retry=_is_transient, sleep=self._sleep)

    def iter_images(
        self,
        *,
        limit: int = 200,
        cursor: str | None = None,
        base_models: list[str] | None = None,
    ) -> Iterator[tuple[dict, str | None]]:
        params: dict = {
            "limit": limit,
            "nsfw": "None",
            "withMeta": "true",
            "type": "image",
            "sort": "Most Reactions",
            "period": "AllTime",
        }
        if base_models:
            params["baseModels"] = ",".join(base_models)
        while True:
            if cursor is not None:
                params["cursor"] = cursor
            page = self._get_page(params)
            next_cursor = page.get("metadata", {}).get("nextCursor") or None
            for item in page.get("items", []):
                yield item, next_cursor
            if next_cursor is None:
                return
            cursor = next_cursor


def default_client(min_interval_s: float) -> CivitaiClient:
    http = httpx.Client(base_url=BASE_URL, headers={"User-Agent": "GenAIPromptCopilot-pipeline/0.1"})
    return CivitaiClient(http, RateLimiter(min_interval_s))
```

- [ ] **Step 5: 實作 fetch_civitai.py**

`scripts/pipeline/fetch_civitai.py`:
```python
"""階段 1：拉 Civitai 圖片 metadata 到 data/raw/images.jsonl，state.json 記 cursor 供續跑。"""

from __future__ import annotations

import argparse
import json
from pathlib import Path

from pipeline.civitai_client import default_client
from pipeline.config import RAW_DIR, settings
from pipeline.jsonl import append_jsonl, existing_keys

RAW_PATH = RAW_DIR / "images.jsonl"
STATE_PATH = RAW_DIR / "state.json"


def _load_state(path: Path) -> dict:
    if path.exists():
        return json.loads(path.read_text(encoding="utf-8"))
    return {"cursor": None, "fetched": 0, "done": False}


def _save_state(path: Path, state: dict) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(state), encoding="utf-8")


def run_fetch(client, *, max_items: int, out_path: Path, state_path: Path,
              base_models: list[str] | None = None) -> int:
    state = _load_state(state_path)
    if state["done"]:
        return 0
    seen = existing_keys(out_path, "id")
    added = 0
    last_cursor = state["cursor"]
    for item, next_cursor in client.iter_images(cursor=state["cursor"], base_models=base_models):
        if item["id"] not in seen:
            append_jsonl(out_path, item)
            seen.add(item["id"])
            added += 1
        last_cursor = next_cursor
        if added >= max_items:
            _save_state(state_path, {"cursor": last_cursor, "fetched": len(seen), "done": last_cursor is None})
            return added
    _save_state(state_path, {"cursor": None, "fetched": len(seen), "done": True})
    return added


def main(argv: list[str] | None = None) -> None:
    ap = argparse.ArgumentParser(description="階段 1：拉取 Civitai 圖片 metadata")
    ap.add_argument("--max-items", type=int, default=3000, help="本次最多新增筆數")
    ap.add_argument("--base-models", default="", help="逗號分隔，如 'Illustrious,SDXL 1.0'；空=不過濾")
    args = ap.parse_args(argv)
    base_models = [b.strip() for b in args.base_models.split(",") if b.strip()] or None
    client = default_client(settings.civitai_min_interval_s)
    n = run_fetch(client, max_items=args.max_items, out_path=RAW_PATH, state_path=STATE_PATH,
                  base_models=base_models)
    print(f"fetch: +{n} → {RAW_PATH}")


if __name__ == "__main__":
    main()
```

- [ ] **Step 6: 跑測試確認通過**

Run: `python -m pytest tests/test_civitai_client.py tests/test_fetch.py -v`
Expected: 7 passed

- [ ] **Step 7: 真打一次小批量驗證 API 契約**

Run（在 `scripts/`）: `python -m pipeline.fetch_civitai --max-items 20`
Expected: `fetch: +20 → ...raw/images.jsonl`

Run: `python -c "from pipeline.jsonl import read_jsonl; from pipeline.config import RAW_DIR; rs=list(read_jsonl(RAW_DIR/'images.jsonl')); print(len(rs)); print({k for r in rs for k in r}); print(sum(1 for r in rs if r.get('meta') and r['meta'].get('prompt')), 'with prompt')"`
Expected: 20；欄位含 `id, url, nsfwLevel, meta, baseModel`；至少大半有 prompt。

若 `meta` 大多為 null，把 `withMeta` 拿掉觀察差異——這一步是在驗證文件與實際行為一致。

- [ ] **Step 8: Commit**

```bash
git add scripts/pipeline/civitai_client.py scripts/pipeline/fetch_civitai.py scripts/tests/test_civitai_client.py scripts/tests/test_fetch.py
git commit -m "feat(pipeline): civitai client with cursor pagination and resumable fetch stage

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 6: 階段 2 — clean（去重、NSFW、正規化）

**Files:**
- Create: `scripts/pipeline/nsfw_filter.py`
- Create: `scripts/pipeline/clean.py`
- Create: `scripts/tests/test_clean.py`

**Interfaces:**
- Consumes: `read_jsonl`、`write_jsonl`、`RAW_DIR`、`CLEAN_DIR`
- Produces:
  - `pipeline.nsfw_filter.is_nsfw_text(text: str) -> bool`
  - `pipeline.clean.normalize_prompt(text: str) -> str`（去 `<lora:...>` 類標記、壓空白與多餘逗號）
  - `pipeline.clean.clean_records(raw: Iterable[dict]) -> list[dict]`（輸出契約見「各階段資料契約」）
  - 檔案：`data/clean/records.jsonl`

- [ ] **Step 1: 寫失敗測試**

`scripts/tests/test_clean.py`:
```python
from pipeline.clean import clean_records, normalize_prompt
from pipeline.nsfw_filter import is_nsfw_text


def _raw(i, prompt, *, neg="lowres", level="None", meta=True, url="https://x/{}.png", likes=5):
    item = {"id": i, "url": url.format(i), "width": 832, "height": 1216, "nsfwLevel": level,
            "baseModel": "Illustrious", "stats": {"likeCount": likes}}
    if meta:
        item["meta"] = {"prompt": prompt, "negativePrompt": neg}
    else:
        item["meta"] = None
    return item


def test_normalize_strips_lora_tokens_and_collapses_separators():
    s = normalize_prompt("masterpiece,  <lora:foo:0.8> 1girl ,, rain  night <lyco:bar:1>")
    assert s == "masterpiece, 1girl, rain night"


def test_nsfw_keyword_filter_is_case_insensitive_and_word_bounded():
    assert is_nsfw_text("1girl, NUDE, beach")
    assert is_nsfw_text("explicit content")
    assert not is_nsfw_text("nudge the camera")  # 'nude' 不能匹配 'nudge'
    assert not is_nsfw_text("1girl, city, rain")


def test_clean_drops_missing_meta_short_nsfw_level_and_keyword_hits():
    raw = [
        _raw(1, "1girl, cyberpunk city street at night, rain, neon signs, looking at viewer"),
        _raw(2, "short", ),
        _raw(3, "1girl, cyberpunk city street at night, rain, neon signs, looking at viewer", meta=False),
        _raw(4, "1girl, cyberpunk city street at night, rain, neon signs, looking at viewer", level="Soft"),
        _raw(5, "1girl, nude, beach, sunset, looking at viewer, masterpiece, best quality"),
    ]
    out = clean_records(raw)
    assert [r["source_id"] for r in out] == [1]


def test_clean_dedupes_by_normalized_prompt_keeping_most_liked():
    p = "1girl, cyberpunk city street at night, rain, neon signs, looking at viewer"
    raw = [_raw(1, p, likes=3), _raw(2, "  " + p.upper() + " ", likes=9)]
    out = clean_records(raw)
    assert [r["source_id"] for r in out] == [2]
    assert out[0]["prompt_hash"] == clean_records([_raw(1, p)])[0]["prompt_hash"]


def test_clean_drops_mostly_non_ascii_prompts():
    raw = [_raw(1, "一個女孩站在雨夜的城市街道上，霓虹燈，看著觀眾，傑作，最高品質")]
    assert clean_records(raw) == []


def test_clean_output_contract():
    raw = [_raw(1, "1girl, cyberpunk city street at night, rain, neon signs, looking at viewer")]
    r = clean_records(raw)[0]
    assert set(r) == {"source_id", "prompt", "negative_prompt", "image_url", "width", "height",
                      "base_model", "like_count", "prompt_hash"}
    assert r["negative_prompt"] == "lowres"
```

- [ ] **Step 2: 跑測試確認失敗**

Run: `python -m pytest tests/test_clean.py -v`
Expected: FAIL — ModuleNotFoundError

- [ ] **Step 3: 實作 nsfw_filter.py**

`scripts/pipeline/nsfw_filter.py`:
```python
"""關鍵詞層 NSFW 過濾。Civitai 的 nsfw=None 與 nsfwLevel 是第一、二層，這是第三層。
清單刻意保守：寧可誤殺，不可漏放——知識庫乾淨是「合規」賣點的前提。"""

from __future__ import annotations

import re

NSFW_KEYWORDS: frozenset[str] = frozenset({
    "nsfw", "nude", "naked", "topless", "bottomless", "nipples", "areola", "nipple",
    "sex", "sexual", "explicit", "porn", "pornographic", "hentai", "erotic", "erotica",
    "penis", "vagina", "pussy", "cum", "orgasm", "masturbation", "fellatio", "cunnilingus",
    "bondage", "bdsm", "lingerie", "underwear only", "see-through", "cameltoe", "ahegao",
    "loli", "shota", "gore", "guro", "dismemberment",
})

_WORD_RE = re.compile(r"[a-z][a-z\-]*")


def is_nsfw_text(text: str) -> bool:
    lowered = text.lower()
    tokens = set(_WORD_RE.findall(lowered))
    if tokens & NSFW_KEYWORDS:
        return True
    return any(" " in kw and kw in lowered for kw in NSFW_KEYWORDS)
```

- [ ] **Step 4: 實作 clean.py**

`scripts/pipeline/clean.py`:
```python
"""階段 2：raw → clean。去掉沒 meta、太短、非英文、NSFW 的紀錄；正規化並依 prompt 去重。"""

from __future__ import annotations

import argparse
import hashlib
import re
from collections.abc import Iterable

from pipeline.config import CLEAN_DIR, RAW_DIR
from pipeline.jsonl import read_jsonl, write_jsonl
from pipeline.nsfw_filter import is_nsfw_text

CLEAN_PATH = CLEAN_DIR / "records.jsonl"
MIN_PROMPT_CHARS = 20
MAX_PROMPT_CHARS = 2000
MAX_NON_ASCII_RATIO = 0.2

_ANGLE_TOKEN_RE = re.compile(r"<[^<>]*>")  # <lora:..>, <lyco:..>, <embedding:..>
_MULTI_COMMA_RE = re.compile(r"(\s*,\s*)+")
_MULTI_SPACE_RE = re.compile(r"\s+")


def normalize_prompt(text: str) -> str:
    text = _ANGLE_TOKEN_RE.sub(" ", text)
    text = _MULTI_SPACE_RE.sub(" ", text)
    text = _MULTI_COMMA_RE.sub(", ", text)
    return text.strip(" ,")


def _prompt_hash(normalized: str) -> str:
    return hashlib.sha1(normalized.lower().encode("utf-8")).hexdigest()


def _non_ascii_ratio(text: str) -> float:
    if not text:
        return 1.0
    return sum(1 for ch in text if ord(ch) > 127) / len(text)


def _to_record(item: dict) -> dict | None:
    meta = item.get("meta") or {}
    prompt = meta.get("prompt")
    if not prompt or item.get("nsfwLevel") != "None":
        return None
    prompt = normalize_prompt(prompt)
    if not (MIN_PROMPT_CHARS <= len(prompt) <= MAX_PROMPT_CHARS):
        return None
    if _non_ascii_ratio(prompt) > MAX_NON_ASCII_RATIO:
        return None
    negative = normalize_prompt(meta.get("negativePrompt") or "")
    if is_nsfw_text(prompt) or is_nsfw_text(negative):
        # 負向詞出現 NSFW 詞很常見（作者在排除），但保守起見一併丟棄
        return None
    return {
        "source_id": item["id"],
        "prompt": prompt,
        "negative_prompt": negative,
        "image_url": item.get("url"),
        "width": item.get("width"),
        "height": item.get("height"),
        "base_model": item.get("baseModel"),
        "like_count": (item.get("stats") or {}).get("likeCount", 0),
        "prompt_hash": _prompt_hash(prompt),
    }


def clean_records(raw: Iterable[dict]) -> list[dict]:
    best: dict[str, dict] = {}
    for item in raw:
        rec = _to_record(item)
        if rec is None:
            continue
        cur = best.get(rec["prompt_hash"])
        if cur is None or rec["like_count"] > cur["like_count"]:
            best[rec["prompt_hash"]] = rec
    return sorted(best.values(), key=lambda r: r["source_id"])


def main(argv: list[str] | None = None) -> None:
    argparse.ArgumentParser(description="階段 2：清洗與去重").parse_args(argv)
    raw = list(read_jsonl(RAW_DIR / "images.jsonl"))
    out = clean_records(raw)
    n = write_jsonl(CLEAN_PATH, out)
    print(f"clean: {len(raw)} → {n} → {CLEAN_PATH}")


if __name__ == "__main__":
    main()
```

- [ ] **Step 5: 跑測試確認通過**

Run: `python -m pytest tests/test_clean.py -v`
Expected: 6 passed

- [ ] **Step 6: 用 Task 5 抓下來的 20 筆實跑一次**

Run: `python -m pipeline.clean`
Expected: `clean: 20 → N → ...clean/records.jsonl`，N 在 5–18 之間屬正常（沒 meta 與重複會被丟）。若 N 為 0，檢查 raw 的 `nsfwLevel` 實際值與 `meta` 是否為 null。

- [ ] **Step 7: Commit**

```bash
git add scripts/pipeline/nsfw_filter.py scripts/pipeline/clean.py scripts/tests/test_clean.py
git commit -m "feat(pipeline): clean stage with normalization, dedupe, and NSFW keyword filter

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 7: Gemini client 包裝（結構化生成 + 批次 embedding）

**Files:**
- Create: `scripts/pipeline/gemini_client.py`
- Create: `scripts/tests/test_gemini_client.py`

**Interfaces:**
- Consumes: `RateLimiter`、`retry`、`settings.*`
- Produces:
  - `pipeline.gemini_client.GeminiClient(sdk, *, structure_model, embedding_model, dimensions, limiter, sleep=time.sleep)`；`sdk` 為 `google.genai.Client` 或任何有 `.models.generate_content(...)` / `.models.embed_content(...)` 的物件
  - `generate_structured(prompt: str, schema: type[BaseModel], *, temperature=0.2) -> BaseModel`
  - `embed_batch(texts: list[str], *, task_type: "RETRIEVAL_DOCUMENT" | "RETRIEVAL_QUERY") -> list[list[float]]`（每筆已 L2 normalize；自動切成 ≤ `BATCH_SIZE`=32 的子批）
  - `pipeline.gemini_client.default_client() -> GeminiClient`（真的建立 `genai.Client(api_key=settings.gemini_api_key)`）

- [ ] **Step 1: 寫失敗測試**

`scripts/tests/test_gemini_client.py`:
```python
import math
from types import SimpleNamespace

import pytest
from pydantic import BaseModel

from pipeline.gemini_client import BATCH_SIZE, GeminiClient
from pipeline.ratelimit import RateLimiter


class Out(BaseModel):
    title: str
    n: int


class FakeModels:
    def __init__(self):
        self.generate_calls = []
        self.embed_calls = []
        self.fail_first_generate = False

    def generate_content(self, *, model, contents, config):
        self.generate_calls.append((model, contents, config))
        if self.fail_first_generate and len(self.generate_calls) == 1:
            raise _api_error(429)
        return SimpleNamespace(text='{"title": "雨夜", "n": 3}')

    def embed_content(self, *, model, contents, config):
        self.embed_calls.append((model, list(contents), config))
        return SimpleNamespace(embeddings=[SimpleNamespace(values=[3.0, 4.0]) for _ in contents])


def _api_error(code):
    from google.genai import errors
    return errors.APIError(code, {"error": {"message": "rate limited", "status": "RESOURCE_EXHAUSTED"}})


def _client(models):
    sdk = SimpleNamespace(models=models)
    return GeminiClient(sdk, structure_model="m-struct", embedding_model="m-embed", dimensions=2,
                        limiter=RateLimiter(0, sleep=lambda s: None), sleep=lambda s: None)


def test_generate_structured_parses_into_schema_and_sets_json_config():
    models = FakeModels()
    out = _client(models).generate_structured("hi", Out)
    assert out == Out(title="雨夜", n=3)
    model, contents, config = models.generate_calls[0]
    assert model == "m-struct" and contents == "hi"
    assert config.response_mime_type == "application/json"
    assert config.response_schema is Out


def test_generate_structured_retries_on_429():
    models = FakeModels()
    models.fail_first_generate = True
    assert _client(models).generate_structured("hi", Out).n == 3
    assert len(models.generate_calls) == 2


def test_embed_batch_chunks_normalizes_and_passes_task_type():
    models = FakeModels()
    texts = [f"t{i}" for i in range(BATCH_SIZE + 5)]
    vecs = _client(models).embed_batch(texts, task_type="RETRIEVAL_DOCUMENT")
    assert len(vecs) == len(texts)
    assert [len(c[1]) for c in models.embed_calls] == [BATCH_SIZE, 5]
    assert models.embed_calls[0][2].task_type == "RETRIEVAL_DOCUMENT"
    assert models.embed_calls[0][2].output_dimensionality == 2
    assert math.isclose(sum(v * v for v in vecs[0]), 1.0, rel_tol=1e-6)  # [3,4] → [0.6,0.8]


def test_embed_batch_empty_is_noop():
    models = FakeModels()
    assert _client(models).embed_batch([], task_type="RETRIEVAL_QUERY") == []
    assert models.embed_calls == []


def test_default_client_requires_api_key(monkeypatch):
    from pipeline import gemini_client
    monkeypatch.setattr(gemini_client.settings, "gemini_api_key", "")
    with pytest.raises(RuntimeError, match="GEMINI_API_KEY"):
        gemini_client.default_client()
```

- [ ] **Step 2: 跑測試確認失敗**

Run: `python -m pytest tests/test_gemini_client.py -v`
Expected: FAIL — ModuleNotFoundError

- [ ] **Step 3: 實作 gemini_client.py**

`scripts/pipeline/gemini_client.py`:
```python
"""Gemini 呼叫的唯一出口：結構化生成與批次 embedding，含節流與 429/5xx 重試。
SDK 物件可注入，測試不打網路。"""

from __future__ import annotations

import math
import time
from collections.abc import Callable
from typing import Literal, TypeVar

from google import genai
from google.genai import errors, types
from pydantic import BaseModel

from pipeline.config import settings
from pipeline.ratelimit import RateLimiter, retry

BATCH_SIZE = 32
TaskType = Literal["RETRIEVAL_DOCUMENT", "RETRIEVAL_QUERY"]
M = TypeVar("M", bound=BaseModel)


def _is_transient(e: Exception) -> bool:
    return isinstance(e, errors.APIError) and e.code in (429, 500, 502, 503, 504)


def _l2_normalize(v: list[float]) -> list[float]:
    norm = math.sqrt(sum(x * x for x in v)) or 1.0
    return [x / norm for x in v]


class GeminiClient:
    def __init__(
        self,
        sdk,
        *,
        structure_model: str,
        embedding_model: str,
        dimensions: int,
        limiter: RateLimiter,
        sleep: Callable[[float], None] = time.sleep,
    ):
        self._sdk = sdk
        self._structure_model = structure_model
        self._embedding_model = embedding_model
        self._dimensions = dimensions
        self._limiter = limiter
        self._sleep = sleep

    def _call(self, fn):
        def wrapped():
            self._limiter.wait()
            return fn()

        return retry(wrapped, attempts=6, base_delay_s=4.0, should_retry=_is_transient, sleep=self._sleep)

    def generate_structured(self, prompt: str, schema: type[M], *, temperature: float = 0.2) -> M:
        config = types.GenerateContentConfig(
            response_mime_type="application/json",
            response_schema=schema,
            temperature=temperature,
        )
        resp = self._call(
            lambda: self._sdk.models.generate_content(
                model=self._structure_model, contents=prompt, config=config
            )
        )
        return schema.model_validate_json(resp.text)

    def _embed_chunk(self, chunk: list[str], task_type: TaskType) -> list[list[float]]:
        config = types.EmbedContentConfig(task_type=task_type, output_dimensionality=self._dimensions)
        resp = self._call(
            lambda: self._sdk.models.embed_content(
                model=self._embedding_model, contents=chunk, config=config
            )
        )
        return [_l2_normalize(list(e.values)) for e in resp.embeddings]

    def embed_batch(self, texts: list[str], *, task_type: TaskType) -> list[list[float]]:
        out: list[list[float]] = []
        for i in range(0, len(texts), BATCH_SIZE):
            out.extend(self._embed_chunk(texts[i : i + BATCH_SIZE], task_type))
        return out


def default_client() -> GeminiClient:
    if not settings.gemini_api_key:
        raise RuntimeError("GEMINI_API_KEY 未設定（見 .env.example）")
    return GeminiClient(
        genai.Client(api_key=settings.gemini_api_key),
        structure_model=settings.gemini_structure_model,
        embedding_model=settings.gemini_embedding_model,
        dimensions=settings.embedding_dimensions,
        limiter=RateLimiter(settings.gemini_min_interval_s),
    )
```

- [ ] **Step 4: 跑測試確認通過**

Run: `python -m pytest tests/test_gemini_client.py -v`
Expected: 5 passed

若 `errors.APIError(code, {...})` 建構子簽名與安裝版本不符，改用 `errors.APIError(code, {"message": "..."})` 或查 `help(errors.APIError.__init__)` 調整測試的 `_api_error`；實作端只依賴 `.code` 屬性。

- [ ] **Step 5: 真打一次驗證金鑰與模型名**

Run:
```powershell
python -c "from pipeline.gemini_client import default_client; c=default_client(); v=c.embed_batch(['雨夜的城市'], task_type='RETRIEVAL_QUERY'); print(len(v[0]))"
```
Expected: `768`

若 404 model not found：到 https://ai.google.dev/gemini-api/docs/models 確認名稱並改 `.env`。

- [ ] **Step 6: Commit**

```bash
git add scripts/pipeline/gemini_client.py scripts/tests/test_gemini_client.py
git commit -m "feat(pipeline): gemini client wrapper for structured output and batched embeddings

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 8: 階段 3 — structure（LLM 結構化，產 histories 與 presets）

**Files:**
- Create: `scripts/pipeline/structure.py`
- Create: `scripts/tests/test_structure.py`

**Interfaces:**
- Consumes: `GeminiClient.generate_structured`、`FacetCatalog`、`read_jsonl`、`append_jsonl`、`existing_keys`、`CLEAN_DIR`、`STRUCTURED_DIR`
- Produces:
  - Pydantic：`PresetOut`、`StructuredRecord`（LLM 回傳 schema）
  - `pipeline.structure.build_prompt(record: dict, catalog: FacetCatalog) -> str`
  - `pipeline.structure.to_outputs(record: dict, result: StructuredRecord, catalog: FacetCatalog, seen_snippets: set[str]) -> tuple[dict, list[dict]]`（history 一筆、presets 多筆；已過濾非法 facet id、空 snippet、重複 snippet）
  - `pipeline.structure.run_structure(client, catalog, *, in_path, histories_path, presets_path, max_records: int | None = None) -> tuple[int, int]`（新增的 histories 數、presets 數；以 `source_ref` 續跑）
  - 檔案：`data/structured/histories.jsonl`、`data/structured/presets.jsonl`

- [ ] **Step 1: 寫失敗測試**

`scripts/tests/test_structure.py`:
```python
from pipeline.config import FACETS_PATH
from pipeline.facets import load_facets
from pipeline.jsonl import read_jsonl, write_jsonl
from pipeline.structure import PresetOut, StructuredRecord, build_prompt, run_structure, to_outputs

CAT = load_facets(FACETS_PATH)
REC = {"source_id": 12345, "prompt": "1girl, cyberpunk city, rain, neon, looking at viewer, masterpiece",
       "negative_prompt": "lowres, bad anatomy", "image_url": "https://x/1.png", "width": 832,
       "height": 1216, "base_model": "Illustrious", "like_count": 10, "prompt_hash": "h"}


def _result(**kw):
    base = dict(
        user_intent="雨夜霓虹城市裡看著鏡頭的少女",
        subject_profile="portrait",
        presets=[
            PresetOut(title="賽博龐克雨夜", category="Scene", description="昏暗雨夜城市與霓虹",
                      tags=["cyberpunk", "rain", "neon"],
                      facet_ids=["scene.location", "scene.weather", "not.a.facet"],
                      prompt_snippet="cyberpunk city, rain, neon", negative_snippet=None),
            PresetOut(title="空的", category="Style", description="x", tags=[], facet_ids=[],
                      prompt_snippet="   ", negative_snippet=None),
        ],
    )
    base.update(kw)
    return StructuredRecord(**base)


def test_build_prompt_contains_record_and_facet_listing():
    p = build_prompt(REC, CAT)
    assert REC["prompt"] in p and REC["negative_prompt"] in p
    assert "scene.weather" in p and "clothing.footwear" in p
    assert "繁體中文" in p


def test_to_outputs_filters_invalid_facets_and_empty_snippets():
    hist, presets = to_outputs(REC, _result(), CAT, seen_snippets=set())
    assert hist["source_ref"] == "civitai:12345"
    assert hist["positive_prompt"] == REC["prompt"] and hist["subject_profile"] == "portrait"
    assert hist["image_url"] == "https://x/1.png"
    assert len(presets) == 1
    assert presets[0]["source_ref"] == "civitai:12345:0"
    assert presets[0]["facet_ids"] == ["scene.location", "scene.weather"]


def test_to_outputs_dedupes_snippets_across_records():
    seen: set[str] = set()
    _, first = to_outputs(REC, _result(), CAT, seen_snippets=seen)
    _, second = to_outputs({**REC, "source_id": 2}, _result(), CAT, seen_snippets=seen)
    assert len(first) == 1 and second == []


def test_to_outputs_drops_preset_with_no_valid_facets():
    r = _result(presets=[PresetOut(title="t", category="Scene", description="d", tags=["a"],
                                   facet_ids=["bogus"], prompt_snippet="ok snippet",
                                   negative_snippet=None)])
    _, presets = to_outputs(REC, r, CAT, seen_snippets=set())
    assert presets == []


class FakeGemini:
    def __init__(self):
        self.calls = 0

    def generate_structured(self, prompt, schema, *, temperature=0.2):
        self.calls += 1
        return _result()


def test_run_structure_resumes_and_respects_max(tmp_path):
    inp = tmp_path / "records.jsonl"
    write_jsonl(inp, [REC, {**REC, "source_id": 2}, {**REC, "source_id": 3}])
    h, p = tmp_path / "h.jsonl", tmp_path / "p.jsonl"
    g = FakeGemini()
    assert run_structure(g, CAT, in_path=inp, histories_path=h, presets_path=p, max_records=2) == (2, 1)
    assert g.calls == 2
    assert run_structure(g, CAT, in_path=inp, histories_path=h, presets_path=p) == (1, 0)
    assert g.calls == 3
    assert [r["source_ref"] for r in read_jsonl(h)] == ["civitai:12345", "civitai:2", "civitai:3"]
```

- [ ] **Step 2: 跑測試確認失敗**

Run: `python -m pytest tests/test_structure.py -v`
Expected: FAIL — ModuleNotFoundError

- [ ] **Step 3: 實作 structure.py**

`scripts/pipeline/structure.py`:
```python
"""階段 3：clean → structured。每筆 prompt 由 Gemini 產出
(a) 一筆完整紀錄（繁中 user_intent + profile）餵 RAG 1，
(b) 多筆片段（繁中 title/description + facet_ids + 英文 snippet）餵 RAG 2。"""

from __future__ import annotations

import argparse
import hashlib
from pathlib import Path
from typing import Literal

from pydantic import BaseModel, Field

from pipeline.config import CLEAN_DIR, FACETS_PATH, STRUCTURED_DIR
from pipeline.facets import FacetCatalog, load_facets
from pipeline.jsonl import append_jsonl, existing_keys, read_jsonl

HISTORIES_PATH = STRUCTURED_DIR / "histories.jsonl"
PRESETS_PATH = STRUCTURED_DIR / "presets.jsonl"

Profile = Literal["portrait", "landscape", "object", "vehicle"]
Category = Literal["Style", "Scene", "Camera", "Appearance", "Pose", "Clothing", "Combined"]


class PresetOut(BaseModel):
    title: str = Field(description="繁體中文，10 字內")
    category: Category
    description: str = Field(description="繁體中文，一到兩句模糊自然語言描述，供語意檢索")
    tags: list[str] = Field(description="3-8 個英文小寫標籤")
    facet_ids: list[str] = Field(description="對應的 facet id，只能用清單裡的")
    prompt_snippet: str = Field(description="從原 prompt 擷取的英文片段，逗號分隔")
    negative_snippet: str | None = Field(description="與此片段風格相關的英文負向詞；無則 null")


class StructuredRecord(BaseModel):
    user_intent: str = Field(description="繁體中文，一到兩句話描述這張圖的需求，像使用者會說的話")
    subject_profile: Profile
    presets: list[PresetOut] = Field(description="1-6 個可重用片段")


PROMPT_TEMPLATE = """你是生圖提示詞知識庫的整理員。給你一組 Stable Diffusion 風格的英文 prompt，請：

1. 用繁體中文寫一到兩句 user_intent：像一個使用者會對助理說的需求描述（不要逐字翻譯 tag）。
2. 判斷 subject_profile：portrait（有人物）/ landscape（風景）/ object（靜物）/ vehicle（載具）。
3. 從 prompt 擷取 1-6 個「可在其他圖重用」的片段（presets）。每個片段：
   - title：繁體中文，10 字內
   - category：Style / Scene / Camera / Appearance / Pose / Clothing / Combined
   - description：繁體中文一到兩句模糊描述，要讓「昏暗雨夜的科幻城市」這種口語能搜到
   - tags：3-8 個英文小寫標籤
   - facet_ids：只能從下方清單選，選最貼切的 1-4 個
   - prompt_snippet：從原 prompt 擷取的英文 tag，逗號分隔；不要改寫、不要翻譯
   - negative_snippet：若此片段有風格專屬負向詞（如動漫風的 "realistic, 3d"）就給，否則 null
   不要把 masterpiece / best quality / highly detailed / lowres / bad anatomy 這類通用畫質詞或通用負向詞當成片段。
   不要把 <lora:...> 或特定模型名稱當成片段。

Facet 清單：
{facets}

原始 prompt：
{prompt}

原始 negative prompt：
{negative}
"""


def build_prompt(record: dict, catalog: FacetCatalog) -> str:
    return PROMPT_TEMPLATE.format(
        facets=catalog.prompt_listing(),
        prompt=record["prompt"],
        negative=record.get("negative_prompt") or "(無)",
    )


def _snippet_key(snippet: str) -> str:
    norm = ", ".join(t.strip().lower() for t in snippet.split(",") if t.strip())
    return hashlib.sha1(norm.encode("utf-8")).hexdigest()


def to_outputs(
    record: dict, result: StructuredRecord, catalog: FacetCatalog, seen_snippets: set[str]
) -> tuple[dict, list[dict]]:
    ref = f"civitai:{record['source_id']}"
    history = {
        "source_ref": ref,
        "user_intent": result.user_intent.strip(),
        "positive_prompt": record["prompt"],
        "negative_prompt": record.get("negative_prompt") or "",
        "subject_profile": result.subject_profile,
        "image_url": record.get("image_url"),
    }
    presets: list[dict] = []
    for p in result.presets:
        snippet = p.prompt_snippet.strip(" ,")
        facet_ids = [f for f in p.facet_ids if f in catalog.all_ids]
        if not snippet or not facet_ids:
            continue
        key = _snippet_key(snippet)
        if key in seen_snippets:
            continue
        seen_snippets.add(key)
        presets.append({
            "source_ref": f"{ref}:{len(presets)}",
            "title": p.title.strip(),
            "category": p.category,
            "description": p.description.strip(),
            "tags": [t.strip().lower() for t in p.tags if t.strip()],
            "facet_ids": facet_ids,
            "prompt_snippet": snippet,
            "negative_snippet": (p.negative_snippet or "").strip() or None,
            "image_url": record.get("image_url"),
        })
    return history, presets


def run_structure(
    client,
    catalog: FacetCatalog,
    *,
    in_path: Path,
    histories_path: Path,
    presets_path: Path,
    max_records: int | None = None,
) -> tuple[int, int]:
    done = existing_keys(histories_path, "source_ref")
    seen_snippets = {_snippet_key(p["prompt_snippet"]) for p in read_jsonl(presets_path)}
    n_hist = n_presets = 0
    for record in read_jsonl(in_path):
        if max_records is not None and n_hist >= max_records:
            break
        if f"civitai:{record['source_id']}" in done:
            continue
        result = client.generate_structured(build_prompt(record, catalog), StructuredRecord)
        history, presets = to_outputs(record, result, catalog, seen_snippets)
        append_jsonl(histories_path, history)
        for p in presets:
            append_jsonl(presets_path, p)
        n_hist += 1
        n_presets += len(presets)
    return n_hist, n_presets


def main(argv: list[str] | None = None) -> None:
    ap = argparse.ArgumentParser(description="階段 3：LLM 結構化")
    ap.add_argument("--max-records", type=int, default=None)
    args = ap.parse_args(argv)
    from pipeline.gemini_client import default_client  # 延遲 import：測試不需要 SDK 金鑰

    h, p = run_structure(
        default_client(), load_facets(FACETS_PATH),
        in_path=CLEAN_DIR / "records.jsonl",
        histories_path=HISTORIES_PATH, presets_path=PRESETS_PATH,
        max_records=args.max_records,
    )
    print(f"structure: +{h} histories, +{p} presets → {STRUCTURED_DIR}")


if __name__ == "__main__":
    main()
```

- [ ] **Step 4: 跑測試確認通過**

Run: `python -m pytest tests/test_structure.py -v`
Expected: 5 passed

- [ ] **Step 5: 真打 5 筆看品質**

Run: `python -m pipeline.structure --max-records 5`
Expected: `structure: +5 histories, +N presets`（N 約 10–25）

Run: `python -c "from pipeline.jsonl import read_jsonl; from pipeline.structure import PRESETS_PATH; [print(r['title'], '|', r['category'], '|', r['facet_ids'], '|', r['prompt_snippet'][:60]) for r in read_jsonl(PRESETS_PATH)]"`

檢查：title/description 是繁中、snippet 是英文原 tag、facet_ids 合理、沒有 masterpiece 之類的片段。若品質差，調 `PROMPT_TEMPLATE` 措辭（這是唯一該調的地方），刪掉 `data/structured/*` 重跑。

- [ ] **Step 6: Commit**

```bash
git add scripts/pipeline/structure.py scripts/tests/test_structure.py
git commit -m "feat(pipeline): structure stage producing histories and facet-tagged presets via Gemini

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 9: 階段 4 — embed（批次向量化、續跑、--reindex）

**Files:**
- Create: `scripts/pipeline/embed.py`
- Create: `scripts/tests/test_embed.py`

**Interfaces:**
- Consumes: `GeminiClient.embed_batch`、`read_jsonl`、`append_jsonl`、`existing_keys`、`write_jsonl`、`STRUCTURED_DIR`、`EMBEDDED_DIR`
- Produces:
  - `pipeline.embed.history_text(r: dict) -> str`（= `user_intent`）
  - `pipeline.embed.preset_text(r: dict) -> str`（= `f"{title}。{description}。標籤：{', '.join(tags)}"`）
  - `pipeline.embed.run_embed(client, *, in_path, out_path, text_fn, reindex: bool = False, chunk: int = 64) -> int`
  - 檔案：`data/embedded/histories.jsonl`、`data/embedded/presets.jsonl`（原紀錄 + `embedding`）

- [ ] **Step 1: 寫失敗測試**

`scripts/tests/test_embed.py`:
```python
from pipeline.embed import history_text, preset_text, run_embed
from pipeline.jsonl import read_jsonl, write_jsonl


class FakeGemini:
    def __init__(self):
        self.batches = []

    def embed_batch(self, texts, *, task_type):
        assert task_type == "RETRIEVAL_DOCUMENT"
        self.batches.append(list(texts))
        return [[float(len(t)), 0.0] for t in texts]


def test_text_builders():
    assert history_text({"user_intent": "雨夜"}) == "雨夜"
    assert preset_text({"title": "T", "description": "D", "tags": ["a", "b"]}) == "T。D。標籤：a, b"


def test_run_embed_writes_embeddings_and_resumes(tmp_path):
    inp, out = tmp_path / "in.jsonl", tmp_path / "out.jsonl"
    write_jsonl(inp, [{"source_ref": "a", "user_intent": "xx"}, {"source_ref": "b", "user_intent": "yyy"}])
    g = FakeGemini()
    assert run_embed(g, in_path=inp, out_path=out, text_fn=history_text) == 2
    rows = list(read_jsonl(out))
    assert rows[0]["embedding"] == [2.0, 0.0] and rows[1]["embedding"] == [3.0, 0.0]
    assert rows[0]["user_intent"] == "xx"  # 原欄位保留

    write_jsonl(inp, [{"source_ref": "a", "user_intent": "xx"},
                      {"source_ref": "b", "user_intent": "yyy"},
                      {"source_ref": "c", "user_intent": "z"}])
    assert run_embed(g, in_path=inp, out_path=out, text_fn=history_text) == 1
    assert g.batches[-1] == ["z"]
    assert [r["source_ref"] for r in read_jsonl(out)] == ["a", "b", "c"]


def test_run_embed_reindex_recomputes_everything(tmp_path):
    inp, out = tmp_path / "in.jsonl", tmp_path / "out.jsonl"
    write_jsonl(inp, [{"source_ref": "a", "user_intent": "xx"}])
    g = FakeGemini()
    run_embed(g, in_path=inp, out_path=out, text_fn=history_text)
    assert run_embed(g, in_path=inp, out_path=out, text_fn=history_text, reindex=True) == 1
    assert len(list(read_jsonl(out))) == 1
    assert len(g.batches) == 2


def test_run_embed_chunks_requests(tmp_path):
    inp, out = tmp_path / "in.jsonl", tmp_path / "out.jsonl"
    write_jsonl(inp, [{"source_ref": str(i), "user_intent": "t"} for i in range(5)])
    g = FakeGemini()
    run_embed(g, in_path=inp, out_path=out, text_fn=history_text, chunk=2)
    assert [len(b) for b in g.batches] == [2, 2, 1]
```

- [ ] **Step 2: 跑測試確認失敗**

Run: `python -m pytest tests/test_embed.py -v`
Expected: FAIL — ModuleNotFoundError

- [ ] **Step 3: 實作 embed.py**

`scripts/pipeline/embed.py`:
```python
"""階段 4：structured → embedded。以 source_ref 續跑；--reindex 全部重算（換 embedding 模型時用）。"""

from __future__ import annotations

import argparse
from collections.abc import Callable
from pathlib import Path

from pipeline.config import EMBEDDED_DIR, STRUCTURED_DIR
from pipeline.jsonl import append_jsonl, existing_keys, read_jsonl, write_jsonl

HISTORIES_IN = STRUCTURED_DIR / "histories.jsonl"
PRESETS_IN = STRUCTURED_DIR / "presets.jsonl"
HISTORIES_OUT = EMBEDDED_DIR / "histories.jsonl"
PRESETS_OUT = EMBEDDED_DIR / "presets.jsonl"


def history_text(r: dict) -> str:
    return r["user_intent"]


def preset_text(r: dict) -> str:
    return f"{r['title']}。{r['description']}。標籤：{', '.join(r['tags'])}"


def run_embed(
    client,
    *,
    in_path: Path,
    out_path: Path,
    text_fn: Callable[[dict], str],
    reindex: bool = False,
    chunk: int = 64,
) -> int:
    if reindex:
        write_jsonl(out_path, [])
    done = existing_keys(out_path, "source_ref")
    pending = [r for r in read_jsonl(in_path) if r["source_ref"] not in done]
    n = 0
    for i in range(0, len(pending), chunk):
        batch = pending[i : i + chunk]
        vectors = client.embed_batch([text_fn(r) for r in batch], task_type="RETRIEVAL_DOCUMENT")
        for r, v in zip(batch, vectors, strict=True):
            append_jsonl(out_path, {**r, "embedding": v})
            n += 1
    return n


def main(argv: list[str] | None = None) -> None:
    ap = argparse.ArgumentParser(description="階段 4：向量化")
    ap.add_argument("--reindex", action="store_true", help="丟棄既有向量全部重算")
    args = ap.parse_args(argv)
    from pipeline.gemini_client import default_client

    client = default_client()
    h = run_embed(client, in_path=HISTORIES_IN, out_path=HISTORIES_OUT, text_fn=history_text, reindex=args.reindex)
    p = run_embed(client, in_path=PRESETS_IN, out_path=PRESETS_OUT, text_fn=preset_text, reindex=args.reindex)
    print(f"embed: +{h} histories, +{p} presets → {EMBEDDED_DIR}")


if __name__ == "__main__":
    main()
```

- [ ] **Step 4: 跑測試確認通過**

Run: `python -m pytest tests/test_embed.py -v`
Expected: 4 passed

- [ ] **Step 5: 真跑 Task 8 產出的資料**

Run: `python -m pipeline.embed`
Expected: `embed: +5 histories, +N presets`

Run: `python -c "from pipeline.jsonl import read_jsonl; from pipeline.embed import PRESETS_OUT; r=next(read_jsonl(PRESETS_OUT)); print(len(r['embedding']))"`
Expected: `768`

- [ ] **Step 6: Commit**

```bash
git add scripts/pipeline/embed.py scripts/tests/test_embed.py
git commit -m "feat(pipeline): resumable embed stage with reindex

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 10: 階段 5 — load（upsert 進兩張表）

**Files:**
- Create: `scripts/pipeline/load.py`
- Create: `scripts/tests/test_load.py`

**Interfaces:**
- Consumes: `db.connect()`、`read_jsonl`、`EMBEDDED_DIR`
- Produces:
  - `pipeline.load.upsert_histories(conn, rows: Iterable[dict]) -> int`
  - `pipeline.load.upsert_presets(conn, rows: Iterable[dict]) -> int`
  - `pipeline.load.run_load(conn, *, histories_path, presets_path) -> tuple[int, int]`

- [ ] **Step 1: 寫失敗測試（integration，DB 未啟動則 skip）**

`scripts/tests/test_load.py`:
```python
import pytest
from pgvector import Vector

from pipeline import db
from pipeline.load import upsert_histories, upsert_presets

pytestmark = pytest.mark.integration

VEC = [1.0] + [0.0] * 767
HIST = {"source_ref": "test:h1", "user_intent": "測試", "positive_prompt": "p", "negative_prompt": "n",
        "subject_profile": "portrait", "image_url": None, "embedding": VEC}
PRESET = {"source_ref": "test:h1:0", "title": "t", "category": "Scene", "description": "d",
          "tags": ["a", "b"], "facet_ids": ["scene.location"], "prompt_snippet": "s",
          "negative_snippet": None, "image_url": None, "embedding": VEC}


@pytest.fixture
def conn():
    if not db.db_available():
        pytest.skip("PostgreSQL 未啟動")
    c = db.connect()
    yield c
    c.rollback()
    c.execute("DELETE FROM shared_prompt_histories WHERE source_ref LIKE 'test:%'")
    c.execute("DELETE FROM prompt_knowledge_presets WHERE source_ref LIKE 'test:%'")
    c.commit()
    c.close()


def test_upsert_histories_is_idempotent(conn):
    assert upsert_histories(conn, [HIST]) == 1
    assert upsert_histories(conn, [{**HIST, "user_intent": "改過"}]) == 1
    conn.commit()
    row = conn.execute(
        "SELECT user_intent, source, intent_embedding FROM shared_prompt_histories WHERE source_ref = %s",
        ("test:h1",),
    ).fetchone()
    assert row[0] == "改過" and row[1] == "civitai"
    assert len(row[2]) == 768
    assert conn.execute("SELECT count(*) FROM shared_prompt_histories WHERE source_ref = 'test:h1'").fetchone()[0] == 1


def test_upsert_presets_arrays_and_vector_search(conn):
    upsert_presets(conn, [PRESET])
    conn.commit()
    row = conn.execute(
        "SELECT tags, facet_ids, preset_embedding <=> %s AS dist "
        "FROM prompt_knowledge_presets WHERE source_ref = %s",
        (Vector(VEC), "test:h1:0"),
    ).fetchone()
    assert row[0] == ["a", "b"] and row[1] == ["scene.location"]
    assert row[2] < 1e-6
    hit = conn.execute(
        "SELECT source_ref FROM prompt_knowledge_presets WHERE facet_ids && %s::text[]",
        (["scene.location"],),
    ).fetchone()
    assert hit[0] == "test:h1:0"
```

- [ ] **Step 2: 跑測試確認失敗**

Run: `python -m pytest tests/test_load.py -m integration -v`
Expected: FAIL — ModuleNotFoundError（DB 需已 `docker compose up -d db`）

- [ ] **Step 3: 實作 load.py**

`scripts/pipeline/load.py`:
```python
"""階段 5：embedded → PostgreSQL。以 source_ref 做 upsert，重跑不會重複。"""

from __future__ import annotations

import argparse
from collections.abc import Iterable
from pathlib import Path

import psycopg
from pgvector import Vector

from pipeline.config import EMBEDDED_DIR
from pipeline.jsonl import read_jsonl

HISTORIES_SQL = """
INSERT INTO shared_prompt_histories
    (source_ref, user_intent, positive_prompt, negative_prompt, subject_profile, source, image_url, intent_embedding)
VALUES (%(source_ref)s, %(user_intent)s, %(positive_prompt)s, %(negative_prompt)s,
        %(subject_profile)s, 'civitai', %(image_url)s, %(embedding)s)
ON CONFLICT (source_ref) DO UPDATE SET
    user_intent      = EXCLUDED.user_intent,
    positive_prompt  = EXCLUDED.positive_prompt,
    negative_prompt  = EXCLUDED.negative_prompt,
    subject_profile  = EXCLUDED.subject_profile,
    image_url        = EXCLUDED.image_url,
    intent_embedding = EXCLUDED.intent_embedding
"""

PRESETS_SQL = """
INSERT INTO prompt_knowledge_presets
    (source_ref, title, category, description, tags, facet_ids, prompt_snippet, negative_snippet, image_url, preset_embedding)
VALUES (%(source_ref)s, %(title)s, %(category)s, %(description)s, %(tags)s, %(facet_ids)s,
        %(prompt_snippet)s, %(negative_snippet)s, %(image_url)s, %(embedding)s)
ON CONFLICT (source_ref) DO UPDATE SET
    title            = EXCLUDED.title,
    category         = EXCLUDED.category,
    description      = EXCLUDED.description,
    tags             = EXCLUDED.tags,
    facet_ids        = EXCLUDED.facet_ids,
    prompt_snippet   = EXCLUDED.prompt_snippet,
    negative_snippet = EXCLUDED.negative_snippet,
    image_url        = EXCLUDED.image_url,
    preset_embedding = EXCLUDED.preset_embedding
"""


def _with_vector(row: dict) -> dict:
    return {**row, "embedding": Vector(row["embedding"])}


def _upsert(conn: psycopg.Connection, sql: str, rows: Iterable[dict]) -> int:
    n = 0
    with conn.cursor() as cur:
        for row in rows:
            cur.execute(sql, _with_vector(row))
            n += 1
    return n


def upsert_histories(conn: psycopg.Connection, rows: Iterable[dict]) -> int:
    return _upsert(conn, HISTORIES_SQL, rows)


def upsert_presets(conn: psycopg.Connection, rows: Iterable[dict]) -> int:
    return _upsert(conn, PRESETS_SQL, rows)


def run_load(conn: psycopg.Connection, *, histories_path: Path, presets_path: Path) -> tuple[int, int]:
    h = upsert_histories(conn, read_jsonl(histories_path))
    p = upsert_presets(conn, read_jsonl(presets_path))
    conn.commit()
    return h, p


def main(argv: list[str] | None = None) -> None:
    argparse.ArgumentParser(description="階段 5：寫入 PostgreSQL").parse_args(argv)
    from pipeline.db import connect

    with connect() as conn:
        h, p = run_load(conn, histories_path=EMBEDDED_DIR / "histories.jsonl",
                        presets_path=EMBEDDED_DIR / "presets.jsonl")
    print(f"load: {h} histories, {p} presets upserted")


if __name__ == "__main__":
    main()
```

- [ ] **Step 4: 跑測試確認通過**

Run: `python -m pytest tests/test_load.py -m integration -v`
Expected: 2 passed

- [ ] **Step 5: 真載入並查數**

Run: `python -m pipeline.load`
Expected: `load: 5 histories, N presets upserted`

Run: `docker exec prompt-copilot-db psql -U postgres -d prompt_copilot -c "SELECT count(*) FROM shared_prompt_histories" -c "SELECT title, category, facet_ids FROM prompt_knowledge_presets LIMIT 5"`

- [ ] **Step 6: Commit**

```bash
git add scripts/pipeline/load.py scripts/tests/test_load.py
git commit -m "feat(pipeline): load stage upserting histories and presets into pgvector

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 11: 入口腳本、驗收查詢、README、全量執行

**Files:**
- Create: `scripts/seed_data.py`
- Create: `scripts/query_check.py`
- Create: `scripts/README.md`
- Create: `scripts/tests/test_seed_data.py`

**Interfaces:**
- Consumes: 所有階段的 `main(argv)`
- Produces:
  - `python seed_data.py [--from {fetch,clean,structure,embed,load}] [--max-items N] [--max-records N] [--reindex]`
  - `python query_check.py "<繁中查詢>" [--top 5] [--facet <facet.id>] [--tag <tag>]`

- [ ] **Step 1: 寫失敗測試（只測階段順序邏輯）**

`scripts/tests/test_seed_data.py`:
```python
from seed_data import STAGES, stages_from


def test_stage_order():
    assert STAGES == ["fetch", "clean", "structure", "embed", "load"]


def test_stages_from_returns_suffix():
    assert stages_from("structure") == ["structure", "embed", "load"]
    assert stages_from("fetch") == STAGES
```

- [ ] **Step 2: 跑測試確認失敗**

Run: `python -m pytest tests/test_seed_data.py -v`
Expected: FAIL — ModuleNotFoundError: seed_data

- [ ] **Step 3: 實作 seed_data.py**

`scripts/seed_data.py`:
```python
"""一鍵跑完五階段。--from 指定起點；每階段本身可續跑，所以中斷後重跑同一指令即可。"""

from __future__ import annotations

import argparse
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

from pipeline import clean, embed, fetch_civitai, load, structure  # noqa: E402

STAGES = ["fetch", "clean", "structure", "embed", "load"]


def stages_from(start: str) -> list[str]:
    return STAGES[STAGES.index(start):]


def main(argv: list[str] | None = None) -> None:
    ap = argparse.ArgumentParser(description="Civitai → pgvector 全管線")
    ap.add_argument("--from", dest="start", choices=STAGES, default="fetch")
    ap.add_argument("--max-items", type=int, default=3000, help="fetch 階段本次最多新增筆數")
    ap.add_argument("--max-records", type=int, default=None, help="structure 階段本次最多處理筆數")
    ap.add_argument("--reindex", action="store_true", help="embed 階段全部重算")
    args = ap.parse_args(argv)

    for stage in stages_from(args.start):
        print(f"=== {stage} ===")
        if stage == "fetch":
            fetch_civitai.main(["--max-items", str(args.max_items)])
        elif stage == "clean":
            clean.main([])
        elif stage == "structure":
            structure.main([] if args.max_records is None else ["--max-records", str(args.max_records)])
        elif stage == "embed":
            embed.main(["--reindex"] if args.reindex else [])
        elif stage == "load":
            load.main([])


if __name__ == "__main__":
    main()
```

- [ ] **Step 4: 跑測試確認通過**

Run: `python -m pytest tests/test_seed_data.py -v`
Expected: 2 passed

- [ ] **Step 5: 實作 query_check.py（驗收腳本）**

`scripts/query_check.py`:
```python
"""驗收用：以繁中口語查詢 presets（混合檢索）與 histories（向量 + profile），印出 Top-K。
用法：python query_check.py "昏暗雨夜的科幻城市" --top 5 [--facet scene.weather] [--tag rain]"""

from __future__ import annotations

import argparse
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

from pgvector import Vector  # noqa: E402

from pipeline.db import connect  # noqa: E402
from pipeline.gemini_client import default_client  # noqa: E402

PRESETS_SQL = """
SELECT title, category, facet_ids, tags, prompt_snippet,
       preset_embedding <=> %(q)s AS dist
FROM prompt_knowledge_presets
WHERE (%(facet)s::text IS NULL OR facet_ids @> ARRAY[%(facet)s]::text[])
  AND (%(tag)s::text IS NULL OR tags @> ARRAY[%(tag)s]::text[])
ORDER BY dist
LIMIT %(k)s
"""

HISTORIES_SQL = """
SELECT user_intent, subject_profile, left(positive_prompt, 80) AS prompt,
       intent_embedding <=> %(q)s AS dist
FROM shared_prompt_histories
ORDER BY dist
LIMIT %(k)s
"""


def main(argv: list[str] | None = None) -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("query")
    ap.add_argument("--top", type=int, default=5)
    ap.add_argument("--facet", default=None)
    ap.add_argument("--tag", default=None)
    args = ap.parse_args(argv)

    qvec = Vector(default_client().embed_batch([args.query], task_type="RETRIEVAL_QUERY")[0])
    with connect() as conn:
        print(f"\n== presets for「{args.query}」 ==")
        for row in conn.execute(PRESETS_SQL, {"q": qvec, "facet": args.facet, "tag": args.tag, "k": args.top}):
            title, category, facets, tags, snippet, dist = row
            print(f"[{dist:.3f}] {title} ({category}) {facets}\n         {snippet[:90]}")
        print(f"\n== histories ==")
        for intent, profile, prompt, dist in conn.execute(HISTORIES_SQL, {"q": qvec, "k": args.top}):
            print(f"[{dist:.3f}] ({profile}) {intent}\n         {prompt}")


if __name__ == "__main__":
    main()
```

- [ ] **Step 6: 寫 README**

`scripts/README.md`:
```markdown
# 資料管線

Civitai 公開 API → 清洗 → Gemini 結構化 → Gemini embedding → PostgreSQL (pgvector)。

## 前置

- Python 3.12+、Docker Desktop
- repo 根目錄 `.env`（由 `.env.example` 複製）填入 `GEMINI_API_KEY`
- `docker compose up -d db`（首次啟動自動執行 `db/init/001_schema.sql`）

## 安裝

    cd scripts
    python -m venv .venv
    .\.venv\Scripts\Activate.ps1
    pip install -r requirements.txt

## 執行

全量（會花時間，可中斷後重跑同一指令續跑）：

    python seed_data.py --max-items 3000

從某階段起跑：

    python seed_data.py --from structure --max-records 200

單一階段：

    python -m pipeline.fetch_civitai --max-items 500
    python -m pipeline.clean
    python -m pipeline.structure --max-records 100
    python -m pipeline.embed [--reindex]
    python -m pipeline.load

## 驗收

    python query_check.py "昏暗雨夜的科幻城市"
    python query_check.py "穿皮夾克的女生" --facet clothing.upper

## 資料流

| 階段 | 讀 | 寫 | 續跑機制 |
| --- | --- | --- | --- |
| fetch | Civitai API | `data/raw/images.jsonl` | `state.json` 的 cursor |
| clean | raw | `data/clean/records.jsonl` | 全量重算（便宜） |
| structure | clean | `data/structured/{histories,presets}.jsonl` | 跳過已有 `source_ref` |
| embed | structured | `data/embedded/*.jsonl` | 跳過已有 `source_ref`；`--reindex` 重算 |
| load | embedded | PostgreSQL | `ON CONFLICT (source_ref)` upsert |

## 換 embedding 模型

1. 改 `.env` 的 `GEMINI_EMBEDDING_MODEL` / `EMBEDDING_DIMENSIONS`
2. 若維度變了：改 `db/init/001_schema.sql` 的 `VECTOR(...)`，然後 `docker compose down -v`、再 `docker compose up -d db`（init 只在 volume 首次建立時執行）
3. `python seed_data.py --from embed --reindex`

## 測試

    python -m pytest              # 單元測試（不需 DB、不需金鑰）
    python -m pytest -m integration   # 需要 DB 已啟動
    ruff check .
```

- [ ] **Step 7: 全量執行**

Run（在 `scripts/`，預估數十分鐘到一兩小時視 rate limit 而定；可隨時 Ctrl+C 後重跑同指令續跑）:
```powershell
python seed_data.py --max-items 3000
```

Expected 最後一行：`load: H histories, P presets upserted`，H 數百、P 上千。

若 structure 階段 429 頻繁，把 `.env` 的 `GEMINI_MIN_INTERVAL_S` 調到 `2.0` 再續跑。

- [ ] **Step 8: 驗收查詢**

Run:
```powershell
python query_check.py "昏暗雨夜的科幻城市"
python query_check.py "穿著皮夾克的短髮女生，低角度仰拍"
python query_check.py "山上的日出" 
python query_check.py "霓虹" --facet scene.lighting
```

驗收標準（spec §14 第 1 列）：第一條查詢的 presets Top-5 中至少 3 筆與「雨夜／城市／科幻／霓虹」相關；histories Top-3 的 `subject_profile` 與 `user_intent` 合理。第三條的 histories 應以 `landscape` 為主。

不合格時的調整順序：(1) `structure.py` 的 `PROMPT_TEMPLATE`（description 是否夠口語）→ 刪 `data/structured`、`data/embedded` 重跑 `--from structure`；(2) `embed.py` 的 `preset_text` 組合方式 → `--from embed --reindex`。

- [ ] **Step 9: 全部測試與 lint**

Run: `python -m pytest -v` → Expected: 全綠（integration deselected）
Run: `python -m pytest -m integration -v` → Expected: 全綠
Run: `ruff check .` → Expected: All checks passed

- [ ] **Step 10: Commit**

```bash
git add scripts/seed_data.py scripts/query_check.py scripts/README.md scripts/tests/test_seed_data.py
git commit -m "feat(pipeline): seed_data entrypoint, query_check acceptance script, README

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

## 子專案 1 完成定義

- [ ] `docker compose up -d db` 後三張表存在、`vector` extension 啟用
- [ ] `python seed_data.py` 跑完，`shared_prompt_histories` 與 `prompt_knowledge_presets` 皆有資料
- [ ] `query_check.py "昏暗雨夜的科幻城市"` 回傳合理結果
- [ ] `python -m pytest` 與 `python -m pytest -m integration` 全綠、`ruff check .` 乾淨
- [ ] `scripts/README.md` 能讓一個沒看過本 repo 的人跑起管線

完成後進入子專案 2（SK Agent 核心）的計畫。
