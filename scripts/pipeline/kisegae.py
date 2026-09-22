"""Kisegaeningyou 服裝語料匯入：desc 檔 → Gemini → Clothing preset。

與 civitai 管線的差別，以及為什麼不共用 structure.py：
- 來源是同一角色的換裝組合，每個 desc 檔就是一套完整造型，不是完整的 SD prompt
  （沒有 style／scene／camera），所以只產 presets、不寫 shared_prompt_histories。
- 資料與圖片皆為第三方所有，見 docs/資料來源.md。圖片不轉存，image_url 指回上游。
"""

from __future__ import annotations

from pathlib import Path

from pydantic import BaseModel, Field

from pipeline.boilerplate import strip_boilerplate
from pipeline.config import FACETS_PATH, STRUCTURED_DIR
from pipeline.facets import FacetCatalog, load_facets
from pipeline.gemini_client import UnusableResponse
from pipeline.jsonl import append_jsonl, existing_keys, read_jsonl
from pipeline.nsfw_filter import NSFW_KEYWORDS, make_matcher
from pipeline.structure import snippet_key

SOURCE_REPO = "https://github.com/hayde0096/Kisegaeningyou"
RAW_BASE = "https://raw.githubusercontent.com/hayde0096/Kisegaeningyou/main"
SOURCE_PREFIX = "kisegae"
CLOTHING_DIMENSION = "clothing"
CATEGORY = "Clothing"
DESC_SUFFIX = ".png.desc.txt"

# 對「同一角色換裝」這種服裝語料而言，NSFW_KEYWORDS 的性暗示形容詞層過嚴：
# 一套造型會因為單一個 cleavage／see-through 被整筆丟掉。放寬的只有這一層，
# 解剖學名詞與性行為詞照擋。NSFW_KEYWORDS 本身不動——civitai 路徑維持原判準。
CLOTHING_SAFE_WORDS: frozenset[str] = frozenset({
    # nsfw_filter.py 裡標為「性暗示形容詞（非解剖學名詞）」的整層
    "sexy", "seductive", "cleavage", "busty", "skimpy", "scantily",
    "voluptuous", "lewd", "suggestive", "provocative",
    # 衣物名詞本身
    "lingerie", "see-through", "underwear only",
})

is_nsfw_outfit = make_matcher(NSFW_KEYWORDS - CLOTHING_SAFE_WORDS)


def scan_descs(source_dir: Path) -> list[dict]:
    """掃 <source_dir>/images*/ 下的 *.png.desc.txt，回傳待處理紀錄。
    圖片不轉存：image_url 直接指回上游 raw URL（見模組 docstring 的授權說明）。
    依 source_ref 排序——stem 是時間戳，等於依產出時間，且讓續跑好對照。"""
    rows: list[dict] = []
    for path in source_dir.glob(f"images*/*{DESC_SUFFIX}"):
        outfit = path.read_text(encoding="utf-8", errors="replace").strip().strip(",")
        if not outfit or is_nsfw_outfit(outfit):
            continue
        stem = path.name[: -len(DESC_SUFFIX)]
        image_dir = path.parent.name
        rows.append({
            "source_ref": f"{SOURCE_PREFIX}:{stem}",
            "outfit": outfit,
            "image_url": f"{RAW_BASE}/{image_dir}/{stem}.png",
        })
    rows.sort(key=lambda r: r["source_ref"])
    return rows


class OutfitPreset(BaseModel):
    title: str = Field(description="繁體中文，10 字內的造型名稱", max_length=100)
    description: str = Field(description="繁體中文，一到兩句模糊自然語言描述，供語意檢索")
    tags: list[str] = Field(description="3-8 個英文小寫標籤")
    facet_ids: list[str] = Field(description="對應的 facet id，只能用清單裡的")
    prompt_snippet: str = Field(description="只保留服裝相關的英文 tag，逗號分隔")


