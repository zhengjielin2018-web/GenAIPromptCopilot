"""回填不打網路、不連 DB：client 與 conn 都是記錄器。"""

from __future__ import annotations

import json

from backfill_facet_tags import BatchOut, build_prompt, normalize, run_backfill, split_tags
from pipeline.config import FACETS_PATH
from pipeline.facets import load_facets

CAT = load_facets(FACETS_PATH)
ROW = {"id": 41720, "facet_ids": ["clothing.upper", "clothing.footwear"],
       "prompt_snippet": "purple kimono, sandals, white socks, purple kimono, ,  tabi "}


class FakeClient:
    def __init__(self, payloads):
        self.payloads = list(payloads)
        self.prompts = []

    def generate_structured(self, prompt, schema):
        self.prompts.append(prompt)
        return schema.model_validate(self.payloads.pop(0))


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


def test_split_tags_strips_dedups_and_drops_empty():
    assert split_tags(ROW["prompt_snippet"]) == ["purple kimono", "sandals", "white socks", "tabi"]


def test_normalize_keeps_only_this_rows_facets_and_original_tags_and_defaults_missing_to_other():
    out = normalize(ROW, {"purple kimono": "clothing.upper", "sandals": "clothing.footwear",
                          "white socks": "clothing.lower",      # 不在這筆的 facet_ids → 丟掉
                          "kimono": "clothing.upper"})          # 不是原字 → 丟掉；tabi 沒給 → other
    assert out == {"clothing.upper": ["purple kimono"], "clothing.footwear": ["sandals"]}


def test_normalize_all_other_is_empty_dict_not_none():
    assert normalize(ROW, None) == {}
    assert normalize(ROW, {"sandals": "other"}) == {}


def test_build_prompt_lists_each_row_with_its_own_facets_and_tags():
    p = build_prompt([ROW, {"id": 7, "facet_ids": ["style.genre"], "prompt_snippet": "oil painting"}], CAT)
    assert "[id=41720]" in p and "clothing.upper, clothing.footwear" in p
    assert "purple kimono | sandals | white socks | tabi" in p
    assert "[id=7]" in p and "oil painting" in p
    assert "clothing.footwear：鞋履" in p          # facet 說明


def test_run_backfill_writes_one_json_per_row_commits_per_batch_and_stops_when_nothing_pending():
    conn = FakeConn([[(41720, ROW["facet_ids"], ROW["prompt_snippet"]), (7, ["style.genre"], "oil painting")], []])
    client = FakeClient([{"items": [
        {"id": 41720, "assignments": {"purple kimono": "clothing.upper", "sandals": "clothing.footwear"}},
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
    client = FakeClient([{"items": [{"id": 41720, "assignments": {}}]}])
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
    b = BatchOut.model_validate({"items": [{"id": 1, "assignments": {"x": "other"}}]})
    assert b.items[0].assignments == {"x": "other"}
