import contextlib
import json  # 檔頭
from types import SimpleNamespace  # 檔頭

import demo  # 檔頭（用 module 名稱，方便 monkeypatch）
from demo import (
    AnalysisResult,
    AssemblyResult,
    BorrowedFrom,
    DimensionSuggestion,
    FacetAssessment,
    SuggestionOption,
    build_analysis_prompt,
    build_assembly_prompt,
    facet_state_map,
    format_candidates,
    format_facet_states,
    missing_labels_by_dimension,
    validate_borrowed,
    validate_suggestions,
)
from demo_render import Palette  # 檔頭
from pipeline.config import FACETS_PATH
from pipeline.facets import load_facets
from pipeline.gemini_client import GeminiClient  # 檔頭
from pipeline.ratelimit import RateLimiter  # 檔頭
from pipeline.retrieval import Candidate, DimensionHits, band  # 檔頭

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


# ---------- prompt ----------


def test_analysis_prompt_lists_facets_and_states_the_query_rules():
    text = build_analysis_prompt("夜晚的湖畔", CAT)
    assert "scene.location" in text and "夜晚的湖畔" in text
    assert "逐字使用使用者的原話" in text  # covered 維度 1 句
    assert "對比" in text and "2 句" in text  # missing 維度 2 句對比


def test_format_facet_states_only_lists_the_profile_facets_grouped_by_dimension():
    states = {"scene.location": "covered", "scene.weather": "missing", "style.genre": "missing"}
    text = format_facet_states(states, CAT, "landscape")
    assert "scene.location（地點類型）：covered" in text
    assert "scene.weather（天氣氛圍）：missing" in text
    assert "[scene] 場景" in text
    assert "clothing" not in text


def test_format_candidates_marks_band_usage_and_facet_coverage():
    a = Candidate(
        preset={"id": 144, "title": "雙馬尾少女", "category": "Appearance",
                "facet_ids": ["appearance.hair", "appearance.face"], "tags": [],
                "prompt_snippet": "1girl, twintails", "negative_snippet": None},
        dimension="appearance", dist=0.19, band="高", grounded=True,
        facet_coverage={"appearance.hair": "covered", "appearance.face": "missing"},
    )
    b = Candidate(
        preset={"id": 900, "title": "新海誠動畫風", "category": "Style", "facet_ids": ["style.reference"], "tags": [],
                "prompt_snippet": "(Makoto Shinkai Style:1.4)", "negative_snippet": "lowres"},
        dimension="style", dist=0.234, band="高", grounded=False, facet_coverage={"style.reference": "missing"},
    )
    text = format_candidates([a, b])
    assert "id=144" in text and "〈雙馬尾少女〉" in text and "相似度：高（0.190）" in text
    assert "可借入提示詞" in text and "僅供建議" in text
    assert "appearance.hair=covered" in text and "appearance.face=missing" in text
    assert "negative: lowres" in text and "negative: (無)" in text


def test_assembly_prompt_contains_every_required_rule_and_all_blocks():
    text = build_assembly_prompt("夜晚的湖畔", "portrait", {"scene.location": "covered"}, [], [], CAT)
    needles = (
        "完整", "複合", "split-color hair", "不要自行發明", "僅供建議", "低", "borrowed", "suggestions", "2–3",
    )
    for needle in needles:
        assert needle in text, needle
    assert "題材：portrait" in text and "夜晚的湖畔" in text
    assert "（無）" in text  # 候選與相似作品都空


# ---------- 端到端 ----------


class ScriptedModels:
    """generate_content 依序回傳預先寫好的 JSON；embed_content 回固定 2 維向量。"""

    def __init__(self, replies: list[str]):
        self.replies = list(replies)
        self.generate_calls: list[str] = []
        self.embed_calls: list[list[str]] = []

    def generate_content(self, *, model, contents, config):
        self.generate_calls.append(contents)
        return SimpleNamespace(text=self.replies.pop(0))

    def embed_content(self, *, model, contents, config):
        self.embed_calls.append(list(contents))
        return SimpleNamespace(embeddings=[SimpleNamespace(values=[1.0, 0.0]) for _ in contents])


def _client(models):
    return GeminiClient(SimpleNamespace(models=models), structure_model="s", embedding_model="e", dimensions=2,
                        limiter=RateLimiter(0, sleep=lambda s: None), sleep=lambda s: None)


QUERY = "夜晚的湖畔，一個紫色雙馬尾的少女"

