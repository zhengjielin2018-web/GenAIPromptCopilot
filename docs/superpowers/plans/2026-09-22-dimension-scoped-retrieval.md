# 分維度檢索與兩段式組裝 — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 把 `scripts/demo.py` 從「整句一個向量撈 8 筆、一次 LLM 組裝」改成「先分析 → 每個維度用自己的子查詢在自己的 facet 候選池裡撈 → 再組裝」，借用的 tag 逐筆可審核、有缺的維度都給建議，並把「GIN 過濾不夠、關鍵是分維度子查詢」寫回主規格 §9。

**Architecture:** 三段：① Gemini 分析（profile、facet 狀態、每維度子查詢）→ ② 無 LLM 的檢索（單次 `embed_batch`、每維度 GIN 過濾＋向量排序、跨維度去重、相似度分級、facet 覆蓋標記）→ ③ Gemini 組裝（提示詞、`borrowed`、`suggestions`）。Python 端對 ③ 的自述做驗證：`grounded=False` 的片段不得進提示詞、借用的 tag 必須同時出現在片段與提示詞裡。檢索邏輯抽到 `pipeline/retrieval.py`（純函式與吃 `conn` 的函式分開），終端機輸出抽到 `demo_render.py`，`demo.py` 只剩 schema、prompt、驗證、編排、CLI。

**Tech Stack:** Python 3.12、pydantic v2、psycopg 3 + pgvector、google-genai（經既有 `GeminiClient`）、PyYAML、pytest、ruff（line-length 120）。PostgreSQL 16 + pgvector（Docker）。

**Spec:** [docs/superpowers/specs/2026-09-22-dimension-scoped-retrieval-design.md](../specs/2026-09-22-dimension-scoped-retrieval-design.md) — 本計畫實作其全部章節；§10 改寫主規格 [2026-09-21-genai-prompt-copilot-design.md](../specs/2026-09-21-genai-prompt-copilot-design.md) 的 §4 工具表與 §9。

---

## Global Constraints

- 工作目錄：所有 `pytest` / `python` 指令在 `scripts/` 下執行，用 `.\.venv\Scripts\python.exe`（PowerShell）或 `./.venv/Scripts/python.exe`（Git Bash）。印中文前設 `PYTHONIOENCODING=utf-8`。
- 單元測試不需 DB、不需金鑰：`python -m pytest`（`pyproject.toml` 預設排除 `integration`）。整合測試：`python -m pytest -m integration`，DB 沒起來要 `pytest.skip`，照 `tests/test_db.py` 的 `db_available()` 寫法。
- ruff：`line-length = 120`、`target-version = "py312"`、規則 `E, F, I, UP, B`。每次 commit 前跑 `python -m ruff check .`。
- 相似度分級（spec §5.1）：`dist < 0.25` 高、`0.25 ≤ dist < 0.30` 中、`≥ 0.30` 低。不設距離門檻。
- 檢索筆數（spec §5.1、§6.1）：grounded 維度 1 句子查詢 k=5；missing 維度最多 2 句、各 k=3。
- 維度固定順序：`style, scene, camera, appearance, pose, clothing`。
- 驗證只改 `borrowed` / `suggestions` 紀錄，**絕不修改** `positive_prompt` / `negative_prompt`（spec §6.2）。
- 被移除的借用歸屬一定顯示在畫面上，措辭是「來源不符」且說明提示詞不受影響，不能寫成像是詞被拿掉（spec §7）。
- Commit 訊息沿用 repo 慣例 `type(scope): 摘要`（`feat(demo)`、`refactor(demo)`、`test(retrieval)`、`docs(spec)`），結尾加 `Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>`。
- 在分支 `feat/dimension-scoped-retrieval` 上工作，完成後用 `superpowers:finishing-a-development-branch` 併回 `master`（子專案 1 也是 merge commit 併入）。

---

## 檔案結構

| 檔案 | 動作 | 職責 |
| :--- | :--- | :--- |
| `scripts/pipeline/retrieval.py` | 新增 | 分維度檢索：`band`、`dimension_facets`、`grounded_dimensions`、`normalize_queries`、`Candidate`、`dedupe`、`annotate_coverage`、`retrieve_presets`、`retrieve_histories` |
| `scripts/demo_render.py` | 新增 | 終端機輸出：從 `demo.py` 搬來的 `display_width`、`pad`、`wrap_tags`、`render_dimension_row`、`group_by_dimension`、`Palette`、`colors_enabled`，加上新的 `render_queries`、`render_retrieval_summary`、`render_verbose`、`DemoView` 系列與 `render` |
| `scripts/demo.py` | 重寫 | 兩段 pydantic schema、`facet_state_map`、`missing_labels_by_dimension`、`validate_borrowed`、`validate_suggestions`、兩個 prompt builder 與格式化、`build_view`、`run_once`、`main` |
| `scripts/tests/test_retrieval.py` | 新增 | retrieval 純函式單元測試 + 兩個 integration 測試 |
| `scripts/tests/test_demo_render.py` | 新增 | 搬過來的 9 個 render 測試（`group_by_dimension` 改吃 dict）+ 新區塊測試 |
| `scripts/tests/test_demo.py` | 重寫 | schema / 狀態整理 / 驗證 / prompt 格式化 / 端到端（`ScriptedModels`） |
| `docs/superpowers/specs/2026-09-21-genai-prompt-copilot-design.md` | 修改 | §4 工具表 `SearchPresets` 列、§9 全文 |
| `scripts/README.md` | 修改 | demo 用法與參數 |

---

### Task 1: 開分支、把 spec 進版控

**Files:**
- Commit: `docs/superpowers/specs/2026-09-22-dimension-scoped-retrieval-design.md`

- [ ] **Step 1: 確認工作樹乾淨且在 master**

Run: `git status --short && git branch --show-current`
Expected: 只有 `?? docs/superpowers/specs/2026-09-22-dimension-scoped-retrieval-design.md`（與本計畫檔）未追蹤；分支 `master`。

- [ ] **Step 2: 開分支**

```bash
git checkout -b feat/dimension-scoped-retrieval
```

- [ ] **Step 3: Commit spec 與計畫**

```bash
git add docs/superpowers/specs/2026-09-22-dimension-scoped-retrieval-design.md docs/superpowers/plans/2026-09-22-dimension-scoped-retrieval.md
git commit -m "docs(spec): dimension-scoped retrieval and two-pass assembly for the demo

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

### Task 2: 把終端機輸出搬到 `demo_render.py`

純搬遷，行為不變，只有 `group_by_dimension` 的輸入從 `list[FacetAssessment]` 改成 `dict[facet_id, state]`——因為 render 模組不能反過來 import `demo.py` 的 pydantic 型別。

**Files:**
- Create: `scripts/demo_render.py`
- Modify: `scripts/demo.py`（刪掉搬走的函式；`render` 暫時改 import）
- Create: `scripts/tests/test_demo_render.py`
- Delete: `scripts/tests/test_demo.py`（內容全搬走；Task 6 會重建）

**Interfaces:**
- Produces:
  - `DIMENSIONS: tuple[str, ...]`（暫放這裡，Task 3 移到 `retrieval.py` 後這裡改成 re-export）
  - `display_width(text: str) -> int`、`pad(text: str, width: int) -> str`
  - `wrap_tags(text: str, indent: str = "    ", width: int = 84) -> str`
  - `render_dimension_row(label: str, entries: list[tuple[str, str]], width: int = 10) -> str`
  - `group_by_dimension(states: dict[str, str], catalog: FacetCatalog) -> dict[str, list[tuple[str, str]]]`
  - `class Palette`（`head/ok/warn/dim`）、`colors_enabled(no_color: bool) -> bool`

- [ ] **Step 1: 建 `tests/test_demo_render.py`，把既有 9 個測試搬過來並改 `group_by_dimension` 的輸入**

```python
from demo_render import (
    display_width,
    group_by_dimension,
    pad,
    render_dimension_row,
    wrap_tags,
)
from pipeline.config import FACETS_PATH
from pipeline.facets import load_facets

CAT = load_facets(FACETS_PATH)


def test_group_by_dimension_buckets_by_the_catalog_and_drops_unknown_ids():
    grouped = group_by_dimension(
        {
            "scene.location": "covered",
            "scene.weather": "missing",
            "clothing.upper": "notApplicable",
            "not.a.real.facet": "covered",
        },
        CAT,
    )
    assert grouped["scene"] == [("地點類型", "covered"), ("天氣氛圍", "missing")]
    assert grouped["clothing"] == [("上半身", "notApplicable")]
    assert grouped["camera"] == []
    assert not any("not.a.real.facet" in str(v) for v in grouped.values())


def test_group_by_dimension_follows_catalog_order_not_input_order():
    grouped = group_by_dimension({"scene.weather": "missing", "scene.location": "covered"}, CAT)
    assert grouped["scene"] == [("地點類型", "covered"), ("天氣氛圍", "missing")]


def test_render_dimension_row_shows_ratio_and_names_what_is_missing():
    row = render_dimension_row("場景", [("地點類型", "covered"), ("天氣氛圍", "missing")])
    assert "●○" in row
    assert "1/2" in row
    assert "缺：天氣氛圍" in row


def test_render_dimension_row_collapses_a_fully_inapplicable_dimension():
    row = render_dimension_row("人物穿著", [("上半身", "notApplicable"), ("鞋履", "notApplicable")])
    assert "不適用" in row
    assert "●" not in row and "○" not in row


def test_render_dimension_row_omits_the_missing_clause_when_complete():
    row = render_dimension_row("鏡頭", [("景別", "covered"), ("視角高度", "covered")])
    assert "2/2" in row
    assert "缺" not in row


def test_wrap_tags_never_splits_a_tag_across_lines():
    tags = [f"tag-number-{i}" for i in range(20)]
    out = wrap_tags(", ".join(tags), indent="  ", width=40)
    assert "\n" in out
    for line in out.splitlines():
        assert len(line) <= 40 or line.count(",") == 0
    for tag in tags:
        assert tag in out


def test_wrap_tags_drops_empty_fragments_and_the_trailing_comma():
    out = wrap_tags("a, , b,  ,c", indent="")
    assert out == "a, b, c"


def test_display_width_counts_cjk_as_two_columns():
    assert display_width("風格") == 4
    assert display_width("人物樣貌") == 8
    assert display_width("Style") == 5


def test_dimension_labels_of_different_cjk_lengths_line_up():
    # 純字元數的 padding 會讓「風格」和「人物樣貌」對不齊，這是這兩個函式存在的原因。
    short = render_dimension_row("風格", [("a", "covered")])
    long_ = render_dimension_row("人物樣貌", [("b", "covered")])
    assert display_width(short.split("●")[0]) == display_width(long_.split("●")[0])


def test_pad_leaves_an_already_wide_string_untouched():
    assert pad("人物樣貌", 4) == "人物樣貌"
```

- [ ] **Step 2: 跑一次確認 import 失敗**

Run: `python -m pytest tests/test_demo_render.py -q`
Expected: `ModuleNotFoundError: No module named 'demo_render'`

- [ ] **Step 3: 建 `scripts/demo_render.py`**

```python
"""demo 的終端機輸出。全是純函式，不碰 DB 與網路。
設計見 docs/superpowers/specs/2026-09-22-dimension-scoped-retrieval-design.md §7。"""

from __future__ import annotations

import os
import sys
import unicodedata

from pipeline.facets import FacetCatalog

DIMENSIONS: tuple[str, ...] = ("style", "scene", "camera", "appearance", "pose", "clothing")


# ---------- 文字對齊 ----------


def display_width(text: str) -> int:
    """終端機顯示寬度：全形／寬字元佔兩欄。用字元數對齊中文會歪掉。"""
    return sum(2 if unicodedata.east_asian_width(c) in "WF" else 1 for c in text)


def pad(text: str, width: int) -> str:
    return text + " " * max(0, width - display_width(text))


