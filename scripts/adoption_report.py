"""量測整套組合推薦的採用率（設計 2026-09-25-set-recommendations-design.md §8）。
讀 audit_logs 的 Turn_Completed，輸出 Markdown 到 stdout。

    python adoption_report.py [--since 2026-09-25]

採用率：有推薦的追問輪／定稿輪裡，後來被採用的比例（兩種分開算）。採用輪往回對：同 session 在它之前
最近的一張追問卡或定稿卡，就是它採用的那張——前端只讓人從最新那張卡採用，中間的討論輪不換卡。
同一張卡被採用兩次只算一次。
有錨／無錨列採用率：推薦卡上一個維度是一列，依該列的 anchored 分開算。分母是出現過的列數，分子是被採用的列
（採用對到的那張卡上、維度相同的那列；同一列採用兩次只算一次，比率才不會超過 100%）。
擴充率／取代率：採用時補上的（原本 missing）與換掉的（原本 covered／waived）facet 數。
採用率與「有錨」只算對得到推薦輪的採用；--since 可能把 session 從中切開，對不到的另列一行，說明兩邊的落差。

定稿卡推薦組法（2026-09-30）：另讀 `Recommendations_Next`，分理由（含你講的／接近你講的／最接近你描述的／換個搭法）
列採用率與取代率、換一批的比例、依原名次分桶的出現與採用。舊資料沒有每套的理由，照列層級的 anchored／similar
推回，不進名次分桶。原名次分桶只算相關位（anchored／similar／query）；探索位的 rank 是跟相關位的差異排名，不是
原名次，不列入分桶，否則會把兩種不同意義的名次混在一起算。

檢索時機（2026-10-06 設計 2026-10-06-retrieval-timing-design.md §5.3）：動手輪（不含採用）、「隨便」確認輪、帶參考方向的
Discuss 輪的檢索率；選項帶 presetId 的比例；每次定稿的借來／碰巧對上／llm／base／adopted；兩種輪有無檢索的延遲中位數。
只算帶 kind 欄位的新資料。重播腳本跑出來的 session 用 --sessions 篩：

    python adoption_report.py --sessions id1,id2,id3
"""

from __future__ import annotations

import argparse
import json
import statistics
import sys
from collections import Counter
from dataclasses import dataclass
from datetime import date
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

SQL = """
SELECT session_id, turn_index, payload, event_type, latency_ms
FROM audit_logs
WHERE event_type IN ('Turn_Completed', 'Recommendations_Next') AND session_id IS NOT NULL AND payload IS NOT NULL
  AND (%(since)s::date IS NULL OR created_at >= %(since)s::date)
  AND (%(sessions)s::text[] IS NULL OR session_id = ANY(%(sessions)s::text[]))
ORDER BY session_id, turn_index, id
"""

ASK, FINAL = "AskOutcome", "FinalizedOutcome"  # 只有這兩種卡會帶推薦（AgenticOrchestrator.TryRecommendAsync）


@dataclass
class Turn:
    session_id: str
    turn_index: int
    payload: dict
    event_type: str = "Turn_Completed"
    latency_ms: int | None = None


def fetch_turns(conn, since: date | None, sessions: list[str] | None = None) -> list[Turn]:
    rows = conn.execute(SQL, {"since": since, "sessions": sessions}).fetchall()
    return [Turn(r[0], r[1], r[2] if isinstance(r[2], dict) else json.loads(r[2]), r[3], r[4]) for r in rows]


def parse_sessions(text: str | None) -> list[str] | None:
    """--sessions 的值：逗號分隔，去空白、去空項；什麼都沒有回 None（不篩）。"""
    ids = [s.strip() for s in (text or "").split(",") if s.strip()]
    return ids or None


def _pct(n: int, d: int) -> str:
    # 分母 0 時印 0.0% 會被讀成「推了沒人用」，其實是根本沒推
    return f"{n}/{d}（{n / d * 100:.1f}%）" if d else f"{n}/{d}（—）"


REASONS = [("anchored", "含你講的"), ("similar", "接近你講的"), ("query", "最接近你描述的"), ("explore", "換個搭法")]
BUCKETS = [("0", 0, 0), ("1–4", 1, 4), ("5–9", 5, 9), ("10+", 10, None)]


def _bucket(rank) -> str | None:
    if rank is None:
        return None
    return next(name for name, lo, hi in BUCKETS if rank >= lo and (hi is None or rank <= hi))


