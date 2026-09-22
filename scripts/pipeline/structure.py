"""階段 3：clean → structured。每筆 prompt 由 Gemini 產出
(a) 一筆完整紀錄（繁中 user_intent + profile）餵 RAG 1，
(b) 多筆片段（繁中 title/description + facet_ids + 英文 snippet）餵 RAG 2。"""

from __future__ import annotations

import argparse
import hashlib
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path
from typing import Literal

from pydantic import BaseModel, Field

from pipeline.boilerplate import strip_boilerplate as _strip_boilerplate
from pipeline.config import CLEAN_DIR, FACETS_PATH, STRUCTURED_DIR
from pipeline.facets import FacetCatalog, load_facets
from pipeline.jsonl import append_jsonl, existing_keys, read_jsonl

HISTORIES_PATH = STRUCTURED_DIR / "histories.jsonl"
PRESETS_PATH = STRUCTURED_DIR / "presets.jsonl"

Profile = Literal["portrait", "landscape", "object", "vehicle"]
Category = Literal["Style", "Scene", "Camera", "Appearance", "Pose", "Clothing", "Combined"]


class PresetOut(BaseModel):
    title: str = Field(description="繁體中文，10 字內", max_length=100)
    category: Category
    description: str = Field(description="繁體中文，一到兩句模糊自然語言描述，供語意檢索")
    tags: list[str] = Field(description="3-8 個英文小寫標籤")
    facet_ids: list[str] = Field(description="對應的 facet id，只能用清單裡的")
    prompt_snippet: str = Field(description="從原 prompt 擷取的英文片段，逗號分隔")
    negative_snippet: str | None = Field(description="與此片段風格相關的英文負向詞；無則 null")


class StructuredRecord(BaseModel):
    user_intent: str = Field(description="繁體中文，一到兩句話描述這張圖的需求，像使用者會說的話")
    subject_profile: Profile
    presets: list[PresetOut] = Field(description="1-6 個可重用片段")


PROMPT_TEMPLATE = """你是生圖提示詞知識庫的整理員。給你一組 Stable Diffusion 風格的英文 prompt，請：

1. 用繁體中文寫一到兩句 user_intent：像一個使用者會對助理說的需求描述（不要逐字翻譯 tag）。
2. 判斷 subject_profile：portrait（有人物）/ landscape（風景）/ object（靜物）/ vehicle（載具）。
3. 從 prompt 擷取 1-6 個「可在其他圖重用」的片段（presets）。每個片段：
   - title：繁體中文，10 字內
   - category：Style / Scene / Camera / Appearance / Pose / Clothing / Combined
   - description：繁體中文一到兩句模糊描述，要讓「昏暗雨夜的科幻城市」這種口語能搜到
   - tags：3-8 個英文小寫標籤
   - facet_ids：只能從下方清單選，選最貼切的 1-4 個
   - prompt_snippet：從原 prompt 擷取的英文 tag，逗號分隔；不要改寫、不要翻譯
   - negative_snippet：若此片段有風格專屬負向詞（如動漫風的 "realistic, 3d"）就給，否則 null
   不要把 masterpiece / best quality / highly detailed / lowres / bad anatomy / score_9 / score_8_up \
/ score_7_up 這類通用畫質詞、分數標籤或通用負向詞當成片段。
   不要把 <lora:...> 或特定模型名稱當成片段。

Facet 清單：
{facets}

原始 prompt：
{prompt}

原始 negative prompt：
{negative}
"""


def build_prompt(record: dict, catalog: FacetCatalog) -> str:
    return PROMPT_TEMPLATE.format(
        facets=catalog.prompt_listing(),
        prompt=record["prompt"],
        negative=record.get("negative_prompt") or "(無)",
    )


def _snippet_key(snippet: str) -> str:
    norm = ", ".join(t.strip().lower() for t in snippet.split(",") if t.strip())
    return hashlib.sha1(norm.encode("utf-8")).hexdigest()