def wrap_tags(text: str, indent: str = "    ", width: int = 84) -> str:
    """逐個 tag 換行，避免把一個 tag 從中間切斷。"""
    out, line = [], indent
    for tag in [t.strip() for t in text.split(",") if t.strip()]:
        piece = tag + ", "
        if len(line) + len(piece) > width and line != indent:
            out.append(line.rstrip())
            line = indent
        line += piece
    if line.strip():
        out.append(line.rstrip().rstrip(","))
    return "\n".join(out)


# ---------- 六維度燈號 ----------


def group_by_dimension(states: dict[str, str], catalog: FacetCatalog) -> dict[str, list[tuple[str, str]]]:
    """回傳 {維度 key: [(facet 顯示名稱, 狀態), ...]}，依 catalog 順序，只保留 catalog 認得的 id。"""
    grouped: dict[str, list[tuple[str, str]]] = {k: [] for k in DIMENSIONS}
    for facet in catalog.facets.values():
        state = states.get(facet.id)
        if state is not None:
            grouped[facet.dimension].append((facet.label, state))
    return grouped


def render_dimension_row(label: str, entries: list[tuple[str, str]], width: int = 10) -> str:
    """把一個維度畫成一行：名稱、覆蓋比例、以及缺了哪些項目。"""
    applicable = [(n, s) for n, s in entries if s != "notApplicable"]
    if not applicable:
        return f"  {pad(label, width)}  ──  不適用"
    covered = [n for n, s in applicable if s == "covered"]
    missing = [n for n, s in applicable if s == "missing"]
    bar = "●" * len(covered) + "○" * len(missing)
    line = f"  {pad(label, width)}  {bar}  {len(covered)}/{len(applicable)}"
    if missing:
        line += f"   缺：{'、'.join(missing)}"
    return line


# ---------- 顏色 ----------


class Palette:
    def __init__(self, enabled: bool):
        self.on = enabled

    def _w(self, code: str, s: str) -> str:
        return f"\033[{code}m{s}\033[0m" if self.on else s

    def head(self, s: str) -> str:
        return self._w("1;36", s)

    def ok(self, s: str) -> str:
        return self._w("32", s)

    def warn(self, s: str) -> str:
        return self._w("33", s)

    def dim(self, s: str) -> str:
        return self._w("2", s)


def colors_enabled(no_color: bool) -> bool:
    if no_color or os.environ.get("NO_COLOR"):
        return False
    return sys.stdout.isatty()
```

- [ ] **Step 4: 在 `demo.py` 刪掉搬走的東西，改成 import**

把 `demo.py` 裡的 `DIMENSION_ORDER`、`group_by_dimension`、`display_width`、`pad`、`render_dimension_row`、`wrap_tags`、`class Palette`、`colors_enabled` 全部刪掉，`import os`、`import unicodedata` 一併移除（`sys` 還要，`sys.path.insert` 用得到）。在 `from pipeline.facets import ...` 之後加：

```python
from demo_render import (  # noqa: E402
    DIMENSIONS,
    Palette,
    colors_enabled,
    group_by_dimension,
    render_dimension_row,
    wrap_tags,
)
```

現有 `render()` 裡的 `group_by_dimension(result.facets, catalog)` 改成：

```python
    grouped = group_by_dimension({a.facet_id: a.state for a in result.facets}, catalog)
```

以及 `for key in DIMENSION_ORDER:` 改成 `for key in DIMENSIONS:`。

- [ ] **Step 5: 刪除舊測試檔**

```bash
git rm scripts/tests/test_demo.py
```

- [ ] **Step 6: 跑測試與 ruff**

Run: `python -m pytest -q && python -m ruff check .`
Expected: 全綠（既有測試數 − 9 + 10）；ruff 無報錯。

- [ ] **Step 7: 手動確認 demo 還能 import（不打 API）**

Run: `python -c "import demo; print('ok')"`
Expected: `ok`

- [ ] **Step 8: Commit**

```bash
git add scripts/demo_render.py scripts/demo.py scripts/tests/test_demo_render.py
git commit -m "refactor(demo): move terminal rendering into demo_render.py

group_by_dimension now takes a {facet_id: state} dict so the render module
does not depend on demo.py's pydantic models.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

### Task 3: `retrieval.py` 純函式（分級、維度 facet、grounded、queries 整理）

**Files:**
- Create: `scripts/pipeline/retrieval.py`
- Modify: `scripts/demo_render.py`（`DIMENSIONS` 改從 retrieval import）
- Create: `scripts/tests/test_retrieval.py`

**Interfaces:**
- Produces:
  - `DIMENSIONS: tuple[str, ...]`、`Band = Literal["高", "中", "低"]`、`FacetState = Literal["covered", "missing", "notApplicable"]`
  - `K_COVERED = 5`、`K_MISSING = 3`、`HIGH_MAX = 0.25`、`MID_MAX = 0.30`
  - `band(dist: float) -> Band`
  - `dimension_facets(catalog, profile: str, dimension: str) -> list[str]`
  - `grounded_dimensions(states: dict[str, str], catalog) -> set[str]`
  - `@dataclass DimensionQuery(dimension: str, query: str, grounded: bool, k: int)`
  - `normalize_queries(raw: list[tuple[str, str]], profile, grounded: set[str], catalog, fallback_query: str, k_covered=K_COVERED, k_missing=K_MISSING) -> list[DimensionQuery]`

- [ ] **Step 1: 寫失敗測試**

```python
# scripts/tests/test_retrieval.py
from pipeline.config import FACETS_PATH
from pipeline.facets import load_facets
from pipeline.retrieval import (
    band,
    dimension_facets,
    grounded_dimensions,
    normalize_queries,
)

CAT = load_facets(FACETS_PATH)


def test_band_boundaries_match_the_spec():
    assert band(0.249) == "高"
    assert band(0.25) == "中"
    assert band(0.299) == "中"
    assert band(0.30) == "低"


def test_dimension_facets_follows_the_profile():
    assert dimension_facets(CAT, "landscape", "clothing") == []
    assert dimension_facets(CAT, "landscape", "appearance") == []
    assert dimension_facets(CAT, "vehicle", "pose") == ["pose.motion_state", "pose.terrain"]
    assert "appearance.hair" in dimension_facets(CAT, "portrait", "appearance")


def test_grounded_dimensions_needs_one_covered_facet_and_ignores_unknown_ids():
    states = {"scene.location": "covered", "scene.weather": "missing", "style.genre": "missing", "bogus.id": "covered"}
    assert grounded_dimensions(states, CAT) == {"scene"}


def test_normalize_drops_dimensions_the_profile_does_not_have():
    out = normalize_queries([("clothing", "夾克"), ("scene", "山")], "landscape", {"scene"}, CAT, "整句")
    assert [q.dimension for q in out] == ["scene"]


def test_normalize_caps_grounded_at_one_and_missing_at_two_keeping_the_first_ones():
    raw = [("scene", "a"), ("scene", "b"), ("style", "x"), ("style", "y"), ("style", "z")]
    out = normalize_queries(raw, "portrait", {"scene"}, CAT, "整句")
    assert [(q.dimension, q.query) for q in out] == [("scene", "a"), ("style", "x"), ("style", "y")]
    assert out[0].grounded is True and out[0].k == 5
    assert out[1].grounded is False and out[1].k == 3


def test_normalize_backfills_a_grounded_dimension_the_llm_skipped_with_the_whole_description():
    out = normalize_queries([("style", "x")], "portrait", {"appearance", "scene"}, CAT, "整句描述")
    backfilled = [(q.dimension, q.query, q.k) for q in out if q.grounded]
    # 依 DIMENSIONS 順序：scene 在 appearance 前
    assert backfilled == [("scene", "整句描述", 5), ("appearance", "整句描述", 5)]


def test_normalize_drops_blank_queries_and_then_backfills_if_grounded():
    out = normalize_queries([("scene", "   ")], "portrait", {"scene"}, CAT, "整句")
    assert [(q.dimension, q.query) for q in out] == [("scene", "整句")]


def test_normalize_allows_a_missing_dimension_to_have_no_query():
    assert normalize_queries([], "portrait", set(), CAT, "整句") == []


def test_normalize_honours_custom_k_values():
    out = normalize_queries([("scene", "a"), ("style", "x")], "portrait", {"scene"}, CAT, "整句", k_covered=7, k_missing=2)
    assert [q.k for q in out] == [7, 2]
```

- [ ] **Step 2: 跑測試確認失敗**

Run: `python -m pytest tests/test_retrieval.py -q`
Expected: `ModuleNotFoundError: No module named 'pipeline.retrieval'`

- [ ] **Step 3: 建 `scripts/pipeline/retrieval.py`（本任務只放純函式，DB 部分 Task 5 加）**

```python
"""分維度檢索：每個維度用自己的子查詢向量、在自己的 facet 候選池裡撈，再跨維度去重與分級。
純函式（本檔上半）與吃 conn 的函式（下半）分開，前者可無 DB 測試。
設計見 docs/superpowers/specs/2026-09-22-dimension-scoped-retrieval-design.md §5、§6.1。"""

from __future__ import annotations

from dataclasses import dataclass
from typing import Literal

from pipeline.facets import FacetCatalog

DIMENSIONS: tuple[str, ...] = ("style", "scene", "camera", "appearance", "pose", "clothing")
Band = Literal["高", "中", "低"]
FacetState = Literal["covered", "missing", "notApplicable"]

K_COVERED = 5  # 使用者有描述的維度：1 句子查詢
K_MISSING = 3  # 使用者未描述的維度：最多 2 句對比子查詢，各撈這麼多
HIGH_MAX = 0.25
MID_MAX = 0.30
_MAX_QUERIES = {True: 1, False: 2}  # grounded -> 每維度子查詢上限


def band(dist: float) -> Band:
    """相似度分級。門檻是跨四種 profile 實測的：可用命中落在 0.18–0.28，全庫 p1≈0.32。"""
    if dist < HIGH_MAX:
        return "高"
    if dist < MID_MAX:
        return "中"
    return "低"


def dimension_facets(catalog: FacetCatalog, profile: str, dimension: str) -> list[str]:
    """該 profile 下這個維度適用的 facet id；維度不適用回空 list。"""
    return list(catalog.profiles[profile].get(dimension, []))


def grounded_dimensions(states: dict[str, str], catalog: FacetCatalog) -> set[str]:
    """有任一 facet 為 covered 的維度。不問 LLM，從 facet 狀態推出來，少一個可被捏造的欄位。"""
    return {catalog.dimension_of(fid) for fid, s in states.items() if s == "covered" and fid in catalog.facets}


@dataclass
class DimensionQuery:
    dimension: str
    query: str
    grounded: bool
    k: int


def normalize_queries(
    raw: list[tuple[str, str]],
    profile: str,
    grounded: set[str],
    catalog: FacetCatalog,
    fallback_query: str,
    k_covered: int = K_COVERED,
    k_missing: int = K_MISSING,
) -> list[DimensionQuery]:
    """整理 LLM 給的 (dimension, query) 清單：
    - 不在 profile 裡的維度、空白 query 丟掉
    - 每維度上限：grounded 1 句、missing 2 句，多的丟
    - grounded 維度若一句都沒有，用整句描述頂上（一次漏答不能毀掉整個維度）"""
    applicable = set(catalog.profiles[profile])
    out: list[DimensionQuery] = []
    count: dict[str, int] = {}
    for dim, q in raw:
        q = q.strip()
        if dim not in applicable or not q:
            continue
        is_grounded = dim in grounded
        if count.get(dim, 0) >= _MAX_QUERIES[is_grounded]:
            continue
        count[dim] = count.get(dim, 0) + 1
        out.append(DimensionQuery(dim, q, is_grounded, k_covered if is_grounded else k_missing))
    for dim in DIMENSIONS:
        if dim in grounded and dim in applicable and dim not in count:
            out.append(DimensionQuery(dim, fallback_query, True, k_covered))
    return out
```

