"""不打網路、不連 DB：client 與 conn 都是記錄器。併發用真的 ThreadPoolExecutor，但 client 是假的。"""

from __future__ import annotations

import json
import threading
import time

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
    # (1, upper) 子表已有列、只是 tag 改過 → 更新；(2, footwear) 子表沒有這一列 → 新增，不在 replacing 裡
    assert plan.replacing == frozenset({(1, "clothing.upper")})


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


def test_run_reports_inserted_and_updated_separately():
    presets = [(1, {"style.genre": ["new"]}), (2, {"style.genre": ["changed"]})]
    conn = FakeConn(presets, [(2, "style.genre", "old")])   # (1,genre) 沒有既有列→新增；(2,genre) 改過→更新
    stats = run(conn, FakeClient(), log=lambda *_: None)
    assert stats["inserted"] == 1 and stats["updated"] == 1 and stats["upserted"] == 2


class ShortClient:
    """對某一批少回一個向量，模擬 Gemini 漏字（不是拋例外，是正常回應但數量對不上）。"""

    def __init__(self, short_on: str):
        self.short_on = short_on

    def embed_batch(self, texts, *, task_type):
        vectors = [[float(len(t))] + [0.0] * 767 for t in texts]
        return vectors[:-1] if any(self.short_on in t for t in texts) else vectors


def test_batch_with_mismatched_vector_count_is_recorded_as_failed():
    # Review round 1, Finding 2：向量數跟送出的文字數對不上時，這一批要算失敗、不能拖垮其他批
    presets = [(1, {"style.genre": ["good"]}), (2, {"style.genre": ["short"]}), (3, {"style.genre": ["fine"]})]
    conn = FakeConn(presets, [])
    stats = run(conn, ShortClient("short"), workers=2, batch_size=1, log=lambda *_: None)
    assert sorted(_upserts(conn)) == [(1, "style.genre", "good"), (3, "style.genre", "fine")]
    assert stats["failed_batches"] == [[(2, "style.genre")]]
    assert stats["upserted"] == 2


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


class RaisingOnFirstUpsertConn(FakeConn):
    """第一次寫 UPSERT_SQL 就丟例外，模擬主執行緒寫入炸掉（review：不該把佇列裡的批次打光才報錯）。"""

    def cursor(self):
        conn = self

        class Cur:
            def __enter__(self):
                return self

            def __exit__(self, *exc):
                return False

            def execute(self, sql, params):
                if sql == UPSERT_SQL:
                    raise RuntimeError("db exploded")
                conn.writes.append((sql, params))
                conn.write_threads.add(threading.get_ident())

            def executemany(self, sql, seq):
                for params in seq:
                    self.execute(sql, params)

        return Cur()


class SlowCountingClient:
    """每次呼叫都睡一下再回傳、計數呼叫次數：讓還沒開始的批次留在池的佇列裡，可以驗證有沒有被取消。"""

    def __init__(self, delay: float = 0.02):
        self.delay = delay
        self.calls = 0
        self._lock = threading.Lock()

    def embed_batch(self, texts, *, task_type):
        time.sleep(self.delay)
        with self._lock:
            self.calls += 1
        return [[float(len(t))] + [0.0] * 767 for t in texts]


def test_db_write_failure_cancels_queued_batches_instead_of_draining_the_pool():
    # 主執行緒寫入炸掉時，`with ThreadPoolExecutor(...)` 離開前不該把佇列裡剩下的批次都打完 Gemini
    # 才讓例外冒出來——那樣一整輪（例如 1,150 批）在真正失敗前還會多打上千次請求。
    presets = [(i, {"style.genre": [f"tag{i}"]}) for i in range(20)]   # 20 個單筆批次
    conn = RaisingOnFirstUpsertConn(presets, [])
    client = SlowCountingClient(delay=0.02)
    try:
        run(conn, client, workers=2, batch_size=1, log=lambda *_: None)
        raise AssertionError("expected RuntimeError to propagate")
    except RuntimeError as e:
        assert str(e) == "db exploded"
    assert client.calls < 10          # 遠少於 20 批：佇列裡沒送出的批次被取消了，不是全部打完才炸
