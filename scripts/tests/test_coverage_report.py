import pytest

from coverage_report import (
    POOL_THRESHOLD,
    build_report,
    query_category_counts,
    query_facet_counts,
    query_pool_sizes,
    query_profile_counts,
)
from pipeline import db
from pipeline.config import FACETS_PATH
from pipeline.facets import load_facets
from pipeline.retrieval import DIMENSIONS

CAT = load_facets(FACETS_PATH)
ALL_APPLICABLE_DIMS = {
    (p, d) for p in CAT.profiles for d in DIMENSIONS if CAT.profiles[p].get(d)
}


def test_build_report_flags_pool_below_threshold():
    result = build_report(
        profile_counts={}, pool_sizes={("vehicle", "pose"): 2},
        facet_counts={}, category_counts={}, catalog=CAT,
    )
    assert ("vehicle", "pose", 2) in result.pool_gaps
    assert result.has_gaps is True
    assert "vehicle" in result.text
    assert "未達門檻" in result.text


def test_build_report_does_not_flag_pool_at_threshold():
    result = build_report(
        profile_counts={}, pool_sizes={("portrait", "style"): POOL_THRESHOLD},
        facet_counts={}, category_counts={}, catalog=CAT,
    )
    assert all(g[:2] != ("portrait", "style") for g in result.pool_gaps)


def test_build_report_flags_facet_below_threshold_including_zero():
    result = build_report(
        profile_counts={}, pool_sizes={}, facet_counts={"pose.terrain": 0},
        category_counts={}, catalog=CAT,
    )
    assert ("pose.terrain", 0) in result.facet_gaps


def test_build_report_has_no_gaps_when_everything_is_well_above_threshold():
    pool_sizes = {pair: 999 for pair in ALL_APPLICABLE_DIMS}
    facet_counts = dict.fromkeys(CAT.facets, 999)
    result = build_report(
        profile_counts={}, pool_sizes=pool_sizes, facet_counts=facet_counts,
        category_counts={}, catalog=CAT,
    )
    assert result.has_gaps is False
    assert "全部達標" in result.text


def test_custom_pool_threshold_is_respected():
    result = build_report(
        profile_counts={}, pool_sizes={("object", "appearance"): 50},
        facet_counts={}, category_counts={}, catalog=CAT, pool_threshold=40,
    )
    assert all(g[:2] != ("object", "appearance") for g in result.pool_gaps)


def test_missing_pool_or_facet_entries_default_to_zero_and_are_flagged():
    """(profile, dim) 或 facet id 完全沒在傳入的字典裡（例如 DB 是空的）要當 0 筆處理，
    而不是被 dict.get 靜默跳過。"""
    result = build_report(
        profile_counts={}, pool_sizes={}, facet_counts={}, category_counts={}, catalog=CAT,
    )
    assert len(result.pool_gaps) == len(ALL_APPLICABLE_DIMS)
    assert len(result.facet_gaps) == len(CAT.facets)


@pytest.mark.integration
def test_query_functions_return_data_shaped_dicts_against_real_db():
    if not db.db_available():
        pytest.skip("PostgreSQL 未啟動")
    with db.connect() as conn:
        profiles = query_profile_counts(conn)
        pools = query_pool_sizes(conn, CAT)
        facets = query_facet_counts(conn)
        categories = query_category_counts(conn)
    assert all(isinstance(v, int) for v in profiles.values())
    assert all(isinstance(k, tuple) and len(k) == 2 for k in pools)
    assert all(isinstance(v, int) for v in facets.values())
    assert all(isinstance(v, int) for v in categories.values())
