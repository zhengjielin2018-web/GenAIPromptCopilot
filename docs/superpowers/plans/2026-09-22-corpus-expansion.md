# 資料庫擴增（分層抓取）Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 把 `scripts/pipeline/fetch_civitai.py` 從單一參數抓取改成 9 層 `baseModels × period`
配額抓取，解決語料題材偏斜（現行參數下人物 60% vs 載具 4%），並提供一支覆蓋度報告工具
驗證擴增後的候選池是否達標。`clean`／`structure`／`embed`／`load` 四個階段完全不動。

**Architecture:** 新增 `pipeline/strata.py` 定義 9 層配額表（純資料）。`civitai_client.py::iter_images()`
把寫死的 `sort`/`period` 開放成參數（`base_models` 已是既有參數）。`fetch_civitai.py::run_fetch()`
比照 `base_models` 的既有做法，加上 `sort`/`period`/`stratum_key` 三個參數，並把 `state.json`
從單一物件升級為 `{version: 2, strata: {key: {cursor, fetched, done}}}`；`main()` 依序對每層算出
剩餘配額（`quota - 已累積 fetched`）後呼叫 `run_fetch()`。`seed_data.py` 與 `coverage_report.py`
跟著同步。所有分層共用同一個 `RAW_PATH`，靠既有的 `existing_keys(out_path, "id")` 自動去重
跨層重複的圖片，不需要新寫跨層去重邏輯。

**Tech Stack:** Python 3.12、httpx（Civitai API mock via `httpx.MockTransport`）、pytest、psycopg 3
（coverage_report 的 DB 查詢，標記 `integration`）。

**Spec:** [docs/superpowers/specs/2026-09-22-corpus-expansion-design.md](../specs/2026-09-22-corpus-expansion-design.md)

## Global Constraints

- `clean.py`／`structure.py`／`embed.py`／`load.py`／`retrieval.py`／`db/init/001_schema.sql` 一行都不改（spec §5）
- 不引入 `modelId` 針對性抓取或 `tags` 查詢參數——兩者皆已實測不可行（spec §4.3、§12）
- 不新增自然語言方言（Flux.1 D／Krea／Qwen／OpenAI）分層、不新增 SD 3.5 分層（spec §4.2、§12）
- `baseline` stratum 的參數必須與現行寫死的抓取參數完全一致（無 `base_models` 過濾、
  `period=AllTime`、`sort=Most Reactions`），確保既有 420 筆 raw 與其 cursor 無縫接續（spec §8）
- 9 層配額合計 9,000（spec §6）
- `coverage_report.py` 的候選池查詢必須直接重用 `pipeline.retrieval.POOL_SQL` 與
  `dimension_facets()`，不得另寫一份查詢邏輯（spec §10）
- 不合成 preset、不做人工 curated 補冷門 facet 清單（spec §3）
- 测试遵循既有慣例：純函式測試預設執行（`pytest`），需要 DB／網路的標 `@pytest.mark.integration`
  （`pyproject.toml` 的 `addopts = "-m 'not integration'"` 已把它們排除在預設跑法之外）

---

## Task 1: `pipeline/strata.py` —— Stratum 定義與 9 層配額表

**Files:**
- Create: `scripts/pipeline/strata.py`
- Test: `scripts/tests/test_strata.py`

**Interfaces:**
- Produces: `Stratum`（frozen dataclass，欄位 `key: str`, `base_models: list[str] | None`,
  `period: str`, `quota: int`, `sort: str = "Most Reactions"`）與模組常數
  `STRATA: tuple[Stratum, ...]`（9 個元素），供 Task 3 的 `fetch_civitai.py::main()` 匯入

- [ ] **Step 1: 寫失敗測試，鎖住配額表的關鍵不變量**

建立 `scripts/tests/test_strata.py`：

```python
from pipeline.strata import STRATA, Stratum


def test_strata_keys_are_unique():
    keys = [s.key for s in STRATA]
    assert len(keys) == len(set(keys))


def test_strata_quota_sums_to_nine_thousand():
    assert sum(s.quota for s in STRATA) == 9000


def test_every_stratum_has_a_positive_quota():
    assert all(s.quota > 0 for s in STRATA)


def test_baseline_stratum_matches_the_original_hardcoded_fetch_params():
    """baseline 必須跟現行寫死的抓取參數完全一致，這樣既有的 420 筆 raw 與其 cursor
    才能無縫接續、不會重抓。見 spec 2026-09-22-corpus-expansion-design.md §8。"""
    baseline = next(s for s in STRATA if s.key == "baseline")
    assert baseline.base_models is None
    assert baseline.period == "AllTime"
    assert baseline.sort == "Most Reactions"


def test_stratum_is_frozen():
    s = Stratum(key="x", base_models=None, period="AllTime", quota=1)
    try:
        s.quota = 2
        raised = False
    except AttributeError:
        raised = True
    assert raised
```

- [ ] **Step 2: 執行測試，確認因模組不存在而失敗**

Run: `cd scripts && .\.venv\Scripts\python.exe -m pytest tests/test_strata.py -v`
Expected: FAIL，`ModuleNotFoundError: No module named 'pipeline.strata'`

- [ ] **Step 3: 寫 `pipeline/strata.py`**

```python
"""分層抓取的配額表：baseModels x period 決定 Civitai 抓取的題材傾向。
數字依據見 docs/superpowers/specs/2026-09-22-corpus-expansion-design.md §4.2、§6
（唯讀公開 API 探測 + 100 筆抽樣關鍵字啟發式）。"""

from __future__ import annotations

from dataclasses import dataclass


@dataclass(frozen=True)
class Stratum:
    key: str                       # state.json 的鍵；一旦寫入就不可更名（改名=整層重抓）
    base_models: list[str] | None  # None = 不過濾（僅 baseline 使用）
    period: str                    # "AllTime" | "Year" | "Month" | "Week"
    quota: int                     # 這一層目標新增幾筆 raw（累積值，非單次呼叫上限）
    sort: str = "Most Reactions"


STRATA: tuple[Stratum, ...] = (
    Stratum(key="baseline", base_models=None, period="AllTime", quota=600),
    Stratum(key="sd15/year", base_models=["SD 1.5"], period="Year", quota=1600),
    Stratum(key="sdxl10/alltime", base_models=["SDXL 1.0"], period="AllTime", quota=1600),
    Stratum(key="sdxl10/year", base_models=["SDXL 1.0"], period="Year", quota=1200),
    Stratum(key="noobai/year", base_models=["NoobAI"], period="Year", quota=1200),
    Stratum(key="noobai/alltime", base_models=["NoobAI"], period="AllTime", quota=800),
    Stratum(key="sd15/alltime", base_models=["SD 1.5"], period="AllTime", quota=800),
    Stratum(key="illustrious/alltime", base_models=["Illustrious"], period="AllTime", quota=700),
    Stratum(key="pony/alltime", base_models=["Pony"], period="AllTime", quota=500),
)
```

