from pipeline.config import FACETS_PATH
from pipeline.facets import load_facets
from pipeline.retrieval import (
    Candidate,
    annotate_coverage,
    band,
    dedupe,
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
