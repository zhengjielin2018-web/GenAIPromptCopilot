import json

import pytest

import pipeline.fetch_civitai as fetch_civitai
from pipeline.fetch_civitai import (
    StateFileError,
    _default_stratum_state,
    _load_state,
    _migrate_to_v2,
    _remaining_quota,
    _save_state,
    run_fetch,
)
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


# --- I-3: 中途當機不能靜默超額重抓 -----------------------------------------


class PagedInfiniteClient:
    """呼應 FakeMainClient：資料源永遠有更多可抓、不靠 cursor 自然耗盡。每頁固定
    page_size 筆，item id 由 (頁號, 頁內位置) 決定性算出，讓不同 instance 從同一個
    cursor resume 時能接上同一組資料（模擬同一個一直有貨的遠端資料源）。crash_after
    有給值時，抓到第 crash_after 筆後丟例外模擬中途當機（例外在該筆已被 run_fetch
    寫入之後、要求下一筆時才丟出，符合「crash 發生在兩次頁界存檔之間」的最壞情況）。"""

    def __init__(self, page_size, crash_after=None):
        self.page_size = page_size
        self.crash_after = crash_after
        self.calls = []
        self._count = 0

    def iter_images(self, *, limit=200, cursor=None, base_models=None,
                     sort="Most Reactions", period="AllTime"):
        self.calls.append(cursor)
        page_no = 0 if cursor is None else int(cursor)
        while True:
            page_cursor = None if page_no == 0 else str(page_no)
            for pos in range(self.page_size):
                if self.crash_after is not None and self._count >= self.crash_after:
                    raise RuntimeError("simulated crash")
                self._count += 1
                yield {"id": page_no * self.page_size + pos + 1}, page_cursor
            page_no += 1


def test_crash_midstratum_persists_progress_at_page_boundaries(tmp_path):
    """finding I-3：run_fetch 原本只在 return 時存檔一次；如果 process 在跑到一半當機，
    已經寫進 raw 的列沒有對應的 state 紀錄，重跑會把整層配額（或更多）重抓一次，
    且 resume cursor 會整個重置回最初（因為連 cursor 都沒存），沒辦法接上原本的分頁。

    這裡用一個「資料永遠抓不完」的來源（呼應 main() 測試用的 FakeMainClient）模擬
    「當機前已經抓了好幾頁」，驗證：(a) 當機當下磁碟上已寫入的列在下次啟動時不會
    被要求整層重抓（resume 的 cursor 不是最初的 None，而是當機前最後一次頁界存檔
    記下的中間頁）；(b) 用 main() 會用的「quota - 已存 fetched」算出的 remaining
    重跑，總筆數收斂在 quota 附近，不是 quota 的兩倍或整層從頭再抓一次。"""
    page_size = 2
    out, state = tmp_path / "images.jsonl", tmp_path / "state.json"
    crashing = PagedInfiniteClient(page_size, crash_after=5)
    with pytest.raises(RuntimeError):
        run_fetch(crashing, max_items=8, out_path=out, state_path=state, stratum_key="sd15/year")

    # 當機當下，前面已經寫過的列都已經在磁碟上——這是既有事實，fix 不是要避免這個
    on_disk_after_crash = [r["id"] for r in read_jsonl(out)]
    assert on_disk_after_crash == [1, 2, 3, 4, 5]

    doc = json.loads(state.read_text())
    st = doc["strata"]["sd15/year"]
    # 頁界存檔生效：state 記到的 cursor 不是最初的 None（否則重跑等於從頭整層重抓），
    # 且 fetched 有被墊高到接近當機當下的實際進度（允許最後一頁的既存 drift，
    # 但不可以是 0——0 就代表完全沒有頁界存檔生效，等同修 I-3 前的行為）。
    assert st["cursor"] != crashing.calls[0]
    assert st["cursor"] is not None
    assert st["fetched"] > 0
    assert st["done"] is False

    # 重跑：比照 main() 用「quota - 已存 fetched」算這次還要抓多少，而不是整份 quota。
    quota = 8
    remaining = quota - st["fetched"]
    resumed = PagedInfiniteClient(page_size)  # 全新 instance，模擬重新啟動的 process
    n = run_fetch(resumed, max_items=remaining, out_path=out, state_path=state, stratum_key="sd15/year")

    # 沒有整層重抓：resume 用的是頁界存下的 cursor，不是最初的 None
    assert resumed.calls[0] != crashing.calls[0]
    assert resumed.calls[0] is not None

    # 這次只新增 remaining 筆（不是 quota 那麼多），且總筆數收斂在 quota 附近，
    # 而不是「當機前的量 + 完整 quota」（那才是「等於沒修」的徵兆，會是 5 + 8 = 13 這種量級）
    assert n == remaining
    total_rows = len(list(read_jsonl(out)))
    assert total_rows < 5 + quota  # 遠低於「當機進度 + 再整層重抓一次」的量級
    assert total_rows <= quota + page_size  # 只允許至多一頁的 drift，符合頁界存檔（非逐筆存檔）的設計


