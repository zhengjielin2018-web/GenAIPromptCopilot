"""demo 的終端機輸出。全是純函式，不碰 DB 與網路。
設計見 docs/superpowers/specs/2026-09-22-dimension-scoped-retrieval-design.md §7。"""

from __future__ import annotations

import os
import sys
import unicodedata
from collections import Counter
from dataclasses import dataclass, field

from pipeline.facets import FacetCatalog
from pipeline.retrieval import DIMENSIONS, Candidate, DimensionHits, DimensionQuery

# ---------- 文字對齊 ----------


def display_width(text: str) -> int:
    """終端機顯示寬度：全形／寬字元佔兩欄。用字元數對齊中文會歪掉。"""
    return sum(2 if unicodedata.east_asian_width(c) in "WF" else 1 for c in text)


def pad(text: str, width: int) -> str:
    return text + " " * max(0, width - display_width(text))


def wrap_tags(text: str, indent: str = "    ", width: int = 84) -> str:
    """逐個 tag 換行，避免把一個 tag 從中間切斷。"""
    out, line = [], indent
    for tag in [t.strip() for t in text.split(",") if t.strip()]:
        piece = tag + ", "
        if len(line) + len(piece) > width and line != indent:
            out.append(line.rstrip())
            line = indent
        line += piece
    if line.strip():
        out.append(line.rstrip().rstrip(","))
    return "\n".join(out)


# ---------- 六維度燈號 ----------


def group_by_dimension(states: dict[str, str], catalog: FacetCatalog) -> dict[str, list[tuple[str, str]]]:
    """回傳 {維度 key: [(facet 顯示名稱, 狀態), ...]}，依 catalog 順序，只保留 catalog 認得的 id。"""
    grouped: dict[str, list[tuple[str, str]]] = {k: [] for k in DIMENSIONS}
    for facet in catalog.facets.values():
        state = states.get(facet.id)
        if state is not None:
            grouped[facet.dimension].append((facet.label, state))
    return grouped


def render_dimension_row(label: str, entries: list[tuple[str, str]], width: int = 10) -> str:
    """把一個維度畫成一行：名稱、覆蓋比例、以及缺了哪些項目。"""
    applicable = [(n, s) for n, s in entries if s != "notApplicable"]
    if not applicable:
        return f"  {pad(label, width)}  ──  不適用"
    covered = [n for n, s in applicable if s == "covered"]
    missing = [n for n, s in applicable if s == "missing"]
    bar = "●" * len(covered) + "○" * len(missing)
    line = f"  {pad(label, width)}  {bar}  {len(covered)}/{len(applicable)}"
    if missing:
        line += f"   缺：{'、'.join(missing)}"
    return line


# ---------- 顏色 ----------


class Palette:
    def __init__(self, enabled: bool):
        self.on = enabled

    def _w(self, code: str, s: str) -> str:
        return f"\033[{code}m{s}\033[0m" if self.on else s

    def head(self, s: str) -> str:
        return self._w("1;36", s)

    def ok(self, s: str) -> str:
        return self._w("32", s)

    def warn(self, s: str) -> str:
        return self._w("33", s)

    def dim(self, s: str) -> str:
        return self._w("2", s)


def colors_enabled(no_color: bool) -> bool:
    if no_color or os.environ.get("NO_COLOR"):
        return False
    return sys.stdout.isatty()


# ---------- 給 render() 的資料 ----------


@dataclass
class BorrowedView:
    band: str
    dist: float
    title: str
    category: str
    tags: list[str]


@dataclass
class OptionView:
    label: str
    tags: str
    source_title: str


@dataclass
class SuggestionView:
    dimension_label: str
    missing_labels: list[str]
    options: list[OptionView]


@dataclass
class DemoView:
    profile: str
    facet_states: dict[str, str]
    positive_prompt: str
    negative_prompt: str
    borrowed: list[BorrowedView]
    rejections: list[str]
    suggestions: list[SuggestionView]
    unserved: list[tuple[str, list[str]]] = field(default_factory=list)


# ---------- 進度列 ----------


