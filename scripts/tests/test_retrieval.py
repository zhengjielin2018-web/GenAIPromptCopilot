from pipeline.config import FACETS_PATH
from pipeline.facets import load_facets
from pipeline.retrieval import (
    band,
    dimension_facets,
    grounded_dimensions,
    normalize_queries,
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
