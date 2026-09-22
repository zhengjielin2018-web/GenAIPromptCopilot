"""分維度檢索：每個維度用自己的子查詢向量、在自己的 facet 候選池裡撈，再跨維度去重與分級。
純函式（本檔上半）與吃 conn 的函式（下半）分開，前者可無 DB 測試。
設計見 docs/superpowers/specs/2026-09-22-dimension-scoped-retrieval-design.md §5、§6.1。"""

from __future__ import annotations

from dataclasses import dataclass, field
from typing import Literal

from pgvector import Vector

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


@dataclass
class Candidate:
    preset: dict
    dimension: str  # 去重後歸屬的維度
    dist: float
    band: Band
    grounded: bool  # 歸屬維度是否 grounded；False 的片段不得借入提示詞
    facet_coverage: dict[str, str] = field(default_factory=dict)  # facet_id -> 本次使用者的狀態

    @property
    def id(self) -> int:
        return self.preset["id"]


def dedupe(hits: list[Candidate]) -> list[Candidate]:
    """同一 preset 跨維度命中只留距離最小的那個（每維用不同子查詢向量，最小即最像該維需求）。
    輸出依 (維度順序, 距離) 排序。"""
    best: dict[int, Candidate] = {}
    for c in hits:
        cur = best.get(c.id)
        if cur is None or c.dist < cur.dist:
            best[c.id] = c
    return sorted(best.values(), key=lambda c: (DIMENSIONS.index(c.dimension), c.dist))


def annotate_coverage(cands: list[Candidate], states: dict[str, str]) -> None:
    """每筆候選的每個 facet_id 標上本次使用者的狀態；不在 ① 清單裡的視為 notApplicable。給 ③ 看的。"""
    for c in cands:
        c.facet_coverage = {fid: states.get(fid, "notApplicable") for fid in c.preset["facet_ids"]}


# ---------- 以下吃 conn ----------

PRESETS_SQL = """
SELECT id, title, category, facet_ids, prompt_snippet, negative_snippet,
       preset_embedding <=> %(q)s AS dist
FROM prompt_knowledge_presets
WHERE facet_ids && %(facets)s
ORDER BY dist
LIMIT %(k)s
"""

POOL_SQL = "SELECT count(*) FROM prompt_knowledge_presets WHERE facet_ids && %(facets)s"

HISTORIES_SQL = """
SELECT user_intent, positive_prompt, subject_profile,
       intent_embedding <=> %(q)s AS dist
FROM shared_prompt_histories
WHERE subject_profile = %(profile)s
ORDER BY dist
LIMIT %(k)s
"""


@dataclass
class DimensionHits:
    query: DimensionQuery
    pool_size: int
    hits: list[Candidate]


def retrieve_presets(
    conn, queries: list[DimensionQuery], vectors: list[Vector], catalog: FacetCatalog, profile: str
) -> list[DimensionHits]:
    """每句子查詢各跑一次：GIN 過濾該維度 facet（idx_presets_facet_ids）+ 向量排序。不設距離門檻。"""
    pool_cache: dict[str, int] = {}
    out: list[DimensionHits] = []
    for q, v in zip(queries, vectors, strict=True):
        facets = dimension_facets(catalog, profile, q.dimension)
        if q.dimension not in pool_cache:
            pool_cache[q.dimension] = conn.execute(POOL_SQL, {"facets": facets}).fetchone()[0]
        hits = [
            Candidate(
                preset={
                    "id": r[0], "title": r[1], "category": r[2], "facet_ids": list(r[3]),
                    "prompt_snippet": r[4], "negative_snippet": r[5],
                },
                dimension=q.dimension, dist=float(r[6]), band=band(float(r[6])), grounded=q.grounded,
            )
            for r in conn.execute(PRESETS_SQL, {"q": v, "facets": facets, "k": q.k})
        ]
        out.append(DimensionHits(q, pool_cache[q.dimension], hits))
    return out


def retrieve_histories(conn, qvec: Vector, profile: str, k: int) -> list[dict]:
    """整段紀錄配整句向量，並依 ① 判定的 profile 過濾（主規格 §9 本來就有）。"""
    return [
        {"user_intent": r[0], "positive_prompt": r[1], "subject_profile": r[2], "dist": float(r[3])}
        for r in conn.execute(HISTORIES_SQL, {"q": qvec, "profile": profile, "k": k})
    ]
