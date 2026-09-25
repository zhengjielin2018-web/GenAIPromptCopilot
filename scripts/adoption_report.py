"""量測整套組合推薦的採用率（設計 2026-09-25-set-recommendations-design.md §8）。
讀 audit_logs 的 Turn_Completed，輸出 Markdown 到 stdout。

    python adoption_report.py [--since 2026-09-25]

採用率：有推薦的那一輪，同 session 的下一輪是採用的比例（定稿輪與追問輪分開算）。
擴充率／取代率：採用時補上的（原本 missing）與換掉的（原本 covered／waived）facet 數。
"""

from __future__ import annotations

import argparse
import json
import sys
from collections import Counter
from dataclasses import dataclass
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

SQL = """
SELECT session_id, turn_index, payload
FROM audit_logs
WHERE event_type = 'Turn_Completed' AND session_id IS NOT NULL AND payload IS NOT NULL
  AND (%(since)s::date IS NULL OR created_at >= %(since)s::date)
ORDER BY session_id, turn_index
"""


@dataclass
class Turn:
    session_id: str
    turn_index: int
    payload: dict


def fetch_turns(conn, since: str | None) -> list[Turn]:
    rows = conn.execute(SQL, {"since": since}).fetchall()
    return [Turn(r[0], r[1], r[2] if isinstance(r[2], dict) else json.loads(r[2])) for r in rows]


def _pct(n: int, d: int) -> str:
    return f"{n}/{d}（{(n / d * 100) if d else 0:.1f}%）"


def build_report(turns: list[Turn]) -> str:
    if not turns:
        return "沒有 Turn_Completed 紀錄。"
    by_key = {(t.session_id, t.turn_index): t for t in turns}
    adoptions = [t.payload["adoption"] for t in turns if "adoption" in t.payload]

    # 有推薦的輪：下一輪是不是採用
    rec_final = rec_ask = adopted_final = adopted_ask = 0
    anchored_hit = anchored_total = 0
    for t in turns:
        rec = t.payload.get("recommendations")
        if not rec:
            continue
        is_final = t.payload.get("outcome") == "FinalizedOutcome"
        nxt = by_key.get((t.session_id, t.turn_index + 1))
        adoption = nxt.payload.get("adoption") if nxt else None
        if is_final:
            rec_final += 1
            adopted_final += bool(adoption)
        else:
            rec_ask += 1
            adopted_ask += bool(adoption)
        if adoption:
            anchored_total += 1
            dim = next((d for d in rec.get("dimensions", []) if d.get("dimension") == adoption.get("dimension")), None)
            anchored_hit += bool(dim and dim.get("anchored"))

    origins = [t.payload["tagOrigins"] for t in turns if isinstance(t.payload.get("tagOrigins"), dict)]
    adopted_tags = sum(o.get("adopted", 0) for o in origins)
    all_tags = sum(sum(o.values()) for o in origins)

    lines = ["# 整套組合推薦：採用率", ""]
    lines.append(f"- 有推薦的定稿輪：{rec_final}；定稿輪採用率：{_pct(adopted_final, rec_final)}")
    lines.append(f"- 有推薦的追問輪：{rec_ask}；追問輪採用率：{_pct(adopted_ask, rec_ask)}")
    lines.append(f"- adopted tag 佔定稿 tag：{_pct(adopted_tags, all_tags)}")
    if not adoptions:
        lines += ["", "尚無採用紀錄。"]
        return "\n".join(lines)
    filled = sum(len(a.get("filled", [])) for a in adoptions) / len(adoptions)
    replaced = sum(len(a.get("replaced", [])) for a in adoptions) / len(adoptions)
    lines.append(f"- 採用 {len(adoptions)} 次；平均補上 {filled:.1f} 個 facet、換掉 {replaced:.1f} 個 facet")
    lines.append(f"- 採用時該維度有錨：{_pct(anchored_hit, anchored_total)}")
    lines += ["", "## 各維度採用次數", ""]
    for dim, n in sorted(Counter(a.get("dimension", "?") for a in adoptions).items()):
        lines.append(f"- {dim}：{n}")
    return "\n".join(lines)


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--since", default=None, help="只算這一天（含）之後的紀錄，YYYY-MM-DD")
    args = ap.parse_args(argv)
    from pipeline.db import connect

    with connect() as conn:
        turns = fetch_turns(conn, args.since)
    print(build_report(turns))
    return 0


if __name__ == "__main__":
    sys.exit(main())