- [ ] **Step 4: 執行測試，確認通過**

Run: `cd scripts && .\.venv\Scripts\python.exe -m pytest tests/test_strata.py -v`
Expected: PASS（5 個測試）

- [ ] **Step 5: Ruff 檢查**

Run: `cd scripts && .\.venv\Scripts\python.exe -m ruff check pipeline/strata.py tests/test_strata.py`
Expected: 無錯誤

- [ ] **Step 6: Commit**

```bash
git add scripts/pipeline/strata.py scripts/tests/test_strata.py
git commit -m "feat(pipeline): add 9-stratum quota table for corpus expansion

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>"
```

---

## Task 2: `civitai_client.py` —— `iter_images()` 開放 `sort`/`period` 參數

**Files:**
- Modify: `scripts/pipeline/civitai_client.py:43-64`
- Modify: `scripts/tests/test_civitai_client.py`

**Interfaces:**
- Consumes: 無（純改既有類別的方法簽名）
- Produces: `CivitaiClient.iter_images(*, limit=200, cursor=None, base_models=None,
  sort="Most Reactions", period="AllTime")`——Task 3 的 `run_fetch()` 會傳入
  `sort`/`period`

- [ ] **Step 1: 寫失敗測試**

在 `scripts/tests/test_civitai_client.py` 現有測試之後新增：

```python
def test_iter_images_uses_given_sort_and_period():
    captured = {}

    def handler(req: httpx.Request) -> httpx.Response:
        captured.update(dict(req.url.params))
        return httpx.Response(200, json=_page([], None))

    list(_client(handler).iter_images(sort="Newest", period="Year"))
    assert captured["sort"] == "Newest"
    assert captured["period"] == "Year"


def test_iter_images_defaults_to_most_reactions_alltime_when_not_given():
    captured = {}

    def handler(req: httpx.Request) -> httpx.Response:
        captured.update(dict(req.url.params))
        return httpx.Response(200, json=_page([], None))

    list(_client(handler).iter_images())
    assert captured["sort"] == "Most Reactions"
    assert captured["period"] == "AllTime"
```

- [ ] **Step 2: 執行測試，確認失敗**

Run: `cd scripts && .\.venv\Scripts\python.exe -m pytest tests/test_civitai_client.py -v`
Expected: `test_iter_images_uses_given_sort_and_period` FAIL（收到 `sort=Most Reactions`
而非 `Newest`）；`test_iter_images_defaults_to_most_reactions_alltime_when_not_given` PASS
（預設值本來就對，這個測試是防止之後改預設值時沒發現）

- [ ] **Step 3: 修改 `iter_images()` 簽名與 `params` 組裝**

編輯 `scripts/pipeline/civitai_client.py` 第 43–64 行：

```python
    def iter_images(
        self,
        *,
        limit: int = 200,
        cursor: str | None = None,
        base_models: list[str] | None = None,
        sort: str = "Most Reactions",
        period: str = "AllTime",
    ) -> Iterator[tuple[dict, str | None]]:
        """Yield (item, cursor_used_to_fetch_this_page) for every item, page by page.

        The second element is the cursor that FETCHED the current page, not the
        cursor for the next page. This lets a caller that stops mid-page persist
        a cursor that re-fetches the same page on resume, rather than skipping
        the unwritten remainder of that page.
        """
        params: dict = {
            "limit": limit,
            "nsfw": "None",
            "withMeta": "true",
            "type": "image",
            "sort": sort,
            "period": period,
        }
```

（其餘 `if base_models: ...` 之後的內容不變。）

- [ ] **Step 4: 執行測試，確認通過**

Run: `cd scripts && .\.venv\Scripts\python.exe -m pytest tests/test_civitai_client.py -v`
Expected: PASS（全部）

- [ ] **Step 5: Ruff 檢查**

Run: `cd scripts && .\.venv\Scripts\python.exe -m ruff check pipeline/civitai_client.py tests/test_civitai_client.py`
Expected: 無錯誤

- [ ] **Step 6: Commit**

```bash
git add scripts/pipeline/civitai_client.py scripts/tests/test_civitai_client.py
git commit -m "feat(pipeline): make iter_images sort/period configurable

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>"
```

---

## Task 3: `fetch_civitai.py` —— state v1→v2 遷移、分層 `run_fetch()`、`main()` 依序跑 9 層

這是本次擴增改動最大的檔案。拆成三個子任務，各自可獨立測試與 commit。

**Files:**
- Modify: `scripts/pipeline/fetch_civitai.py`（整個檔案重寫，原本 60 行）
- Modify: `scripts/tests/test_fetch.py`（整個檔案重寫，因為 state.json 的 schema 改變，
  既有斷言 `json.loads(state.read_text())["done"]` 這種直接讀頂層欄位的寫法全部要改成
  讀 `doc["strata"][key]["done"]`）

**Interfaces:**
- Consumes: `pipeline.strata.STRATA`（Task 1）、`CivitaiClient.iter_images(sort=, period=)`（Task 2）
- Produces:
  - `_migrate_to_v2(raw: dict) -> dict`：純函式，`{"version": 2, "strata": {...}}`
  - `_default_stratum_state() -> dict`：`{"cursor": None, "fetched": 0, "done": False}`
  - `_load_state(path: Path) -> dict` / `_save_state(path: Path, doc: dict) -> None`：操作
    整份 v2 文件
  - `run_fetch(client, *, max_items, out_path, state_path, base_models=None,
    sort="Most Reactions", period="AllTime", stratum_key="baseline") -> int`——`stratum_key`
    為新參數，其餘簽名不變
  - `_remaining_quota(stratum_quota: int, quota_scale: float, fetched_so_far: int) -> int`：
    純函式，`main()` 用它算每層這次還要抓多少
  - `main(argv: list[str] | None = None) -> None`：CLI 從 `--max-items`/`--base-models`
    改為 `--quota-scale FLOAT`（Task 4 的 `seed_data.py` 要跟著改）