**只匯入 `dataclass`，不要匯入 `field`** —— 本任務沒有用到它，匯入會觸發 ruff F401 讓 Step 5 失敗。Task 4 的 `Candidate` 需要時再加。

- [ ] **Step 4: `demo_render.py` 的 `DIMENSIONS` 改成從 retrieval 匯入**

把 `demo_render.py` 裡 `DIMENSIONS: tuple[str, ...] = (...)` 那行刪掉，改成：

```python
from pipeline.facets import FacetCatalog
from pipeline.retrieval import DIMENSIONS
```

`demo.py` 的 `from demo_render import (DIMENSIONS, ...)` 不用動（re-export 仍有效）。

- [ ] **Step 5: 跑測試與 ruff**

Run: `python -m pytest -q && python -m ruff check .`
Expected: 全綠、無 ruff 錯誤。

- [ ] **Step 6: Commit**

```bash
git add scripts/pipeline/retrieval.py scripts/demo_render.py scripts/tests/test_retrieval.py
git commit -m "feat(retrieval): band, dimension facets, grounded detection and query normalisation

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

### Task 4: `retrieval.py` 候選結構、跨維度去重、facet 覆蓋標記

**Files:**
- Modify: `scripts/pipeline/retrieval.py`
- Modify: `scripts/tests/test_retrieval.py`

**Interfaces:**
- Produces:
  - `@dataclass Candidate(preset: dict, dimension: str, dist: float, band: Band, grounded: bool, facet_coverage: dict[str, str] = {})`，property `id -> int`
  - `dedupe(hits: list[Candidate]) -> list[Candidate]`
  - `annotate_coverage(cands: list[Candidate], states: dict[str, str]) -> None`

`preset` dict 的 key 固定為：`id, title, category, facet_ids, tags, prompt_snippet, negative_snippet`。

- [ ] **Step 1: 加失敗測試（追加到 `tests/test_retrieval.py`）**

```python
from pipeline.retrieval import Candidate, annotate_coverage, dedupe  # 加到檔頭 import


def _cand(pid, dim, dist, facet_ids=(), grounded=True):
    return Candidate(
        preset={
            "id": pid, "title": f"t{pid}", "category": "X", "facet_ids": list(facet_ids), "tags": [],
            "prompt_snippet": "", "negative_snippet": None,
        },
        dimension=dim, dist=dist, band=band(dist), grounded=grounded,
    )


def test_dedupe_keeps_the_closest_dimension_and_its_grounded_flag():
    hits = [_cand(1, "scene", 0.30, grounded=True), _cand(1, "camera", 0.22, grounded=False), _cand(2, "scene", 0.28)]
    out = dedupe(hits)
    assert [(c.id, c.dimension, c.grounded) for c in out] == [(2, "scene", True), (1, "camera", False)]


def test_dedupe_orders_by_dimension_then_distance():
    hits = [_cand(3, "clothing", 0.1), _cand(1, "style", 0.3), _cand(2, "style", 0.2)]
    assert [c.id for c in dedupe(hits)] == [2, 1, 3]


def test_annotate_coverage_marks_every_facet_of_the_preset_with_the_users_state():
    c = _cand(1, "scene", 0.2, facet_ids=["scene.lighting", "scene.weather", "camera.shot"])
    annotate_coverage([c], {"scene.lighting": "covered", "scene.weather": "missing"})
    assert c.facet_coverage == {
        "scene.lighting": "covered",
        "scene.weather": "missing",
        "camera.shot": "notApplicable",
    }
```

- [ ] **Step 2: 跑測試確認失敗**

Run: `python -m pytest tests/test_retrieval.py -q`
Expected: `ImportError: cannot import name 'Candidate'`

- [ ] **Step 3: 在 `retrieval.py` 的 `normalize_queries` 之後加**

檔頭的 `from dataclasses import dataclass` 改成 `from dataclasses import dataclass, field`（本任務開始用到 `field`）。

```python
@dataclass
class Candidate:
    preset: dict
    dimension: str  # 去重後歸屬的維度
    dist: float
    band: Band
    grounded: bool  # 歸屬維度是否 grounded；False 的片段不得借入提示詞
    facet_coverage: dict[str, str] = field(default_factory=dict)  # facet_id -> 本次使用者的狀態

    @property
    def id(self) -> int:
        return self.preset["id"]


def dedupe(hits: list[Candidate]) -> list[Candidate]:
    """同一 preset 跨維度命中只留距離最小的那個（每維用不同子查詢向量，最小即最像該維需求）。
    輸出依 (維度順序, 距離) 排序。"""
    best: dict[int, Candidate] = {}
    for c in hits:
        cur = best.get(c.id)
        if cur is None or c.dist < cur.dist:
            best[c.id] = c
    return sorted(best.values(), key=lambda c: (DIMENSIONS.index(c.dimension), c.dist))


def annotate_coverage(cands: list[Candidate], states: dict[str, str]) -> None:
    """每筆候選的每個 facet_id 標上本次使用者的狀態；不在 ① 清單裡的視為 notApplicable。給 ③ 看的。"""
    for c in cands:
        c.facet_coverage = {fid: states.get(fid, "notApplicable") for fid in c.preset["facet_ids"]}
```

- [ ] **Step 4: 跑測試與 ruff**

Run: `python -m pytest -q && python -m ruff check .`
Expected: 全綠。

- [ ] **Step 5: Commit**

```bash
git add scripts/pipeline/retrieval.py scripts/tests/test_retrieval.py
git commit -m "feat(retrieval): candidate model, cross-dimension dedupe and facet coverage annotation

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

### Task 5: `retrieval.py` 的 DB 函式與整合測試

**Files:**
- Modify: `scripts/pipeline/retrieval.py`
- Modify: `scripts/tests/test_retrieval.py`

**Interfaces:**
- Consumes: `DimensionQuery`、`Candidate`、`band`、`dimension_facets`（Task 3、4）
- Produces:
  - `@dataclass DimensionHits(query: DimensionQuery, pool_size: int, hits: list[Candidate])`
  - `retrieve_presets(conn, queries: list[DimensionQuery], vectors: list[Vector], catalog, profile: str) -> list[DimensionHits]`（`queries` 與 `vectors` 一一對應）
  - `retrieve_histories(conn, qvec: Vector, profile: str, k: int) -> list[dict]`（key：`user_intent, positive_prompt, subject_profile, dist`）

- [ ] **Step 1: 加整合測試（追加到 `tests/test_retrieval.py`）**

```python
import pytest  # 檔頭
from pgvector import Vector  # 檔頭

from pipeline import db  # 檔頭
from pipeline.config import settings  # 檔頭
from pipeline.retrieval import DimensionQuery, retrieve_histories, retrieve_presets  # 檔頭


def _unit_vector() -> Vector:
    return Vector([1.0] + [0.0] * (settings.embedding_dimensions - 1))


def _conn_with_data():
    if not db.db_available():
        pytest.skip("PostgreSQL 未啟動")
    conn = db.connect()
    if conn.execute("SELECT count(*) FROM prompt_knowledge_presets").fetchone()[0] == 0:
        conn.close()
        pytest.skip("presets 表是空的")
    return conn


@pytest.mark.integration
def test_retrieve_presets_only_returns_rows_touching_the_dimension_and_respects_k():
    with _conn_with_data() as conn:
        v = _unit_vector()
        queries = [DimensionQuery("scene", "q", True, 5), DimensionQuery("style", "q", False, 3)]
        out = retrieve_presets(conn, queries, [v, v], CAT, "portrait")
        assert [dh.query.dimension for dh in out] == ["scene", "style"]
        for dh in out:
            allowed = set(dimension_facets(CAT, "portrait", dh.query.dimension))
            assert dh.pool_size >= len(dh.hits) > 0
            assert len(dh.hits) <= dh.query.k
            for c in dh.hits:
                assert allowed & set(c.preset["facet_ids"])
                assert c.dimension == dh.query.dimension
                assert c.grounded is dh.query.grounded
                assert c.band == band(c.dist)
                assert set(c.preset) == {"id", "title", "category", "facet_ids", "tags", "prompt_snippet", "negative_snippet"}


@pytest.mark.integration
def test_retrieve_presets_pool_differs_between_profiles():
    with _conn_with_data() as conn:
        v = _unit_vector()
        q = DimensionQuery("scene", "q", True, 5)
        portrait = retrieve_presets(conn, [q], [v], CAT, "portrait")[0]
        landscape = retrieve_presets(conn, [q], [v], CAT, "landscape")[0]
        # landscape 的 scene 多了 scene.season，候選池只會更大
        assert landscape.pool_size >= portrait.pool_size


@pytest.mark.integration
def test_retrieve_histories_filters_by_profile():
    with _conn_with_data() as conn:
        rows = retrieve_histories(conn, _unit_vector(), "portrait", 3)
        assert len(rows) <= 3
        assert all(r["subject_profile"] == "portrait" for r in rows)
        assert all(set(r) == {"user_intent", "positive_prompt", "subject_profile", "dist"} for r in rows)
```

- [ ] **Step 2: 跑整合測試確認失敗**

Run: `python -m pytest tests/test_retrieval.py -m integration -q`
Expected: `ImportError: cannot import name 'DimensionHits'`（DB 沒起來會是 skip，那就先 `docker compose up -d db` 再跑）

- [ ] **Step 3: 在 `retrieval.py` 檔尾加 DB 函式**

檔頭 import 加 `from pgvector import Vector`。

```python
# ---------- 以下吃 conn ----------

PRESETS_SQL = """
SELECT id, title, category, facet_ids, tags, prompt_snippet, negative_snippet,
       preset_embedding <=> %(q)s AS dist
FROM prompt_knowledge_presets
WHERE facet_ids && %(facets)s
ORDER BY dist
LIMIT %(k)s
"""

POOL_SQL = "SELECT count(*) FROM prompt_knowledge_presets WHERE facet_ids && %(facets)s"

HISTORIES_SQL = """
SELECT user_intent, positive_prompt, subject_profile,
       intent_embedding <=> %(q)s AS dist
FROM shared_prompt_histories
WHERE subject_profile = %(profile)s
ORDER BY dist
LIMIT %(k)s
"""


@dataclass
class DimensionHits:
    query: DimensionQuery
    pool_size: int
    hits: list[Candidate]


def retrieve_presets(
    conn, queries: list[DimensionQuery], vectors: list[Vector], catalog: FacetCatalog, profile: str
) -> list[DimensionHits]:
    """每句子查詢各跑一次：GIN 過濾該維度 facet（idx_presets_facet_ids）+ 向量排序。不設距離門檻。"""
    pool_cache: dict[str, int] = {}
    out: list[DimensionHits] = []
    for q, v in zip(queries, vectors, strict=True):
        facets = dimension_facets(catalog, profile, q.dimension)
        if q.dimension not in pool_cache:
            pool_cache[q.dimension] = conn.execute(POOL_SQL, {"facets": facets}).fetchone()[0]
        hits = [
            Candidate(
                preset={
                    "id": r[0], "title": r[1], "category": r[2], "facet_ids": list(r[3]), "tags": list(r[4]),
                    "prompt_snippet": r[5], "negative_snippet": r[6],
                },
                dimension=q.dimension, dist=float(r[7]), band=band(float(r[7])), grounded=q.grounded,
            )
            for r in conn.execute(PRESETS_SQL, {"q": v, "facets": facets, "k": q.k})
        ]
        out.append(DimensionHits(q, pool_cache[q.dimension], hits))
    return out


def retrieve_histories(conn, qvec: Vector, profile: str, k: int) -> list[dict]:
    """整段紀錄配整句向量，並依 ① 判定的 profile 過濾（主規格 §9 本來就有）。"""
    return [
        {"user_intent": r[0], "positive_prompt": r[1], "subject_profile": r[2], "dist": float(r[3])}
        for r in conn.execute(HISTORIES_SQL, {"q": qvec, "profile": profile, "k": k})
    ]
```

- [ ] **Step 4: 跑單元 + 整合測試 + ruff**

