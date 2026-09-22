import json

import pipeline.fetch_civitai as fetch_civitai
from pipeline.fetch_civitai import _default_stratum_state, _migrate_to_v2, _remaining_quota, run_fetch
from pipeline.jsonl import append_jsonl, read_jsonl
from pipeline.strata import STRATA


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


def test_remaining_quota_scales_and_floors_at_zero():
    """main() 每次執行都用「目標 − 已累積」算這次還要抓多少；若直接每次都傳整個 quota，
    重跑腳本會讓已達配額但 cursor 未耗盡的層再多抓一整份。"""
    assert _remaining_quota(1000, 1.0, 400) == 600
    assert _remaining_quota(1000, 0.5, 400) == 100
    assert _remaining_quota(1000, 1.0, 1000) == 0
    assert _remaining_quota(1000, 1.0, 1500) == 0  # 已超額也不會變負的
    assert _remaining_quota(9, 0.5, 0) == 4  # 9*0.5=4.5; Python round() 用銀行家捨入法捨到偶數 4


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


class FakeMainClient:
    """模擬 CivitaiClient.iter_images 的無限資料來源：每層永遠有更多資料可抓（不會自然耗盡），
    逼 main() 對每層都只能靠「這次還要抓多少」的計算來停手，而不是靠 cursor 用完停手。
    記錄每次呼叫收到的 (base_models, sort, period)，用來驗證 main() 有沒有把 9 層的專屬
    參數傳對、或誤把 sort/period 對調、或漏傳 base_models。"""

    def __init__(self):
        self.calls: list[tuple] = []
        self._next_id = 0

    def iter_images(self, *, limit=200, cursor=None, base_models=None,
                     sort="Most Reactions", period="AllTime"):
        self.calls.append((tuple(base_models) if base_models else None, sort, period))
        c = 0 if cursor is None else int(cursor)
        while True:
            self._next_id += 1
            yield {"id": self._next_id}, str(c)
            c += 1


def test_main_wires_each_stratum_with_own_params_and_is_idempotent_on_rerun(tmp_path, monkeypatch):
    """finding: main() 的每層參數配線完全沒有測試覆蓋到——把 max_items 誤傳成
    stratum.quota（整份配額而非這次還要抓多少）、把 sort/period 對調、或漏傳 base_models，
    都會讓現有 12 個測試全部照樣通過。這裡直接跑 main()，監看假 client 實際收到的參數，
    並驗證重跑一次不會再新增任何項目，藉此把這兩類迴歸釘死。"""
    fake = FakeMainClient()
    raw, state = tmp_path / "images.jsonl", tmp_path / "state.json"
    monkeypatch.setattr(fetch_civitai, "default_client", lambda *_a, **_kw: fake)
    monkeypatch.setattr(fetch_civitai, "RAW_PATH", raw)
    monkeypatch.setattr(fetch_civitai, "STATE_PATH", state)

    fetch_civitai.main(["--quota-scale", "0.01"])

    # (a) 每層都用自己的 (base_models, sort, period) 呼叫 client，而不是 9 份重複的同一組
    expected_triples = {
        (tuple(s.base_models) if s.base_models else None, s.sort, s.period) for s in STRATA
    }
    assert len(fake.calls) == len(STRATA)
    assert set(fake.calls) == expected_triples

    # 第一次執行只抓「這次還要抓多少」（remaining），不是整份 quota
    expected_total = sum(_remaining_quota(s.quota, 0.01, 0) for s in STRATA)
    assert len(list(read_jsonl(raw))) == expected_total

    # (b) 每層都已達配額（未達 done）：重跑一次不該再呼叫 client 或新增任何項目——
    # 如果 main() 誤傳 stratum.quota 而非 remaining，或讀錯累積用的 state 欄位，
    # 這裡就會再抓到東西
    fake.calls.clear()
    fetch_civitai.main(["--quota-scale", "0.01"])
    assert fake.calls == []
    assert len(list(read_jsonl(raw))) == expected_total