### Task 3a：state 遷移純函式

- [ ] **Step 1: 寫失敗測試**

用下面完整內容**取代** `scripts/tests/test_fetch.py`（既有測試會在 3b 一併重寫，這裡先加
遷移測試在檔案最上方，其餘保留舊有內容不動，下一子任務再整體替換）：

```python
import json

from pipeline.fetch_civitai import _default_stratum_state, _migrate_to_v2, run_fetch
from pipeline.jsonl import read_jsonl


def test_migrate_v1_state_wraps_it_as_baseline_stratum():
    v1 = {"cursor": "400|1753734600000", "fetched": 420, "done": False}
    assert _migrate_to_v2(v1) == {"version": 2, "strata": {"baseline": v1}}


def test_migrate_v2_state_passes_through_unchanged():
    v2 = {"version": 2, "strata": {"sd15/year": {"cursor": None, "fetched": 0, "done": False}}}
    assert _migrate_to_v2(v2) == v2


def test_migrate_empty_dict_yields_empty_strata():
    assert _migrate_to_v2({}) == {"version": 2, "strata": {}}


def test_default_stratum_state_is_fresh():
    assert _default_stratum_state() == {"cursor": None, "fetched": 0, "done": False}


class FakeClient:
    """模擬 CivitaiClient.iter_images：依 cursor 回不同批次。"""

    def __init__(self, pages):
        self.pages = pages  # {cursor_or_None: (items, next_cursor)}
        self.calls = []

    def iter_images(self, *, limit=200, cursor=None, base_models=None,
                     sort="Most Reactions", period="AllTime"):
        self.calls.append(cursor)
        while True:
            items, nxt = self.pages[cursor]
            for it in items:
                yield it, cursor
            if nxt is None:
                return
            cursor = nxt


def test_fetch_writes_raw_and_marks_stratum_done(tmp_path):
    client = FakeClient({None: ([{"id": 1}, {"id": 2}], "c2"), "c2": ([{"id": 3}], None)})
    out, state = tmp_path / "images.jsonl", tmp_path / "state.json"
    n = run_fetch(client, max_items=10, out_path=out, state_path=state, stratum_key="s1")
    assert n == 3
    assert [r["id"] for r in read_jsonl(out)] == [1, 2, 3]
    doc = json.loads(state.read_text())
    assert doc["version"] == 2
    assert doc["strata"]["s1"] == {"cursor": None, "fetched": 3, "done": True}
```

- [ ] **Step 2: 執行測試，確認失敗**

Run: `cd scripts && .\.venv\Scripts\python.exe -m pytest tests/test_fetch.py -v`
Expected: FAIL（`ImportError: cannot import name '_migrate_to_v2'`，因為
`pipeline/fetch_civitai.py` 尚未修改）

- [ ] **Step 3: 重寫 `pipeline/fetch_civitai.py`**

用下面內容**完整取代** `scripts/pipeline/fetch_civitai.py`：

```python
"""階段 1：分層拉取 Civitai 圖片 metadata 到 data/raw/images.jsonl。
每層（pipeline.strata.STRATA 的一筆）各自的抓取進度記在 state.json 的
{"version": 2, "strata": {<key>: {cursor, fetched, done}}} 裡，供續跑。"""

from __future__ import annotations

import argparse
import json
from pathlib import Path

from pipeline.civitai_client import default_client
from pipeline.config import RAW_DIR, settings
from pipeline.jsonl import append_jsonl, existing_keys
from pipeline.strata import STRATA

RAW_PATH = RAW_DIR / "images.jsonl"
STATE_PATH = RAW_DIR / "state.json"


def _default_stratum_state() -> dict:
    return {"cursor": None, "fetched": 0, "done": False}


def _migrate_to_v2(raw: dict) -> dict:
    """v1（單層，整個物件就是 baseline 的狀態）→ v2（{version, strata}）。"""
    if raw.get("version") == 2:
        return {"version": 2, "strata": dict(raw.get("strata", {}))}
    if not raw:
        return {"version": 2, "strata": {}}
    return {"version": 2, "strata": {"baseline": raw}}


def _load_state(path: Path) -> dict:
    if path.exists():
        return _migrate_to_v2(json.loads(path.read_text(encoding="utf-8")))
    return {"version": 2, "strata": {}}


def _save_state(path: Path, doc: dict) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(doc), encoding="utf-8")


def _remaining_quota(stratum_quota: int, quota_scale: float, fetched_so_far: int) -> int:
    target = round(stratum_quota * quota_scale)
    return max(0, target - fetched_so_far)


def run_fetch(
    client,
    *,
    max_items: int,
    out_path: Path,
    state_path: Path,
    base_models: list[str] | None = None,
    sort: str = "Most Reactions",
    period: str = "AllTime",
    stratum_key: str = "baseline",
) -> int:
    doc = _load_state(state_path)
    st = doc["strata"].get(stratum_key) or _default_stratum_state()
    if st["done"]:
        return 0
    seen = existing_keys(out_path, "id")
    added = 0
    resume_cursor = st["cursor"]
    for item, page_cursor in client.iter_images(
        cursor=st["cursor"], base_models=base_models, sort=sort, period=period
    ):
        resume_cursor = page_cursor
        if item["id"] not in seen:
            append_jsonl(out_path, item)
            seen.add(item["id"])
            added += 1
        if added >= max_items:
            doc["strata"][stratum_key] = {
                "cursor": resume_cursor, "fetched": st["fetched"] + added, "done": False,
            }
            _save_state(state_path, doc)
            return added
    doc["strata"][stratum_key] = {"cursor": None, "fetched": st["fetched"] + added, "done": True}
    _save_state(state_path, doc)
    return added


def main(argv: list[str] | None = None) -> None:
    ap = argparse.ArgumentParser(description="階段 1：分層拉取 Civitai 圖片 metadata")
    ap.add_argument(
        "--quota-scale", type=float, default=1.0,
        help="乘上 pipeline.strata.STRATA 每層的 quota，四捨五入（用於分階段驗收先跑小規模）",
    )
    args = ap.parse_args(argv)
    client = default_client(settings.civitai_min_interval_s)
    doc = _load_state(STATE_PATH)
    total_added = 0
    for stratum in STRATA:
        st = doc["strata"].get(stratum.key) or _default_stratum_state()
        if st["done"]:
            print(f"fetch[{stratum.key}]: 已耗盡（done），略過")
            continue
        remaining = _remaining_quota(stratum.quota, args.quota_scale, st["fetched"])
        if remaining <= 0:
            print(f"fetch[{stratum.key}]: 已達配額 {st['fetched']}，略過")
            continue
        n = run_fetch(
            client, max_items=remaining, out_path=RAW_PATH, state_path=STATE_PATH,
            base_models=stratum.base_models, sort=stratum.sort, period=stratum.period,
            stratum_key=stratum.key,
        )
        doc = _load_state(STATE_PATH)  # run_fetch 已寫回，重讀取得最新的 doc 供下一層判斷
        total_added += n
        print(f"fetch[{stratum.key}]: +{n} → {RAW_PATH}")
    print(f"fetch: 合計 +{total_added} → {RAW_PATH}")


if __name__ == "__main__":
    main()
```

