"""一次性回填 prompt_knowledge_presets.facet_tags：把每筆片段的 tag 歸到它 facet_ids 裡的哪個 facet
（設計 2026-09-25-set-recommendations-design.md §4.2）。整套組合推薦的對照表與「只採用這幾個 facet」靠它。

    python backfill_facet_tags.py [--limit N] [--batch-size 20] [--dry-run]

只處理 facet_tags IS NULL 的列，每批一個交易，可中斷、可重跑。全部歸不進 facet 的列寫 {}（不是 NULL），
重跑時不會再送一次。某批被 Gemini 擋掉或回壞 JSON 就改一筆一筆送；單筆仍失敗的留 NULL、本次不再碰，
結尾列出 id，下次重跑會再試。structure.py 不產這欄：語料現在沒在長，新片段進來後重跑這支即可。
"""

from __future__ import annotations

import argparse
import json
import sys
from collections import Counter
from collections.abc import Callable
from pathlib import Path

from pydantic import BaseModel, Field, ValidationError

sys.path.insert(0, str(Path(__file__).resolve().parent))

from pipeline.config import FACETS_PATH  # noqa: E402
from pipeline.facets import FacetCatalog, load_facets  # noqa: E402
from pipeline.gemini_client import UnusableResponse, default_client  # noqa: E402

BATCH_SIZE = 20
OTHER = "other"

# skip＝本次逐筆重送仍失敗的列。不排除的話 ORDER BY id 每批都先撈到它們，後面的列永遠輪不到。
# ::bigint[] 讓空 list 也有型別
PENDING_SQL = """
SELECT id, facet_ids, prompt_snippet FROM prompt_knowledge_presets
WHERE facet_tags IS NULL AND id <> ALL(%(skip)s::bigint[]) ORDER BY id LIMIT %(limit)s
"""
# 再檢查一次 IS NULL：兩支程式同時跑也不會互相覆蓋
UPDATE_SQL = """
UPDATE prompt_knowledge_presets SET facet_tags = %(facet_tags)s::jsonb
WHERE id = %(id)s AND facet_tags IS NULL
"""


# 用 list 不用 dict[str, str]：dict 會轉成 additionalProperties，Gemini Developer API 模式送出前就拒收
class TagFacet(BaseModel):
    tag: str = Field(description="tag 原字照抄")
    facet: str = Field(description="facet id 或 other")


class Assignment(BaseModel):
    id: int
    assignments: list[TagFacet]


class BatchOut(BaseModel):
    items: list[Assignment]


PROMPT_TEMPLATE = """你是生圖提示詞知識庫的整理員。下面每一筆是一個知識庫片段：
它涵蓋的 facet 清單，以及它的英文 tag 清單。
請把每個 tag 歸到「最貼切的一個 facet」；歸不進任何 facet 的填 "other"。

規則：
- 每個 tag 只歸一個 facet；facet 只能從該筆自己的清單選。
- tag 原字照抄：不要改寫、不要翻譯、不要合併、不要漏掉。
- 回 JSON，每一筆都要有：
  {{"items":[{{"id":<id>,"assignments":[{{"tag":"<tag>","facet":"<facet id 或 other>"}}, ...]}}, ...]}}

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


def fetch_pending(conn, limit: int, skip: list[int]) -> list[dict]:
    rows = conn.execute(PENDING_SQL, {"limit": limit, "skip": skip}).fetchall()
    return [{"id": r[0], "facet_ids": list(r[1]), "prompt_snippet": r[2]} for r in rows]


def ask(client, rows: list[dict], catalog: FacetCatalog) -> dict[int, dict[str, str]]:
    """送一個 prompt，回 id → {tag: facet}。"""
    result = client.generate_structured(build_prompt(rows, catalog), BatchOut)
    return {a.id: {x.tag: x.facet for x in a.assignments} for a in result.items}


def assign(client, rows: list[dict], catalog: FacetCatalog, skipped: list[int],
           log: Callable[..., None]) -> tuple[list[dict], dict[int, dict[str, str]]]:
    """回 (拿到結果的列, id → {tag: facet})。整批被擋或回壞 JSON 就一筆一筆重送，只犧牲出事的那筆；
    單筆仍失敗的記進 skipped、留 NULL。"""
    try:
        return rows, ask(client, rows, catalog)
    except (UnusableResponse, ValidationError) as e:
        if len(rows) == 1:
            # 單筆不原封不動重送：內容攔截重送到過為止等於規避安全判定（見 gemini_client.UnusableResponse）
            skipped.append(rows[0]["id"])
            log(f"  跳過 id={rows[0]['id']}：{type(e).__name__} {e}")
            return [], {}
        log(f"  本批 {len(rows)} 筆失敗（{type(e).__name__}），改一筆一筆送")
    done: list[dict] = []
    by_id: dict[int, dict[str, str]] = {}
    for row in rows:
        ok, got = assign(client, [row], catalog, skipped, log)
        done += ok
        by_id.update(got)
    return done, by_id


def run_backfill(conn, client, catalog: FacetCatalog, *, limit: int | None = None, batch_size: int = BATCH_SIZE,
                 dry_run: bool = False, log: Callable[..., None] = print) -> dict:
    stats: dict = {"rows": 0, "tags": 0, "other": 0, "facets": Counter(), "skipped": []}
    remaining = limit
    while remaining is None or remaining > 0:
        n = batch_size if remaining is None else min(batch_size, remaining)
        rows = fetch_pending(conn, n, stats["skipped"])
        if not rows:
            break
        done, by_id = assign(client, rows, catalog, stats["skipped"], log)
        with conn.cursor() as cur:
            for row in done:
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
            for row in done:
                log(f"[dry-run] {row['id']}: {json.dumps(normalize(row, by_id.get(row['id'])), ensure_ascii=False)}")
            break
        conn.commit()
        if remaining is not None:
            remaining -= len(rows)  # 跳過的也算：--limit 是這次最多送幾筆
        log(f"backfill: {stats['rows']} 筆已寫入（本批 {len(done)}）")
    return stats


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--limit", type=int, default=None, help="本次最多處理幾筆")
    ap.add_argument("--batch-size", type=int, default=BATCH_SIZE)
    ap.add_argument("--dry-run", action="store_true", help="只送第一批給 Gemini、印結果，不寫入")
    args = ap.parse_args(argv)

    from pipeline.db import connect

    catalog = load_facets(FACETS_PATH)
    with connect() as conn:
        stats = run_backfill(conn, default_client(), catalog, limit=args.limit, batch_size=args.batch_size,
                             dry_run=args.dry_run)
    print(f"處理 {stats['rows']} 筆、{stats['tags']} 個 tag，other {stats['other']}"
          f"（{(stats['other'] / stats['tags'] * 100) if stats['tags'] else 0:.1f}%）")
    for facet, n in sorted(stats["facets"].items()):
        print(f"  {facet:<26}{n:>7} 筆有 tag")
    if stats["skipped"]:
        print(f"跳過 {len(stats['skipped'])} 筆（留 NULL，下次重跑會再試）：{', '.join(map(str, stats['skipped']))}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
