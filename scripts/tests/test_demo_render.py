from demo_render import (
    display_width,
    group_by_dimension,
    pad,
    render_dimension_row,
    wrap_tags,
)
from pipeline.config import FACETS_PATH
from pipeline.facets import load_facets

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