- [ ] **Step 4: 執行測試，確認這四個測試通過**

Run: `cd scripts && .\.venv\Scripts\python.exe -m pytest tests/test_fetch.py -v`
Expected: 目前檔案裡的 5 個測試（遷移 3 個 + default state 1 個 + `run_fetch` 1 個）PASS；
舊測試（`test_fetch_stops_at_max_items_and_records_cursor` 等）此時還在檔案下半部、尚未
改寫，預期會因為斷言仍讀取舊 schema 而 FAIL——這是預期中的，下一步驟會處理

### Task 3b：重寫其餘 `run_fetch()` 測試以符合新 schema

- [ ] **Step 1: 用下面內容取代 `test_fetch.py` 檔案下半部（`FakeClient` 定義之後的所有既有測試）**

刪除舊的 `test_fetch_writes_raw_and_state`（已被 3a 的
`test_fetch_writes_raw_and_marks_stratum_done` 取代）以及後面四個測試，改成：

```python
def test_fetch_stops_at_max_items_and_records_cursor(tmp_path):
    client = FakeClient({None: ([{"id": 1}, {"id": 2}], "c2"), "c2": ([{"id": 3}], None)})
    out, state = tmp_path / "images.jsonl", tmp_path / "state.json"
    n = run_fetch(client, max_items=2, out_path=out, state_path=state, stratum_key="s1")
    assert n == 2
    doc = json.loads(state.read_text())
    assert doc["strata"]["s1"] == {"cursor": None, "fetched": 2, "done": False}


def test_fetch_resumes_from_state_and_skips_duplicates(tmp_path):
    client = FakeClient({None: ([{"id": 1}, {"id": 2}], "c2"), "c2": ([{"id": 3}, {"id": 4}], None)})
    out, state = tmp_path / "images.jsonl", tmp_path / "state.json"
    assert run_fetch(client, max_items=3, out_path=out, state_path=state, stratum_key="s1") == 3
    doc = json.loads(state.read_text())
    assert doc["strata"]["s1"]["cursor"] == "c2"
    assert doc["strata"]["s1"]["fetched"] == 3
    assert run_fetch(client, max_items=10, out_path=out, state_path=state, stratum_key="s1") == 1
    assert client.calls[-1] == "c2"
    assert [r["id"] for r in read_jsonl(out)] == [1, 2, 3, 4]
    doc = json.loads(state.read_text())
    assert doc["strata"]["s1"]["fetched"] == 4
    assert doc["strata"]["s1"]["done"] is True


def test_fetch_noop_when_stratum_already_done(tmp_path):
    client = FakeClient({None: ([{"id": 1}], None)})
    out, state = tmp_path / "images.jsonl", tmp_path / "state.json"
    run_fetch(client, max_items=10, out_path=out, state_path=state, stratum_key="s1")
    assert run_fetch(client, max_items=10, out_path=out, state_path=state, stratum_key="s1") == 0
    assert client.calls == [None]


def test_fetch_midpage_stop_does_not_lose_rest_of_page(tmp_path):
    client = FakeClient({None: ([{"id": 1}, {"id": 2}], "c2"), "c2": ([{"id": 3}], None)})
    out, state = tmp_path / "images.jsonl", tmp_path / "state.json"
    assert run_fetch(client, max_items=1, out_path=out, state_path=state, stratum_key="s1") == 1
    assert run_fetch(client, max_items=10, out_path=out, state_path=state, stratum_key="s1") == 2
    assert [r["id"] for r in read_jsonl(out)] == [1, 2, 3]


def test_two_strata_share_raw_file_and_dedupe_across_each_other(tmp_path):
    """spec §7：不同層抓到同一張圖片時，existing_keys 對同一個 out_path 的既有去重
    自動生效，不需要另寫跨層去重邏輯。"""
    out, state = tmp_path / "images.jsonl", tmp_path / "state.json"
    a = FakeClient({None: ([{"id": 1}, {"id": 2}], None)})
    b = FakeClient({None: ([{"id": 2}, {"id": 3}], None)})  # id=2 兩層都會抓到
    n_a = run_fetch(a, max_items=10, out_path=out, state_path=state, stratum_key="layer_a")
    n_b = run_fetch(b, max_items=10, out_path=out, state_path=state, stratum_key="layer_b")
    assert n_a == 2
    assert n_b == 1  # id=2 被 layer_a 已寫入的紀錄跳過，只有 id=3 算新增
    assert [r["id"] for r in read_jsonl(out)] == [1, 2, 3]
    doc = json.loads(state.read_text())
    assert doc["strata"]["layer_a"] == {"cursor": None, "fetched": 2, "done": True}
    assert doc["strata"]["layer_b"] == {"cursor": None, "fetched": 1, "done": True}


def test_v1_state_file_migrates_and_baseline_stratum_resumes_from_it(tmp_path):
    """既有的 420 筆語料留下的舊版 state.json 必須能無縫接續，見 spec §8。"""
    out, state = tmp_path / "images.jsonl", tmp_path / "state.json"
    state.write_text(json.dumps({"cursor": "c2", "fetched": 2, "done": False}), encoding="utf-8")
    for i in (1, 2):
        append_jsonl(out, {"id": i})
    client = FakeClient({"c2": ([{"id": 3}], None)})
    n = run_fetch(client, max_items=10, out_path=out, state_path=state, stratum_key="baseline")
    assert n == 1
    assert client.calls == ["c2"]
    doc = json.loads(state.read_text())
    assert doc["version"] == 2
    assert doc["strata"]["baseline"] == {"cursor": None, "fetched": 3, "done": True}


def test_remaining_quota_scales_and_floors_at_zero():
    assert _remaining_quota(1000, 1.0, 400) == 600
    assert _remaining_quota(1000, 0.5, 400) == 100
    assert _remaining_quota(1000, 1.0, 1000) == 0
    assert _remaining_quota(1000, 1.0, 1500) == 0  # 已超額也不會變負的
    assert _remaining_quota(9, 0.5, 0) == round(9 * 0.5)
```

