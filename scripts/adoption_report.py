"""量測整套組合推薦的採用率（設計 2026-09-25-set-recommendations-design.md §8）。
讀 audit_logs 的 Turn_Completed，輸出 Markdown 到 stdout。

    python adoption_report.py [--since 2026-09-25]

採用率：有推薦的追問輪／定稿輪裡，後來被採用的比例（兩種分開算）。採用輪往回對：同 session 在它之前
最近的一張追問卡或定稿卡，就是它採用的那張——前端只讓人從最新那張卡採用，中間的討論輪不換卡。
同一張卡被採用兩次只算一次。
擴充率／取代率：採用時補上的（原本 missing）與換掉的（原本 covered／waived）facet 數。
採用率與「有錨」只算對得到推薦輪的採用；--since 可能把 session 從中切開，對不到的另列一行，說明兩邊的落差。
"""

from __future__ import annotations

import argparse
import json
import sys
from collections import Counter
from dataclasses import dataclass
from datetime import date
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

SQL = """
SELECT session_id, turn_index, payload
FROM audit_logs
WHERE event_type = 'Turn_Completed' AND session_id IS NOT NULL AND payload IS NOT NULL
  AND (%(since)s::date IS NULL OR created_at >= %(since)s::date)
ORDER BY session_id, turn_index
"""

ASK, FINAL = "AskOutcome", "FinalizedOutcome"  # 只有這兩種卡會帶推薦（AgenticOrchestrator.TryRecommendAsync）


@dataclass
class Turn:
    session_id: str
    turn_index: int
    payload: dict


def fetch_turns(conn, since: date | None) -> list[Turn]:
    rows = conn.execute(SQL, {"since": since}).fetchall()
    return [Turn(r[0], r[1], r[2] if isinstance(r[2], dict) else json.loads(r[2])) for r in rows]


def _pct(n: int, d: int) -> str:
    # 分母 0 時印 0.0% 會被讀成「推了沒人用」，其實是根本沒推
    return f"{n}/{d}（{n / d * 100:.1f}%）" if d else f"{n}/{d}（—）"


def build_report(turns: list[Turn]) -> str:
    if not turns:
        return "沒有 Turn_Completed 紀錄。"
    ordered = sorted(turns, key=lambda t: (t.session_id, t.turn_index))
    adoptions = [t.payload["adoption"] for t in ordered if t.payload.get("adoption")]

    rec_turns = {ASK: 0, FINAL: 0}
    adopted = {ASK: 0, FINAL: 0}
    credited: set[tuple[str, int]] = set()
    linked = unlinked = anchored_hit = 0
    session: str | None = None
    card: Turn | None = None  # 這個 session 目前最新的追問卡／定稿卡
    for t in ordered:
        if t.session_id != session:
            session, card = t.session_id, None
        outcome = t.payload.get("outcome")
        if outcome in rec_turns and t.payload.get("recommendations"):
            rec_turns[outcome] += 1
        adoption = t.payload.get("adoption")
        if adoption:
            rec = card.payload.get("recommendations") if card else None
            if not rec:
                unlinked += 1
            else:
                linked += 1
                if (card.session_id, card.turn_index) not in credited:
                    credited.add((card.session_id, card.turn_index))
                    adopted[card.payload["outcome"]] += 1
                dims = rec.get("dimensions", [])
                dim = next((d for d in dims if d.get("dimension") == adoption.get("dimension")), None)
                anchored_hit += bool(dim and dim.get("anchored"))
        if outcome in rec_turns:
            card = t

    origins = [t.payload["tagOrigins"] for t in turns if isinstance(t.payload.get("tagOrigins"), dict)]
    adopted_tags = sum(o.get("adopted", 0) for o in origins)
    all_tags = sum(sum(o.values()) for o in origins)

    lines = ["# 整套組合推薦：採用率", ""]
    lines.append(f"- 有推薦的定稿輪：{rec_turns[FINAL]}；定稿輪採用率：{_pct(adopted[FINAL], rec_turns[FINAL])}")
    lines.append(f"- 有推薦的追問輪：{rec_turns[ASK]}；追問輪採用率：{_pct(adopted[ASK], rec_turns[ASK])}")
    lines.append(f"- adopted tag 佔定稿 tag：{_pct(adopted_tags, all_tags)}")
    if not adoptions:
        lines += ["", "尚無採用紀錄。"]
        return "\n".join(lines)
    filled = sum(len(a.get("filled", [])) for a in adoptions) / len(adoptions)
    replaced = sum(len(a.get("replaced", [])) for a in adoptions) / len(adoptions)
    lines.append(f"- 採用 {len(adoptions)} 次；平均補上 {filled:.1f} 個 facet、換掉 {replaced:.1f} 個 facet")
    lines.append(f"- 未對到推薦輪的採用：{unlinked}")
    lines.append(f"- 採用時該維度有錨：{_pct(anchored_hit, linked)}")
    lines += ["", "## 各維度採用次數", ""]
    for dim, n in sorted(Counter(a.get("dimension", "?") for a in adoptions).items()):
        lines.append(f"- {dim}：{n}")
    return "\n".join(lines)


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--since", type=date.fromisoformat, default=None,
                    help="只算這一天（含）之後的紀錄，YYYY-MM-DD；日界以資料庫連線的時區為準")
    args = ap.parse_args(argv)
    from pipeline.db import connect

    with connect() as conn:
        turns = fetch_turns(conn, args.since)
    print(build_report(turns))
    return 0


if __name__ == "__main__":
    sys.exit(main())
