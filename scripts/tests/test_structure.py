from pipeline.boilerplate import strip_boilerplate as _strip_boilerplate
from pipeline.config import FACETS_PATH
from pipeline.facets import load_facets
from pipeline.jsonl import read_jsonl, write_jsonl
from pipeline.structure import (
    PresetOut,
    StructuredRecord,
    build_prompt,
    run_structure,
    to_outputs,
)

CAT = load_facets(FACETS_PATH)
REC = {"source_id": 12345, "prompt": "1girl, cyberpunk city, rain, neon, looking at viewer, masterpiece",
       "negative_prompt": "lowres, bad anatomy", "image_url": "https://x/1.png", "width": 832,
       "height": 1216, "base_model": "Illustrious", "like_count": 10, "prompt_hash": "h"}


def _result(**kw):
    base = dict(
        user_intent="雨夜霓虹城市裡看著鏡頭的少女",
        subject_profile="portrait",
        presets=[
            PresetOut(title="賽博龐克雨夜", category="Scene", description="昏暗雨夜城市與霓虹",
                      tags=["cyberpunk", "rain", "neon"],
                      facet_ids=["scene.location", "scene.weather", "not.a.facet"],
                      prompt_snippet="cyberpunk city, rain, neon", negative_snippet=None),
            PresetOut(title="空的", category="Style", description="x", tags=[], facet_ids=[],
                      prompt_snippet="   ", negative_snippet=None),
        ],
    )
    base.update(kw)
    return StructuredRecord(**base)


def test_build_prompt_contains_record_and_facet_listing():
    p = build_prompt(REC, CAT)
    assert REC["prompt"] in p and REC["negative_prompt"] in p
    assert "scene.weather" in p and "clothing.footwear" in p
    assert "繁體中文" in p
    assert "score_9 / score_8_up / score_7_up" in p


def test_to_outputs_filters_invalid_facets_and_empty_snippets():
    hist, presets = to_outputs(REC, _result(), CAT, seen_snippets=set())
    assert hist["source_ref"] == "civitai:12345"
    assert hist["subject_profile"] == "portrait"
    assert hist["image_url"] == "https://x/1.png"
    assert len(presets) == 1
    assert presets[0]["source_ref"] == "civitai:12345:0"
    assert presets[0]["facet_ids"] == ["scene.location", "scene.weather"]


def test_to_outputs_strips_boilerplate_from_history_prompt_columns():
    hist, _ = to_outputs(REC, _result(), CAT, seen_snippets=set())
    # REC's prompt ends in ", masterpiece"; REC's negative_prompt is "lowres, bad anatomy" (all boilerplate).
    assert hist["positive_prompt"] == "1girl, cyberpunk city, rain, neon, looking at viewer"
    assert hist["negative_prompt"] == ""


def test_to_outputs_falls_back_to_original_prompt_when_stripping_would_empty_it():
    boilerplate_only = {**REC, "source_id": 99, "prompt": "masterpiece, best quality, score_9, score_8_up"}
    hist, _ = to_outputs(boilerplate_only, _result(), CAT, seen_snippets=set())
    assert hist["positive_prompt"] == boilerplate_only["prompt"]


def test_to_outputs_dedupes_snippets_across_records():
    seen: set[str] = set()
    _, first = to_outputs(REC, _result(), CAT, seen_snippets=seen)
    _, second = to_outputs({**REC, "source_id": 2}, _result(), CAT, seen_snippets=seen)
    assert len(first) == 1 and second == []


def test_to_outputs_drops_preset_with_no_valid_facets():
    r = _result(presets=[PresetOut(title="t", category="Scene", description="d", tags=["a"],
                                   facet_ids=["bogus"], prompt_snippet="ok snippet",
                                   negative_snippet=None)])
    _, presets = to_outputs(REC, r, CAT, seen_snippets=set())
    assert presets == []


class FakeGemini:
    def __init__(self):
        self.calls = 0

    def generate_structured(self, prompt, schema, *, temperature=0.2):
        self.calls += 1
        return _result()


def test_run_structure_resumes_and_respects_max(tmp_path):
    inp = tmp_path / "records.jsonl"
    write_jsonl(inp, [REC, {**REC, "source_id": 2}, {**REC, "source_id": 3}])
    h, p = tmp_path / "h.jsonl", tmp_path / "p.jsonl"
    g = FakeGemini()
    assert run_structure(g, CAT, in_path=inp, histories_path=h, presets_path=p, max_records=2) == (2, 1)
    assert g.calls == 2
    assert run_structure(g, CAT, in_path=inp, histories_path=h, presets_path=p) == (1, 0)
    assert g.calls == 3
    assert [r["source_ref"] for r in read_jsonl(h)] == ["civitai:12345", "civitai:2", "civitai:3"]


def test_strip_boilerplate_removes_score_and_artifact_tags_but_keeps_style_negatives():
    out = _strip_boilerplate(
        "score_6, score_5, score_4, censored, furry, child, kid, chibi, 3d, "
        "aidxlv05_neg, signature, watermark, subtitle"
    )
    assert out == "censored, furry, child, kid, chibi, 3d"


def test_strip_boilerplate_keeps_multiword_tags_containing_a_boilerplate_word():
    assert _strip_boilerplate("neon text, glowing signage") == "neon text, glowing signage"


def test_strip_boilerplate_empties_an_all_boilerplate_snippet():
    assert _strip_boilerplate("score_9, score_8_up, masterpiece, best quality") == ""


def test_to_outputs_nulls_a_fully_boilerplate_negative_snippet():
    result = _result()
    result.presets[0].negative_snippet = "score_6, score_5, score_4"
    _, presets = to_outputs(REC, result, CAT, seen_snippets=set())
    assert presets[0]["negative_snippet"] is None


def test_to_outputs_keeps_style_relevant_negatives():
    result = _result()
    result.presets[0].negative_snippet = "score_6, realistic, 3d"
    _, presets = to_outputs(REC, result, CAT, seen_snippets=set())
    assert presets[0]["negative_snippet"] == "realistic, 3d"


def test_to_outputs_drops_preset_whose_snippet_is_only_boilerplate():
    result = _result()
    result.presets[0].prompt_snippet = "score_9, score_8_up, masterpiece"
    _, presets = to_outputs(REC, result, CAT, seen_snippets=set())
    assert presets == []


def test_strip_boilerplate_sees_through_sd_weight_syntax():
    assert _strip_boilerplate("(worst quality, bad quality, low quality:1.2), old") == "old"
    assert _strip_boilerplate("(((masterpiece))), (lowres:1.2), forest") == "forest"
    assert _strip_boilerplate("{worst quality, normal quality:2, rain") == "rain"


def test_strip_boilerplate_preserves_weighted_non_boilerplate_verbatim():
    assert _strip_boilerplate("(rella:1.2), (stylized), (enhanced textures)") == (
        "(rella:1.2), (stylized), (enhanced textures)"
    )


def test_strip_boilerplate_treats_break_as_a_delimiter():
    assert _strip_boilerplate("score_7_up BREAK, robot, mecha") == "robot, mecha"
    assert _strip_boilerplate("masterpiece BREAK detailed background") == "detailed background"


def test_strip_boilerplate_keeps_bad_as_an_ordinary_word():
    assert _strip_boilerplate("bad guy, city street") == "bad guy, city street"