同時把檔案最上方的 import 補上 `append_jsonl`（`test_v1_state_file_migrates...` 用得到）：

```python
from pipeline.jsonl import append_jsonl, read_jsonl
```

- [ ] **Step 2: 執行完整測試檔，確認全部通過**

Run: `cd scripts && .\.venv\Scripts\python.exe -m pytest tests/test_fetch.py -v`
Expected: PASS（全部，共 13 個測試）

- [ ] **Step 3: Ruff 檢查**

Run: `cd scripts && .\.venv\Scripts\python.exe -m ruff check pipeline/fetch_civitai.py tests/test_fetch.py`
Expected: 無錯誤

- [ ] **Step 4: Commit**

```bash
git add scripts/pipeline/fetch_civitai.py scripts/tests/test_fetch.py
git commit -m "feat(pipeline): stratified fetch with v1->v2 state migration

- run_fetch() 新增 stratum_key/sort/period 參數，state.json 從單層物件
  升級為 {version, strata: {key: {cursor, fetched, done}}}
- main() 依序對 pipeline.strata.STRATA 的 9 層算剩餘配額並抓取；
  --max-items/--base-models 改為 --quota-scale
- 舊版 state.json（無 version 欄位）自動遷移並包成 baseline 層，
  既有 420 筆語料與其 cursor 無縫接續

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>"
```

---

## Task 4: `seed_data.py` —— 跟著 fetch 階段的 CLI 改為 `--quota-scale`

`seed_data.py` 目前把自己的 `--max-items` 原封轉給 `fetch_civitai.main()`；Task 3 移除了
`fetch_civitai.py` 的 `--max-items`，這裡若不同步會在跑 `python seed_data.py` 時直接炸掉。

**Files:**
- Modify: `scripts/seed_data.py:22-23,32`
- Modify: `scripts/tests/test_seed_data.py`

**Interfaces:**
- Consumes: `fetch_civitai.main()` 現在接受 `--quota-scale`（Task 3）
- Produces: `seed_data.main()` 的 `--max-items` 參數改名為 `--quota-scale`（float，預設 1.0）

- [ ] **Step 1: 寫失敗測試**

修改 `scripts/tests/test_seed_data.py` 的 `test_main_threads_the_right_argv_to_each_stage`：

```python
def test_main_threads_the_right_argv_to_each_stage(monkeypatch):
    calls = _record_stage_mains(monkeypatch)
    main(["--quota-scale", "0.5", "--max-records", "3", "--reindex"])
    assert calls == {
        "fetch": ["--quota-scale", "0.5"],
        "clean": [],
        "structure": ["--max-records", "3"],
        "embed": ["--reindex"],
        "load": [],
    }
```

- [ ] **Step 2: 執行測試，確認失敗**

Run: `cd scripts && .\.venv\Scripts\python.exe -m pytest tests/test_seed_data.py -v`
Expected: FAIL（`argparse` 對 `--quota-scale` 報 unrecognized arguments，因為 `seed_data.py`
還沒改）

- [ ] **Step 3: 修改 `scripts/seed_data.py`**

第 22–23 行：

```python
    ap.add_argument(
        "--quota-scale", type=float, default=1.0,
        help="fetch 階段：乘上 pipeline.strata.STRATA 每層的 quota，四捨五入",
    )
```

第 32 行（`if stage == "fetch":` 區塊內）：

```python
        if stage == "fetch":
            fetch_civitai.main(["--quota-scale", str(args.quota_scale)])
```

- [ ] **Step 4: 執行測試，確認通過**

Run: `cd scripts && .\.venv\Scripts\python.exe -m pytest tests/test_seed_data.py -v`
Expected: PASS（全部 4 個測試）

- [ ] **Step 5: Ruff 檢查**

Run: `cd scripts && .\.venv\Scripts\python.exe -m ruff check seed_data.py tests/test_seed_data.py`
Expected: 無錯誤

- [ ] **Step 6: Commit**

```bash
git add scripts/seed_data.py scripts/tests/test_seed_data.py
git commit -m "fix(pipeline): thread --quota-scale to fetch stage in seed_data

fetch_civitai.py 的 CLI 已改為 --quota-scale（見上一個 commit），
seed_data.py 原本轉發 --max-items 會導致 unrecognized arguments。

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>"
```

---

## Task 5: `coverage_report.py` —— 覆蓋度報告與門檻判定

**Files:**
- Create: `scripts/coverage_report.py`
- Test: `scripts/tests/test_coverage_report.py`

**Interfaces:**
- Consumes: `pipeline.retrieval.DIMENSIONS`、`pipeline.retrieval.POOL_SQL`、
  `pipeline.retrieval.dimension_facets()`、`pipeline.facets.FacetCatalog`、
  `pipeline.facets.load_facets()`、`pipeline.config.FACETS_PATH`（皆為既有介面，不修改）
- Produces: `build_report(...) -> ReportResult`（純函式，`ReportResult.text: str`、
  `.pool_gaps: list[tuple[str,str,int]]`、`.facet_gaps: list[tuple[str,int]]`、
  `.has_gaps: bool` property）；`main()` 的 exit code：有缺口 → 1，全數達標 → 0

- [ ] **Step 1: 寫失敗測試**

建立 `scripts/tests/test_coverage_report.py`：

