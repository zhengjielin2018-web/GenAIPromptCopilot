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

PRESETS_SQL = (
    "SELECT id, facet_tags::text FROM prompt_knowledge_presets "
    "WHERE facet_tags IS NOT NULL AND facet_tags <> '{}'::jsonb ORDER BY id"
)
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
    # upserts 裡屬於「更新」的 (preset_id, facet_id)；不在裡面的 upserts 是「新增」
    replacing: frozenset[tuple[int, str]] = field(default_factory=frozenset)


def make_plan(presets: list[tuple[int, dict[str, list[str]]]], existing: dict[tuple[int, str], str]) -> Plan:
    """presets：(id, facet_tags)；existing：(preset_id, facet_id) → 子表現有的 tag_key。"""
    upserts: list[Item] = []
    unchanged = 0
    wanted: set[tuple[int, str]] = set()
    replacing: set[tuple[int, str]] = set()
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
                if (pid, facet_id) in existing:          # 子表已有這一列、只是 tag_key 不同：算「更新」不是「新增」
                    replacing.add((pid, facet_id))
    deletes = sorted(k for k in existing if k not in wanted)
    return Plan(upserts=upserts, deletes=deletes, unchanged=unchanged, replacing=frozenset(replacing))


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
    stats: dict = {"upserted": 0, "inserted": 0, "updated": 0, "deleted": 0, "unchanged": plan.unchanged,
                   "failed_batches": [], "planned_upserts": len(todo), "planned_deletes": len(plan.deletes),
                   "elapsed_s": 0.0}
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
        vectors = client.embed_batch([it[3] for it in batch], task_type="RETRIEVAL_DOCUMENT")
        if len(vectors) != len(batch):
            # Gemini 回傳的向量數跟送出的文字數對不上（漏字）：當成這一批失敗，不要用位置對齊硬寫，
            # 也不要讓後面的 zip(strict=True) 自己炸——那樣這一批會整批丟例外但訊息看不出是這個原因。
            raise ValueError(f"Gemini 回傳 {len(vectors)} 個向量，預期 {len(batch)} 個")
        return batch, vectors

    # 送出在執行緒池、寫入在主執行緒：as_completed 誰先回來誰先寫，每批一個交易。
    with ThreadPoolExecutor(max_workers=max(1, workers)) as pool:
        futures = {pool.submit(embed, b): b for b in batches}
        try:
            for fut in as_completed(futures):
                batch = futures[fut]
                try:
                    items, vectors = fut.result()
                except Exception as e:  # noqa: BLE001 - 一批失敗不該拖垮其他批；記下來結尾回報
                    stats["failed_batches"].append([(it[0], it[1]) for it in batch])
                    log(f"  批次失敗（{len(batch)} 筆）：{type(e).__name__} {e}")
                    continue
                # 這裡的資料庫錯誤（連線斷掉等）不攔截、直接往外炸掉整個 run：那不是「這一批」的問題，
                # 硬要當成失敗批記下來、continue 下一批，只會在同一條斷掉的連線上再炸一次。已經 commit
                # 的批次不會因此消失，重跑照常只補沒寫進去的。
                with conn.cursor() as cur:
                    for (pid, fid, key, _), vec in zip(items, vectors, strict=True):
                        cur.execute(UPSERT_SQL, (pid, fid, key, Vector(vec)))
                        if (pid, fid) in plan.replacing:
                            stats["updated"] += 1
                        else:
                            stats["inserted"] += 1
                conn.commit()
                stats["upserted"] += len(items)
                log(f"embed_facet_tags: {stats['upserted']}/{len(todo)} 已寫入")
        except BaseException:
            # 寫入炸掉、或使用者按 Ctrl-C：此時池裡可能還排著幾百批還沒送出。預設的
            # `with ThreadPoolExecutor(...)` 離開時只會 shutdown(wait=True)，不會取消還沒開始跑
            # 的 future，結果是例外要等所有排隊中的批次都打完 Gemini（可能上千次請求）才會冒出來。
            # 這裡主動 cancel_futures，讓還沒開始執行的批次直接取消；已經在跑的那幾批（數量最多等於
            # workers）跑完即可，已經 commit 的批次不受影響，重跑照常只補沒寫進去的。
            pool.shutdown(wait=False, cancel_futures=True)
            raise
    stats["elapsed_s"] = time.monotonic() - started
    return stats


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--workers", type=int, default=WORKERS, help="同時送 Gemini 的執行緒數")
    ap.add_argument("--batch-size", type=int, default=BATCH_SIZE,
                     help="每次請求幾段文字（gemini_client.BATCH_SIZE 是上限）")
    ap.add_argument("--limit", type=int, default=None, help="本次最多算幾個 (片段, facet)")
    ap.add_argument("--dry-run", action="store_true", help="只算待辦、印前 10 筆，不打 Gemini、不寫入")
    args = ap.parse_args(argv)

    from pipeline.db import connect
    from pipeline.gemini_client import default_client

    with connect() as conn:
        stats = run(conn, default_client(), workers=args.workers, batch_size=args.batch_size,
                    limit=args.limit, dry_run=args.dry_run)
    print(f"完成：新增 {stats['inserted']}、更新 {stats['updated']}、刪除 {stats['deleted']}、"
          f"不變（略過）{stats['unchanged']}，耗時 {stats['elapsed_s']:.0f} 秒")
    if stats["failed_batches"]:
        n = sum(len(b) for b in stats["failed_batches"])
        print(f"失敗 {len(stats['failed_batches'])} 批、{n} 筆，重跑會再試。第一批：{stats['failed_batches'][0][:5]}")
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