ANALYSIS_JSON = json.dumps({
    "subject_profile": "portrait",
    "facets": [
        {"facet_id": "appearance.hair", "state": "covered"},
        {"facet_id": "scene.location", "state": "covered"},
    ],
    "queries": [
        {"dimension": "appearance", "query": "紫色雙馬尾的少女"},
        {"dimension": "scene", "query": "夜晚的湖畔"},
        {"dimension": "style", "query": "動漫插畫"},
        {"dimension": "style", "query": "寫實攝影"},
        {"dimension": "clothing", "query": "休閒穿搭"},  # clothing 全 missing，只給 1 句也合法
    ],
}, ensure_ascii=False)

ASSEMBLY_JSON = json.dumps({
    "positive_prompt": "masterpiece, 1girl, purple hair, twintails, night lakeside",
    "negative_prompt": "bad anatomy, lowres",
    "borrowed": [
        {"preset_id": 144, "tags": ["twintails", "purple hair"]},
        {"preset_id": 900, "tags": ["anime"]},
    ],
    "suggestions": [
        {"dimension": "style", "missing_labels": ["whatever"],
         "options": [{"label": "新海誠風", "tags": "Makoto Shinkai Style", "preset_id": 900}]},
    ],
}, ensure_ascii=False)


def _fake_retrieve_presets(conn, queries, vectors, catalog, profile):
    assert conn is None and len(queries) == len(vectors)
    out = []
    for q in queries:
        if q.dimension == "appearance":
            hits = [Candidate(preset={"id": 144, "title": "雙馬尾少女", "category": "Appearance",
                                      "facet_ids": ["appearance.hair"], "tags": [],
                                      "prompt_snippet": "1girl, twintails, grey eyes", "negative_snippet": None},
                              dimension="appearance", dist=0.19, band="高", grounded=q.grounded)]
        elif q.dimension == "style":
            hits = [Candidate(preset={"id": 900, "title": "新海誠動畫風", "category": "Style",
                                      "facet_ids": ["style.reference"], "tags": [],
                                      "prompt_snippet": "anime, Makoto Shinkai Style", "negative_snippet": None},
                              dimension="style", dist=0.23, band="高", grounded=q.grounded)]
        else:
            hits = []
        out.append(DimensionHits(q, 100, hits))
    return out


def test_run_once_end_to_end(monkeypatch, capsys):
    monkeypatch.setattr(demo, "retrieve_presets", _fake_retrieve_presets)
    monkeypatch.setattr(demo, "retrieve_histories", lambda conn, qvec, profile, k: [])
    models = ScriptedModels([ANALYSIS_JSON, ASSEMBLY_JSON])
    args = SimpleNamespace(k_covered=5, k_missing=3, top_histories=3, verbose=False)

    demo.run_once(QUERY, None, _client(models), CAT, args, Palette(False))
    out = capsys.readouterr().out

    # 兩段依序呼叫；embed 只呼叫一次，內容是整句 + 整理後的子查詢
    assert len(models.generate_calls) == 2
    assert models.embed_calls == [[QUERY, "紫色雙馬尾的少女", "夜晚的湖畔", "動漫插畫", "寫實攝影", "休閒穿搭"]]
    # ① 的 facet 狀態原封到畫面：人物樣貌 1/5、場景 1/6
    assert "1/5" in out and "1/6" in out
    # ③ 的候選清單標了用途
    assert "id=144" in models.generate_calls[1] and "可借入提示詞" in models.generate_calls[1]
    assert "id=900" in models.generate_calls[1] and "僅供建議" in models.generate_calls[1]
    # 驗證結果：twintails 借入成立；purple hair 來源不符；900 是未描述維度
    assert "→ 借入 twintails" in out
    assert '來源不符：id 144〈雙馬尾少女〉沒有 "purple hair"' in out
    assert "id 900〈新海誠動畫風〉屬於使用者未描述的維度" in out
    # 建議：missing_labels 被 ① 的真相覆寫
    assert "風格（缺：藝術流派／媒材、參照畫師或作品、渲染引擎／技術風格詞、色調傾向）" in out
    assert "A. 新海誠風" in out and "Makoto Shinkai Style" in out and "〈新海誠動畫風〉" in out
    # 提示詞原封輸出
    assert "purple hair" in out and "night lakeside" in out


def test_main_parses_new_flags(monkeypatch):
    captured = {}

    def fake_run_once(query, conn, client, catalog, args, p):
        captured.update(query=query, args=args)

    monkeypatch.setattr(demo, "run_once", fake_run_once)
    monkeypatch.setattr(demo, "connect", lambda: contextlib.nullcontext(None))  # 檔頭需 import contextlib
    monkeypatch.setattr("pipeline.gemini_client.default_client", lambda: object())
    demo.main(["山上的日出", "--k-covered", "7", "--k-missing", "2", "--top-histories", "1", "--verbose", "--no-color"])
    assert captured["query"] == "山上的日出"
    a = captured["args"]
    assert (a.k_covered, a.k_missing, a.top_histories, a.verbose) == (7, 2, 1, True)