```python
from pipeline.config import FACETS_PATH
from pipeline.facets import load_facets
from pipeline.retrieval import DIMENSIONS

from coverage_report import POOL_THRESHOLD, build_report

CAT = load_facets(FACETS_PATH)
ALL_APPLICABLE_DIMS = {
    (p, d) for p in CAT.profiles for d in DIMENSIONS if CAT.profiles[p].get(d)
}


def test_build_report_flags_pool_below_threshold():
    result = build_report(
        profile_counts={}, pool_sizes={("vehicle", "pose"): 2},
        facet_counts={}, category_counts={}, catalog=CAT,
    )
    assert ("vehicle", "pose", 2) in result.pool_gaps
    assert result.has_gaps is True
    assert "vehicle" in result.text
    assert "未達門檻" in result.text


def test_build_report_does_not_flag_pool_at_threshold():
    result = build_report(
        profile_counts={}, pool_sizes={("portrait", "style"): POOL_THRESHOLD},
        facet_counts={}, category_counts={}, catalog=CAT,
    )
    assert all(g[:2] != ("portrait", "style") for g in result.pool_gaps)


def test_build_report_flags_facet_below_threshold_including_zero():
    result = build_report(
        profile_counts={}, pool_sizes={}, facet_counts={"pose.terrain": 0},
        category_counts={}, catalog=CAT,
    )
    assert ("pose.terrain", 0) in result.facet_gaps


def test_build_report_has_no_gaps_when_everything_is_well_above_threshold():
    pool_sizes = {pair: 999 for pair in ALL_APPLICABLE_DIMS}
    facet_counts = dict.fromkeys(CAT.facets, 999)
    result = build_report(
        profile_counts={}, pool_sizes=pool_sizes, facet_counts=facet_counts,
        category_counts={}, catalog=CAT,
    )
    assert result.has_gaps is False
    assert "全部達標" in result.text


def test_custom_pool_threshold_is_respected():
    result = build_report(
        profile_counts={}, pool_sizes={("object", "appearance"): 50},
        facet_counts={}, category_counts={}, catalog=CAT, pool_threshold=40,
    )
    assert result.pool_gaps == []


def test_missing_pool_or_facet_entries_default_to_zero_and_are_flagged():
    """(profile, dim) 或 facet id 完全沒在傳入的字典裡（例如 DB 是空的）要當 0 筆處理，
    而不是被 dict.get 靜默跳過。"""
    result = build_report(
        profile_counts={}, pool_sizes={}, facet_counts={}, category_counts={}, catalog=CAT,
    )
    assert len(result.pool_gaps) == len(ALL_APPLICABLE_DIMS)
    assert len(result.facet_gaps) == len(CAT.facets)
```

- [ ] **Step 2: 執行測試，確認失敗**

Run: `cd scripts && .\.venv\Scripts\python.exe -m pytest tests/test_coverage_report.py -v`
Expected: FAIL，`ModuleNotFoundError: No module named 'coverage_report'`

- [ ] **Step 3: 寫 `scripts/coverage_report.py`**

```python
"""驗收工具：量測每 (profile, 維度) 候選池與每個 facet 的 preset 筆數是否達門檻。
候選池查詢直接重用 pipeline.retrieval.POOL_SQL / dimension_facets，這裡量到的池子
定義上就等於 retrieval.py 實際檢索時看到的池子，不會有兩份實作各自漂移。
門檻依據見 docs/superpowers/specs/2026-09-22-corpus-expansion-design.md §10。"""

from __future__ import annotations

import argparse
import sys
from dataclasses import dataclass
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

from pipeline.config import FACETS_PATH  # noqa: E402
from pipeline.facets import FacetCatalog, load_facets  # noqa: E402
from pipeline.retrieval import DIMENSIONS, POOL_SQL, dimension_facets  # noqa: E402

POOL_THRESHOLD = 60  # retrieval.py::K_COVERED = 5；池子要有明顯大於 K 的挑選空間
FACET_THRESHOLD = 10

PROFILE_COUNTS_SQL = "SELECT subject_profile, count(*) FROM shared_prompt_histories GROUP BY 1"
CATEGORY_COUNTS_SQL = "SELECT category, count(*) FROM prompt_knowledge_presets GROUP BY 1"
FACET_COUNTS_SQL = (
    "SELECT f, count(*) FROM prompt_knowledge_presets, unnest(facet_ids) AS f GROUP BY 1"
)


@dataclass
class ReportResult:
    text: str
    pool_gaps: list[tuple[str, str, int]]
    facet_gaps: list[tuple[str, int]]

    @property
    def has_gaps(self) -> bool:
        return bool(self.pool_gaps or self.facet_gaps)


def build_report(
    *,
    profile_counts: dict[str, int],
    pool_sizes: dict[tuple[str, str], int],
    facet_counts: dict[str, int],
    category_counts: dict[str, int],
    catalog: FacetCatalog,
    pool_threshold: int = POOL_THRESHOLD,
    facet_threshold: int = FACET_THRESHOLD,
) -> ReportResult:
    lines: list[str] = ["== histories：每 profile 筆數 =="]
    for profile in catalog.profiles:
        lines.append(f"  {profile:10} {profile_counts.get(profile, 0)}")

    lines.append("\n== 候選池：每 (profile, 維度) ==")
    pool_gaps: list[tuple[str, str, int]] = []
    for profile in catalog.profiles:
        for dim in DIMENSIONS:
            if not dimension_facets(catalog, profile, dim):
                continue
            n = pool_sizes.get((profile, dim), 0)
            flag = "  <== 未達門檻" if n < pool_threshold else ""
            lines.append(f"  {profile:10} {dim:12} {n:6d}{flag}")
            if n < pool_threshold:
                pool_gaps.append((profile, dim, n))

    lines.append("\n== facet：每個 id 的 preset 筆數 ==")
    facet_gaps: list[tuple[str, int]] = []
    for facet_id in sorted(catalog.facets, key=lambda fid: facet_counts.get(fid, 0)):
        n = facet_counts.get(facet_id, 0)
        flag = "  <== 未達門檻" if n < facet_threshold else ""
        lines.append(f"  {facet_id:24} {n:6d}{flag}")
        if n < facet_threshold:
            facet_gaps.append((facet_id, n))

    lines.append("\n== preset category 分布 ==")
    for category, n in sorted(category_counts.items(), key=lambda kv: -kv[1]):
        lines.append(f"  {category:10} {n}")

    if pool_gaps or facet_gaps:
        lines.append(f"\n共 {len(pool_gaps)} 個候選池、{len(facet_gaps)} 個 facet 未達門檻。")
    else:
        lines.append("\n全部達標。")

    return ReportResult("\n".join(lines), pool_gaps, facet_gaps)


# ---------- 以下吃 conn ----------


def query_profile_counts(conn) -> dict[str, int]:
    return dict(conn.execute(PROFILE_COUNTS_SQL).fetchall())


def query_category_counts(conn) -> dict[str, int]:
    return dict(conn.execute(CATEGORY_COUNTS_SQL).fetchall())


def query_facet_counts(conn) -> dict[str, int]:
    return dict(conn.execute(FACET_COUNTS_SQL).fetchall())


def query_pool_sizes(conn, catalog: FacetCatalog) -> dict[tuple[str, str], int]:
    out: dict[tuple[str, str], int] = {}
    cache: dict[tuple[str, ...], int] = {}
    for profile in catalog.profiles:
        for dim in DIMENSIONS:
            facets = dimension_facets(catalog, profile, dim)
            if not facets:
                continue
            cache_key = tuple(sorted(facets))
            if cache_key not in cache:
                cache[cache_key] = conn.execute(POOL_SQL, {"facets": facets}).fetchone()[0]
            out[(profile, dim)] = cache[cache_key]
    return out


def main(argv: list[str] | None = None) -> None:
    ap = argparse.ArgumentParser(description="覆蓋度報告：候選池與 facet 缺口")
    ap.add_argument("--pool-threshold", type=int, default=POOL_THRESHOLD)
    ap.add_argument("--facet-threshold", type=int, default=FACET_THRESHOLD)
    args = ap.parse_args(argv)

    from pipeline.db import connect

    catalog = load_facets(FACETS_PATH)
    with connect() as conn:
        result = build_report(
            profile_counts=query_profile_counts(conn),
            pool_sizes=query_pool_sizes(conn, catalog),
            facet_counts=query_facet_counts(conn),
            category_counts=query_category_counts(conn),
            catalog=catalog,
            pool_threshold=args.pool_threshold,
            facet_threshold=args.facet_threshold,
        )
    print(result.text)
    sys.exit(1 if result.has_gaps else 0)


if __name__ == "__main__":
    main()
```