Run: `python -m pytest -q && python -m pytest tests/test_retrieval.py -m integration -q && python -m ruff check .`
Expected: 全綠（整合測試 3 passed，或 DB 未啟動時 3 skipped）。

- [ ] **Step 5: Commit**

```bash
git add scripts/pipeline/retrieval.py scripts/tests/test_retrieval.py
git commit -m "feat(retrieval): per-dimension preset search and profile-filtered history search

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

### Task 6: `demo.py` 兩段 schema、facet 狀態整理、驗證

從這個任務起 `demo.py` 重寫。舊的 `PRESETS_SQL`、`HISTORIES_SQL`、`DemoResult`、`retrieve`、`build_prompt`、`render`、`run_once` 全部刪除；本任務只放 schema 與純函式，prompt 與編排在 Task 7、9。

**Files:**
- Rewrite: `scripts/demo.py`
- Create: `scripts/tests/test_demo.py`

**Interfaces:**
- Consumes: `Candidate`（Task 4）、`FacetCatalog`
- Produces:
  - pydantic：`FacetAssessment(facet_id, state)`、`SubQuery(dimension, query)`、`AnalysisResult(subject_profile, facets, queries)`、`BorrowedFrom(preset_id, tags)`、`SuggestionOption(label, tags, preset_id)`、`DimensionSuggestion(dimension, missing_labels, options)`、`AssemblyResult(positive_prompt, negative_prompt, borrowed, suggestions)`
  - `facet_state_map(analysis: AnalysisResult, catalog, profile: str) -> dict[str, str]`
  - `missing_labels_by_dimension(states: dict[str, str], catalog) -> dict[str, list[str]]`
  - `validate_borrowed(assembly: AssemblyResult, by_id: dict[int, Candidate]) -> tuple[list[BorrowedFrom], list[str]]`
  - `validate_suggestions(assembly: AssemblyResult, by_id: dict[int, Candidate], missing_by_dim: dict[str, list[str]]) -> tuple[list[DimensionSuggestion], list[str]]`

- [ ] **Step 1: 寫失敗測試 `tests/test_demo.py`**

```python
from demo import (
    AnalysisResult,
    AssemblyResult,
    BorrowedFrom,
    DimensionSuggestion,
    FacetAssessment,
    SubQuery,
    SuggestionOption,
    facet_state_map,
    missing_labels_by_dimension,
    validate_borrowed,
    validate_suggestions,
)
from pipeline.config import FACETS_PATH
from pipeline.facets import load_facets
from pipeline.retrieval import Candidate, band

CAT = load_facets(FACETS_PATH)


def _cand(pid, dim, dist, snippet, negative=None, grounded=True, title=None):
    return Candidate(
        preset={
            "id": pid, "title": title or f"t{pid}", "category": dim.title(), "facet_ids": [], "tags": [],
            "prompt_snippet": snippet, "negative_snippet": negative,
        },
        dimension=dim, dist=dist, band=band(dist), grounded=grounded,
    )


def _assembly(**kw):
    base = dict(positive_prompt="1girl, purple hair, twintails", negative_prompt="bad anatomy", borrowed=[], suggestions=[])
    base.update(kw)
    return AssemblyResult(**base)


# ---------- facet 狀態整理 ----------


def test_facet_state_map_keeps_only_profile_facets_and_backfills_missing():
    analysis = AnalysisResult(
        subject_profile="landscape",
        facets=[
            FacetAssessment(facet_id="scene.location", state="covered"),
            FacetAssessment(facet_id="clothing.upper", state="covered"),  # landscape 沒有這個
            FacetAssessment(facet_id="not.real", state="covered"),
        ],
        queries=[],
    )
    states = facet_state_map(analysis, CAT, "landscape")
    assert states["scene.location"] == "covered"
    assert "clothing.upper" not in states and "not.real" not in states
    assert states["scene.season"] == "missing"  # LLM 漏判 → 保守補 missing
    assert set(states) == set(CAT.ids_for_profile("landscape"))


def test_missing_labels_by_dimension_groups_labels_in_catalog_order():
    states = {"style.genre": "missing", "style.palette": "missing", "scene.location": "covered", "scene.weather": "missing"}
    assert missing_labels_by_dimension(states, CAT) == {
        "style": ["藝術流派／媒材", "色調傾向"],
        "scene": ["天氣氛圍"],
    }


# ---------- validate_borrowed ----------


def test_validate_borrowed_keeps_tags_present_in_both_snippet_and_prompt():
    by_id = {144: _cand(144, "appearance", 0.19, "1girl, short twintails, pink hair, green eyes")}
    a = _assembly(borrowed=[BorrowedFrom(preset_id=144, tags=["twintails"])])
    kept, rejected = validate_borrowed(a, by_id)
    assert kept == [BorrowedFrom(preset_id=144, tags=["twintails"])]
    assert rejected == []


def test_validate_borrowed_rejects_a_tag_the_snippet_does_not_contain_without_touching_the_prompt():
    by_id = {483: _cand(483, "appearance", 0.21, "1girl, pink hair, purple eyes", title="粉髮紫瞳少女")}
    a = _assembly(borrowed=[BorrowedFrom(preset_id=483, tags=["purple hair"])])
    before = (a.positive_prompt, a.negative_prompt)
    kept, rejected = validate_borrowed(a, by_id)
    assert kept == []
    assert len(rejected) == 1
    assert "來源不符" in rejected[0] and "483" in rejected[0] and "粉髮紫瞳少女" in rejected[0]
    assert '"purple hair"' in rejected[0] and "提示詞不受影響" in rejected[0]
    assert (a.positive_prompt, a.negative_prompt) == before


def test_validate_borrowed_rejects_a_tag_that_is_not_actually_in_the_prompt():
    by_id = {144: _cand(144, "appearance", 0.19, "1girl, twintails, blush")}
    a = _assembly(borrowed=[BorrowedFrom(preset_id=144, tags=["blush"])])
    kept, rejected = validate_borrowed(a, by_id)
    assert kept == []
    assert "未出現在提示詞" in rejected[0] and '"blush"' in rejected[0]


def test_validate_borrowed_rejects_ungrounded_presets_entirely():
    by_id = {900: _cand(900, "style", 0.23, "anime, Makoto Shinkai Style", grounded=False)}
    a = _assembly(positive_prompt="1girl, anime", borrowed=[BorrowedFrom(preset_id=900, tags=["anime"])])
    kept, rejected = validate_borrowed(a, by_id)
    assert kept == []
    assert "未描述" in rejected[0] and "900" in rejected[0]


def test_validate_borrowed_rejects_unknown_preset_ids():
    a = _assembly(borrowed=[BorrowedFrom(preset_id=1, tags=["1girl"])])
    kept, rejected = validate_borrowed(a, {})
    assert kept == [] and "不在本次檢索結果" in rejected[0]


def test_validate_borrowed_is_case_insensitive_and_can_borrow_from_negative_snippet():
    by_id = {7: _cand(7, "scene", 0.2, "Night Sky", negative="Bad Anatomy")}
    a = _assembly(positive_prompt="night sky", negative_prompt="bad anatomy",
                  borrowed=[BorrowedFrom(preset_id=7, tags=["night sky", "bad anatomy"])])
    kept, rejected = validate_borrowed(a, by_id)
    assert kept == [BorrowedFrom(preset_id=7, tags=["night sky", "bad anatomy"])]
    assert rejected == []


def test_validate_borrowed_keeps_the_good_tags_and_reports_the_bad_ones_of_the_same_preset():
    by_id = {144: _cand(144, "appearance", 0.19, "1girl, twintails, pink hair")}
    a = _assembly(borrowed=[BorrowedFrom(preset_id=144, tags=["twintails", "purple hair"])])
    kept, rejected = validate_borrowed(a, by_id)
    assert kept == [BorrowedFrom(preset_id=144, tags=["twintails"])]
    assert len(rejected) == 1 and '"purple hair"' in rejected[0]


# ---------- validate_suggestions ----------


def test_validate_suggestions_keeps_options_with_known_sources_and_overrides_missing_labels_from_analysis():
    by_id = {900: _cand(900, "style", 0.23, "anime", grounded=False)}
    a = _assembly(suggestions=[DimensionSuggestion(
        dimension="style", missing_labels=["LLM 亂寫的"],
        options=[SuggestionOption(label="動漫", tags="anime", preset_id=900),
                 SuggestionOption(label="幽靈", tags="ghost", preset_id=999)],
    )])
    kept, rejected = validate_suggestions(a, by_id, {"style": ["藝術流派／媒材", "色調傾向"]})
    assert len(kept) == 1
    assert kept[0].missing_labels == ["藝術流派／媒材", "色調傾向"]
    assert [o.preset_id for o in kept[0].options] == [900]
    assert len(rejected) == 1 and "999" in rejected[0]


def test_validate_suggestions_can_source_from_grounded_presets():
    by_id = {7: _cand(7, "scene", 0.2, "full moon, mist", grounded=True)}
    a = _assembly(suggestions=[DimensionSuggestion(
        dimension="scene", missing_labels=[], options=[SuggestionOption(label="薄霧", tags="mist", preset_id=7)],
    )])
    kept, rejected = validate_suggestions(a, by_id, {"scene": ["天氣氛圍"]})
    assert len(kept) == 1 and rejected == []


def test_validate_suggestions_drops_a_dimension_that_has_nothing_missing():
    by_id = {7: _cand(7, "scene", 0.2, "mist")}
    a = _assembly(suggestions=[DimensionSuggestion(
        dimension="scene", missing_labels=[], options=[SuggestionOption(label="薄霧", tags="mist", preset_id=7)],
    )])
    kept, rejected = validate_suggestions(a, by_id, {})
    assert kept == [] and "沒有缺的 facet" in rejected[0]
```

- [ ] **Step 2: 跑測試確認失敗**

Run: `python -m pytest tests/test_demo.py -q`
Expected: `ImportError: cannot import name 'AnalysisResult'`

- [ ] **Step 3: 重寫 `scripts/demo.py`（本任務的版本；Task 7、9 會在檔尾追加）**

```python
"""單輪示範：中文描述 → ① 分析 → ② 分維度檢索 → ③ Gemini 組裝 → 英文 prompt + 六維度燈號 + 建議。

這是子專案 1 的成果展示，也是子專案 2 互動流程的縮小版：它只跑一輪、不追問。
用法：
    python demo.py "昏暗雨夜的科幻城市，一個穿皮夾克的短髮女生"
    python demo.py "山上的日出" --k-covered 8 --verbose --no-color
不給描述就進入互動模式，可以連續輸入，Ctrl+C 離開。
設計見 docs/superpowers/specs/2026-09-22-dimension-scoped-retrieval-design.md
"""

from __future__ import annotations

import logging
import sys
from pathlib import Path
from typing import Literal

# google-genai 每次 generate_content 都會印一段 AFC 的提醒；對這支 demo 是純噪音。
logging.getLogger("google_genai").setLevel(logging.ERROR)

sys.path.insert(0, str(Path(__file__).resolve().parent))

from pydantic import BaseModel, Field  # noqa: E402

from pipeline.facets import FacetCatalog  # noqa: E402
from pipeline.retrieval import Candidate  # noqa: E402

Profile = Literal["portrait", "landscape", "object", "vehicle"]
FacetState = Literal["covered", "missing", "notApplicable"]
Dimension = Literal["style", "scene", "camera", "appearance", "pose", "clothing"]


# ---------- ① 分析 ----------


class FacetAssessment(BaseModel):
    facet_id: str = Field(description="必須是清單中的 id")
    state: FacetState


class SubQuery(BaseModel):
    dimension: Dimension
    query: str = Field(description="繁體中文，供向量檢索用")


class AnalysisResult(BaseModel):
    subject_profile: Profile
    facets: list[FacetAssessment]
    queries: list[SubQuery]


# ---------- ③ 組裝 ----------


