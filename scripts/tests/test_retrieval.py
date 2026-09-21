import pytest
from pgvector import Vector

from pipeline import db
from pipeline.config import FACETS_PATH, settings
from pipeline.facets import load_facets
from pipeline.retrieval import (
    Candidate,
    DimensionQuery,
    annotate_coverage,
    band,
    dedupe,
    dimension_facets,
    grounded_dimensions,
    normalize_queries,
    retrieve_histories,
    retrieve_presets,
)

CAT = load_facets(FACETS_PATH)


def test_band_boundaries_match_the_spec():
    assert band(0.249) == "高"
    assert band(0.25) == "中"
    assert band(0.299) == "中"
    assert band(0.30) == "低"


def test_dimension_facets_follows_the_profile():
    assert dimension_facets(CAT, "landscape", "clothing") == []
    assert dimension_facets(CAT, "landscape", "appearance") == []
    assert dimension_facets(CAT, "vehicle", "pose") == ["pose.motion_state", "pose.terrain"]
    assert "appearance.hair" in dimension_facets(CAT, "portrait", "appearance")


def test_grounded_dimensions_needs_one_covered_facet_and_ignores_unknown_ids():
    states = {"scene.location": "covered", "scene.weather": "missing", "style.genre": "missing", "bogus.id": "covered"}
    assert grounded_dimensions(states, CAT) == {"scene"}


def test_normalize_drops_dimensions_the_profile_does_not_have():
    out = normalize_queries([("clothing", "夾克"), ("scene", "山")], "landscape", {"scene"}, CAT, "整句")
    assert [q.dimension for q in out] == ["scene"]


def test_normalize_caps_grounded_at_one_and_missing_at_two_keeping_the_first_ones():
    raw = [("scene", "a"), ("scene", "b"), ("style", "x"), ("style", "y"), ("style", "z")]
    out = normalize_queries(raw, "portrait", {"scene"}, CAT, "整句")
    assert [(q.dimension, q.query) for q in out] == [("scene", "a"), ("style", "x"), ("style", "y")]
    assert out[0].grounded is True and out[0].k == 5
    assert out[1].grounded is False and out[1].k == 3


def test_normalize_backfills_a_grounded_dimension_the_llm_skipped_with_the_whole_description():
    out = normalize_queries([("style", "x")], "portrait", {"appearance", "scene"}, CAT, "整句描述")
    backfilled = [(q.dimension, q.query, q.k) for q in out if q.grounded]
    # 依 DIMENSIONS 順序：scene 在 appearance 前
    assert backfilled == [("scene", "整句描述", 5), ("appearance", "整句描述", 5)]


def test_normalize_drops_blank_queries_and_then_backfills_if_grounded():
    out = normalize_queries([("scene", "   ")], "portrait", {"scene"}, CAT, "整句")
    assert [(q.dimension, q.query) for q in out] == [("scene", "整句")]


def test_normalize_allows_a_missing_dimension_to_have_no_query():
    assert normalize_queries([], "portrait", set(), CAT, "整句") == []


def test_normalize_honours_custom_k_values():
    out = normalize_queries(
        [("scene", "a"), ("style", "x")], "portrait", {"scene"}, CAT, "整句", k_covered=7, k_missing=2
    )
    assert [q.k for q in out] == [7, 2]


def _cand(pid, dim, dist, facet_ids=(), grounded=True):
    return Candidate(
        preset={
            "id": pid, "title": f"t{pid}", "category": "X", "facet_ids": list(facet_ids), "tags": [],
            "prompt_snippet": "", "negative_snippet": None,
        },
        dimension=dim, dist=dist, band=band(dist), grounded=grounded,
    )


def test_dedupe_keeps_the_closest_dimension_and_its_grounded_flag():
    hits = [_cand(1, "scene", 0.30, grounded=True), _cand(1, "camera", 0.22, grounded=False), _cand(2, "scene", 0.28)]
    out = dedupe(hits)
    assert [(c.id, c.dimension, c.grounded) for c in out] == [(2, "scene", True), (1, "camera", False)]


def test_dedupe_orders_by_dimension_then_distance():
    hits = [_cand(3, "clothing", 0.1), _cand(1, "style", 0.3), _cand(2, "style", 0.2)]
    assert [c.id for c in dedupe(hits)] == [2, 1, 3]


def test_annotate_coverage_marks_every_facet_of_the_preset_with_the_users_state():
    c = _cand(1, "scene", 0.2, facet_ids=["scene.lighting", "scene.weather", "camera.shot"])
    annotate_coverage([c], {"scene.lighting": "covered", "scene.weather": "missing"})
    assert c.facet_coverage == {
        "scene.lighting": "covered",
        "scene.weather": "missing",
        "camera.shot": "notApplicable",
    }


def _unit_vector() -> Vector:
    return Vector([1.0] + [0.0] * (settings.embedding_dimensions - 1))


def _conn_with_data():
    if not db.db_available():
        pytest.skip("PostgreSQL 未啟動")
    conn = db.connect()
    if conn.execute("SELECT count(*) FROM prompt_knowledge_presets").fetchone()[0] == 0:
        conn.close()
        pytest.skip("presets 表是空的")
    return conn


@pytest.mark.integration
def test_retrieve_presets_only_returns_rows_touching_the_dimension_and_respects_k():
    with _conn_with_data() as conn:
        v = _unit_vector()
        queries = [DimensionQuery("scene", "q", True, 5), DimensionQuery("style", "q", False, 3)]
        out = retrieve_presets(conn, queries, [v, v], CAT, "portrait")
        assert [dh.query.dimension for dh in out] == ["scene", "style"]
        for dh in out:
            allowed = set(dimension_facets(CAT, "portrait", dh.query.dimension))
            assert dh.pool_size >= len(dh.hits) > 0
            assert len(dh.hits) <= dh.query.k
            for c in dh.hits:
                assert allowed & set(c.preset["facet_ids"])
                assert c.dimension == dh.query.dimension
                assert c.grounded is dh.query.grounded
                assert c.band == band(c.dist)
                assert set(c.preset) == {
                    "id", "title", "category", "facet_ids", "tags", "prompt_snippet", "negative_snippet",
                }


@pytest.mark.integration
def test_retrieve_presets_pool_differs_between_profiles():
    with _conn_with_data() as conn:
        v = _unit_vector()
        q = DimensionQuery("scene", "q", True, 5)
        portrait = retrieve_presets(conn, [q], [v], CAT, "portrait")[0]
        landscape = retrieve_presets(conn, [q], [v], CAT, "landscape")[0]
        # landscape 的 scene 多了 scene.season，候選池只會更大
        assert landscape.pool_size >= portrait.pool_size


@pytest.mark.integration
def test_retrieve_histories_filters_by_profile():
    with _conn_with_data() as conn:
        rows = retrieve_histories(conn, _unit_vector(), "portrait", 3)
        assert len(rows) <= 3
        assert all(r["subject_profile"] == "portrait" for r in rows)
        assert all(set(r) == {"user_intent", "positive_prompt", "subject_profile", "dist"} for r in rows)
