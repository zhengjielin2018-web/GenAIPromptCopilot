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
    # 送出在執行緒池，不在主執行緒（池會不會開到 3 條看排程，不斷言條數）
    assert threading.get_ident() not in client.threads
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
    conn, client = FakeConn([(1, {"style.genre": ["a"]})], [(1, "style.palette", "x")]), FakeClient()
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