class BorrowedFrom(BaseModel):
    preset_id: int
    tags: list[str] = Field(description="真的寫進提示詞、且真的來自這筆片段的 tag")


class SuggestionOption(BaseModel):
    label: str = Field(description="繁體中文方向名")
    tags: str = Field(description="可直接貼上的英文 tag")
    preset_id: int = Field(description="來源片段 id")


class DimensionSuggestion(BaseModel):
    dimension: Dimension
    missing_labels: list[str]
    options: list[SuggestionOption] = Field(description="2–3 個不同方向")


class AssemblyResult(BaseModel):
    positive_prompt: str = Field(description="英文，逗號分隔 tag")
    negative_prompt: str = Field(description="英文，逗號分隔 tag")
    borrowed: list[BorrowedFrom]
    suggestions: list[DimensionSuggestion]


# ---------- 純函式：狀態整理與驗證 ----------


def facet_state_map(analysis: AnalysisResult, catalog: FacetCatalog, profile: str) -> dict[str, str]:
    """只留 profile 適用的 facet；LLM 漏判的補 missing（保守：寧可多報缺也不假裝有）。"""
    applicable = catalog.ids_for_profile(profile)
    states = {a.facet_id: a.state for a in analysis.facets if a.facet_id in applicable}
    for fid in applicable:
        states.setdefault(fid, "missing")
    return states


def missing_labels_by_dimension(states: dict[str, str], catalog: FacetCatalog) -> dict[str, list[str]]:
    out: dict[str, list[str]] = {}
    for f in catalog.facets.values():
        if states.get(f.id) == "missing":
            out.setdefault(f.dimension, []).append(f.label)
    return out


def validate_borrowed(assembly: AssemblyResult, by_id: dict[int, Candidate]) -> tuple[list[BorrowedFrom], list[str]]:
    """驗的是「來源」不是「正確性」。只改 borrowed 紀錄，絕不動提示詞。
    每個 tag 必須同時出現在片段（真的來自這裡）與提示詞（真的用了）。"""
    kept: list[BorrowedFrom] = []
    rejected: list[str] = []
    prompt_text = f"{assembly.positive_prompt}\n{assembly.negative_prompt}".lower()
    for b in assembly.borrowed:
        c = by_id.get(b.preset_id)
        if c is None:
            rejected.append(f"id {b.preset_id} 不在本次檢索結果內，整筆不計入借用")
            continue
        if not c.grounded:
            rejected.append(f"id {c.id}〈{c.preset['title']}〉屬於使用者未描述的維度（{c.dimension}），只能進建議，不計入借用")
            continue
        source = f"{c.preset['prompt_snippet']}\n{c.preset['negative_snippet'] or ''}".lower()
        ok: list[str] = []
        for raw in b.tags:
            tag = raw.strip()
            if not tag:
                continue
            if tag.lower() not in source:
                rejected.append(f'來源不符：id {c.id}〈{c.preset["title"]}〉沒有 "{tag}"，不計入借用（提示詞不受影響）')
            elif tag.lower() not in prompt_text:
                rejected.append(f'id {c.id}〈{c.preset["title"]}〉的 "{tag}" 未出現在提示詞裡，不計入借用')
            else:
                ok.append(tag)
        if ok:
            kept.append(BorrowedFrom(preset_id=c.id, tags=ok))
    return kept, rejected


def validate_suggestions(
    assembly: AssemblyResult, by_id: dict[int, Candidate], missing_by_dim: dict[str, list[str]]
) -> tuple[list[DimensionSuggestion], list[str]]:
    """來源不受 grounded 限制（閘門只管提示詞）；missing_labels 以 ① 為準覆寫 LLM 給的。"""
    kept: list[DimensionSuggestion] = []
    rejected: list[str] = []
    for s in assembly.suggestions:
        truth = missing_by_dim.get(s.dimension)
        if not truth:
            rejected.append(f"建議：{s.dimension} 沒有缺的 facet，不該有建議，整則移除")
            continue
        options: list[SuggestionOption] = []
        for o in s.options:
            if o.preset_id in by_id:
                options.append(o)
            else:
                rejected.append(f"建議「{o.label}」的來源 id {o.preset_id} 不在本次檢索結果內，移除")
        if options:
            kept.append(DimensionSuggestion(dimension=s.dimension, missing_labels=truth, options=options))
    return kept, rejected
```

**import 只放本任務真的用到的東西**（上面那份清單已經是完整的）。`argparse`、`Vector`、`demo_render` 的所有名稱、`FACETS_PATH`、`connect`、`load_facets`、`retrieval` 的其餘名稱都由 Task 9 在需要時加入。**任何情況下都不要寫 `# noqa: F401`** —— 檔頭那幾個 `# noqa: E402` 是必要的（因為 `sys.path.insert` 必須在 import 之前），F401 則代表匯入了用不到的東西，是要避免而不是壓抑的。

- [ ] **Step 4: `demo_render.py` 先補 Task 8 會用到的 View 型別空殼，讓 import 過**

在 `demo_render.py` 檔尾加（Task 8 會補 `render*` 函式）：

```python
# ---------- 給 render() 的資料 ----------


@dataclass
class BorrowedView:
    band: str
    dist: float
    title: str
    category: str
    tags: list[str]


@dataclass
class OptionView:
    label: str
    tags: str
    source_title: str


@dataclass
class SuggestionView:
    dimension_label: str
    missing_labels: list[str]
    options: list[OptionView]


@dataclass
class DemoView:
    profile: str
    facet_states: dict[str, str]
    positive_prompt: str
    negative_prompt: str
    borrowed: list[BorrowedView]
    rejections: list[str]
    suggestions: list[SuggestionView]
```

檔頭加 `from dataclasses import dataclass`。這四個 View 型別本任務只是定義，`demo.py` 還不會 import 它們（Task 9 才會）；`render*` 系列函式由 Task 8 補上。

- [ ] **Step 5: 跑測試與 ruff**

Run: `python -m pytest -q && python -m ruff check .`
Expected: 全綠。

- [ ] **Step 6: Commit**

```bash
git add scripts/demo.py scripts/demo_render.py scripts/tests/test_demo.py
git commit -m "feat(demo): two-pass schemas, facet state map and provenance validation of borrowed tags

Validation edits only the borrowed/suggestions records and never the
prompts; a rejected attribution is reported as a source mismatch.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

### Task 7: `demo.py` 兩個 prompt 與候選格式化

**Files:**
- Modify: `scripts/demo.py`
- Modify: `scripts/tests/test_demo.py`

**Interfaces:**
- Consumes: `Candidate`、`FacetCatalog.prompt_listing()`
- Produces:
  - `format_facet_states(states: dict[str, str], catalog, profile) -> str`
  - `format_candidates(cands: list[Candidate]) -> str`
  - `format_histories(histories: list[dict]) -> str`
  - `build_analysis_prompt(query: str, catalog) -> str`
  - `build_assembly_prompt(query, profile, states, cands, histories, catalog) -> str`

- [ ] **Step 1: 加失敗測試（追加到 `tests/test_demo.py`）**

```python
from demo import build_analysis_prompt, build_assembly_prompt, format_candidates, format_facet_states  # 檔頭


def test_analysis_prompt_lists_facets_and_states_the_query_rules():
    text = build_analysis_prompt("夜晚的湖畔", CAT)
    assert "scene.location" in text and "夜晚的湖畔" in text
    assert "逐字使用使用者的原話" in text  # covered 維度 1 句
    assert "對比" in text and "2 句" in text  # missing 維度 2 句對比


def test_format_facet_states_only_lists_the_profile_facets_grouped_by_dimension():
    states = {"scene.location": "covered", "scene.weather": "missing", "style.genre": "missing"}
    text = format_facet_states(states, CAT, "landscape")
    assert "scene.location（地點類型）：covered" in text
    assert "scene.weather（天氣氛圍）：missing" in text
    assert "[scene] 場景" in text
    assert "clothing" not in text


def test_format_candidates_marks_band_usage_and_facet_coverage():
    a = Candidate(
        preset={"id": 144, "title": "雙馬尾少女", "category": "Appearance",
                "facet_ids": ["appearance.hair", "appearance.face"], "tags": [],
                "prompt_snippet": "1girl, twintails", "negative_snippet": None},
        dimension="appearance", dist=0.19, band="高", grounded=True,
        facet_coverage={"appearance.hair": "covered", "appearance.face": "missing"},
    )
    b = Candidate(
        preset={"id": 900, "title": "新海誠動畫風", "category": "Style", "facet_ids": ["style.reference"], "tags": [],
                "prompt_snippet": "(Makoto Shinkai Style:1.4)", "negative_snippet": "lowres"},
        dimension="style", dist=0.234, band="高", grounded=False, facet_coverage={"style.reference": "missing"},
    )
    text = format_candidates([a, b])
    assert "id=144" in text and "〈雙馬尾少女〉" in text and "相似度：高（0.190）" in text
    assert "可借入提示詞" in text and "僅供建議" in text
    assert "appearance.hair=covered" in text and "appearance.face=missing" in text
    assert "negative: lowres" in text and "negative: (無)" in text


def test_assembly_prompt_contains_every_required_rule_and_all_blocks():
    text = build_assembly_prompt("夜晚的湖畔", "portrait", {"scene.location": "covered"}, [], [], CAT)
    for needle in ("完整", "複合", "split-color hair", "不要自行發明", "僅供建議", "低", "borrowed", "suggestions", "2–3"):
        assert needle in text, needle
    assert "題材：portrait" in text and "夜晚的湖畔" in text
    assert "（無）" in text  # 候選與相似作品都空
```

- [ ] **Step 2: 跑測試確認失敗**

Run: `python -m pytest tests/test_demo.py -q`
Expected: `ImportError: cannot import name 'build_analysis_prompt'`

- [ ] **Step 3: 在 `demo.py` 的驗證函式之後加 prompt 與格式化**

```python
# ---------- prompt ----------

ANALYSIS_TEMPLATE = """你是 AI 生圖提示詞助理的「分析」階段。使用者用繁體中文描述想要的畫面，你只做分析，不產出提示詞。

1. 判斷 subject_profile：portrait（畫面主體是人）、landscape（風景）、object（靜物；**動物也歸在這一類**）、\
vehicle（載具）。
2. 對下面清單裡的**每一個** facet 判斷狀態，一個都不能漏：
   - covered：使用者的描述已經提供了這項資訊
   - missing：這項對本題材有意義，但使用者沒有提到
   - notApplicable：這項對本題材根本不適用（例如風景沒有「人物穿著」）
3. 產出 queries，每筆是 {{dimension, query}}，query 為繁體中文，供向量檢索用：
   - 有任一 facet 為 covered 的維度：給 **1 句**，**逐字使用使用者的原話**（只挑出屬於這個維度的那部分），不改寫、不補充
   - 全部 facet 為 missing 的維度：給 **2 句**，依整體畫面推想這個維度可能的方向，兩句必須是**對比**的方向\
（例如「寫實攝影」對「動漫插畫」），不是同一方向的兩種說法
   - 全部 facet 為 notApplicable 的維度：不給

Facet 清單：
{facets}

使用者的描述：
{query}
"""

