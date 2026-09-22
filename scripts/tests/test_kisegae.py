from pipeline.config import FACETS_PATH
from pipeline.facets import load_facets
from pipeline.jsonl import read_jsonl, write_jsonl
from pipeline.kisegae import (
    RAW_BASE,
    OutfitPreset,
    build_prompt,
    is_nsfw_outfit,
    run_kisegae,
    scan_descs,
    to_preset,
)
from pipeline.nsfw_filter import is_nsfw_text
from pipeline.structure import snippet_key

CAT = load_facets(FACETS_PATH)
ROW = {
    "source_ref": "kisegae:1741118591102",
    "outfit": "evening gown, halterneck, side slit, crystal footwear",
    "image_url": f"{RAW_BASE}/images/1741118591102.png",
}


def _outfit(**kw):
    base = dict(
        title="水晶晚禮服",
        description="削肩露背的長版晚禮服，搭配開衩裙襬與透明高跟鞋。",
        tags=["evening gown", "halterneck", "side slit"],
        facet_ids=["clothing.upper", "clothing.footwear"],
        prompt_snippet="evening gown, halterneck, side slit, crystal footwear",
    )
    base.update(kw)
    return OutfitPreset(**base)


def _desc(root, image_dir, stem, text):
    d = root / image_dir
    d.mkdir(parents=True, exist_ok=True)
    (d / f"{stem}.png.desc.txt").write_text(text, encoding="utf-8")
    return d / f"{stem}.png.desc.txt"


def test_outfit_filter_allows_clothing_adjectives_that_the_strict_filter_rejects():
    """服裝形容詞對這個資料集過嚴：整套造型不該因為一個 'cleavage' 被丟掉。"""
    for text in ("evening gown, side slit, cleavage",
                 "black dress, see-through, lace trim",
                 "lingerie-inspired camisole, silk"):
        assert is_nsfw_text(text), f"前提錯了，現行清單本來就沒擋：{text}"
        assert not is_nsfw_outfit(text)


def test_outfit_filter_still_blocks_anatomical_and_explicit_tags():
    """放寬的只有服裝形容詞；解剖學與性行為硬詞一律照擋。"""
    for text in ("nude, beach", "covered nipples, dress", "bottomless, cape",
                 "topless, gloves", "bandaid on pussy", "bondage outfit"):
        assert is_nsfw_outfit(text)


def test_scan_descs_builds_source_ref_and_upstream_image_url(tmp_path):
    """圖片不轉存，image_url 指回上游 repo；stem 要剝掉 .png.desc.txt 兩層副檔名。"""
    _desc(tmp_path, "images2", "1741118591102", "evening gown, side slit, crystal footwear")
    [r] = scan_descs(tmp_path)
    assert r["source_ref"] == "kisegae:1741118591102"
    assert r["image_url"] == f"{RAW_BASE}/images2/1741118591102.png"
    assert r["outfit"] == "evening gown, side slit, crystal footwear"


def test_scan_descs_drops_outfits_rejected_by_the_narrowed_filter(tmp_path):
    _desc(tmp_path, "images", "keep", "black dress, cleavage, lace trim")
    _desc(tmp_path, "images", "drop", "black dress, topless")
    assert [r["source_ref"] for r in scan_descs(tmp_path)] == ["kisegae:keep"]


def test_scan_descs_orders_by_source_ref_not_by_directory(tmp_path):
    """按 source_ref 排序（stem 是時間戳，等於依時間），不是按目錄。
    續跑靠 source_ref 比對，順序穩定才好對照兩次跑的結果。"""
    _desc(tmp_path, "images5", "a", "white shirt, pleated skirt")
    _desc(tmp_path, "images", "b", "white shirt, pleated skirt")
    rows = scan_descs(tmp_path)
    assert [r["source_ref"] for r in rows] == ["kisegae:a", "kisegae:b"]
    assert [r["image_url"].rsplit("/", 2)[1] for r in rows] == ["images5", "images"]


def test_scan_descs_ignores_unrelated_files(tmp_path):
    _desc(tmp_path, "images", "ok", "red dress, gold trim")
    (tmp_path / "images" / "notes.txt").write_text("x", encoding="utf-8")
    (tmp_path / "README.md").write_text("x", encoding="utf-8")
    assert [r["source_ref"] for r in scan_descs(tmp_path)] == ["kisegae:ok"]


