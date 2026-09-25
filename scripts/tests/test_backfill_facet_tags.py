"""回填不打網路、不連 DB：client 與 conn 都是記錄器。"""

from __future__ import annotations

import json
import re

import pytest
from google import genai

from backfill_facet_tags import BatchOut, build_prompt, normalize, run_backfill, split_tags
from pipeline.config import FACETS_PATH
from pipeline.facets import load_facets
from pipeline.gemini_client import GeminiClient, UnusableResponse
from pipeline.ratelimit import RateLimiter

CAT = load_facets(FACETS_PATH)
ROW = {"id": 41720, "facet_ids": ["clothing.upper", "clothing.footwear"],
       "prompt_snippet": "purple kimono, sandals, white socks, purple kimono, ,  tabi "}


class FakeClient:
    """payloads 依序吐出；是 Exception 就照丟（模擬 Gemini 擋內容或回壞 JSON）。"""

    def __init__(self, payloads):
        self.payloads = list(payloads)
        self.prompts = []

    def generate_structured(self, prompt, schema):
        self.prompts.append(prompt)
        payload = self.payloads.pop(0)
        if isinstance(payload, Exception):
            raise payload
        return schema.model_validate(payload)


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


class TableConn(FakeConn):
    """照 SQL 語意演：pending＝還沒寫過、不在 skip 裡的列（依 id 排序）取 limit 筆。"""

    def __init__(self, rows):
        super().__init__([])
        self.rows = rows
        self.fetches = []

    def execute(self, sql, params):
        self.fetches.append({**params, "skip": list(params["skip"])})   # 快照：skip 是同一個會長大的 list
        written = {p["id"] for _, p in self.writes}
        pending = [r for r in self.rows if r[0] not in written and r[0] not in params["skip"]]
        return FakeResult(pending[: params["limit"]])


def test_split_tags_strips_dedups_and_drops_empty():
    assert split_tags(ROW["prompt_snippet"]) == ["purple kimono", "sandals", "white socks", "tabi"]


def test_normalize_keeps_only_this_rows_facets_and_original_tags_and_defaults_missing_to_other():
    out = normalize(ROW, {"purple kimono": "clothing.upper", "sandals": "clothing.footwear",
                          "white socks": "clothing.lower",      # 不在這筆的 facet_ids → 丟掉
                          "kimono": "clothing.upper"})          # 不是原字 → 丟掉；tabi 沒給 → other
    assert out == {"clothing.upper": ["purple kimono"], "clothing.footwear": ["sandals"]}


def test_normalize_splits_an_echoed_pipe_joined_key_and_each_part_keeps_the_facet():
    out = normalize(ROW, {"purple kimono | sandals": "clothing.upper"})
    assert out == {"clothing.upper": ["purple kimono", "sandals"]}


def test_normalize_splits_a_json_array_key():
    row = {"id": 1, "facet_ids": ["style.genre"], "prompt_snippet": "a, b, c"}
    assert normalize(row, {'["a","b"]': "style.genre"}) == {"style.genre": ["a", "b"]}


def test_normalize_matches_case_insensitively_after_strip_and_keeps_the_original_spelling():
    assert normalize(ROW, {" Sandals ": "clothing.footwear"}) == {"clothing.footwear": ["sandals"]}


def test_normalize_prefers_a_single_assignment_over_the_same_tag_inside_a_joined_key():
    out = normalize(ROW, {"purple kimono | sandals": "clothing.upper", "sandals": "clothing.footwear"})
    assert out == {"clothing.upper": ["purple kimono"], "clothing.footwear": ["sandals"]}


def test_normalize_does_not_split_a_key_that_is_itself_an_original_tag():
    row = {"id": 1, "facet_ids": ["style.genre"], "prompt_snippet": "a | b, c"}
    assert normalize(row, {"a | b": "style.genre"}) == {"style.genre": ["a | b"]}


