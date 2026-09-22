"""驗收工具：量測每 (profile, 維度) 候選池與每個 facet 的 preset 筆數是否達門檻。
候選池查詢直接重用 pipeline.retrieval.POOL_SQL / dimension_facets，這裡量到的池子
定義上就等於 retrieval.py 實際檢索時看到的池子，不會有兩份實作各自漂移。
門檻依據見 docs/superpowers/specs/2026-09-22-corpus-expansion-design.md §10。"""

from __future__ import annotations

import argparse
import sys
from dataclasses import dataclass
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

from pipeline.config import FACETS_PATH  # noqa: E402
from pipeline.facets import FacetCatalog, load_facets  # noqa: E402
from pipeline.retrieval import DIMENSIONS, POOL_SQL, dimension_facets  # noqa: E402

POOL_THRESHOLD = 60  # retrieval.py::K_COVERED = 5；池子要有明顯大於 K 的挑選空間
FACET_THRESHOLD = 10

PROFILE_COUNTS_SQL = "SELECT subject_profile, count(*) FROM shared_prompt_histories GROUP BY 1"
CATEGORY_COUNTS_SQL = "SELECT category, count(*) FROM prompt_knowledge_presets GROUP BY 1"
FACET_COUNTS_SQL = (
    "SELECT f, count(*) FROM prompt_knowledge_presets, unnest(facet_ids) AS f GROUP BY 1"
)


@dataclass
class ReportResult:
    text: str
    pool_gaps: list[tuple[str, str, int]]
    facet_gaps: list[tuple[str, int]]

    @property
    def has_gaps(self) -> bool:
        return bool(self.pool_gaps or self.facet_gaps)


def build_report(
    *,
    profile_counts: dict[str, int],
    pool_sizes: dict[tuple[str, str], int],
    facet_counts: dict[str, int],
    category_counts: dict[str, int],
    catalog: FacetCatalog,
    pool_threshold: int = POOL_THRESHOLD,
    facet_threshold: int = FACET_THRESHOLD,
) -> ReportResult:
    lines: list[str] = ["== histories：每 profile 筆數 =="]
    for profile in catalog.profiles:
        lines.append(f"  {profile:10} {profile_counts.get(profile, 0)}")

    lines.append("\n== 候選池：每 (profile, 維度) ==")
    pool_gaps: list[tuple[str, str, int]] = []
    for profile in catalog.profiles:
        for dim in DIMENSIONS:
            if not dimension_facets(catalog, profile, dim):
                continue
            n = pool_sizes.get((profile, dim), 0)
            flag = "  <== 未達門檻" if n < pool_threshold else ""
            lines.append(f"  {profile:10} {dim:12} {n:6d}{flag}")
            if n < pool_threshold:
                pool_gaps.append((profile, dim, n))

    lines.append("\n== facet：每個 id 的 preset 筆數 ==")
    facet_gaps: list[tuple[str, int]] = []
    for facet_id in sorted(catalog.facets, key=lambda fid: facet_counts.get(fid, 0)):
        n = facet_counts.get(facet_id, 0)
        flag = "  <== 未達門檻" if n < facet_threshold else ""
        lines.append(f"  {facet_id:24} {n:6d}{flag}")
        if n < facet_threshold:
            facet_gaps.append((facet_id, n))

    lines.append("\n== preset category 分布 ==")
    for category, n in sorted(category_counts.items(), key=lambda kv: -kv[1]):
        lines.append(f"  {category:10} {n}")

    if pool_gaps or facet_gaps:
        lines.append(f"\n共 {len(pool_gaps)} 個候選池、{len(facet_gaps)} 個 facet 未達門檻。")
    else:
        lines.append("\n全部達標。")

    return ReportResult("\n".join(lines), pool_gaps, facet_gaps)


# ---------- 以下吃 conn ----------


def query_profile_counts(conn) -> dict[str, int]:
    return dict(conn.execute(PROFILE_COUNTS_SQL).fetchall())


def query_category_counts(conn) -> dict[str, int]:
    return dict(conn.execute(CATEGORY_COUNTS_SQL).fetchall())


def query_facet_counts(conn) -> dict[str, int]:
    return dict(conn.execute(FACET_COUNTS_SQL).fetchall())


def query_pool_sizes(conn, catalog: FacetCatalog) -> dict[tuple[str, str], int]:
    out: dict[tuple[str, str], int] = {}
    cache: dict[tuple[str, ...], int] = {}
    for profile in catalog.profiles:
        for dim in DIMENSIONS:
            facets = dimension_facets(catalog, profile, dim)
            if not facets:
                continue
            cache_key = tuple(sorted(facets))
            if cache_key not in cache:
                cache[cache_key] = conn.execute(POOL_SQL, {"facets": facets}).fetchone()[0]
            out[(profile, dim)] = cache[cache_key]
    return out


def main(argv: list[str] | None = None) -> None:
    ap = argparse.ArgumentParser(description="覆蓋度報告：候選池與 facet 缺口")
    ap.add_argument("--pool-threshold", type=int, default=POOL_THRESHOLD)
    ap.add_argument("--facet-threshold", type=int, default=FACET_THRESHOLD)
    args = ap.parse_args(argv)

    from pipeline.db import connect

    catalog = load_facets(FACETS_PATH)
    with connect() as conn:
        result = build_report(
            profile_counts=query_profile_counts(conn),
            pool_sizes=query_pool_sizes(conn, catalog),
            facet_counts=query_facet_counts(conn),
            category_counts=query_category_counts(conn),
            catalog=catalog,
            pool_threshold=args.pool_threshold,
            facet_threshold=args.facet_threshold,
        )
    print(result.text)
    sys.exit(1 if result.has_gaps else 0)


if __name__ == "__main__":
    main()