def test_scan_descs_skips_blank_descs(tmp_path):
    _desc(tmp_path, "images", "blank", "   \n")
    _desc(tmp_path, "images", "ok", "red dress")
    assert [r["source_ref"] for r in scan_descs(tmp_path)] == ["kisegae:ok"]


def test_build_prompt_offers_only_clothing_facets():
    """facet_ids 是檢索的硬門檻，選項越少越不會標錯；非服裝維度根本不該出現在選單裡。"""
    p = build_prompt(ROW, CAT)
    assert ROW["outfit"] in p
    assert "clothing.footwear" in p and "clothing.material" in p
    assert "scene.location" not in p and "camera.shot" not in p
    assert "appearance.hair" not in p


def test_build_prompt_asks_for_traditional_chinese_description():
    """preset_embedding 吃的是繁中 title+description，這是檢索命中率的來源。"""
    p = build_prompt(ROW, CAT)
    assert "繁體中文" in p


def test_to_preset_forces_clothing_category_and_keeps_upstream_image_url():
    r = to_preset(ROW, _outfit(), CAT, seen_snippets=set())
    assert r["category"] == "Clothing"
    assert r["source_ref"] == "kisegae:1741118591102"  # 1 desc = 1 preset，不加 :0 後綴
    assert r["image_url"] == f"{RAW_BASE}/images/1741118591102.png"
    assert r["negative_snippet"] is None  # desc 檔沒有負向詞，不得憑空生


def test_to_preset_drops_facet_ids_outside_the_clothing_dimension():
    r = to_preset(ROW, _outfit(facet_ids=["clothing.upper", "scene.location", "bogus"]),
                  CAT, seen_snippets=set())
    assert r["facet_ids"] == ["clothing.upper"]


def test_to_preset_returns_none_when_no_clothing_facet_survives():
    assert to_preset(ROW, _outfit(facet_ids=["scene.location"]), CAT, seen_snippets=set()) is None


def test_to_preset_returns_none_on_empty_snippet():
    assert to_preset(ROW, _outfit(prompt_snippet="  ,  "), CAT, seen_snippets=set()) is None


def test_to_preset_dedupes_against_snippets_already_in_the_corpus():
    """跨來源去重：civitai 已經有同一組 tag 時不重複收錄。"""
    seen = {snippet_key("evening gown, halterneck, side slit, crystal footwear")}
    assert to_preset(ROW, _outfit(), CAT, seen_snippets=seen) is None


def test_to_preset_lowercases_tags():
    r = to_preset(ROW, _outfit(tags=["Evening Gown", " Side Slit "]), CAT, seen_snippets=set())
    assert r["tags"] == ["evening gown", "side slit"]


class FakeGemini:
    """回傳的 snippet 跟著輸入的 outfit 走，這樣不同 desc 不會被去重collapse 掉。"""

    def __init__(self):
        self.calls = 0

    def _outfit_of(self, prompt):
        return prompt.rstrip().rsplit("\n", 1)[-1]

    def generate_structured(self, prompt, schema, *, temperature=0.2):
        self.calls += 1
        return _outfit(prompt_snippet=self._outfit_of(prompt))


class UnusableGemini(FakeGemini):
    def __init__(self, bad_index):
        super().__init__()
        self.bad_index = bad_index

    def generate_structured(self, prompt, schema, *, temperature=0.2):
        from pipeline.gemini_client import UnusableResponse

        i = self.calls
        self.calls += 1
        if i == self.bad_index:
            raise UnusableResponse("回應沒有文字內容（finish_reason=SAFETY）")
        return _outfit(prompt_snippet=self._outfit_of(prompt))


def _corpus(tmp_path, n):
    for i in range(n):
        _desc(tmp_path, "images", f"{i}", f"outfit number {i}, white shirt")
    return tmp_path / "presets.jsonl"


