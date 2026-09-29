"""tag 正規化要與 C# TagAttribution.Normalize 逐字相同：同一組 SHARED_CASES 在
src/PromptCopilot.Api.Tests/Sessions/TagAttributionTests.cs 也跑一次。改一邊就要改另一邊。"""

from __future__ import annotations

import pytest

from pipeline.tags import SHARED_CASES, embedding_text, normalize_tag, tag_key


@pytest.mark.parametrize(("raw", "expected"), SHARED_CASES)
def test_normalize_matches_the_shared_cases(raw, expected):
    assert normalize_tag(raw) == expected


def test_shared_cases_cover_the_rules_the_csharp_side_implements():
    raw = dict(SHARED_CASES)
    assert raw["(Sandals:1.2)"] == "sandals"                 # 權重＋括號
    assert raw["platform_sandals"] == "platform sandals"      # 底線
    assert raw["((tag))"] == "tag"                            # 多層括號
    assert raw["(a) (b)"] == "(a) (b)"                        # 頭尾括號但不是同一對
    assert raw["  Long   Hair "] == "long hair"               # 連續空白
    assert raw["(masterpiece:1.2)"] == "masterpiece"
    assert raw["tag:0.8"] == "tag"                            # 沒括號的權重
    assert raw["( :1.2)"] == ""                               # 剝完什麼都不剩


def test_embedding_text_keeps_order_drops_empty_and_dedups_after_normalization():
    assert embedding_text(["Sandals", "(sandals:1.2)", "white_socks", "", "( :1.2)"]) == "sandals, white socks"


def test_tag_key_ignores_order_and_duplicates():
    assert tag_key(["sunset", "golden hour"]) == tag_key(["golden hour", "sunset"]) == "golden hour, sunset"
    assert tag_key(["Sandals", "(sandals:1.2)"]) == "sandals"


def test_tag_key_and_text_dedup_after_normalization():
    # Review Focus 2：只差大小寫或權重的 tag 算同一個
    tags = ["Sandals", "(sandals:1.2)", "sandals"]
    assert tag_key(tags) == "sandals" and embedding_text(tags) == "sandals"


def test_empty_input_gives_empty_strings():
    assert embedding_text([]) == "" and tag_key([]) == ""
    assert embedding_text(["( :1.2)"]) == "" and tag_key(["( :1.2)"]) == ""
