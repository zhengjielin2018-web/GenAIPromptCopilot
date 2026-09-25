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


PROMPT_TEMPLATE = """你是生圖提示詞知識庫的整理員。下面每一筆是一個知識庫片段：
它涵蓋的 facet 清單，以及它的英文 tag 清單。
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
        stats = run_backfill(conn, default_client(), catalog, limit=args.limit, batch_size=args.batch_size,
                             dry_run=args.dry_run)
    print(f"處理 {stats['rows']} 筆、{stats['tags']} 個 tag，other {stats['other']}"
          f"（{(stats['other'] / stats['tags'] * 100) if stats['tags'] else 0:.1f}%）")
    for facet, n in sorted(stats["facets"].items()):
        print(f"  {facet:<26}{n:>7} 筆有 tag")
    return 0


if __name__ == "__main__":
    sys.exit(main())
