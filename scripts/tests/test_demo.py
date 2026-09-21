from demo import (
    AnalysisResult,
    AssemblyResult,
    BorrowedFrom,
    DimensionSuggestion,
    FacetAssessment,
    SuggestionOption,
    facet_state_map,
    missing_labels_by_dimension,
    validate_borrowed,
    validate_suggestions,
)
from pipeline.config import FACETS_PATH
from pipeline.facets import load_facets
from pipeline.retrieval import Candidate, band

CAT = load_facets(FACETS_PATH)


def _cand(pid, dim, dist, snippet, negative=None, grounded=True, title=None):
    return Candidate(
        preset={
            "id": pid, "title": title or f"t{pid}", "category": dim.title(), "facet_ids": [], "tags": [],
            "prompt_snippet": snippet, "negative_snippet": negative,
        },
        dimension=dim, dist=dist, band=band(dist), grounded=grounded,
    )


def _assembly(**kw):
    base = dict(
        positive_prompt="1girl, purple hair, twintails", negative_prompt="bad anatomy", borrowed=[], suggestions=[]
    )
    base.update(kw)
    return AssemblyResult(**base)


# ---------- facet 狀態整理 ----------


def test_facet_state_map_keeps_only_profile_facets_and_backfills_missing():
    analysis = AnalysisResult(
        subject_profile="landscape",
        facets=[
            FacetAssessment(facet_id="scene.location", state="covered"),
            FacetAssessment(facet_id="clothing.upper", state="covered"),  # landscape 沒有這個
            FacetAssessment(facet_id="not.real", state="covered"),
        ],
        queries=[],
    )
    states = facet_state_map(analysis, CAT, "landscape")
    assert states["scene.location"] == "covered"
    assert "clothing.upper" not in states and "not.real" not in states
    assert states["scene.season"] == "missing"  # LLM 漏判 → 保守補 missing
    assert set(states) == set(CAT.ids_for_profile("landscape"))


def test_missing_labels_by_dimension_groups_labels_in_catalog_order():
    states = {
        "style.genre": "missing", "style.palette": "missing", "scene.location": "covered", "scene.weather": "missing"
    }
    assert missing_labels_by_dimension(states, CAT) == {
        "style": ["藝術流派／媒材", "色調傾向"],
        "scene": ["天氣氛圍"],
    }


# ---------- validate_borrowed ----------


def test_validate_borrowed_keeps_tags_present_in_both_snippet_and_prompt():
    by_id = {144: _cand(144, "appearance", 0.19, "1girl, short twintails, pink hair, green eyes")}
    a = _assembly(borrowed=[BorrowedFrom(preset_id=144, tags=["twintails"])])
    kept, rejected = validate_borrowed(a, by_id)
    assert kept == [BorrowedFrom(preset_id=144, tags=["twintails"])]
    assert rejected == []


def test_validate_borrowed_rejects_a_tag_the_snippet_does_not_contain_without_touching_the_prompt():
    by_id = {483: _cand(483, "appearance", 0.21, "1girl, pink hair, purple eyes", title="粉髮紫瞳少女")}
    a = _assembly(borrowed=[BorrowedFrom(preset_id=483, tags=["purple hair"])])
    before = (a.positive_prompt, a.negative_prompt)
    kept, rejected = validate_borrowed(a, by_id)
    assert kept == []
    assert len(rejected) == 1
    assert "來源不符" in rejected[0] and "483" in rejected[0] and "粉髮紫瞳少女" in rejected[0]
    assert '"purple hair"' in rejected[0] and "提示詞不受影響" in rejected[0]
    assert (a.positive_prompt, a.negative_prompt) == before


def test_validate_borrowed_rejects_a_tag_that_is_not_actually_in_the_prompt():
    by_id = {144: _cand(144, "appearance", 0.19, "1girl, twintails, blush")}
    a = _assembly(borrowed=[BorrowedFrom(preset_id=144, tags=["blush"])])
    kept, rejected = validate_borrowed(a, by_id)
    assert kept == []
    assert "未出現在提示詞" in rejected[0] and '"blush"' in rejected[0]


