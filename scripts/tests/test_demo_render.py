from demo_render import (
    BorrowedView,
    DemoView,
    OptionView,
    Palette,
    SuggestionView,
    display_width,
    group_by_dimension,
    pad,
    render,
    render_dimension_row,
    render_queries,
    render_retrieval_summary,
    render_verbose,
    wrap_tags,
)
from pipeline.config import FACETS_PATH
from pipeline.facets import load_facets
from pipeline.retrieval import Candidate, DimensionHits, DimensionQuery, band

CAT = load_facets(FACETS_PATH)


def test_group_by_dimension_buckets_by_the_catalog_and_drops_unknown_ids():
    grouped = group_by_dimension(
        {
            "scene.location": "covered",
            "scene.weather": "missing",
            "clothing.upper": "notApplicable",
            "not.a.real.facet": "covered",
        },
        CAT,
    )
    assert grouped["scene"] == [("地點類型", "covered"), ("天氣氛圍", "missing")]
    assert grouped["clothing"] == [("上半身", "notApplicable")]
    assert grouped["camera"] == []
    assert not any("not.a.real.facet" in str(v) for v in grouped.values())


def test_group_by_dimension_follows_catalog_order_not_input_order():
    grouped = group_by_dimension({"scene.weather": "missing", "scene.location": "covered"}, CAT)
    assert grouped["scene"] == [("地點類型", "covered"), ("天氣氛圍", "missing")]


def test_render_dimension_row_shows_ratio_and_names_what_is_missing():
    row = render_dimension_row("場景", [("地點類型", "covered"), ("天氣氛圍", "missing")])
    assert "●○" in row
    assert "1/2" in row
    assert "缺：天氣氛圍" in row


def test_render_dimension_row_collapses_a_fully_inapplicable_dimension():
    row = render_dimension_row("人物穿著", [("上半身", "notApplicable"), ("鞋履", "notApplicable")])
    assert "不適用" in row
    assert "●" not in row and "○" not in row


def test_render_dimension_row_omits_the_missing_clause_when_complete():
    row = render_dimension_row("鏡頭", [("景別", "covered"), ("視角高度", "covered")])
    assert "2/2" in row
    assert "缺" not in row


def test_wrap_tags_never_splits_a_tag_across_lines():
    tags = [f"tag-number-{i}" for i in range(20)]
    out = wrap_tags(", ".join(tags), indent="  ", width=40)
    assert "\n" in out
    for line in out.splitlines():
        assert len(line) <= 40 or line.count(",") == 0
    for tag in tags:
        assert tag in out


def test_wrap_tags_drops_empty_fragments_and_the_trailing_comma():
    out = wrap_tags("a, , b,  ,c", indent="")
    assert out == "a, b, c"


def test_display_width_counts_cjk_as_two_columns():
    assert display_width("風格") == 4
    assert display_width("人物樣貌") == 8
    assert display_width("Style") == 5


def test_dimension_labels_of_different_cjk_lengths_line_up():
    # 純字元數的 padding 會讓「風格」和「人物樣貌」對不齊，這是這兩個函式存在的原因。
    short = render_dimension_row("風格", [("a", "covered")])
    long_ = render_dimension_row("人物樣貌", [("b", "covered")])
    assert display_width(short.split("●")[0]) == display_width(long_.split("●")[0])


def test_pad_leaves_an_already_wide_string_untouched():
    assert pad("人物樣貌", 4) == "人物樣貌"


P = Palette(False)


def _cand(pid, dim, dist, grounded=True, coverage=None, title=None):
    return Candidate(
        preset={"id": pid, "title": title or f"t{pid}", "category": dim.title(), "facet_ids": [], "tags": [],
                "prompt_snippet": "1girl, twintails", "negative_snippet": None},
        dimension=dim, dist=dist, band=band(dist), grounded=grounded, facet_coverage=coverage or {},
    )


def test_render_queries_separates_user_words_from_guesses():
    qs = [DimensionQuery("scene", "夜晚的湖畔", True, 5), DimensionQuery("style", "動漫", False, 3),
          DimensionQuery("style", "寫實", False, 3)]
    text = render_queries(qs, CAT, "portrait")
    assert "子查詢：場景「夜晚的湖畔」" in text
    assert "推想：風格「動漫」 風格「寫實」" in text


def test_render_queries_uses_the_profile_specific_dimension_label():
    qs = [DimensionQuery("appearance", "鏽蝕的鐵劍", True, 5)]
    text = render_queries(qs, CAT, "object")
    assert "主體外觀「鏽蝕的鐵劍」" in text
    assert "人物樣貌" not in text


def test_render_retrieval_summary_reports_pool_hits_and_bands_per_dimension_merging_multi_query_dimensions():
    hits = [
        DimensionHits(DimensionQuery("scene", "q", True, 5), 292, [_cand(1, "scene", 0.26), _cand(2, "scene", 0.31)]),
        DimensionHits(DimensionQuery("style", "a", False, 3), 238, [_cand(3, "style", 0.2)]),
        DimensionHits(DimensionQuery("style", "b", False, 3), 238, [_cand(4, "style", 0.2), _cand(5, "style", 0.29)]),
    ]
    text = render_retrieval_summary(hits, CAT, 3, "portrait")
    assert "風格 池 238 → 3（高 2 中 1）" in text
    assert "場景 池 292 → 2（中 1 低 1）" in text
    assert text.index("風格") < text.index("場景")  # 依 DIMENSIONS 順序，不是輸入順序
    assert "相似作品 3（portrait）" in text


