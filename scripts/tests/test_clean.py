from pipeline.clean import clean_records, normalize_prompt
from pipeline.nsfw_filter import is_nsfw_text


def _raw(i, prompt, *, neg="lowres", level="None", meta=True, url="https://x/{}.png", likes=5):
    item = {"id": i, "url": url.format(i), "width": 832, "height": 1216, "nsfwLevel": level,
            "baseModel": "Illustrious", "stats": {"likeCount": likes}}
    if meta:
        item["meta"] = {"prompt": prompt, "negativePrompt": neg}
    else:
        item["meta"] = None
    return item


def test_normalize_strips_lora_tokens_and_collapses_separators():
    s = normalize_prompt("masterpiece,  <lora:foo:0.8> 1girl ,, rain  night <lyco:bar:1>")
    assert s == "masterpiece, 1girl, rain night"


def test_nsfw_keyword_filter_is_case_insensitive_and_word_bounded():
    assert is_nsfw_text("1girl, NUDE, beach")
    assert is_nsfw_text("explicit content")
    assert not is_nsfw_text("nudge the camera")  # 'nude' 不能匹配 'nudge'
    assert not is_nsfw_text("1girl, city, rain")


def test_clean_drops_missing_meta_short_nsfw_level_and_keyword_hits():
    raw = [
        _raw(1, "1girl, cyberpunk city street at night, rain, neon signs, looking at viewer"),
        _raw(2, "short", ),
        _raw(3, "1girl, cyberpunk city street at night, rain, neon signs, looking at viewer", meta=False),
        _raw(4, "1girl, cyberpunk city street at night, rain, neon signs, looking at viewer", level="Soft"),
        _raw(5, "1girl, nude, beach, sunset, looking at viewer, masterpiece, best quality"),
    ]
    out = clean_records(raw)
    assert [r["source_id"] for r in out] == [1]


def test_clean_dedupes_by_normalized_prompt_keeping_most_liked():
    p = "1girl, cyberpunk city street at night, rain, neon signs, looking at viewer"
    raw = [_raw(1, p, likes=3), _raw(2, "  " + p.upper() + " ", likes=9)]
    out = clean_records(raw)
    assert [r["source_id"] for r in out] == [2]
    assert out[0]["prompt_hash"] == clean_records([_raw(1, p)])[0]["prompt_hash"]


def test_clean_drops_mostly_non_ascii_prompts():
    raw = [_raw(1, "一個女孩站在雨夜的城市街道上，霓虹燈，看著觀眾，傑作，最高品質")]
    assert clean_records(raw) == []


def test_clean_output_contract():
    raw = [_raw(1, "1girl, cyberpunk city street at night, rain, neon signs, looking at viewer")]
    r = clean_records(raw)[0]
    assert set(r) == {"source_id", "prompt", "negative_prompt", "image_url", "width", "height",
                      "base_model", "like_count", "prompt_hash"}
    assert r["negative_prompt"] == "lowres"
