"""檢索時機驗收的重播腳本（docs/superpowers/specs/2026-10-06-retrieval-timing-design.md §6.2）：
照劇本送話、自動按確認卡（有解讀選項就選第一個），記下每一步的確認輪與動手輪有沒有叫 SearchPresets。

只用標準函式庫，沿用 chat.py 的 HTTP／SSE 函式。API 要先在跑（docker compose 的 api 容器，預設 http://localhost:5000）。
用法：python manual-tests/replay.py --scenario Q1 --runs 3
      python manual-tests/replay.py --scenario all --runs 3
劇本在 replay_scenarios.json：say 是直接送的話；pick 照前端的格式用最近一張追問卡每個維度的第一個選項回答，可附 extra。
最後一行印出這次跑出來的 session id（逗號分隔），交給 scripts/adoption_report.py --sessions 算報表。
"""

from __future__ import annotations

import argparse
import json
import sys
import time
import urllib.error
from dataclasses import dataclass, field
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from chat import http_json, sse_events  # noqa: E402

SCENARIOS = Path(__file__).resolve().parent / "replay_scenarios.json"


@dataclass
class TurnResult:
    kind: str                     # propose | act
    outcome: str | None = None    # final 的 kind（confirm／ask／message／finalized）；被攔、出錯是 blocked／error／http 4xx
    searched: bool = False
    ms: int = 0
    turn_index: int | None = None
    choices: list[str] = field(default_factory=list)
    asks: list[dict] = field(default_factory=list)
    positive: str | None = None


def compose_pick(asks: list[dict], dim_labels: dict[str, str], extra: str | None) -> str | None:
    """照前端 composer 的格式回答追問卡：每個維度一行「[維度] 第一個選項」，再接 extra。都沒有就回 None（這步跳過）。"""
    lines = [f"[{dim_labels.get(a['dimension'], a['dimension'])}] {a['options'][0]['label']}"
             for a in asks if a.get("options")]
    if extra:
        lines.append(extra)
    return "\n".join(lines) or None


def judge(expect: str, propose: TurnResult | None, act: TurnResult | None) -> str:
    """expect：propose／act／both／none。要求的那一輪沒發生記 NO-TURN；發生了但沒檢索記 MISS。
    要求動手輪檢索、動手輪沒查但同一步的確認輪有查時記 CARRY：內容沿用確認輪的檢索，流程說明允許（算有查）。"""
    need = {"propose": ["propose"], "act": ["act"], "both": ["propose", "act"], "none": []}[expect]
    got = {"propose": propose, "act": act}
    carried = False
    for k in need:
        if got[k] is None:
            return "NO-TURN"
        if not got[k].searched:
            if k == "act" and propose is not None and propose.searched:
                carried = True
                continue
            return "MISS"
    return "CARRY" if carried else "OK"


def run_turn(base: str, sid: str, body: dict, kind: str) -> TurnResult:
    r = TurnResult(kind)
    start = time.monotonic()
    try:
        for name, ev in sse_events(f"{base}/api/sessions/{sid}/messages", body):
            if name == "session":
                r.turn_index = ev["turnIndex"]
            elif name == "tool_call" and ev.get("name") == "SearchPresets":
                r.searched = True
            elif name == "final":
                r.outcome = ev["kind"]
                r.choices = ev.get("choices") or []
                r.asks = ev.get("asks") or []
                r.positive = ev.get("positive")
            elif name in ("blocked", "error"):
                r.outcome = name
    except urllib.error.HTTPError as e:      # 要在 URLError 之前，它是 URLError 的子類
        r.outcome = f"http {e.code}"
    except (urllib.error.URLError, TimeoutError, ConnectionError) as e:
        r.outcome = f"net {type(e).__name__}"   # 逾時、斷線只記在這一輪，不讓整批重播中斷
    r.ms = int((time.monotonic() - start) * 1000)
    return r


def run_scenario(base: str, steps: list[dict], dim_labels: dict[str, str]) -> tuple[str, list[dict], str | None]:
    _, body = http_json("POST", f"{base}/api/sessions")
    sid = body["sessionId"]
    asks: list[dict] = []
    rows: list[dict] = []
    last_positive: str | None = None
    for i, step in enumerate(steps, 1):
        text = step.get("say") or (compose_pick(asks, dim_labels, step.get("extra")) if step.get("pick") else None)
        if text is None:
            rows.append({"step": i, "text": "（跳過：沒有追問卡）", "propose": None, "act": None, "verdict": "SKIP"})
            continue
        propose = run_turn(base, sid, {"text": text}, "propose")
        act = None
        if propose.outcome == "confirm":
            choice = 0 if propose.choices else None
            act = run_turn(base, sid, {"confirm": {"turnIndex": propose.turn_index, "choice": choice}}, "act")
            # 追問卡只在動手輪有結果時換：ask 換成新卡、finalized 清掉；被攔／出錯／http 錯誤時伺服器已還原這一輪，
            # 舊卡仍是有效的。確認輪結束在 Discuss 的不會進這個分支（outcome 不是 confirm），卡也原封不動。
            if act.outcome == "ask":
                asks = act.asks
            elif act.outcome == "finalized":
                asks = []
                last_positive = act.positive
        rows.append({"step": i, "text": text.replace("\n", " / "), "propose": propose, "act": act,
                     "verdict": judge(step.get("expect", "none"), propose, act)})
    return sid, rows, last_positive


def fmt(r: TurnResult | None) -> str:
    if r is None:
        return "—"
    return f"{r.outcome or '?'}{'（查）' if r.searched else ''} {r.ms / 1000:.1f}s"


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--base", default="http://localhost:5000")
    ap.add_argument("--scenario", default="all", help="Q1／Q2／Q3 或 all")
    ap.add_argument("--runs", type=int, default=1)
    args = ap.parse_args(argv)
    base = args.base.rstrip("/")
    scenarios = json.loads(SCENARIOS.read_text(encoding="utf-8"))
    names = list(scenarios) if args.scenario == "all" else [args.scenario]
    _, cfg = http_json("GET", f"{base}/api/config/facets")
    dim_labels = {d["key"]: d["label"] for d in cfg["dimensions"]}
    sessions: list[str] = []
    try:
        for run in range(1, args.runs + 1):
            for name in names:
                sid, rows, positive = run_scenario(base, scenarios[name], dim_labels)
                sessions.append(sid)
                print(f"\n## {name} 第 {run} 次（session {sid}）\n")
                print("| 步 | 送出 | 確認輪 | 動手輪 | 檢索判定 |")
                print("| :--- | :--- | :--- | :--- | :--- |")
                for row in rows:
                    print(f"| {row['step']} | {row['text'][:40]} | {fmt(row['propose'])} | {fmt(row['act'])} "
                          f"| {row['verdict']} |")
                print(f"\n最後定稿 positive：{positive or '（沒有定稿）'}")
                sys.stdout.flush()
    finally:   # 中途出事也要印，報表要用已跑完的 session id
        print("\nsessions: " + ",".join(sessions))
    return 0


if __name__ == "__main__":
    sys.exit(main())