def test_normalize_all_other_is_empty_dict_not_none():
    assert normalize(ROW, None) == {}
    assert normalize(ROW, {"sandals": "other"}) == {}


def test_build_prompt_lists_each_row_with_its_own_facets_and_tags():
    p = build_prompt([ROW, {"id": 7, "facet_ids": ["style.genre"], "prompt_snippet": "oil painting"}], CAT)
    assert "[id=41720]" in p and "clothing.upper, clothing.footwear" in p
    assert 'tags: ["purple kimono", "sandals", "white socks", "tabi"]' in p     # JSON 陣列，不是「 | 」串起的一行
    assert "purple kimono | sandals" not in p
    assert "不要把整個陣列合併成一個 tag" in p
    assert "[id=7]" in p and "oil painting" in p
    assert "clothing.footwear：鞋履" in p          # facet 說明


def test_run_backfill_writes_one_json_per_row_commits_per_batch_and_stops_when_nothing_pending():
    conn = FakeConn([[(41720, ROW["facet_ids"], ROW["prompt_snippet"]), (7, ["style.genre"], "oil painting")], []])
    client = FakeClient([{"items": [
        {"id": 41720, "assignments": [{"tag": "purple kimono", "facet": "clothing.upper"},
                                      {"tag": "sandals", "facet": "clothing.footwear"}]},
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
    conn = FakeConn([[(41720, ROW["facet_ids"], ROW["prompt_snippet"])],
                     [(41720, ROW["facet_ids"], ROW["prompt_snippet"])]])
    client = FakeClient([{"items": [{"id": 41720, "assignments": []}]}])
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
    b = BatchOut.model_validate({"items": [{"id": 1, "assignments": [{"tag": "x", "facet": "other"}]}]})
    assert b.items[0].assignments[0].tag == "x" and b.items[0].assignments[0].facet == "other"


def _one(row_id, tag, facet):
    return {"items": [{"id": row_id, "assignments": [{"tag": tag, "facet": facet}]}]}


def test_failed_batch_retries_row_by_row_and_skips_only_the_row_that_still_fails():
    rows = [(1, ["style.genre"], "oil painting"), (2, ["style.palette"], "monochrome"), (3, ["style.genre"], "anime")]
    conn = TableConn(rows)
    blocked = UnusableResponse("blocked", block_reason="PROHIBITED_CONTENT")
    client = FakeClient([blocked,                                   # 整批被擋
                         _one(1, "oil painting", "style.genre"),
                         blocked,                                   # id 2 單獨送還是被擋
                         _one(3, "anime", "style.genre")])
    stats = run_backfill(conn, client, CAT, log=lambda *_: None)

    assert [(p["id"], json.loads(p["facet_tags"])) for _, p in conn.writes] == [
        (1, {"style.genre": ["oil painting"]}), (3, {"style.genre": ["anime"]}),
    ]
    assert [re.findall(r"\[id=(\d+)\]", p) for p in client.prompts] == [["1", "2", "3"], ["1"], ["2"], ["3"]]
    assert [f["skip"] for f in conn.fetches] == [[], [2]]                  # 下一次查 pending 排除它，然後收工
    assert stats["skipped"] == [2] and stats["rows"] == 2 and conn.commits == 1


def test_lone_failing_row_is_skipped_without_resending_and_the_loop_moves_on():
    rows = [(1, ["style.genre"], "oil painting"), (2, ["style.genre"], "anime")]
    conn = TableConn(rows)
    client = FakeClient([{"items": "not a list"},                   # ValidationError
                         _one(2, "anime", "style.genre")])
    stats = run_backfill(conn, client, CAT, batch_size=1, log=lambda *_: None)

    assert [p["id"] for _, p in conn.writes] == [2]
    assert len(client.prompts) == 2                                  # 單筆批失敗不再原封不動重送
    assert [f["skip"] for f in conn.fetches] == [[], [1], [1]]
    assert stats["skipped"] == [1] and stats["rows"] == 1


class _ReachedTransport(Exception):
    pass


def test_batch_out_schema_is_accepted_by_gemini_developer_api_mode(monkeypatch):
    """dict[str, str] 轉成 additionalProperties，Developer API 模式在送出前就 ValueError。
    走真的 SDK 轉換、只換掉傳輸層：走得到送出那一步，schema 就過關。"""
    sdk = genai.Client(api_key="offline-test", vertexai=False)

    def refuse(*args, **kwargs):
        raise _ReachedTransport

    monkeypatch.setattr(sdk._api_client, "request", refuse)
    client = GeminiClient(sdk, structure_model="gemini-test", embedding_model="unused", dimensions=768,
                          limiter=RateLimiter(0), sleep=lambda _: None)
    with pytest.raises(_ReachedTransport):
        client.generate_structured("x", BatchOut)


class RedoConn(FakeConn):
    """照 SQL 語意演 facet_tags 狀態：pending＝NULL，或 redo_empty 時 {}；UPDATE 只寫仍待處理的列。"""

    def __init__(self, rows, state):
        super().__init__([])
        self.rows, self.state = rows, dict(state)
        self.fetches = []

    def _pending(self, row_id, redo_empty):
        v = self.state.get(row_id)
        return v is None or (redo_empty and v == {})

    def execute(self, sql, params):
        self.fetches.append({**params, "skip": list(params["skip"])})
        pending = [r for r in self.rows if self._pending(r[0], params["redo_empty"]) and r[0] not in params["skip"]]
        return FakeResult(pending[: params["limit"]])

    def cursor(self):
        conn = self

        class Cur(FakeCursor):
            def execute(self, sql, params):
                super().execute(sql, params)
                if conn._pending(params["id"], params["redo_empty"]):
                    conn.state[params["id"]] = json.loads(params["facet_tags"])

        return Cur(self.writes)


def test_redo_empty_reprocesses_empty_rows_and_does_not_refetch_a_row_that_stays_empty():
    rows = [(1, ["style.genre"], "oil painting"), (2, ["style.palette"], "monochrome"), (3, ["style.genre"], "anime")]
    conn = RedoConn(rows, {1: {}, 2: {}, 3: {"style.genre": ["anime"]}})
    client = FakeClient([{"items": [{"id": 1, "assignments": [{"tag": "oil painting", "facet": "style.genre"}]}]}])
    stats = run_backfill(conn, client, CAT, redo_empty=True, log=lambda *_: None)   # id 2 沒回 → 仍是 {}

    assert all(f["redo_empty"] is True for f in conn.fetches)
    assert [f["skip"] for f in conn.fetches] == [[], [2]]          # 仍是 {} 的不再撈，迴圈收工
    assert len(client.prompts) == 1
    assert [(p["id"], json.loads(p["facet_tags"]), p["redo_empty"]) for _, p in conn.writes] == [
        (1, {"style.genre": ["oil painting"]}, True), (2, {}, True),
    ]
    assert all("facet_tags = '{}'::jsonb" in sql for sql, _ in conn.writes)
    assert conn.state == {1: {"style.genre": ["oil painting"]}, 2: {}, 3: {"style.genre": ["anime"]}}
    assert stats["still_empty"] == [2] and stats["skipped"] == [] and stats["rows"] == 2


def test_without_redo_empty_rows_already_empty_are_left_alone():
    rows = [(1, ["style.genre"], "oil painting"), (2, ["style.genre"], "anime")]
    conn = RedoConn(rows, {1: {}, 2: None})
    client = FakeClient([_one(2, "anime", "style.genre")])
    stats = run_backfill(conn, client, CAT, log=lambda *_: None)

    assert all(f["redo_empty"] is False for f in conn.fetches)
    assert [p["id"] for _, p in conn.writes] == [2]
    assert conn.state[1] == {} and stats["still_empty"] == []