- [ ] **Step 4: 執行純函式測試，確認通過**

Run: `cd scripts && .\.venv\Scripts\python.exe -m pytest tests/test_coverage_report.py -v`
Expected: PASS（6 個測試，全部不需要 DB）

- [ ] **Step 5: 補一個 integration 測試（需要 DB 已啟動，驗證 SQL 本體）**

在 `scripts/tests/test_coverage_report.py` 檔案最後新增：

```python
import pytest

from pipeline import db


@pytest.mark.integration
def test_query_functions_return_data_shaped_dicts_against_real_db():
    if not db.db_available():
        pytest.skip("PostgreSQL 未啟動")
    from coverage_report import query_category_counts, query_facet_counts, query_pool_sizes, query_profile_counts

    with db.connect() as conn:
        profiles = query_profile_counts(conn)
        pools = query_pool_sizes(conn, CAT)
        facets = query_facet_counts(conn)
        categories = query_category_counts(conn)
    assert all(isinstance(v, int) for v in profiles.values())
    assert all(isinstance(k, tuple) and len(k) == 2 for k in pools)
    assert all(isinstance(v, int) for v in facets.values())
    assert all(isinstance(v, int) for v in categories.values())
```

（把檔案最上方的 `import pytest` 移到檔頭，與既有 import 合併，不要留兩份 `import pytest`。）

- [ ] **Step 6: 若本機 DB 已啟動，執行 integration 測試確認可跑**

Run: `cd scripts && .\.venv\Scripts\python.exe -m pytest tests/test_coverage_report.py -v -m integration`
Expected: PASS，或若 DB 未啟動則 SKIPPED（兩者皆可接受，取決於本機 `docker compose up -d db` 是否已跑）

- [ ] **Step 7: Ruff 檢查**

Run: `cd scripts && .\.venv\Scripts\python.exe -m ruff check coverage_report.py tests/test_coverage_report.py`
Expected: 無錯誤

- [ ] **Step 8: Commit**

```bash
git add scripts/coverage_report.py scripts/tests/test_coverage_report.py
git commit -m "feat(pipeline): add coverage_report.py acceptance tool

重用 retrieval.py 的 POOL_SQL/dimension_facets 計算候選池，
門檻未達標時 exit code 1，可接進驗收流程或 CI。

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>"
```

---

## Task 6: 文件同步

不需要真的把 9 層跑完（那需要使用者自己的付費 Gemini 金鑰與已啟動的 DB，是操作性步驟，
見本檔最後的「擴增之後」）。這個任務只更新靜態、不依賴真實執行結果的文件內容。

**Files:**
- Modify: `scripts/README.md`
- Modify: `docs/superpowers/specs/2026-09-21-genai-prompt-copilot-design.md`（§15 決定紀錄表）
- Modify: `docs/單輪流程說明.md`（加註，不改真實執行輸出）

- [ ] **Step 1: 改寫 `scripts/README.md` 的執行段落**

把現有「全量（會花時間，可中斷後重跑同一指令續跑）」段落（含 `python seed_data.py
--max-items 400` 那幾行）取代為：

```markdown
全量分層抓取（fetch 階段免費，約 1 分鐘量級；structure 階段是唯一的付費瓶頸，5,000+
次 Gemini 結構化呼叫，會花時間也會花額度）：

    python seed_data.py

fetch 階段抓取邏輯見 `pipeline/strata.py` 的 9 層配額表（`baseModels x period`），
用來解決「同一組固定查詢參數永遠抓到同一批熱門人物向內容」的題材偏斜問題，設計細節見
[docs/superpowers/specs/2026-09-22-corpus-expansion-design.md](../docs/superpowers/specs/2026-09-22-corpus-expansion-design.md)。
先用 `--quota-scale` 跑小規模驗證候選池是否符合預期，再跑全量：

    python seed_data.py --from structure --max-records 500   # 先用已抓好的 raw 小量跑過結構化
    python coverage_report.py                                # 檢查候選池與 facet 是否達門檻
    python seed_data.py --from structure                     # 確認沒問題後跑全量結構化

    python seed_data.py --quota-scale 0.5   # fetch 階段只抓每層一半配額，用於快速試跑
```