def test_fetch_saves_state_at_each_page_boundary_not_just_at_return(tmp_path):
    """更直接地釘住「頁界存檔」這個機制本身：用一個不會當機、但會在每次 iter_images
    被呼叫時記錄呼叫當下 state.json 內容的假 client，驗證進到第二頁時 state 已經
    被存過一次（cursor 指到第二頁），不用等到整層抓完或呼叫端拿到回傳值才看得到。"""
    calls_seen_state = []

    class ObservingClient:
        def iter_images(self, *, limit=200, cursor=None, base_models=None,
                         sort="Most Reactions", period="AllTime"):
            pages = {None: ([{"id": 1}, {"id": 2}], "c2"), "c2": ([{"id": 3}, {"id": 4}], None)}
            c = cursor
            while True:
                items, nxt = pages[c]
                for it in items:
                    # 讀取「當下」state.json 的內容（在這個 item 被 run_fetch 處理之前）
                    calls_seen_state.append(
                        json.loads(state.read_text()) if state.exists() else None
                    )
                    yield it, c
                if nxt is None:
                    return
                c = nxt

    out, state = tmp_path / "images.jsonl", tmp_path / "state.json"
    run_fetch(ObservingClient(), max_items=10, out_path=out, state_path=state, stratum_key="s1")

    # 四筆讀取順序依序對應 id1/id2（頁 None）、id3/id4（頁 c2）。邊界存檔是 run_fetch
    # 收到「新頁的第一筆」之後才做的動作，所以要等到抓 id4（頁 c2 的第二筆）之前，
    # 才看得到「id3 觸發的 None -> c2 邊界存檔」已經生效；抓 id3 之前 state 仍不存在。
    assert calls_seen_state[0] is None
    assert calls_seen_state[1] is None
    assert calls_seen_state[2] is None
    state_before_page2_second_item = calls_seen_state[3]
    assert state_before_page2_second_item is not None
    assert state_before_page2_second_item["strata"]["s1"]["fetched"] == 2
    assert state_before_page2_second_item["strata"]["s1"]["cursor"] == "c2"


# --- I-4: state.json 非原子寫入 + 缺欄位就崩潰 -------------------------------


def test_save_state_writes_atomically_so_a_failed_replace_does_not_corrupt_target(tmp_path, monkeypatch):
    """finding I-4(a)：_save_state 原本直接 write_text（truncate-then-write），
    寫到一半被中斷會留下截斷或空的 state.json。改成寫暫存檔後 os.replace 原子換入；
    這裡模擬「暫存檔寫完了，但 rename 那一步失敗」，驗證目標檔案完全沒被動過。"""
    path = tmp_path / "state.json"
    original_doc = {"version": 2, "strata": {"s1": {"cursor": None, "fetched": 1, "done": False}}}
    path.write_text(json.dumps(original_doc), encoding="utf-8")
    original_text = path.read_text(encoding="utf-8")

    def boom(*_a, **_kw):
        raise OSError("simulated crash during rename")

    monkeypatch.setattr(fetch_civitai.os, "replace", boom)

    with pytest.raises(OSError):
        _save_state(path, {"version": 2, "strata": {"s1": {"cursor": "x", "fetched": 2, "done": False}}})

    assert path.read_text(encoding="utf-8") == original_text