def render_queries(queries: list[DimensionQuery], catalog: FacetCatalog, profile: str) -> str:
    def fmt(qs: list[DimensionQuery]) -> str:
        return " ".join(f"{catalog.dimension_label(q.dimension, profile)}「{q.query}」" for q in qs)

    grounded = [q for q in queries if q.grounded]
    guessed = [q for q in queries if not q.grounded]
    parts = []
    if grounded:
        parts.append("子查詢：" + fmt(grounded))
    if guessed:
        parts.append("推想：" + fmt(guessed))
    return "      " + ("；".join(parts) if parts else "（沒有子查詢）")


def render_retrieval_summary(hits: list[DimensionHits], catalog: FacetCatalog, n_histories: int, profile: str) -> str:
    """每維度：池大小 → 命中數（高/中/低各幾筆）。同維度兩句子查詢合併計數。知識庫缺口在這裡直接暴露。"""
    per: dict[str, tuple[int, Counter[str]]] = {}
    for dh in hits:
        pool, cnt = per.get(dh.query.dimension, (dh.pool_size, Counter()))
        cnt.update(c.band for c in dh.hits)
        per[dh.query.dimension] = (pool, cnt)
    cells = []
    for dim in DIMENSIONS:
        if dim not in per:
            continue
        pool, cnt = per[dim]
        grades = " ".join(f"{b} {cnt[b]}" for b in ("高", "中", "低") if cnt[b])
        cells.append(f"{catalog.dimension_label(dim, profile)} 池 {pool} → {sum(cnt.values())}（{grades or '無'}）")
    first = "      " + ("  ".join(cells) if cells else "（沒有維度可檢索）")
    return f"{first}\n      相似作品 {n_histories}（{profile}）"


def render_verbose(cands: list[Candidate], catalog: FacetCatalog, p: Palette, profile: str) -> str:
    lines = [p.head("━━ 全部候選（去重後）━━")]
    for dim in DIMENSIONS:
        group = [c for c in cands if c.dimension == dim]
        if not group:
            continue
        lines.append(f"  [{catalog.dimension_label(dim, profile)}]")
        for c in group:
            usage = "可借入" if c.grounded else "僅供建議"
            coverage = ", ".join(f"{k}={v}" for k, v in c.facet_coverage.items()) or "(無)"
            lines.append(f"    [{c.band} {c.dist:.3f}] id={c.id} {c.preset['title']}  {usage}")
            lines.append(p.dim(f"        facets: {coverage}"))
            lines.append(p.dim(f"        {c.preset['prompt_snippet'][:76]}"))
    return "\n".join(lines)


# ---------- 最終畫面 ----------


def render(view: DemoView, catalog: FacetCatalog, p: Palette) -> str:
    grouped = group_by_dimension(view.facet_states, catalog)
    lines = ["", p.head("━━ 題材判定 ━━"), f"  {view.profile}", "", p.head("━━ 六維度充足度 ━━")]
    for key in DIMENSIONS:
        row = render_dimension_row(catalog.dimension_label(key, view.profile), grouped[key])
        lines.append(p.dim(row) if "不適用" in row else row)

    lines += ["", p.head("━━ 正向提示詞 ━━"), p.ok(wrap_tags(view.positive_prompt))]
    lines += ["", p.head("━━ 負向提示詞 ━━"), p.warn(wrap_tags(view.negative_prompt))]

    lines += ["", p.head("━━ 借用的知識庫片段 ━━")]
    for b in view.borrowed:
        lines.append(f"  [{b.band} {b.dist:.3f}] {b.title}（{b.category}）→ 借入 {', '.join(b.tags)}")
    for r in view.rejections:
        lines.append(p.warn(f"  ✗ {r}"))
    if not view.borrowed and not view.rejections:
        lines.append(p.dim("  （這次沒有借用檢索到的片段）"))

    lines += ["", p.head("━━ 建議 ━━")]
    for s in view.suggestions:
        lines.append(f"  {s.dimension_label}（缺：{'、'.join(s.missing_labels)}）")
        for letter, o in zip("ABCDEFG", s.options, strict=False):
            lines.append(f"    {letter}. {pad(o.label, 14)} {o.tags}   〈{o.source_title}〉")
    for dim_label, missing in view.unserved:
        lines.append(p.warn(f"  ✗ {dim_label}（缺：{'、'.join(missing)}）這次沒有可用的建議"))
    if not view.suggestions and not view.unserved:
        lines.append(p.dim("  （所有維度都已覆蓋，沒有建議）"))
    lines.append("")
    return "\n".join(lines)