def to_outputs(
    record: dict, result: StructuredRecord, catalog: FacetCatalog, seen_snippets: set[str]
) -> tuple[dict, list[dict]]:
    ref = f"civitai:{record['source_id']}"
    stripped_positive = _strip_boilerplate(record["prompt"]).strip(" ,")
    history = {
        "source_ref": ref,
        "user_intent": result.user_intent.strip(),
        "positive_prompt": stripped_positive or record["prompt"],
        "negative_prompt": _strip_boilerplate(record.get("negative_prompt") or "").strip(" ,"),
        "subject_profile": result.subject_profile,
        "image_url": record.get("image_url"),
    }
    presets: list[dict] = []
    for p in result.presets:
        snippet = _strip_boilerplate(p.prompt_snippet).strip(" ,")
        facet_ids = [f for f in p.facet_ids if f in catalog.all_ids]
        if not snippet or not facet_ids:
            continue
        key = _snippet_key(snippet)
        if key in seen_snippets:
            continue
        seen_snippets.add(key)
        presets.append({
            "source_ref": f"{ref}:{len(presets)}",
            "title": p.title.strip()[:100],
            "category": p.category,
            "description": p.description.strip(),
            "tags": [t.strip().lower() for t in p.tags if t.strip()],
            "facet_ids": facet_ids,
            "prompt_snippet": snippet,
            "negative_snippet": _strip_boilerplate(p.negative_snippet or "").strip(" ,") or None,
            "image_url": record.get("image_url"),
        })
    return history, presets


def run_structure(
    client,
    catalog: FacetCatalog,
    *,
    in_path: Path,
    histories_path: Path,
    presets_path: Path,
    max_records: int | None = None,
    concurrency: int = 1,
) -> tuple[int, int]:
    """concurrency > 1 時只有「呼叫 LLM」並行；寫檔與 seen_snippets 去重一律留在
    主執行緒依序執行，所以 _emit 的寫入順序不變、跨筆去重也不會有 race。
    代價：一個 batch 中途拋例外時，該 batch 內已付費但尚未寫出的結果會作廢，
    續跑時重新產生（最多損失 concurrency 筆）。"""
    done = existing_keys(histories_path, "source_ref")
    seen_snippets = {_snippet_key(p["prompt_snippet"]) for p in read_jsonl(presets_path)}

    pending: list[dict] = []
    for record in read_jsonl(in_path):
        if f"civitai:{record['source_id']}" in done:
            continue
        pending.append(record)
        if max_records is not None and len(pending) >= max_records:
            break

    n_hist = n_presets = 0

    def _emit(record: dict, result: StructuredRecord) -> int:
        history, presets = to_outputs(record, result, catalog, seen_snippets)
        # 先寫 presets 再寫 history：history 的存在是續跑的「已完成」標記
        # (existing_keys 用它判斷是否跳過)。若順序反過來，寫入 history 後、
        # presets 尚未落盤前當掉，續跑會因為 history 已存在而永久跳過這筆
        # record，導致它的 presets 全部遺失且無法補救。現在的順序下，最壞情況
        # 是 presets 寫了一部分就當掉、history 沒寫到——續跑會重新呼叫 LLM
        # 重新產生整筆記錄，而 seen_snippets（從 presets 檔重建）會濾掉這次
        # 重新產生出的重複 snippet，所以不會產生重複資料。
        for p in presets:
            append_jsonl(presets_path, p)
        append_jsonl(histories_path, history)
        return len(presets)

    def _call(record: dict) -> StructuredRecord:
        return client.generate_structured(build_prompt(record, catalog), StructuredRecord)

    if concurrency <= 1:
        for record in pending:
            n_presets += _emit(record, _call(record))
            n_hist += 1
        return n_hist, n_presets

    with ThreadPoolExecutor(max_workers=concurrency) as pool:
        for i in range(0, len(pending), concurrency):
            batch = pending[i : i + concurrency]
            for record, result in zip(batch, list(pool.map(_call, batch)), strict=True):
                n_presets += _emit(record, result)
                n_hist += 1
    return n_hist, n_presets


def main(argv: list[str] | None = None) -> None:
    ap = argparse.ArgumentParser(description="階段 3：LLM 結構化")
    ap.add_argument("--max-records", type=int, default=None)
    ap.add_argument(
        "--concurrency", type=int, default=1,
        help="同時發出的 Gemini 請求數；瓶頸是回應延遲不是節流，調大可大幅縮短全量時間",
    )
    args = ap.parse_args(argv)
    from pipeline.gemini_client import default_client  # 延遲 import：測試不需要 SDK 金鑰

    h, p = run_structure(
        default_client(), load_facets(FACETS_PATH),
        in_path=CLEAN_DIR / "records.jsonl",
        histories_path=HISTORIES_PATH, presets_path=PRESETS_PATH,
        max_records=args.max_records,
        concurrency=args.concurrency,
    )
    print(f"structure: +{h} histories, +{p} presets → {STRUCTURED_DIR}")


if __name__ == "__main__":
    main()
