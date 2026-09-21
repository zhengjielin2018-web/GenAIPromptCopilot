import json

from pipeline.fetch_civitai import run_fetch
from pipeline.jsonl import read_jsonl


class FakeClient:
    """模擬 CivitaiClient.iter_images:依 cursor 回不同批次。"""

    def __init__(self, pages):
        self.pages = pages  # {cursor_or_None: (items, next_cursor)}
        self.calls = []

    def iter_images(self, *, limit=200, cursor=None, base_models=None):
        self.calls.append(cursor)
        while True:
            items, nxt = self.pages[cursor]
            for it in items:
                yield it, cursor
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
    assert s == {"cursor": None, "fetched": 2, "done": False}


def test_fetch_resumes_from_state_and_skips_duplicates(tmp_path):
    client = FakeClient({None: ([{"id": 1}, {"id": 2}], "c2"), "c2": ([{"id": 3}, {"id": 4}], None)})
    out, state = tmp_path / "images.jsonl", tmp_path / "state.json"
    assert run_fetch(client, max_items=3, out_path=out, state_path=state) == 3
    assert json.loads(state.read_text())["cursor"] == "c2"
    assert run_fetch(client, max_items=10, out_path=out, state_path=state) == 1
    assert client.calls[-1] == "c2"
    assert [r["id"] for r in read_jsonl(out)] == [1, 2, 3, 4]


def test_fetch_noop_when_done(tmp_path):
    client = FakeClient({None: ([{"id": 1}], None)})
    out, state = tmp_path / "images.jsonl", tmp_path / "state.json"
    run_fetch(client, max_items=10, out_path=out, state_path=state)
    assert run_fetch(client, max_items=10, out_path=out, state_path=state) == 0
    assert client.calls == [None]


def test_fetch_midpage_stop_does_not_lose_rest_of_page(tmp_path):
    client = FakeClient({None: ([{"id": 1}, {"id": 2}], "c2"), "c2": ([{"id": 3}], None)})
    out, state = tmp_path / "images.jsonl", tmp_path / "state.json"
    assert run_fetch(client, max_items=1, out_path=out, state_path=state) == 1
    assert run_fetch(client, max_items=10, out_path=out, state_path=state) == 2
    assert [r["id"] for r in read_jsonl(out)] == [1, 2, 3]