def _row_sets(d: dict) -> list[dict]:
    """一排的每一套：新資料照 sets；舊資料照列層級的 anchored／similar 推回理由，
    批次當 1、名次缺值（推薦組法設計 §7）。"""
    if d.get("sets"):
        return [dict(s, batch=d.get("batch") or 1) for s in d["sets"]]
    reason = "anchored" if d.get("anchored") else "similar" if d.get("similar") else "query"
    return [{"presetId": p, "reason": reason, "rank": None, "prob": None, "batch": 1} for p in d.get("presetIds", [])]


def _match(sets: list[dict], adoption: dict) -> dict | None:
    """採用對回哪一套：有 batch 就找那一批；沒有就取最後出現的那一套（設計 §7）。"""
    pid, batch = adoption.get("presetId"), adoption.get("batch")
    hits = [s for s in sets if s["presetId"] == pid and (batch is None or s.get("batch") == batch)]
    return hits[-1] if hits else None


def build_slate_section(completed: list[Turn], nexts: list[Turn]) -> list[str]:
    """定稿卡推薦組法（設計 2026-09-30-recommendation-slate-design.md §7）：分理由的採用率與取代率、換一批、名次與採用。
    採用對回卡片的規則跟上面的採用率相同：同 session 在它之前最近的一張追問卡或定稿卡；這一節只算定稿卡。"""
    cards: dict[tuple[str, int], dict[str, list[dict]]] = {}
    for t in completed:
        rec = t.payload.get("recommendations")
        if t.payload.get("outcome") == FINAL and rec:
            cards[(t.session_id, t.turn_index)] = {d["dimension"]: _row_sets(d) for d in rec.get("dimensions", [])}
    if not cards:
        return []
    for n in nexts:
        rows = cards.get((n.session_id, n.turn_index))
        if rows is not None:
            batch = n.payload.get("batch")
            rows.setdefault(n.payload.get("dimension"), []).extend(
                dict(s, batch=batch) for s in n.payload.get("sets", []))

    shown, bucket_shown = Counter(), Counter()
    for rows in cards.values():
        for sets in rows.values():
            for s in sets:
                shown[s["reason"]] += 1
                # 探索位的 rank 是跟相關位的差異排名，不是原名次；混進原名次分桶會失真，只算相關位（見下方表格說明）
                if s["reason"] != "explore" and (b := _bucket(s.get("rank"))) is not None:
                    bucket_shown[b] += 1

    adopted, adoptions, replaced = Counter(), Counter(), Counter()
    replaced_facets, bucket_adopted = Counter(), Counter()
    credited: set = set()
    linked = later = 0
    session, card = None, None
    for t in sorted(completed, key=lambda t: (t.session_id, t.turn_index)):
        if t.session_id != session:
            session, card = t.session_id, None
        a = t.payload.get("adoption")
        if a and card in cards and (s := _match(cards[card].get(a.get("dimension"), []), a)) is not None:
            r = s["reason"]
            linked += 1
            adoptions[r] += 1
            replaced[r] += bool(a.get("replaced"))
            replaced_facets[r] += len(a.get("replaced", []))
            later += (s.get("batch") or 1) >= 2
            key = (card, a.get("dimension"), s["presetId"], s.get("batch"))
            if key not in credited:  # 同一套採用兩次只算一次，採用率才不會超過 100%
                credited.add(key)
                adopted[r] += 1
                if r != "explore" and (b := _bucket(s.get("rank"))) is not None:
                    bucket_adopted[b] += 1
        if t.payload.get("outcome") in (ASK, FINAL):
            card = (t.session_id, t.turn_index)

    rows = [sets for r in cards.values() for sets in r.values()]
    batches = [max((s.get("batch") or 1) for s in sets) if sets else 1 for sets in rows]
    refreshed = sum(b >= 2 for b in batches)
    presses = sum(b - 1 for b in batches) / len(batches) if batches else 0

    lines = ["", "## 定稿卡推薦組法", "",
             "| 理由 | 出現 | 採用率 | 取代率 | 平均換掉 facet |", "| :--- | ---: | ---: | ---: | ---: |"]
    for reason, label in REASONS:
        avg = f"{replaced_facets[reason] / adoptions[reason]:.1f}" if adoptions[reason] else "—"
        adopt_rate, replace_rate = _pct(adopted[reason], shown[reason]), _pct(replaced[reason], adoptions[reason])
        lines.append(f"| {label} | {shown[reason]} | {adopt_rate} | {replace_rate} | {avg} |")
    lines += ["", f"- 定稿排按過換一批：{_pct(refreshed, len(batches))}；平均每排按 {presses:.1f} 次",
              f"- 採用來自第 2 批以後：{_pct(later, linked)}", "",
              "原名次分桶只算相關位；探索位的 rank 是差異排名，不列入。", "",
              "| 原名次 | 出現 | 採用 |", "| :--- | ---: | ---: |"]
    lines += [f"| {name} | {bucket_shown[name]} | {bucket_adopted[name]} |" for name, _, _ in BUCKETS]
    return lines