def test_render_verbose_lists_every_candidate_with_usage_and_coverage():
    cands = [_cand(1, "scene", 0.26, coverage={"scene.lighting": "covered"}),
             _cand(9, "style", 0.2, grounded=False, coverage={"style.genre": "missing"})]
    text = render_verbose(cands, CAT, P, "portrait")
    assert "[場景]" in text and "[風格]" in text
    assert "id=1" in text and "可借入" in text and "scene.lighting=covered" in text
    assert "id=9" in text and "僅供建議" in text


def test_render_verbose_uses_the_profile_specific_dimension_label():
    cands = [_cand(1, "appearance", 0.2, coverage={"appearance.material": "covered"})]
    text = render_verbose(cands, CAT, P, "vehicle")
    assert "[主體外觀]" in text
    assert "人物樣貌" not in text


def _view(**kw):
    base = dict(
        profile="portrait",
        facet_states={"appearance.hair": "covered", "appearance.face": "missing", "style.genre": "missing"},
        positive_prompt="1girl, purple hair, twintails",
        negative_prompt="bad anatomy",
        borrowed=[], rejections=[], suggestions=[], unserved=[],
    )
    base.update(kw)
    return DemoView(**base)


def test_render_shows_borrowed_tags_with_band_and_rejections_verbatim():
    view = _view(
        borrowed=[BorrowedView("高", 0.19, "粉紅雙馬尾少女", "Appearance", ["twintails", "green eyes"])],
        rejections=['來源不符：id 483〈粉髮紫瞳少女〉沒有 "purple hair"，不計入借用（提示詞不受影響）'],
    )
    text = render(view, CAT, P)
    assert "[高 0.190] 粉紅雙馬尾少女（Appearance）→ 借入 twintails, green eyes" in text
    assert '✗ 來源不符：id 483〈粉髮紫瞳少女〉沒有 "purple hair"，不計入借用（提示詞不受影響）' in text
    assert "1/2" in text  # 人物樣貌 1/2
    assert "沒有借用" not in text


def test_render_shows_rejection_only_borrowed_section_without_the_nothing_borrowed_line():
    """borrowed=[] 但 rejections 非空：一定要印出拒絕，不能落回「這次沒有借用」。
    這條鎖死 `if not view.borrowed and not view.rejections:` 的雙重守門，
    防止未來有人簡化成只看 `view.borrowed`（object 的真實跑法踩過這個坑）。"""
    view = _view(borrowed=[], rejections=["來源不符：id 1〈甲〉沒有 \"x\"，不計入借用（提示詞不受影響）"])
    text = render(view, CAT, P)
    assert "✗ 來源不符：id 1〈甲〉" in text
    assert "沒有借用" not in text


def test_render_shows_one_suggestion_block_per_dimension_naming_missing_facets_and_lettered_options():
    view = _view(suggestions=[SuggestionView(
        "風格", ["藝術流派／媒材", "色調傾向"],
        [OptionView("新海誠風", "Makoto Shinkai Style, Soft Realism", "新海誠動畫風"),
         OptionView("寫實夜景攝影", "photo realism", "寫實攝影")],
    )])
    text = render(view, CAT, P)
    assert "風格（缺：藝術流派／媒材、色調傾向）" in text
    assert "A. 新海誠風" in text and "Makoto Shinkai Style, Soft Realism" in text and "〈新海誠動畫風〉" in text
    assert "B. 寫實夜景攝影" in text


def test_render_shows_an_unserved_dimension_instead_of_silently_dropping_it():
    """③ 對某個有缺 facet 的維度一則建議都沒給（或全被驗證丟光）時，畫面必須點名，不能悄悄消失。"""
    view = _view(unserved=[("場景", ["前景元素", "背景與遠景"])])
    text = render(view, CAT, P)
    assert "✗ 場景（缺：前景元素、背景與遠景）" in text
    assert "沒有可用的建議" in text
    assert "所有維度都已覆蓋" not in text  # 明明有缺，不能同時說「都已覆蓋」


def test_render_all_covered_line_only_appears_when_nothing_is_missing_at_all():
    """即使 suggestions 是空的，只要 unserved 非空就代表還有缺，不能印「都已覆蓋」。"""
    view = _view(suggestions=[], unserved=[("場景", ["前景元素"])])
    text = render(view, CAT, P)
    assert "所有維度都已覆蓋" not in text

    view_all_covered = _view(facet_states={"appearance.hair": "covered"}, suggestions=[], unserved=[])
    text_all_covered = render(view_all_covered, CAT, P)
    assert "所有維度都已覆蓋" in text_all_covered


def test_render_says_so_when_nothing_was_borrowed_and_nothing_is_missing():
    view = _view(facet_states={"appearance.hair": "covered"})
    text = render(view, CAT, P)
    assert "沒有借用" in text
    assert "沒有建議" in text


def test_render_uses_the_profile_specific_dimension_label_in_the_gauge():
    view = _view(
        profile="object",
        facet_states={"appearance.material": "covered", "appearance.wear": "missing"},
    )
    text = render(view, CAT, P)
    assert "主體外觀" in text
    assert "人物樣貌" not in text
