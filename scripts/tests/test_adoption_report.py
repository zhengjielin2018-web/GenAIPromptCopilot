from adoption_report import Turn, build_report

REC_STYLE = {"dimensions": [{"dimension": "style", "anchored": False, "presetIds": [7, 8]}]}
REC_CLOTHING = {"dimensions": [{"dimension": "clothing", "anchored": True, "presetIds": [1, 2, 3]}]}
ADOPT_CLOTHING = {"presetId": 1, "dimension": "clothing", "take": ["clothing.upper", "clothing.head"],
                  "filled": ["clothing.upper"], "replaced": ["clothing.head"]}


def fin(session, turn, rec=None, adoption=None, origins=None):
    p = {"outcome": "FinalizedOutcome"}
    if rec is not None:
        p["recommendations"] = rec
    if adoption is not None:
        p["adoption"] = adoption
    if origins is not None:
        p["tagOrigins"] = origins
    return Turn(session, turn, p)


def ask(session, turn, rec=None):
    p = {"outcome": "AskOutcome"}
    if rec is not None:
        p["recommendations"] = rec
    return Turn(session, turn, p)


def test_adoption_rate_counts_only_adoptions_in_the_same_session():
    turns = [
        fin("a", 1, rec=REC_CLOTHING), fin("a", 2, adoption=ADOPT_CLOTHING),  # 採用
        fin("b", 1, rec=REC_STYLE), fin("b", 2),                             # 沒採用
        fin("c", 1, rec=REC_STYLE), fin("d", 2, adoption=ADOPT_CLOTHING),    # 別的 session 的採用不算
    ]
    text = build_report(turns)
    assert "定稿輪採用率：1/3（33.3%）" in text
    assert "未對到推薦輪的採用：1" in text  # d 的採用之前沒有卡


def test_adoption_after_a_discuss_turn_links_back_to_the_latest_card():
    turns = [
        ask("a", 1, rec=REC_CLOTHING),
        Turn("a", 2, {"outcome": "MessageOutcome"}),  # 中間討論一輪，卡沒換
        fin("a", 3, adoption=ADOPT_CLOTHING),
    ]
    assert "追問輪採用率：1/1（100.0%）" in build_report(turns)


def test_a_card_adopted_twice_counts_once():
    turns = [
        ask("a", 1, rec=REC_CLOTHING),
        Turn("a", 2, {"outcome": "MessageOutcome", "adoption": ADOPT_CLOTHING}),  # 採用後模型只回話、沒出新卡
        fin("a", 3, adoption=ADOPT_CLOTHING),
    ]
    text = build_report(turns)
    assert "追問輪採用率：1/1（100.0%）" in text
    assert "採用 2 次" in text
    assert "採用時該維度有錨：2/2" in text


def test_zero_denominator_prints_a_dash_not_zero_percent():
    assert "定稿輪採用率：0/0（—）" in build_report([fin("a", 1)])


def test_ask_turns_are_reported_separately():
    turns = [
        ask("a", 1, rec=REC_CLOTHING), fin("a", 2, adoption=ADOPT_CLOTHING),
        ask("b", 1, rec=REC_STYLE), fin("b", 2),
    ]
    text = build_report(turns)
    assert "追問輪採用率：1/2（50.0%）" in text
    assert "定稿輪採用率：0/0" in text


def test_filled_replaced_averages_dimension_counts_and_anchored_split():
    turns = [
        fin("a", 1, rec=REC_CLOTHING), fin("a", 2, adoption=ADOPT_CLOTHING),
        fin("b", 1, rec=REC_STYLE),
        fin("b", 2, adoption={"presetId": 7, "dimension": "style", "take": ["style.genre"],
                              "filled": ["style.genre"], "replaced": []}),
    ]
    text = build_report(turns)
    assert "平均補上 1.0 個 facet、換掉 0.5 個 facet" in text
    assert "clothing：1" in text and "style：1" in text
    assert "採用時該維度有錨：1/2" in text


def test_adopted_tag_share_over_finalized_turns():
    turns = [
        fin("a", 1, origins={"rag": 2, "adopted": 2, "llm": 4, "base": 2}),
        fin("a", 2, origins={"rag": 0, "adopted": 0, "llm": 5, "base": 3}),
    ]
    assert "adopted tag 佔定稿 tag：2/18（11.1%）" in build_report(turns)


def test_no_adoptions_prints_the_notice_but_still_counts_recommendations():
    text = build_report([fin("a", 1, rec=REC_STYLE)])
    assert "尚無採用紀錄" in text
    assert "有推薦的定稿輪：1" in text


def test_empty_input():
    assert "沒有 Turn_Completed 紀錄" in build_report([])