同時把後面「從某階段起跑」段落下方的

```
    python -m pipeline.fetch_civitai --max-items 500
```

改成：

```
    python -m pipeline.fetch_civitai --quota-scale 0.1   # 只抓每層 10% 配額，快速驗證分層設定
```

- [ ] **Step 2: 改寫「續跑與規模」段落**

把提到 `--max-items 400`／`--max-items 3000`／`--max-items <更大的數字>` 的段落改寫成：

```markdown
五個階段（fetch / clean / structure / embed / load）**全部可續跑**：`fetch` 靠
`state.json`（每層各自的 cursor，見 `pipeline/strata.py`）續跑，`structure`／`embed`
靠已存在的 `source_ref` 跳過重複，`load` 用 `ON CONFLICT (source_ref)` upsert。因此
重新執行 `python seed_data.py` 會從每個階段上次停下的地方繼續，不會重跑已完成的部分，
也不會產生重複資料。

擴增前的正式語料是用 `python seed_data.py --max-items 400`（舊版單層 CLI）建立的
420 raw / 258 clean。分層抓取的配額表與規模換算見
[docs/superpowers/specs/2026-09-22-corpus-expansion-design.md](../docs/superpowers/specs/2026-09-22-corpus-expansion-design.md) §2、§6：
9 層合計 9,000 raw，預估落在 5,200–5,500 clean。要調整規模就改
`pipeline/strata.py::STRATA` 裡各層的 `quota`（程式碼常數，改了要走 code review，
不是隱藏在設定檔裡的旋鈕）。
```

- [ ] **Step 3: 確認 README 沒有殘留 `--max-items`／`--base-models` 的舊用法**

Run: `cd .. && grep -n "max-items\|base-models" scripts/README.md`
Expected: 無輸出（全部已改寫成 `--quota-scale` 或已移除）

- [ ] **Step 4: 補主 spec §15 決定紀錄**

編輯 `docs/superpowers/specs/2026-09-21-genai-prompt-copilot-design.md`，在 §15 的表格
（`| 跨維度去重歸屬 | ... |` 那一行之後）新增一行：

```markdown
| `modelId`／`tags` 針對性抓取 | 已實測不可行，不採用 | `modelId` 反查圖片回傳內容 100% 無 `meta.prompt`；`tags` 查詢參數回 400 Bad Request。見 `docs/superpowers/specs/2026-09-22-corpus-expansion-design.md` §4.3 |
```

- [ ] **Step 5: 在 `docs/單輪流程說明.md` 的候選池數字旁加註**

在「同時撈候選池大小」小節裡 `` ```text ... ``` `` 區塊（`風格 池 238 → 6...`）之後、
下一個 `「池 N → M」是...` 段落之前，插入：

```markdown
> 以上數字反映的是擴增前的語料規模（859 筆 presets）。資料庫擴增後（見
> [docs/superpowers/specs/2026-09-22-corpus-expansion-design.md](superpowers/specs/2026-09-22-corpus-expansion-design.md)）
> 這些池子大小會改變；要看當下真實的候選池，跑 `python coverage_report.py`
> 而不要沿用這裡寫死的數字。
```

同時在「跨維度去重」小節提到「859 筆裡有 134 筆、15.6%」那句後面加一個同樣性質的註記：

```markdown
（此比例同樣反映擴增前的語料規模，會隨擴增變動，不重新實測更新這裡的數字。）
```

- [ ] **Step 6: Commit**

```bash
git add scripts/README.md docs/superpowers/specs/2026-09-21-genai-prompt-copilot-design.md "docs/單輪流程說明.md"
git commit -m "docs: sync README/spec/walkthrough with stratified fetch

- README：全量指令改為 python seed_data.py（分層抓取自動跑 9 層），
  --max-items/--base-models 改為 --quota-scale
- 主 spec §15 補一筆 modelId/tags 針對性抓取已實測不可行的決定紀錄
- 單輪流程說明.md 的候選池數字旁加註：反映擴增前規模，當下數字看
  coverage_report.py，不沿用寫死的數字

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>"
```

（若上面 `git add` 路徑因殼層編碼問題貼不出正確中文檔名，改用
`git add -A -- scripts/README.md docs/superpowers/specs docs/單輪流程說明.md` 或直接
`git add -A` 後用 `git status --short` 確認只有這三個檔案被納入再 commit。）

---

## 全部任務完成後的整體驗證

- [ ] **執行完整單元測試（不需 DB／網路）**

Run: `cd scripts && .\.venv\Scripts\python.exe -m pytest -v`
Expected: PASS（全部，`-m 'not integration'` 是 `pyproject.toml` 的預設 addopts）

- [ ] **執行 ruff 檢查整個 scripts 目錄**

Run: `cd scripts && .\.venv\Scripts\python.exe -m ruff check .`
Expected: 無錯誤

- [ ] **若本機 DB 已啟動，執行 integration 測試**

Run: `cd scripts && .\.venv\Scripts\python.exe -m pytest -v -m integration`
Expected: PASS 或依資料量 SKIPPED（`prompt_knowledge_presets` 是空的會被既有的
`test_retrieval.py::_conn_with_data` skip 邏輯跳過）

---

## 擴增之後（操作性步驟，不在本計畫任務內）

本計畫只交付程式碼與測試；實際把 9 層跑完、花費使用者自己的 Gemini 付費額度，是
spec §9 定義的操作性步驟，由使用者自己決定何時執行：

1. `python -m pipeline.fetch_civitai`（全部 9 層，免費，分鐘量級）→ `python -m pipeline.clean`
2. `python seed_data.py --from structure --max-records 500` → `embed` → `load` →
   `python coverage_report.py`，檢查 profile 分布是否接近 spec §2／§6 的估算
3. 確認無誤後跑全量 `python seed_data.py --from structure`
4. 跑完全量後，重新執行一次 `docs/單輪流程說明.md` 描述的 demo 指令，把該文件裡寫死
   的候選池數字換成新的真實輸出（Task 6 Step 5 加的註記到時候可以拿掉）
