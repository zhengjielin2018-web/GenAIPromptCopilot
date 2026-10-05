"""replay.py 的純函式（不打 API）。從 repo 根目錄跑：scripts/.venv/Scripts/python.exe -m pytest manual-tests/test_replay.py -q"""

import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

import replay  # noqa: E402
from replay import TurnResult, compose_pick, judge, run_scenario, run_turn  # noqa: E402

LABELS = {"style": "風格", "pose": "人物動作"}


def test_compose_pick_takes_the_first_option_of_each_ask_and_appends_extra():
    asks = [{"dimension": "style", "options": [{"label": "寫實攝影"}, {"label": "動漫"}]},
            {"dimension": "pose", "options": [{"label": "漫步雨中"}]}]
    assert compose_pick(asks, LABELS, "衣服你幫我設計") == "[風格] 寫實攝影\n[人物動作] 漫步雨中\n衣服你幫我設計"


def test_compose_pick_without_asks_sends_only_extra_or_skips():
    assert compose_pick([], LABELS, "衣服你幫我設計") == "衣服你幫我設計"
    assert compose_pick([], LABELS, None) is None


def test_judge():
    searched, idle = TurnResult("propose", searched=True), TurnResult("act")
    assert judge("act", idle, TurnResult("act", searched=True)) == "OK"
    assert judge("act", searched, idle) == "MISS"
    assert judge("act", TurnResult("propose", outcome="message", searched=True), None) == "NO-TURN"
    assert judge("both", searched, TurnResult("act", searched=True)) == "OK"
    assert judge("both", idle, TurnResult("act", searched=True)) == "MISS"
    assert judge("propose", searched, None) == "OK"
    assert judge("none", idle, None) == "OK"


CARD = [{"dimension": "style", "options": [{"label": "寫實攝影"}]}]
PICK = [{"pick": True}]


def _scripted(monkeypatch, results):
    """http_json 回固定 session；run_turn 依序回腳本裡的結果並記下收到的 body。"""
    bodies, it = [], iter(results)
    monkeypatch.setattr(replay, "http_json", lambda *a, **k: (200, {"sessionId": "s1"}))

    def fake(base, sid, body, kind):
        bodies.append(body)
        return next(it)
    monkeypatch.setattr(replay, "run_turn", fake)
    return bodies


def _ask_step_results():
    return [TurnResult("propose", outcome="confirm", turn_index=1, choices=["a"]),
            TurnResult("act", outcome="ask", asks=CARD)]


def test_pending_card_survives_a_propose_turn_that_ends_in_message(monkeypatch):
    bodies = _scripted(monkeypatch, _ask_step_results() + [TurnResult("propose", outcome="message")]
                       + [TurnResult("propose", outcome="message")])
    run_scenario("b", [{"say": "x"}, {"say": "問問題"}] + PICK, LABELS)
    assert bodies[-1] == {"text": "[風格] 寫實攝影"}


def test_pending_card_survives_a_blocked_act_turn(monkeypatch):
    results = _ask_step_results() + [TurnResult("propose", outcome="confirm", turn_index=2),
                                     TurnResult("act", outcome="blocked"),
                                     TurnResult("propose", outcome="message")]
    bodies = _scripted(monkeypatch, results)
    run_scenario("b", [{"say": "x"}, {"say": "y"}] + PICK, LABELS)
    assert bodies[-1] == {"text": "[風格] 寫實攝影"}


def test_finalized_act_turn_clears_the_card(monkeypatch):
    results = _ask_step_results() + [TurnResult("propose", outcome="confirm", turn_index=2),
                                     TurnResult("act", outcome="finalized", positive="p")]
    bodies = _scripted(monkeypatch, results)
    _, rows, positive = run_scenario("b", [{"say": "x"}, {"say": "y"}] + PICK, LABELS)
    assert rows[-1]["verdict"] == "SKIP" and len(bodies) == 4 and positive == "p"


def test_confirm_with_choices_sends_choice_zero(monkeypatch):
    bodies = _scripted(monkeypatch, _ask_step_results())
    run_scenario("b", [{"say": "x"}], LABELS)
    assert bodies[1] == {"confirm": {"turnIndex": 1, "choice": 0}}


def test_run_turn_records_network_errors_instead_of_raising(monkeypatch):
    def boom(*a, **k):
        raise TimeoutError("timed out")
        yield  # noqa: unreachable，讓它是 generator
    monkeypatch.setattr(replay, "sse_events", boom)
    r = run_turn("b", "s1", {"text": "x"}, "propose")
    assert r.outcome.startswith("net ")