def test_run_kisegae_writes_one_preset_per_desc(tmp_path):
    out = _corpus(tmp_path, 3)
    g = FakeGemini()
    assert run_kisegae(g, CAT, source_dir=tmp_path, presets_path=out) == 3
    assert g.calls == 3
    rows = list(read_jsonl(out))
    assert [r["source_ref"] for r in rows] == ["kisegae:0", "kisegae:1", "kisegae:2"]
    assert {r["category"] for r in rows} == {"Clothing"}


def test_run_kisegae_resumes_without_recalling_the_llm(tmp_path):
    out = _corpus(tmp_path, 4)
    g1 = FakeGemini()
    run_kisegae(g1, CAT, source_dir=tmp_path, presets_path=out, max_records=2)
    assert g1.calls == 2
    g2 = FakeGemini()
    assert run_kisegae(g2, CAT, source_dir=tmp_path, presets_path=out) == 2
    assert g2.calls == 2  # 只補剩下的兩筆
    assert [r["source_ref"] for r in read_jsonl(out)] == [
        "kisegae:0", "kisegae:1", "kisegae:2", "kisegae:3"]


def test_run_kisegae_appends_without_disturbing_existing_civitai_rows(tmp_path):
    """這支是往 18,977 筆的既有檔案裡 append，不得動到別人的資料。"""
    out = _corpus(tmp_path, 2)
    existing = {"source_ref": "civitai:999:0", "title": "既有", "category": "Scene",
                "description": "d", "tags": ["a"], "facet_ids": ["scene.location"],
                "prompt_snippet": "forest, mist", "negative_snippet": None, "image_url": None}
    write_jsonl(out, [existing])
    run_kisegae(FakeGemini(), CAT, source_dir=tmp_path, presets_path=out)
    rows = list(read_jsonl(out))
    assert rows[0] == existing
    assert [r["source_ref"] for r in rows[1:]] == ["kisegae:0", "kisegae:1"]


def test_run_kisegae_dedupes_against_snippets_already_in_the_file(tmp_path):
    """既有語料已經有同一組 tag 時不重複收錄。"""
    out = _corpus(tmp_path, 2)
    write_jsonl(out, [{"source_ref": "civitai:1:0", "title": "t", "category": "Clothing",
                       "description": "d", "tags": [], "facet_ids": ["clothing.upper"],
                       "prompt_snippet": "outfit number 0, white shirt",
                       "negative_snippet": None, "image_url": None}])
    assert run_kisegae(FakeGemini(), CAT, source_dir=tmp_path, presets_path=out) == 1
    assert [r["source_ref"] for r in read_jsonl(out)][1:] == ["kisegae:1"]


def test_run_kisegae_skips_unusable_response_instead_of_aborting(tmp_path):
    """一筆被模型擋掉不能毀掉整批。"""
    out = _corpus(tmp_path, 4)
    assert run_kisegae(UnusableGemini(bad_index=1), CAT, source_dir=tmp_path, presets_path=out) == 3
    assert [r["source_ref"] for r in read_jsonl(out)] == ["kisegae:0", "kisegae:2", "kisegae:3"]


def test_run_kisegae_retries_a_skipped_record_on_resume(tmp_path):
    out = _corpus(tmp_path, 3)
    run_kisegae(UnusableGemini(bad_index=1), CAT, source_dir=tmp_path, presets_path=out)
    assert [r["source_ref"] for r in read_jsonl(out)] == ["kisegae:0", "kisegae:2"]
    run_kisegae(FakeGemini(), CAT, source_dir=tmp_path, presets_path=out)
    assert sorted(r["source_ref"] for r in read_jsonl(out)) == [
        "kisegae:0", "kisegae:1", "kisegae:2"]


def test_build_prompt_demands_concrete_garment_description():
    """description 要像既有語料那樣講「穿的是什麼」，不能整句都是氛圍與場合詞。
    實跑 20 筆發現：只要求「描述質感、場合與氛圍」，模型會 5/6 筆都用「散發…氣息的…
    適合…」開頭——378 筆共用同一句首會讓向量擠成一團，也對不上使用者逐字描述衣著時
    的子查詢（既有 1,980 筆 Clothing 片段沒有任何一筆這樣寫）。"""
    p = build_prompt(ROW, CAT)
    assert "款式、顏色、材質" in p
    assert "固定開頭" in p