ASSEMBLY_TEMPLATE = """你是 AI 生圖提示詞助理的「組裝」階段。分析階段已判定題材與每個 facet 的狀態，並從知識庫撈出候選片段。\
你要產出可直接使用的 Stable Diffusion / SDXL 提示詞（英文、逗號分隔的 tag 風格）。

規則：
1. 使用者已描述的內容必須**完整**反映在提示詞裡。
2. **複合屬性用複合 tag 完整表達**：雙色髮、半邊、漸層、混色這類，要寫成像 `split-color hair, two-tone hair, \
purple hair, pink hair` 這種能讓生圖模型理解結構的組合，不可被候選片段裡的單色詞（如 `pink hair`）取代或吃掉一半。\
知識庫沒有的詞由你自己翻譯，不因為片段裡沒有就省略。
3. 標為 missing 的 facet **不要自行發明**，留白交給生圖模型；基礎畫質詞與基礎負向詞例外（慣例 boilerplate）。
4. 候選片段每筆都標了「可借入提示詞」或「僅供建議」，以及它涉及的 facet 對本次使用者是 covered 還是 missing：
   - 「僅供建議」的片段，任何詞都不可寫進提示詞
   - 「可借入提示詞」的片段，標 missing 的 facet 對應的詞也不可寫進提示詞，只可進建議
   - 相似度「低」的片段仍可借用其中與使用者描述相符的詞（例如 `sitting on the mushroom` 裡的 `sitting`），\
但不可借入與描述矛盾的詞
5. borrowed 只列**真的寫進提示詞**且**真的來自該片段**的詞；每個詞要跟片段裡的寫法一致。
6. suggestions：每個有 missing facet 的維度都要給一則，missing_labels 逐一點名該維度缺的 facet，\
options 給 2–3 個**不同方向**的選項，每個選項的 tags 是可直接貼的英文、preset_id 是來源片段。\
沒有 missing facet 的維度不要給。

題材：{profile}

Facet 狀態：
{facet_states}

候選片段（依維度分組，相似度依向量距離分級）：
{candidates}

類似的既有作品（僅供參考風格，不要照抄）：
{histories}

使用者的描述：
{query}
"""


def format_facet_states(states: dict[str, str], catalog: FacetCatalog, profile: str) -> str:
    lines: list[str] = []
    for dim, ids in catalog.profiles[profile].items():
        lines.append(f"[{dim}] {catalog.dimensions[dim]}")
        for fid in ids:
            lines.append(f"  - {fid}（{catalog.facets[fid].label}）：{states.get(fid, 'missing')}")
    return "\n".join(lines)


def format_candidates(cands: list[Candidate]) -> str:
    if not cands:
        return "  （無）"
    lines: list[str] = []
    for c in cands:
        usage = "可借入提示詞" if c.grounded else "僅供建議"
        coverage = ", ".join(f"{k}={v}" for k, v in c.facet_coverage.items()) or "(無)"
        lines.append(f"  id={c.id} 〈{c.preset['title']}〉[{c.dimension}] 相似度：{c.band}（{c.dist:.3f}） {usage}")
        lines.append(f"      facets: {coverage}")
        lines.append(f"      positive: {c.preset['prompt_snippet']}")
        lines.append(f"      negative: {c.preset['negative_snippet'] or '(無)'}")
    return "\n".join(lines)


def format_histories(histories: list[dict]) -> str:
    if not histories:
        return "  （無）"
    return "\n".join(
        f"  ({x['subject_profile']}) {x['user_intent']}\n      {x['positive_prompt'][:150]}" for x in histories
    )


def build_analysis_prompt(query: str, catalog: FacetCatalog) -> str:
    return ANALYSIS_TEMPLATE.format(facets=catalog.prompt_listing(), query=query)


def build_assembly_prompt(
    query: str,
    profile: str,
    states: dict[str, str],
    cands: list[Candidate],
    histories: list[dict],
    catalog: FacetCatalog,
) -> str:
    return ASSEMBLY_TEMPLATE.format(
        profile=profile,
        facet_states=format_facet_states(states, catalog, profile),
        candidates=format_candidates(cands),
        histories=format_histories(histories),
        query=query,
    )
```

注意 `ANALYSIS_TEMPLATE` 裡的 `{{dimension, query}}` 是雙大括號——它會被 `.format()` 轉成字面 `{dimension, query}`。

- [ ] **Step 4: 跑測試與 ruff**

Run: `python -m pytest -q && python -m ruff check .`
Expected: 全綠。

- [ ] **Step 5: Commit**

```bash
git add scripts/demo.py scripts/tests/test_demo.py
git commit -m "feat(demo): analysis and assembly prompts with band and facet-coverage annotated candidates

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

### Task 8: `demo_render.py` 新區塊

**Files:**
- Modify: `scripts/demo_render.py`
- Modify: `scripts/tests/test_demo_render.py`

**Interfaces:**
- Consumes: `DimensionQuery`、`DimensionHits`、`Candidate`（retrieval）、`DemoView` 系列（Task 6 已建）
- Produces:
  - `render_queries(queries: list[DimensionQuery], catalog) -> str`
  - `render_retrieval_summary(hits: list[DimensionHits], catalog, n_histories: int, profile: str) -> str`
  - `render_verbose(cands: list[Candidate], catalog, p: Palette) -> str`
  - `render(view: DemoView, catalog, p: Palette) -> str`

- [ ] **Step 1: 加失敗測試（追加到 `tests/test_demo_render.py`）**

```python
from demo_render import (  # 檔頭 import 加這些
    BorrowedView,
    DemoView,
    OptionView,
    Palette,
    SuggestionView,
    render,
    render_queries,
    render_retrieval_summary,
    render_verbose,
)
from pipeline.retrieval import Candidate, DimensionHits, DimensionQuery, band  # 檔頭

P = Palette(False)


def _cand(pid, dim, dist, grounded=True, coverage=None, title=None):
    return Candidate(
        preset={"id": pid, "title": title or f"t{pid}", "category": dim.title(), "facet_ids": [], "tags": [],
                "prompt_snippet": "1girl, twintails", "negative_snippet": None},
        dimension=dim, dist=dist, band=band(dist), grounded=grounded, facet_coverage=coverage or {},
    )


def test_render_queries_separates_user_words_from_guesses():
    qs = [DimensionQuery("scene", "夜晚的湖畔", True, 5), DimensionQuery("style", "動漫", False, 3),
          DimensionQuery("style", "寫實", False, 3)]
    text = render_queries(qs, CAT)
    assert "子查詢：場景「夜晚的湖畔」" in text
    assert "推想：風格「動漫」 風格「寫實」" in text


def test_render_retrieval_summary_reports_pool_hits_and_bands_per_dimension_merging_multi_query_dimensions():
    hits = [
        DimensionHits(DimensionQuery("scene", "q", True, 5), 292, [_cand(1, "scene", 0.26), _cand(2, "scene", 0.31)]),
        DimensionHits(DimensionQuery("style", "a", False, 3), 238, [_cand(3, "style", 0.2)]),
        DimensionHits(DimensionQuery("style", "b", False, 3), 238, [_cand(4, "style", 0.2), _cand(5, "style", 0.29)]),
    ]
    text = render_retrieval_summary(hits, CAT, 3, "portrait")
    assert "風格 池 238 → 3（高 2 中 1）" in text
    assert "場景 池 292 → 2（中 1 低 1）" in text
    assert text.index("風格") < text.index("場景")  # 依 DIMENSIONS 順序，不是輸入順序
    assert "相似作品 3（portrait）" in text


def test_render_verbose_lists_every_candidate_with_usage_and_coverage():
    cands = [_cand(1, "scene", 0.26, coverage={"scene.lighting": "covered"}),
             _cand(9, "style", 0.2, grounded=False, coverage={"style.genre": "missing"})]
    text = render_verbose(cands, CAT, P)
    assert "[場景]" in text and "[風格]" in text
    assert "id=1" in text and "可借入" in text and "scene.lighting=covered" in text
    assert "id=9" in text and "僅供建議" in text


def _view(**kw):
    base = dict(
        profile="portrait",
        facet_states={"appearance.hair": "covered", "appearance.face": "missing", "style.genre": "missing"},
        positive_prompt="1girl, purple hair, twintails",
        negative_prompt="bad anatomy",
        borrowed=[], rejections=[], suggestions=[],
    )
    base.update(kw)
    return DemoView(**base)


def test_render_shows_borrowed_tags_with_band_and_rejections_verbatim():
    view = _view(
        borrowed=[BorrowedView("高", 0.19, "粉紅雙馬尾少女", "Appearance", ["twintails", "green eyes"])],
        rejections=['來源不符：id 483〈粉髮紫瞳少女〉沒有 "purple hair"，不計入借用（提示詞不受影響）'],
    )
    text = render(view, CAT, P)
    assert "[高 0.190] 粉紅雙馬尾少女（Appearance）→ 借入 twintails, green eyes" in text
    assert '✗ 來源不符：id 483〈粉髮紫瞳少女〉沒有 "purple hair"，不計入借用（提示詞不受影響）' in text
    assert "1/2" in text  # 人物樣貌 1/2
    assert "沒有借用" not in text


def test_render_shows_one_suggestion_block_per_dimension_naming_missing_facets_and_lettered_options():
    view = _view(suggestions=[SuggestionView(
        "風格", ["藝術流派／媒材", "色調傾向"],
        [OptionView("新海誠風", "Makoto Shinkai Style, Soft Realism", "新海誠動畫風"),
         OptionView("寫實夜景攝影", "photo realism", "寫實攝影")],
    )])
    text = render(view, CAT, P)
    assert "風格（缺：藝術流派／媒材、色調傾向）" in text
    assert "A. 新海誠風" in text and "Makoto Shinkai Style, Soft Realism" in text and "〈新海誠動畫風〉" in text
    assert "B. 寫實夜景攝影" in text


def test_render_says_so_when_nothing_was_borrowed_and_nothing_is_missing():
    view = _view(facet_states={"appearance.hair": "covered"})
    text = render(view, CAT, P)
    assert "沒有借用" in text
    assert "沒有建議" in text
```

- [ ] **Step 2: 跑測試確認失敗**

Run: `python -m pytest tests/test_demo_render.py -q`
Expected: `ImportError: cannot import name 'render'`

- [ ] **Step 3: 在 `demo_render.py` 加 render 函式（放在 View 型別之後）**

檔頭 import 加：

```python
from collections import Counter

from pipeline.facets import FacetCatalog
from pipeline.retrieval import DIMENSIONS, Candidate, DimensionHits, DimensionQuery
```

```python
# ---------- 進度列 ----------


def render_queries(queries: list[DimensionQuery], catalog: FacetCatalog) -> str:
    def fmt(qs: list[DimensionQuery]) -> str:
        return " ".join(f"{catalog.dimensions[q.dimension]}「{q.query}」" for q in qs)

    grounded = [q for q in queries if q.grounded]
    guessed = [q for q in queries if not q.grounded]
    parts = []
    if grounded:
        parts.append("子查詢：" + fmt(grounded))
    if guessed:
        parts.append("推想：" + fmt(guessed))
    return "      " + ("；".join(parts) if parts else "（沒有子查詢）")


def render_retrieval_summary(hits: list[DimensionHits], catalog: FacetCatalog, n_histories: int, profile: str) -> str:
    """每維度：池大小 → 命中數（高/中/低各幾筆）。同維度兩句子查詢合併計數。知識庫缺口在這裡直接暴露。"""
    per: dict[str, tuple[int, Counter[str]]] = {}
    for dh in hits:
        pool, cnt = per.get(dh.query.dimension, (dh.pool_size, Counter()))
        cnt.update(c.band for c in dh.hits)
        per[dh.query.dimension] = (pool, cnt)
    cells = []
    for dim in DIMENSIONS:
        if dim not in per:
            continue
        pool, cnt = per[dim]
        grades = " ".join(f"{b} {cnt[b]}" for b in ("高", "中", "低") if cnt[b])
        cells.append(f"{catalog.dimensions[dim]} 池 {pool} → {sum(cnt.values())}（{grades or '無'}）")
    first = "      " + ("  ".join(cells) if cells else "（沒有維度可檢索）")
    return f"{first}\n      相似作品 {n_histories}（{profile}）"


def render_verbose(cands: list[Candidate], catalog: FacetCatalog, p: Palette) -> str:
    lines = [p.head("━━ 全部候選（去重後）━━")]
    for dim in DIMENSIONS:
        group = [c for c in cands if c.dimension == dim]
        if not group:
            continue
        lines.append(f"  [{catalog.dimensions[dim]}]")
        for c in group:
            usage = "可借入" if c.grounded else "僅供建議"
            coverage = ", ".join(f"{k}={v}" for k, v in c.facet_coverage.items()) or "(無)"
            lines.append(f"    [{c.band} {c.dist:.3f}] id={c.id} {c.preset['title']}  {usage}")
            lines.append(p.dim(f"        facets: {coverage}"))
            lines.append(p.dim(f"        {c.preset['prompt_snippet'][:76]}"))
    return "\n".join(lines)


# ---------- 最終畫面 ----------


def render(view: DemoView, catalog: FacetCatalog, p: Palette) -> str:
    grouped = group_by_dimension(view.facet_states, catalog)
    lines = ["", p.head("━━ 題材判定 ━━"), f"  {view.profile}", "", p.head("━━ 六維度充足度 ━━")]
    for key in DIMENSIONS:
        row = render_dimension_row(catalog.dimensions.get(key, key), grouped[key])
        lines.append(p.dim(row) if "不適用" in row else row)

    lines += ["", p.head("━━ 正向提示詞 ━━"), p.ok(wrap_tags(view.positive_prompt))]
    lines += ["", p.head("━━ 負向提示詞 ━━"), p.warn(wrap_tags(view.negative_prompt))]

    lines += ["", p.head("━━ 借用的知識庫片段 ━━")]
    for b in view.borrowed:
        lines.append(f"  [{b.band} {b.dist:.3f}] {b.title}（{b.category}）→ 借入 {', '.join(b.tags)}")
    for r in view.rejections:
        lines.append(p.warn(f"  ✗ {r}"))
    if not view.borrowed and not view.rejections:
        lines.append(p.dim("  （這次沒有借用檢索到的片段）"))

    lines += ["", p.head("━━ 建議 ━━")]
    if view.suggestions:
        for s in view.suggestions:
            lines.append(f"  {s.dimension_label}（缺：{'、'.join(s.missing_labels)}）")
            for letter, o in zip("ABCDEFG", s.options, strict=False):
                lines.append(f"    {letter}. {pad(o.label, 14)} {o.tags}   〈{o.source_title}〉")
    else:
        lines.append(p.dim("  （所有維度都已覆蓋，沒有建議）"))
    lines.append("")
    return "\n".join(lines)
```