PROMPT_TEMPLATE = """你是生圖提示詞知識庫的整理員。給你一套動漫角色的服裝組合——它來自同一個角色的
換裝集，所以人物長相是固定的、不是這裡的重點，衣服才是。請整理成一筆可重用的「服裝」知識片段：

- title：繁體中文，10 字內，要像個造型名稱（例：哥德蘿莉黑洋裝），不要用「造型一」這種編號
- description：繁體中文一到兩句，講「穿的是什麼」——款式、顏色、材質、關鍵配件，像在跟人
  描述這套衣服長什麼樣（例：「穿著綴有金色刺繡的華麗紅色長禮服，裙襬散落在地上」）。
  可以帶一點風格或場合的形容，但不要整句都是氛圍詞，也不要逐字翻譯 tag 清單。
  每一筆都要自己的寫法，**不要用固定開頭**（像每筆都以「散發…氣息」開場）——
  這句話會拿去算檢索向量，句型雷同會讓所有片段擠在一起
- tags：3-8 個英文小寫標籤
- facet_ids：只能從下方清單選，選最貼切的 1-4 個
- prompt_snippet：從原始 tag 裡「只」擷取服裝、配件、鞋履、材質相關的，逗號分隔，不要改寫、
  不要翻譯、不要補充。武器、場景、畫風、身體部位、動作一律丟掉（例：holding scythe、
  machinery factory、warhammer 40k、navel、looking at viewer 都不要）

Facet 清單（本任務只開放服裝維度）：
{facets}

原始服裝 tag：
{outfit}
"""


def _clothing_listing(catalog: FacetCatalog) -> str:
    return "\n".join(
        f"  - {f.id}：{f.label}（例：{f.hint}）"
        for f in catalog.facets.values()
        if f.dimension == CLOTHING_DIMENSION
    )


def build_prompt(row: dict, catalog: FacetCatalog) -> str:
    return PROMPT_TEMPLATE.format(facets=_clothing_listing(catalog), outfit=row["outfit"])


def to_preset(
    row: dict, result: OutfitPreset, catalog: FacetCatalog, seen_snippets: set[str]
) -> dict | None:
    """一個 desc = 一筆 preset，所以 source_ref 直接沿用、不加 :idx 後綴。
    回 None 代表這筆不收錄（無有效服裝 facet、片段空掉、或整組 tag 已在語料庫裡）。"""
    snippet = strip_boilerplate(result.prompt_snippet).strip(" ,")
    facet_ids = [
        f for f in result.facet_ids
        if f in catalog.all_ids and catalog.dimension_of(f) == CLOTHING_DIMENSION
    ]
    if not snippet or not facet_ids:
        return None
    key = snippet_key(snippet)
    if key in seen_snippets:
        return None
    seen_snippets.add(key)
    return {
        "source_ref": row["source_ref"],
        "title": result.title.strip()[:100],
        "category": CATEGORY,
        "description": result.description.strip(),
        "tags": [t.strip().lower() for t in result.tags if t.strip()],
        "facet_ids": facet_ids,
        "prompt_snippet": snippet,
        "negative_snippet": None,  # desc 檔沒有負向詞，不得憑空生
        "image_url": row["image_url"],
    }


def run_kisegae(
    client,
    catalog: FacetCatalog,
    *,
    source_dir: Path,
    presets_path: Path,
    max_records: int | None = None,
) -> int:
    """掃描 → 每筆一次 Gemini → append 到 presets_path。

    presets_path 是 civitai 管線也在寫的那個檔：只 append、不重寫，且 seen_snippets
    由整份檔案（含 civitai 片段）建立，所以去重是跨來源的。
    續跑以 source_ref 比對；被 UnusableResponse 跳過的那筆不算完成，下次會再試。
    """
    done = existing_keys(presets_path, "source_ref")
    seen_snippets = {snippet_key(p["prompt_snippet"]) for p in read_jsonl(presets_path)}

    written = 0
    for row in scan_descs(source_dir):
        if row["source_ref"] in done:
            continue
        if max_records is not None and written >= max_records:
            break
        try:
            result = client.generate_structured(build_prompt(row, catalog), OutfitPreset)
        except UnusableResponse:
            continue
        preset = to_preset(row, result, catalog, seen_snippets)
        if preset is None:
            continue
        append_jsonl(presets_path, preset)
        written += 1
    return written


def main(argv: list[str] | None = None) -> None:
    import argparse

    ap = argparse.ArgumentParser(
        description="一次性匯入 Kisegaeningyou 服裝語料（只產 presets，不寫 histories）"
    )
    ap.add_argument("--source-dir", type=Path, required=True,
                    help=f"{SOURCE_REPO} 的本機路徑（內含 images*/ 目錄）")
    ap.add_argument("--max-records", type=int, default=None,
                    help="本次最多寫出幾筆（算成功寫出的筆數，不算跳過的）")
    ap.add_argument("--presets-path", type=Path, default=STRUCTURED_DIR / "presets.jsonl")
    args = ap.parse_args(argv)

    from pipeline.gemini_client import default_client

    catalog = load_facets(FACETS_PATH)
    n = run_kisegae(default_client(), catalog, source_dir=args.source_dir,
                    presets_path=args.presets_path, max_records=args.max_records)
    print(f"kisegae: {n} presets written to {args.presets_path}")


if __name__ == "__main__":
    main()