def test_validate_borrowed_rejects_ungrounded_presets_entirely():
    by_id = {900: _cand(900, "style", 0.23, "anime, Makoto Shinkai Style", grounded=False)}
    a = _assembly(positive_prompt="1girl, anime", borrowed=[BorrowedFrom(preset_id=900, tags=["anime"])])
    kept, rejected = validate_borrowed(a, by_id)
    assert kept == []
    assert "未描述" in rejected[0] and "900" in rejected[0]


def test_validate_borrowed_rejects_unknown_preset_ids():
    a = _assembly(borrowed=[BorrowedFrom(preset_id=1, tags=["1girl"])])
    kept, rejected = validate_borrowed(a, {})
    assert kept == [] and "不在本次檢索結果" in rejected[0]


def test_validate_borrowed_is_case_insensitive_and_can_borrow_from_negative_snippet():
    by_id = {7: _cand(7, "scene", 0.2, "Night Sky", negative="Bad Anatomy")}
    a = _assembly(positive_prompt="night sky", negative_prompt="bad anatomy",
                  borrowed=[BorrowedFrom(preset_id=7, tags=["night sky", "bad anatomy"])])
    kept, rejected = validate_borrowed(a, by_id)
    assert kept == [BorrowedFrom(preset_id=7, tags=["night sky", "bad anatomy"])]
    assert rejected == []


def test_validate_borrowed_keeps_the_good_tags_and_reports_the_bad_ones_of_the_same_preset():
    by_id = {144: _cand(144, "appearance", 0.19, "1girl, twintails, pink hair")}
    a = _assembly(borrowed=[BorrowedFrom(preset_id=144, tags=["twintails", "purple hair"])])
    kept, rejected = validate_borrowed(a, by_id)
    assert kept == [BorrowedFrom(preset_id=144, tags=["twintails"])]
    assert len(rejected) == 1 and '"purple hair"' in rejected[0]


# ---------- validate_suggestions ----------


def test_validate_suggestions_keeps_options_with_known_sources_and_overrides_missing_labels_from_analysis():
    by_id = {900: _cand(900, "style", 0.23, "anime", grounded=False)}
    a = _assembly(suggestions=[DimensionSuggestion(
        dimension="style", missing_labels=["LLM 亂寫的"],
        options=[SuggestionOption(label="動漫", tags="anime", preset_id=900),
                 SuggestionOption(label="幽靈", tags="ghost", preset_id=999)],
    )])
    kept, rejected = validate_suggestions(a, by_id, {"style": ["藝術流派／媒材", "色調傾向"]})
    assert len(kept) == 1
    assert kept[0].missing_labels == ["藝術流派／媒材", "色調傾向"]
    assert [o.preset_id for o in kept[0].options] == [900]
    assert len(rejected) == 1 and "999" in rejected[0]


def test_validate_suggestions_can_source_from_grounded_presets():
    by_id = {7: _cand(7, "scene", 0.2, "full moon, mist", grounded=True)}
    a = _assembly(suggestions=[DimensionSuggestion(
        dimension="scene", missing_labels=[], options=[SuggestionOption(label="薄霧", tags="mist", preset_id=7)],
    )])
    kept, rejected = validate_suggestions(a, by_id, {"scene": ["天氣氛圍"]})
    assert len(kept) == 1 and rejected == []


def test_validate_suggestions_drops_a_dimension_that_has_nothing_missing():
    by_id = {7: _cand(7, "scene", 0.2, "mist")}
    a = _assembly(suggestions=[DimensionSuggestion(
        dimension="scene", missing_labels=[], options=[SuggestionOption(label="薄霧", tags="mist", preset_id=7)],
    )])
    kept, rejected = validate_suggestions(a, by_id, {})
    assert kept == [] and "沒有缺的 facet" in rejected[0]