- [ ] **Step 4: 跑測試與 ruff**

Run: `python -m pytest -q && python -m ruff check .`
Expected: 全綠。

- [ ] **Step 5: Commit**

```bash
git add scripts/demo_render.py scripts/tests/test_demo_render.py
git commit -m "feat(demo): render per-dimension retrieval summary, borrowed tags, rejections and suggestions

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

### Task 9: `demo.py` 編排、CLI、端到端測試

**Files:**
- Modify: `scripts/demo.py`
- Modify: `scripts/tests/test_demo.py`

**Interfaces:**
- Consumes: 全部前面任務
- Produces:
  - `build_view(profile, states, assembly, kept_borrowed, rejections, kept_suggestions, by_id, catalog) -> DemoView`
  - `run_once(query, conn, client, catalog, args, p) -> None`；`args` 需有 `k_covered, k_missing, top_histories, verbose`
  - `main(argv=None)`；CLI：`query`（可省略）、`--k-covered 5`、`--k-missing 3`、`--top-histories 3`、`--verbose`、`--no-color`

- [ ] **Step 1: 加端到端測試（追加到 `tests/test_demo.py`）**

```python
import json  # 檔頭
from types import SimpleNamespace  # 檔頭

import demo  # 檔頭（用 module 名稱，方便 monkeypatch）
from demo_render import Palette  # 檔頭
from pipeline.gemini_client import GeminiClient  # 檔頭
from pipeline.ratelimit import RateLimiter  # 檔頭
from pipeline.retrieval import DimensionHits  # 檔頭


class ScriptedModels:
    """generate_content 依序回傳預先寫好的 JSON；embed_content 回固定 2 維向量。"""

    def __init__(self, replies: list[str]):
        self.replies = list(replies)
        self.generate_calls: list[str] = []
        self.embed_calls: list[list[str]] = []

    def generate_content(self, *, model, contents, config):
        self.generate_calls.append(contents)
        return SimpleNamespace(text=self.replies.pop(0))

    def embed_content(self, *, model, contents, config):
        self.embed_calls.append(list(contents))
        return SimpleNamespace(embeddings=[SimpleNamespace(values=[1.0, 0.0]) for _ in contents])


def _client(models):
    return GeminiClient(SimpleNamespace(models=models), structure_model="s", embedding_model="e", dimensions=2,
                        limiter=RateLimiter(0, sleep=lambda s: None), sleep=lambda s: None)


QUERY = "夜晚的湖畔，一個紫色雙馬尾的少女"

ANALYSIS_JSON = json.dumps({
    "subject_profile": "portrait",
    "facets": [
        {"facet_id": "appearance.hair", "state": "covered"},
        {"facet_id": "scene.location", "state": "covered"},
    ],
    "queries": [
        {"dimension": "appearance", "query": "紫色雙馬尾的少女"},
        {"dimension": "scene", "query": "夜晚的湖畔"},
        {"dimension": "style", "query": "動漫插畫"},
        {"dimension": "style", "query": "寫實攝影"},
        {"dimension": "clothing", "query": "休閒穿搭"},  # clothing 全 missing，只給 1 句也合法
    ],
}, ensure_ascii=False)

ASSEMBLY_JSON = json.dumps({
    "positive_prompt": "masterpiece, 1girl, purple hair, twintails, night lakeside",
    "negative_prompt": "bad anatomy, lowres",
    "borrowed": [
        {"preset_id": 144, "tags": ["twintails", "purple hair"]},
        {"preset_id": 900, "tags": ["anime"]},
    ],
    "suggestions": [
        {"dimension": "style", "missing_labels": ["whatever"],
         "options": [{"label": "新海誠風", "tags": "Makoto Shinkai Style", "preset_id": 900}]},
    ],
}, ensure_ascii=False)


def _fake_retrieve_presets(conn, queries, vectors, catalog, profile):
    assert conn is None and len(queries) == len(vectors)
    out = []
    for q in queries:
        if q.dimension == "appearance":
            hits = [Candidate(preset={"id": 144, "title": "雙馬尾少女", "category": "Appearance",
                                      "facet_ids": ["appearance.hair"], "tags": [],
                                      "prompt_snippet": "1girl, twintails, grey eyes", "negative_snippet": None},
                              dimension="appearance", dist=0.19, band="高", grounded=q.grounded)]
        elif q.dimension == "style":
            hits = [Candidate(preset={"id": 900, "title": "新海誠動畫風", "category": "Style",
                                      "facet_ids": ["style.reference"], "tags": [],
                                      "prompt_snippet": "anime, Makoto Shinkai Style", "negative_snippet": None},
                              dimension="style", dist=0.23, band="高", grounded=q.grounded)]
        else:
            hits = []
        out.append(DimensionHits(q, 100, hits))
    return out


def test_run_once_end_to_end(monkeypatch, capsys):
    monkeypatch.setattr(demo, "retrieve_presets", _fake_retrieve_presets)
    monkeypatch.setattr(demo, "retrieve_histories", lambda conn, qvec, profile, k: [])
    models = ScriptedModels([ANALYSIS_JSON, ASSEMBLY_JSON])
    args = SimpleNamespace(k_covered=5, k_missing=3, top_histories=3, verbose=False)

    demo.run_once(QUERY, None, _client(models), CAT, args, Palette(False))
    out = capsys.readouterr().out

    # 兩段依序呼叫；embed 只呼叫一次，內容是整句 + 整理後的子查詢
    assert len(models.generate_calls) == 2
    assert models.embed_calls == [[QUERY, "紫色雙馬尾的少女", "夜晚的湖畔", "動漫插畫", "寫實攝影", "休閒穿搭"]]
    # ① 的 facet 狀態原封到畫面：人物樣貌 1/5、場景 1/6
    assert "1/5" in out and "1/6" in out
    # ③ 的候選清單標了用途
    assert "id=144" in models.generate_calls[1] and "可借入提示詞" in models.generate_calls[1]
    assert "id=900" in models.generate_calls[1] and "僅供建議" in models.generate_calls[1]
    # 驗證結果：twintails 借入成立；purple hair 來源不符；900 是未描述維度
    assert "→ 借入 twintails" in out
    assert '來源不符：id 144〈雙馬尾少女〉沒有 "purple hair"' in out
    assert "id 900〈新海誠動畫風〉屬於使用者未描述的維度" in out
    # 建議：missing_labels 被 ① 的真相覆寫
    assert "風格（缺：藝術流派／媒材、參照畫師或作品、渲染引擎／技術風格詞、色調傾向）" in out
    assert "A. 新海誠風" in out and "Makoto Shinkai Style" in out and "〈新海誠動畫風〉" in out
    # 提示詞原封輸出
    assert "purple hair" in out and "night lakeside" in out


def test_main_parses_new_flags(monkeypatch):
    captured = {}

    def fake_run_once(query, conn, client, catalog, args, p):
        captured.update(query=query, args=args)

    monkeypatch.setattr(demo, "run_once", fake_run_once)
    monkeypatch.setattr(demo, "connect", lambda: contextlib.nullcontext(None))  # 檔頭需 import contextlib
    monkeypatch.setattr("pipeline.gemini_client.default_client", lambda: object())
    demo.main(["山上的日出", "--k-covered", "7", "--k-missing", "2", "--top-histories", "1", "--verbose", "--no-color"])
    assert captured["query"] == "山上的日出"
    a = captured["args"]
    assert (a.k_covered, a.k_missing, a.top_histories, a.verbose) == (7, 2, 1, True)
```

- [ ] **Step 2: 跑測試確認失敗**

Run: `python -m pytest tests/test_demo.py -q`
Expected: `AttributeError: module 'demo' has no attribute 'run_once'`

- [ ] **Step 3: 在 `demo.py` 檔尾加編排與 CLI，並補齊 import**

Task 6 只匯入了它自己用到的東西。本任務在 `demo.py` 檔頭補上（維持 isort 順序、每行帶 `# noqa: E402`）：

```python
import argparse            # 與 logging / sys 並列在最上面

from pgvector import Vector  # noqa: E402

from demo_render import (  # noqa: E402
    BorrowedView,
    DemoView,
    OptionView,
    Palette,
    SuggestionView,
    colors_enabled,
    render,
    render_queries,
    render_retrieval_summary,
    render_verbose,
)
from pipeline.config import FACETS_PATH  # noqa: E402
from pipeline.db import connect  # noqa: E402
from pipeline.facets import FacetCatalog, load_facets  # noqa: E402
from pipeline.retrieval import (  # noqa: E402
    Candidate,
    annotate_coverage,
    dedupe,
    grounded_dimensions,
    normalize_queries,
    retrieve_histories,
    retrieve_presets,
)
```

（`FacetCatalog` 與 `Candidate` 是 Task 6 已有的，合併進同一行/同一個 import 群組即可。`DIMENSIONS` 在 `demo.py` 沒用到，不要匯入。）