def build_retrieval_section(completed: list[Turn]) -> list[str]:
    """檢索時機（設計 §5.3）。只算帶 kind 的新資料；舊資料只列筆數。採用輪（kind=adopt）不算動手輪，但算進定稿平均。"""
    lines = ["", "## 檢索時機", ""]
    new = [t for t in completed if "kind" in t.payload and t.payload.get("retrieval") != "off"]
    if not new:
        return lines + ["尚無檢索時機資料（2026-10-06 之後的紀錄才有）。"]

    def searched(t: Turn) -> bool:
        return (t.payload.get("searches") or 0) > 0

    def rate(ts: list[Turn]) -> str:
        return _pct(sum(searched(t) for t in ts), len(ts))

    act = [t for t in new if t.payload["kind"] == "act"]

    # 第二輪（2026-10-06）：動手輪沒查、但同一個 session 的前一筆是有查的確認輪，內容沿用那次檢索，也算有查
    prev: dict[tuple[str, int], Turn] = {}
    last: dict[str, Turn] = {}
    for t in sorted(new, key=lambda x: (x.session_id, x.turn_index)):
        if t.session_id in last:
            prev[(t.session_id, t.turn_index)] = last[t.session_id]
        last[t.session_id] = t

    def covered(t: Turn) -> bool:
        p = prev.get((t.session_id, t.turn_index))
        return searched(t) or (p is not None and p.payload["kind"] == "propose" and searched(p))

    auto = [t for t in new if t.payload["kind"] == "propose" and t.payload.get("autoComplete")]
    discuss = [t for t in new if t.payload.get("outcome") == "MessageOutcome"
               and (t.payload.get("options") or {}).get("total", 0) > 0]
    opts = [t.payload["options"] for t in new if isinstance(t.payload.get("options"), dict)]
    with_preset = sum(o.get("withPreset", 0) for o in opts)
    lines.append(f"- 動手輪檢索率（不含採用）：{rate(act)}")
    lines.append(f"- 動手輪檢索率（含沿用前一個確認輪的檢索）：{_pct(sum(covered(t) for t in act), len(act))}")
    lines.append(f"- 「隨便」確認輪檢索率：{rate(auto)}")
    lines.append(f"- 帶參考方向的 Discuss 輪檢索率：{rate(discuss)}")
    lines.append(f"- 選項帶 presetId：{_pct(with_preset, sum(o.get('total', 0) for o in opts))}")

    finals = [t.payload for t in new
              if isinstance(t.payload.get("ragSplit"), dict) and isinstance(t.payload.get("tagOrigins"), dict)]
    if finals:
        n = len(finals)
        borrowed = sum(len(p["ragSplit"].get("borrowed", [])) for p in finals)
        echo = sum(len(p["ragSplit"].get("echo", [])) for p in finals)

        def avg(key: str) -> float:
            return sum(p["tagOrigins"].get(key, 0) for p in finals) / n

        nonbase = sum(sum(p["tagOrigins"].values()) - p["tagOrigins"].get("base", 0) for p in finals)
        lines.append(f"- 每次定稿平均（{n} 次）：借來 {borrowed / n:.1f}、碰巧對上 {echo / n:.1f}、"
                     f"llm {avg('llm'):.1f}、base {avg('base'):.1f}、adopted {avg('adopted'):.1f}")
        lines.append(f"- rag（借來＋碰巧對上）佔非基礎詞：{_pct(borrowed + echo, nonbase)}")
    else:
        lines.append("- 尚無定稿")

    def med(kind: str, with_search: bool) -> str:
        xs = [t.latency_ms for t in new
              if t.payload["kind"] == kind and searched(t) == with_search and t.latency_ms is not None]
        return f"{statistics.median(xs):.0f} ms（{len(xs)} 輪）" if xs else "—（0 輪）"

    lines.append(f"- 延遲中位數：確認輪 有檢索 {med('propose', True)}／沒檢索 {med('propose', False)}；"
                 f"動手輪 有檢索 {med('act', True)}／沒檢索 {med('act', False)}")
    old = sum(1 for t in completed if "kind" not in t.payload)
    if old:
        lines.append(f"- 沒有 kind 欄位的舊資料：{old} 輪，不計入上面各項")
    off = sum(1 for t in completed if t.payload.get("retrieval") == "off")
    if off:
        lines.append(f"- 知識庫關閉（retrieval: off）的輪：{off} 輪，不計入上面各項")
    return lines