def test_load_state_raises_clear_error_not_bare_json_decode_error_on_empty_file(tmp_path):
    """finding I-4(b)：空檔（例如上次寫入被中斷留下的截斷檔）原本會讓
    json.loads 丟 JSONDecodeError 的裸 traceback。改成丟出有指引的錯誤，
    且不能靜默把它當成空 state 重置掉（那等於幫使用者做了「刪 state.json」這個
    finding 裡明講會導致整層重抓的危險操作）。"""
    path = tmp_path / "state.json"
    path.write_text("", encoding="utf-8")
    with pytest.raises(StateFileError):
        _load_state(path)


def test_load_state_raises_clear_error_not_bare_json_decode_error_on_truncated_json(tmp_path):
    path = tmp_path / "state.json"
    path.write_text('{"version": 2, "strata": {"s1": {"cursor": null,', encoding="utf-8")
    with pytest.raises(StateFileError):
        _load_state(path)


def test_run_fetch_tolerates_v1_state_missing_done_field(tmp_path):
    """finding I-4(b)：v1 狀態檔（沒有 version 欄位，整個物件就是 baseline 的狀態）
    若缺 'done' 欄位，原本 st["done"] 會丟 KeyError。改成 st.get("done", False)。"""
    out, state = tmp_path / "images.jsonl", tmp_path / "state.json"
    state.write_text(json.dumps({"cursor": None, "fetched": 5}), encoding="utf-8")  # 沒有 done
    client = FakeClient({None: ([{"id": 1}], None)})
    n = run_fetch(client, max_items=10, out_path=out, state_path=state, stratum_key="baseline")
    assert n == 1
    doc = json.loads(state.read_text())
    assert doc["strata"]["baseline"]["fetched"] == 6
    assert doc["strata"]["baseline"]["done"] is True


def test_run_fetch_tolerates_stratum_entry_missing_cursor_and_fetched(tmp_path):
    """finding I-4(b)：v2 狀態檔裡某層若缺 'cursor'／'fetched'（例如手動編輯過、
    或未來版本欄位有增減），原本 st["cursor"]／st["fetched"] 會丟 KeyError。"""
    out, state = tmp_path / "images.jsonl", tmp_path / "state.json"
    state.write_text(json.dumps({"version": 2, "strata": {"s1": {"done": False}}}), encoding="utf-8")
    client = FakeClient({None: ([{"id": 1}], None)})
    n = run_fetch(client, max_items=10, out_path=out, state_path=state, stratum_key="s1")
    assert n == 1
    doc = json.loads(state.read_text())
    assert doc["strata"]["s1"]["fetched"] == 1
    assert doc["strata"]["s1"]["cursor"] is None
    assert doc["strata"]["s1"]["done"] is True


def test_migrate_to_v2_rejects_unknown_version_with_actionable_error(tmp_path):
    """finding I-4(c)：version: 3（或其他非 1/2 的值）如果被當成 v1 原地包成
    baseline 狀態，會把一份格式本來就看不懂的資料當合法資料用，實質上是資料毀損。
    必須明確拒絕，訊息裡要點名那個意料之外的版本號。"""
    with pytest.raises(StateFileError) as exc_info:
        _migrate_to_v2({"version": 3, "strata": {}})
    assert "3" in str(exc_info.value)


def test_load_state_rejects_unknown_version_file(tmp_path):
    path = tmp_path / "state.json"
    path.write_text(json.dumps({"version": 3, "strata": {}}), encoding="utf-8")
    with pytest.raises(StateFileError):
        _load_state(path)