```python
# ---------- 主流程 ----------


def build_view(
    profile: str,
    states: dict[str, str],
    assembly: AssemblyResult,
    kept_borrowed: list[BorrowedFrom],
    rejections: list[str],
    kept_suggestions: list[DimensionSuggestion],
    by_id: dict[int, Candidate],
    catalog: FacetCatalog,
) -> DemoView:
    borrowed = [
        BorrowedView(
            band=by_id[b.preset_id].band, dist=by_id[b.preset_id].dist,
            title=by_id[b.preset_id].preset["title"], category=by_id[b.preset_id].preset["category"], tags=b.tags,
        )
        for b in kept_borrowed
    ]
    suggestions = [
        SuggestionView(
            dimension_label=catalog.dimensions[s.dimension],
            missing_labels=s.missing_labels,
            options=[OptionView(o.label, o.tags, by_id[o.preset_id].preset["title"]) for o in s.options],
        )
        for s in kept_suggestions
    ]
    return DemoView(
        profile=profile, facet_states=states,
        positive_prompt=assembly.positive_prompt, negative_prompt=assembly.negative_prompt,
        borrowed=borrowed, rejections=rejections, suggestions=suggestions,
    )


def run_once(query: str, conn, client, catalog: FacetCatalog, args, p: Palette) -> None:
    print(p.dim(f"\n[1/3] 分析：{query}"))
    analysis = client.generate_structured(build_analysis_prompt(query, catalog), AnalysisResult)
    profile = analysis.subject_profile
    states = facet_state_map(analysis, catalog, profile)
    grounded = grounded_dimensions(states, catalog)
    queries = normalize_queries(
        [(q.dimension, q.query) for q in analysis.queries], profile, grounded, catalog, query,
        k_covered=args.k_covered, k_missing=args.k_missing,
    )
    print(p.dim(f"      題材 {profile}"))
    print(p.dim(render_queries(queries, catalog)))

    print(p.dim("[2/3] 檢索"))
    vectors = client.embed_batch([query] + [q.query for q in queries], task_type="RETRIEVAL_QUERY")
    qvec, subvecs = Vector(vectors[0]), [Vector(v) for v in vectors[1:]]
    hits = retrieve_presets(conn, queries, subvecs, catalog, profile)
    cands = dedupe([c for dh in hits for c in dh.hits])
    annotate_coverage(cands, states)
    histories = retrieve_histories(conn, qvec, profile, args.top_histories)
    print(p.dim(render_retrieval_summary(hits, catalog, len(histories), profile)))
    if args.verbose:
        print(render_verbose(cands, catalog, p))

    print(p.dim("[3/3] 交給 Gemini 組裝提示詞…"))
    assembly = client.generate_structured(
        build_assembly_prompt(query, profile, states, cands, histories, catalog), AssemblyResult
    )
    by_id = {c.id: c for c in cands}
    kept_borrowed, rejected_b = validate_borrowed(assembly, by_id)
    kept_suggestions, rejected_s = validate_suggestions(assembly, by_id, missing_labels_by_dimension(states, catalog))
    view = build_view(profile, states, assembly, kept_borrowed, rejected_b + rejected_s, kept_suggestions, by_id, catalog)
    print(render(view, catalog, p))


def main(argv: list[str] | None = None) -> None:
    ap = argparse.ArgumentParser(description="單輪示範：中文描述 → 英文生圖提示詞")
    ap.add_argument("query", nargs="?", help="中文描述；省略則進入互動模式")
    ap.add_argument("--k-covered", type=int, default=5, help="使用者有描述的維度，每維撈幾筆")
    ap.add_argument("--k-missing", type=int, default=3, help="使用者未描述的維度，每句推想子查詢撈幾筆")
    ap.add_argument("--top-histories", type=int, default=3)
    ap.add_argument("--verbose", action="store_true", help="印出每維度全部候選與分級")
    ap.add_argument("--no-color", action="store_true")
    args = ap.parse_args(argv)

    from pipeline.gemini_client import default_client  # 延遲匯入：沒金鑰時才在這裡報錯

    p = Palette(colors_enabled(args.no_color))
    catalog = load_facets(FACETS_PATH)
    client = default_client()

    with connect() as conn:
        if args.query:
            run_once(args.query, conn, client, catalog, args, p)
            return
        print(p.head("互動模式：輸入中文描述後按 Enter，Ctrl+C 離開。"))
        while True:
            try:
                q = input(p.head("\n> ")).strip()
            except (EOFError, KeyboardInterrupt):
                print("\n再見。")
                return
            if q:
                run_once(q, conn, client, catalog, args, p)


if __name__ == "__main__":
    main()
```



- [ ] **Step 4: 跑全部測試與 ruff，確認沒有殘留 `noqa: F401`**

Run: `python -m pytest -q && python -m ruff check . && grep -n "F401" demo.py demo_render.py pipeline/retrieval.py`
Expected: 全綠；grep 無輸出。

- [ ] **Step 5: 用真實 API 跑原始題目一次（需 `.env` 有金鑰、DB 已啟動）**

Run（Git Bash）:
```bash
PYTHONIOENCODING=utf-8 ./.venv/Scripts/python.exe demo.py "夜晚的湖畔，月亮很圓，湖上很多燈籠，遠處是高山，一個少女坐在橋的扶手上，她是紫色雙馬尾，綠色眼睛，穿著夾克、熱褲、拖鞋，她手中拿著攝影機" --verbose --no-color
```
Expected（人工檢查）：
- `[2/3]` 印出六個維度各自的池大小與高/中/低計數，appearance 有「高」。
- 「借用的知識庫片段」不只 1 筆，且每筆列出借入的 tag。
- 「建議」對風格與鏡頭各有一則，各 2–3 個選項並附來源片段名。
- 正向提示詞含 `purple hair`、`green eyes`、`twintails`，不含 `pink hair`。
- 若有 ✗ 行，措辭是「來源不符 … 提示詞不受影響」。

把這次輸出貼到 commit 訊息或執行紀錄裡（不用進 repo）。

- [ ] **Step 6: Commit**

```bash
git add scripts/demo.py scripts/tests/test_demo.py
git commit -m "feat(demo): three-stage analyze -> per-dimension retrieve -> assemble pipeline with --verbose

Replaces the single whole-sentence vector (which averaged six dimensions
into one blurry query and could never surface Style/Camera presets) with
one sub-query per dimension, GIN-filtered to that dimension's facets.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

### Task 10: 主規格 §4 / §9 改寫、README 更新

**Files:**
- Modify: `docs/superpowers/specs/2026-09-21-genai-prompt-copilot-design.md`（§4 工具表第 100 行附近、§9 第 444–451 行）
- Modify: `scripts/README.md`（demo 段落第 41–46 行附近）

- [ ] **Step 1: 改 §4 工具表的 `SearchPresets` 列**

找到這行：

```
| `KnowledgePlugin.SearchPresets` | `query: string, facetIds: string[], tags: string[], topK: int = 5` | 可重複；RAG 2，混合檢索 `prompt_knowledge_presets` |
```

改成：

```
| `KnowledgePlugin.SearchPresets` | `query: string, facetIds: string[], topK: int = 5` | 可重複；RAG 2，**分維度檢索** `prompt_knowledge_presets`：一次呼叫一個維度，`query` 是該維度專屬語句，見 §9 |
```

- [ ] **Step 2: 用新 spec §10 的引文取代 §9 全文**

從 `## 9. 檢索策略` 那行起、到 `## 10. API 與 SSE 協定` 前一行止，整段替換成 [新 spec §10](../specs/2026-09-22-dimension-scoped-retrieval-design.md) 引文區塊裡的內容（去掉每行開頭的 `> `）。替換後 §9 必須包含：分維度的表格、「GIN 過濾本身不夠」那段實測數字、四條 agent 呼叫規則、`tags && $2` 決定不做、單次 `embed_batch`、指回新 spec 的連結。

- [ ] **Step 3: 更新 README 的 demo 段落**

把示例指令與說明改成：

```
`demo.py` 是子專案 1 成果的展示程式，也是子專案 2 互動流程的縮小版——它只跑一輪、
不追問。流程是三段：Gemini 先分析（題材、六維度 facet 狀態、每個維度的檢索子查詢）→
每個維度在自己的 facet 候選池裡做向量檢索 → Gemini 組裝提示詞。使用者沒描述的維度撈到的
片段只會進「建議」，不會寫進提示詞。

    python demo.py "昏暗雨夜的科幻城市，一個穿皮夾克的短髮女生站在霓虹招牌下"
    python demo.py "清晨山頂的日出，雲海翻騰" --verbose      # 印出每維度全部候選與相似度分級
    python demo.py "山上的日出" --k-covered 8 --k-missing 2  # 每維度撈幾筆
    python demo.py                      # 不給描述則進入互動模式，Ctrl+C 離開

`[2/3]` 那行會印每個維度的候選池大小與高／中／低命中數；池很小或全是「低」就是知識庫
在那個維度的覆蓋缺口。
```

- [ ] **Step 4: 檢查主規格沒有殘留舊說法**

Run: `grep -n "混合檢索\|tags && \$2\|tags: string\[\]" docs/superpowers/specs/2026-09-21-genai-prompt-copilot-design.md`
Expected: 只剩 §9 裡「`tags && $2` 決定不做」那一處，以及 §1 展示重點的「向量 + GIN 混合檢索」（那是技術名詞，保留）。

- [ ] **Step 5: Commit**

```bash
git add docs/superpowers/specs/2026-09-21-genai-prompt-copilot-design.md scripts/README.md
git commit -m "docs(spec): rewrite retrieval strategy as dimension-scoped; drop tags filter from SearchPresets

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

### Task 11: 收尾

- [ ] **Step 1: 全套驗證**

Run: `python -m pytest -q && python -m pytest -m integration -q && python -m ruff check .`
Expected: 全綠。

- [ ] **Step 2: 用不同題材各跑一次 demo，記錄 `[2/3]` 那行**

```bash
PYTHONIOENCODING=utf-8 ./.venv/Scripts/python.exe demo.py "秋天的楓葉森林，清晨薄霧，陽光穿過樹林" --no-color
PYTHONIOENCODING=utf-8 ./.venv/Scripts/python.exe demo.py "一把生鏽的老舊鐵劍，木頭劍柄有磨損" --no-color
PYTHONIOENCODING=utf-8 ./.venv/Scripts/python.exe demo.py "跑車在山路上高速過彎，揚起塵土" --no-color
```

Expected：三個都跑完不報錯；landscape 沒有人物三維的列；vehicle 的「人物動作」池會印出 2 筆左右——這是已知的知識庫缺口（spec §11），不是 bug。把三行 `[2/3]` 的內容記到執行紀錄，作為之後補資料的依據。

- [ ] **Step 3: 併回 master**

用 `superpowers:finishing-a-development-branch`。

---

## Self-Review

**Spec coverage**

| Spec 章節 | 任務 |
| :--- | :--- |
| §3 三段資料流、單次 embed_batch、① 為準 | Task 9 `run_once` |
| §4.1 / §4.2 契約 | Task 6 schema |
| §4.3 Candidate | Task 4 |
| §4.4 兩段 prompt 必含規則 | Task 7（測試逐條 assert 關鍵字） |
| §5.1 SQL、k、分級、不用 tags | Task 3 `band`、Task 5 SQL |
| §5.2 histories profile 過濾 | Task 5 |
| §5.3 去重 | Task 4 |
| §5.4 facet 覆蓋標記 | Task 4 `annotate_coverage`、Task 7 `format_candidates` |
| §6.1 queries 整理 | Task 3 `normalize_queries` |
| §6.2 borrowed 驗證（雙向、不動提示詞） | Task 6 |
| §6.3 suggestions 驗證 | Task 6 |
| §7 畫面（[2/3] 池與分級、借用、✗ 措辭、建議、--verbose） | Task 8、9 |
| §8 三檔拆分 | Task 2、3、6 |
| §9 測試清單 | 對應各任務 |
| §10 主規格改寫 | Task 10 |
| §11 已知限制 | Task 11 Step 2 記錄缺口 |

**Placeholder scan**：無 TBD／TODO；每個程式碼步驟都有完整內容。Task 6 對暫時性 `noqa: F401` 的處理給了兩個選項並要求 Task 9 清乾淨（Task 9 Step 4 有 grep 驗證）。

**Type consistency**：`Candidate.preset` 的 key 集合在 Task 4 定義、Task 5 產生、Task 6/7/8/9 測試建構時一致；`validate_*` 回傳 `tuple[list[...], list[str]]`，Task 9 以 `rejected_b + rejected_s` 串接；`DemoView` 欄位在 Task 6 定義、Task 8 `render` 消費、Task 9 `build_view` 產生，名稱一致；`normalize_queries` 的 `k_covered/k_missing` 關鍵字參數在 Task 3 測試與 Task 9 呼叫一致；`render_retrieval_summary(hits, catalog, n_histories, profile)` 在 Task 8 定義與 Task 9 呼叫參數順序一致。