def build_report(turns: list[Turn]) -> str:
    completed = [t for t in turns if t.event_type == "Turn_Completed"]
    nexts = [t for t in turns if t.event_type == "Recommendations_Next"]
    if not completed:
        return "沒有 Turn_Completed 紀錄。"
    slate = build_slate_section(completed, nexts)
    retrieval = build_retrieval_section(completed)
    ordered = sorted(completed, key=lambda t: (t.session_id, t.turn_index))
    adoptions = [t.payload["adoption"] for t in ordered if t.payload.get("adoption")]

    rec_turns = {ASK: 0, FINAL: 0}
    adopted = {ASK: 0, FINAL: 0}
    credited: set[tuple[str, int]] = set()
    shown_rows = {True: 0, False: 0}  # 依 anchored 分：推薦卡上出現過的維度列
    adopted_rows = {True: 0, False: 0}
    credited_rows: set[tuple[str, int, str]] = set()
    linked = unlinked = anchored_hit = 0
    session: str | None = None
    card: Turn | None = None  # 這個 session 目前最新的追問卡／定稿卡
    for t in ordered:
        if t.session_id != session:
            session, card = t.session_id, None
        outcome = t.payload.get("outcome")
        if outcome in rec_turns and t.payload.get("recommendations"):
            rec_turns[outcome] += 1
            for d in t.payload["recommendations"].get("dimensions", []):
                shown_rows[bool(d.get("anchored"))] += 1
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
                row = (card.session_id, card.turn_index, adoption.get("dimension"))
                if dim and row not in credited_rows:
                    credited_rows.add(row)
                    adopted_rows[bool(dim.get("anchored"))] += 1
        if outcome in rec_turns:
            card = t

    origins = [t.payload["tagOrigins"] for t in completed if isinstance(t.payload.get("tagOrigins"), dict)]
    adopted_tags = sum(o.get("adopted", 0) for o in origins)
    all_tags = sum(sum(o.values()) for o in origins)

    lines = ["# 整套組合推薦：採用率", ""]
    lines.append(f"- 有推薦的定稿輪：{rec_turns[FINAL]}；定稿輪採用率：{_pct(adopted[FINAL], rec_turns[FINAL])}")
    lines.append(f"- 有推薦的追問輪：{rec_turns[ASK]}；追問輪採用率：{_pct(adopted[ASK], rec_turns[ASK])}")
    lines.append(f"- 有錨列採用率：{_pct(adopted_rows[True], shown_rows[True])}")
    lines.append(f"- 無錨列採用率：{_pct(adopted_rows[False], shown_rows[False])}")
    lines.append(f"- adopted tag 佔定稿 tag：{_pct(adopted_tags, all_tags)}")
    if not adoptions:
        lines += ["", "尚無採用紀錄。"]
        return "\n".join(lines + slate + retrieval)
    filled = sum(len(a.get("filled", [])) for a in adoptions) / len(adoptions)
    replaced = sum(len(a.get("replaced", [])) for a in adoptions) / len(adoptions)
    lines.append(f"- 採用 {len(adoptions)} 次；平均補上 {filled:.1f} 個 facet、換掉 {replaced:.1f} 個 facet")
    lines.append(f"- 未對到推薦輪的採用：{unlinked}")
    lines.append(f"- 採用時該維度有錨：{_pct(anchored_hit, linked)}")
    lines += ["", "## 各維度採用次數", ""]
    for dim, n in sorted(Counter(a.get("dimension", "?") for a in adoptions).items()):
        lines.append(f"- {dim}：{n}")
    return "\n".join(lines + slate + retrieval)


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--since", type=date.fromisoformat, default=None,
                    help="只算這一天（含）之後的紀錄，YYYY-MM-DD；日界以資料庫連線的時區為準")
    ap.add_argument("--sessions", default=None,
                    help="只算這些 session（逗號分隔）；manual-tests/replay.py 最後一行會印")
    args = ap.parse_args(argv)
    from pipeline.db import connect

    with connect() as conn:
        turns = fetch_turns(conn, args.since, parse_sessions(args.sessions))
    print(build_report(turns))
    return 0


if __name__ == "__main__":
    sys.exit(main())
