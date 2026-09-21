"""demo 的終端機輸出。全是純函式，不碰 DB 與網路。
設計見 docs/superpowers/specs/2026-09-22-dimension-scoped-retrieval-design.md §7。"""

from __future__ import annotations

import os
import sys
import unicodedata
from dataclasses import dataclass

from pipeline.facets import FacetCatalog
from pipeline.retrieval import DIMENSIONS  # noqa: E402

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
