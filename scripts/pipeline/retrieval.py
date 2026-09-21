"""分維度檢索：每個維度用自己的子查詢向量、在自己的 facet 候選池裡撈，再跨維度去重與分級。
純函式（本檔上半）與吃 conn 的函式（下半）分開，前者可無 DB 測試。
設計見 docs/superpowers/specs/2026-09-22-dimension-scoped-retrieval-design.md §5、§6.1。"""

from __future__ import annotations

from dataclasses import dataclass
from typing import Literal

from pipeline.facets import FacetCatalog

DIMENSIONS: tuple[str, ...] = ("style", "scene", "camera", "appearance", "pose", "clothing")
Band = Literal["高", "中", "低"]
FacetState = Literal["covered", "missing", "notApplicable"]

K_COVERED = 5  # 使用者有描述的維度：1 句子查詢
K_MISSING = 3  # 使用者未描述的維度：最多 2 句對比子查詢，各撈這麼多
HIGH_MAX = 0.25
MID_MAX = 0.30
_MAX_QUERIES = {True: 1, False: 2}  # grounded -> 每維度子查詢上限


def band(dist: float) -> Band:
    """相似度分級。門檻是跨四種 profile 實測的：可用命中落在 0.18–0.28，全庫 p1≈0.32。"""
    if dist < HIGH_MAX:
        return "高"
    if dist < MID_MAX:
        return "中"
    return "低"


def dimension_facets(catalog: FacetCatalog, profile: str, dimension: str) -> list[str]:
    """該 profile 下這個維度適用的 facet id；維度不適用回空 list。"""
    return list(catalog.profiles[profile].get(dimension, []))


def grounded_dimensions(states: dict[str, str], catalog: FacetCatalog) -> set[str]:
    """有任一 facet 為 covered 的維度。不問 LLM，從 facet 狀態推出來，少一個可被捏造的欄位。"""
    return {catalog.dimension_of(fid) for fid, s in states.items() if s == "covered" and fid in catalog.facets}


@dataclass
class DimensionQuery:
    dimension: str
    query: str
    grounded: bool
    k: int


def normalize_queries(
    raw: list[tuple[str, str]],
    profile: str,
    grounded: set[str],
    catalog: FacetCatalog,
    fallback_query: str,
    k_covered: int = K_COVERED,
    k_missing: int = K_MISSING,
) -> list[DimensionQuery]:
    """整理 LLM 給的 (dimension, query) 清單：
    - 不在 profile 裡的維度、空白 query 丟掉
    - 每維度上限：grounded 1 句、missing 2 句，多的丟
    - grounded 維度若一句都沒有，用整句描述頂上（一次漏答不能毀掉整個維度）"""
    applicable = set(catalog.profiles[profile])
    out: list[DimensionQuery] = []
    count: dict[str, int] = {}
    for dim, q in raw:
        q = q.strip()
        if dim not in applicable or not q:
            continue
        is_grounded = dim in grounded
        if count.get(dim, 0) >= _MAX_QUERIES[is_grounded]:
            continue
        count[dim] = count.get(dim, 0) + 1
        out.append(DimensionQuery(dim, q, is_grounded, k_covered if is_grounded else k_missing))
    for dim in DIMENSIONS:
        if dim in grounded and dim in applicable and dim not in count:
            out.append(DimensionQuery(dim, fallback_query, True, k_covered))
    return out
