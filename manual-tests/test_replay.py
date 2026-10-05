"""replay.py 的純函式（不打 API）。從 repo 根目錄跑：scripts/.venv/Scripts/python.exe -m pytest manual-tests/test_replay.py -q"""

import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

from replay import TurnResult, compose_pick, judge  # noqa: E402

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
