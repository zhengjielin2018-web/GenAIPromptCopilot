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
    assert "有錨列採用率：1/1（100.0%）" in text  # 同一列採用兩次只算一次


def test_zero_denominator_prints_a_dash_not_zero_percent():
    text = build_report([fin("a", 1)])
    assert "定稿輪採用率：0/0（—）" in text
    assert "有錨列採用率：0/0（—）" in text


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
    assert "有錨列採用率：1/1（100.0%）" in text
    assert "無錨列採用率：1/1（100.0%）" in text


def test_anchored_and_unanchored_row_rates_split_by_the_adopted_row():
    rec = {"dimensions": [
        {"dimension": "clothing", "anchored": True, "presetIds": [1, 2, 3]},
        {"dimension": "style", "anchored": False, "presetIds": [7, 8]},
    ]}
    turns = [fin("a", 1, rec=rec), fin("a", 2, adoption=ADOPT_CLOTHING)]
    text = build_report(turns)
    assert "有錨列採用率：1/1（100.0%）" in text
    assert "無錨列採用率：0/1（0.0%）" in text
    assert "採用時該維度有錨：1/1" in text


def test_row_rates_count_shown_rows_even_without_adoptions():
    text = build_report([fin("a", 1, rec=REC_STYLE), ask("b", 1, rec=REC_CLOTHING)])
    assert "有錨列採用率：0/1（0.0%）" in text
    assert "無錨列採用率：0/1（0.0%）" in text
    assert "尚無採用紀錄" in text


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


SLATE = {"dimensions": [{"dimension": "clothing", "anchored": False, "similar": False, "presetIds": [1, 2, 3], "batch": 1, "sets": [
    {"presetId": 1, "reason": "anchored", "rank": 0, "prob": 1},
    {"presetId": 2, "reason": "anchored", "rank": 4, "prob": 0.3},
    {"presetId": 3, "reason": "explore", "rank": 0, "prob": 0.5},
]}]}


def adopt(pid, replaced=(), batch=None):
    a = {"presetId": pid, "dimension": "clothing", "take": ["clothing.footwear"], "filled": [], "replaced": list(replaced)}
    if batch is not None:
        a["batch"] = batch
    return a


def nxt(session, turn, batch, sets):
    return Turn(session, turn, {"dimension": "clothing", "batch": batch, "sets": sets}, "Recommendations_Next")


def test_slate_section_counts_reasons_and_replace_rate():
    text = build_report([fin("a", 1, rec=SLATE), fin("a", 2, adoption=adopt(3, replaced=["clothing.footwear"]))])
    assert "## 定稿卡推薦組法" in text
    assert "| 換個搭法 | 1 | 1/1（100.0%） | 1/1（100.0%） | 1.0 |" in text
    assert "| 含你講的 | 2 | 0/2（0.0%） | 0/0（—） | — |" in text


def test_next_batch_sets_join_the_row_and_adoption_with_batch_links_there():
    turns = [fin("a", 1, rec=SLATE), nxt("a", 1, 2, [{"presetId": 9, "reason": "query", "rank": 7, "prob": 0.1}]),
             fin("a", 2, adoption=adopt(9, batch=2))]
    text = build_report(turns)
    assert "- 定稿排按過換一批：1/1（100.0%）；平均每排按 1.0 次" in text
    assert "- 採用來自第 2 批以後：1/1（100.0%）" in text
    assert "| 5–9 | 1 | 1 |" in text


def test_adoption_with_batch_links_to_that_batch_even_if_the_preset_repeats():
    # Review Focus 1：看過的會被權重輪回來，同一個 preset 可能在兩批都出現
    turns = [fin("a", 1, rec=SLATE), nxt("a", 1, 2, [{"presetId": 1, "reason": "anchored", "rank": 0, "prob": 1}]),
             fin("a", 2, adoption=adopt(1, batch=1))]
    text = build_report(turns)
    assert "- 採用來自第 2 批以後：0/1（0.0%）" in text


def test_old_rows_without_sets_infer_reason_from_row_flags():
    # Review Focus 5
    text = build_report([fin("a", 1, rec=REC_CLOTHING), fin("a", 2, adoption=ADOPT_CLOTHING)])
    assert "| 含你講的 | 3 | 1/3（33.3%） | 1/1（100.0%） | 1.0 |" in text
    assert "| 0 | 0 | 0 |" in text                                   # 舊資料沒有名次，不進分桶


def test_explore_set_at_rank_0_does_not_enter_the_original_rank_bucket():
    # Final-review finding 2：探索位的 rank 是跟相關位的差異排名，不是原名次；SLATE 的 explore 套剛好也是 rank 0，
    # 混進去分桶「0」會虛報成 2（跟相關位那套疊在一起），只算相關位才是 1
    text = build_report([fin("a", 1, rec=SLATE)])
    assert "| 0 | 1 | 0 |" in text
    assert "| 1–4 | 1 | 0 |" in text


def test_no_final_card_recommendations_means_no_slate_section():
    assert "定稿卡推薦組法" not in build_report([ask("a", 1, rec=REC_CLOTHING)])


def test_empty_next_batch_adds_no_sets_and_no_press():
    # 控制器裁決：空批次（「沒有更多了」）也會被 audit，sets 是空陣列——不能算進出現數，也不能推高換一批
    turns = [fin("a", 1, rec=SLATE), nxt("a", 1, 2, []), fin("a", 2, adoption=adopt(3))]
    text = build_report(turns)
    assert "| 換個搭法 | 1 | 1/1（100.0%）" in text
    assert "- 定稿排按過換一批：0/1（0.0%）；平均每排按 0.0 次" in text
