"""單輪示範：中文描述 → 從知識庫檢索 → Gemini 組裝 → 英文 prompt + 六維度燈號。

這是子專案 1 的成果展示，也是子專案 2 互動流程的縮小版：它只跑一輪、不追問。
用法：
    python demo.py "昏暗雨夜的科幻城市，一個穿皮夾克的短髮女生"
    python demo.py "山上的日出" --top-presets 12 --no-color
不給描述就進入互動模式，可以連續輸入，Ctrl+C 離開。
"""

from __future__ import annotations

import argparse
import logging
import sys
from pathlib import Path
from typing import Literal

# google-genai 每次 generate_content 都會印一段 AFC 的提醒；對這支 demo 是純噪音。
logging.getLogger("google_genai").setLevel(logging.ERROR)

sys.path.insert(0, str(Path(__file__).resolve().parent))

from pgvector import Vector  # noqa: E402
from pydantic import BaseModel, Field  # noqa: E402

from demo_render import (  # noqa: E402
    DIMENSIONS,
    Palette,
    colors_enabled,
    group_by_dimension,
    render_dimension_row,
    wrap_tags,
)
from pipeline.config import FACETS_PATH  # noqa: E402
from pipeline.db import connect  # noqa: E402
from pipeline.facets import FacetCatalog, load_facets  # noqa: E402

Profile = Literal["portrait", "landscape", "object", "vehicle"]
FacetState = Literal["covered", "missing", "notApplicable"]

PRESETS_SQL = """
SELECT id, title, category, facet_ids, tags, prompt_snippet, negative_snippet,
       preset_embedding <=> %(q)s AS dist
FROM prompt_knowledge_presets
ORDER BY dist
LIMIT %(k)s
"""

HISTORIES_SQL = """
SELECT user_intent, positive_prompt, subject_profile,
       intent_embedding <=> %(q)s AS dist
FROM shared_prompt_histories
ORDER BY dist
LIMIT %(k)s
"""

PROMPT_TEMPLATE = """你是 AI 生圖提示詞助理。使用者用中文描述想要的畫面，你要產出可直接使用的 \
Stable Diffusion / SDXL 提示詞。

1. 判斷 subject_profile：portrait（畫面主體是人）、landscape（風景）、object（靜物；\
**動物也歸在這一類**）、vehicle（載具）。
2. 對下面清單裡的**每一個** facet 判斷狀態，一個都不能漏：
   - covered：使用者的描述已經提供了這項資訊
   - missing：這項對本題材有意義，但使用者沒有提到
   - notApplicable：這項對本題材根本不適用（例如風景沒有「人物穿著」）
3. 產出 positive_prompt 與 negative_prompt，英文、逗號分隔的 tag 風格：
   - 使用者已經提供的內容必須完整反映出來
   - 標為 missing 的項目**不要自行發明**，留白交給生圖模型決定
   - 但基礎畫質詞與基礎負向詞一定要加（這是慣例 boilerplate，不算發明細節）
   - 可以取用下方「可重用片段」的內容；凡是採用了的，把它的 id 放進 used_preset_ids
4. tips：一到兩句繁體中文建議，講「最值得補上的那一項資訊」，而不是泛泛的鼓勵。

Facet 清單：
{facets}

可重用片段（從知識庫依相關度檢索）：
{presets}

類似的既有作品（僅供參考風格，不要照抄）：
{histories}

使用者的描述：
{query}
"""


class FacetAssessment(BaseModel):
    facet_id: str = Field(description="必須是清單中的 id")
    state: FacetState


class DemoResult(BaseModel):
    subject_profile: Profile
    facets: list[FacetAssessment]
    positive_prompt: str = Field(description="英文，逗號分隔 tag")
    negative_prompt: str = Field(description="英文，逗號分隔 tag")
    tips: str = Field(description="繁體中文，一到兩句")
    used_preset_ids: list[int] = Field(description="實際採用的片段 id")


# ---------- 輸出 ----------


def render(result: DemoResult, catalog: FacetCatalog, presets: list[dict], p: Palette) -> str:
    grouped = group_by_dimension({a.facet_id: a.state for a in result.facets}, catalog)
    lines = [
        "",
        p.head("━━ 題材判定 ━━"),
        f"  {result.subject_profile}",
        "",
        p.head("━━ 六維度充足度 ━━"),
    ]
    for key in DIMENSIONS:
        label = catalog.dimensions.get(key, key)
        row = render_dimension_row(label, grouped[key])
        lines.append(p.dim(row) if "不適用" in row else row)

    lines += ["", p.head("━━ 正向提示詞 ━━"), p.ok(wrap_tags(result.positive_prompt))]
    lines += ["", p.head("━━ 負向提示詞 ━━"), p.warn(wrap_tags(result.negative_prompt))]

    used = [x for x in presets if x["id"] in set(result.used_preset_ids)]
    lines += ["", p.head("━━ 採用的知識庫片段 ━━")]
    if used:
        for x in used:
            lines.append(f"  [{x['dist']:.3f}] {x['title']}（{x['category']}）")
            lines.append(p.dim(f"         {x['prompt_snippet'][:76]}"))
    else:
        lines.append(p.dim("  （這次沒有採用檢索到的片段）"))

    lines += ["", p.head("━━ 建議 ━━"), f"  {result.tips}", ""]
    return "\n".join(lines)


# ---------- 主流程 ----------


def retrieve(conn, qvec: Vector, top_presets: int, top_histories: int):
    presets = [
        {
            "id": r[0], "title": r[1], "category": r[2], "facet_ids": r[3], "tags": r[4],
            "prompt_snippet": r[5], "negative_snippet": r[6], "dist": r[7],
        }
        for r in conn.execute(PRESETS_SQL, {"q": qvec, "k": top_presets})
    ]
    histories = [
        {"user_intent": r[0], "positive_prompt": r[1], "subject_profile": r[2], "dist": r[3]}
        for r in conn.execute(HISTORIES_SQL, {"q": qvec, "k": top_histories})
    ]
    return presets, histories


def build_prompt(query: str, catalog: FacetCatalog, presets: list[dict], histories: list[dict]) -> str:
    preset_block = "\n".join(
        f"  id={x['id']} 〈{x['title']}〉[{x['category']}] facets={x['facet_ids']}\n"
        f"      positive: {x['prompt_snippet']}\n"
        f"      negative: {x['negative_snippet'] or '(無)'}"
        for x in presets
    ) or "  （無）"
    history_block = "\n".join(
        f"  ({x['subject_profile']}) {x['user_intent']}\n      {x['positive_prompt'][:150]}"
        for x in histories
    ) or "  （無）"
    return PROMPT_TEMPLATE.format(
        facets=catalog.prompt_listing(), presets=preset_block, histories=history_block, query=query
    )


def run_once(query: str, conn, client, catalog: FacetCatalog, args, p: Palette) -> None:
    print(p.dim(f"\n[1/3] 向量化查詢：{query}"))
    qvec = Vector(client.embed_batch([query], task_type="RETRIEVAL_QUERY")[0])

    print(p.dim(f"[2/3] 檢索知識庫（presets top-{args.top_presets}、histories top-{args.top_histories}）"))
    presets, histories = retrieve(conn, qvec, args.top_presets, args.top_histories)
    print(p.dim(f"      命中 {len(presets)} 個片段、{len(histories)} 筆相似作品"))

    print(p.dim("[3/3] 交給 Gemini 組裝提示詞…"))
    result = client.generate_structured(build_prompt(query, catalog, presets, histories), DemoResult)
    print(render(result, catalog, presets, p))


def main(argv: list[str] | None = None) -> None:
    ap = argparse.ArgumentParser(description="單輪示範：中文描述 → 英文生圖提示詞")
    ap.add_argument("query", nargs="?", help="中文描述；省略則進入互動模式")
    ap.add_argument("--top-presets", type=int, default=8)
    ap.add_argument("--top-histories", type=int, default=3)
    ap.add_argument("--no-color", action="store_true")
    args = ap.parse_args(argv)

    from pipeline.gemini_client import default_client  # 延遲匯入：沒金鑰時才在這裡報錯

    p = Palette(colors_enabled(args.no_color))
    catalog = load_facets(FACETS_PATH)
    client = default_client()

    with connect() as conn:
        if args.query:
            run_once(args.query, conn, client, catalog, args, p)
            return
        print(p.head("互動模式：輸入中文描述後按 Enter，Ctrl+C 離開。"))
        while True:
            try:
                q = input(p.head("\n> ")).strip()
            except (EOFError, KeyboardInterrupt):
                print("\n再見。")
                return
            if q:
                run_once(q, conn, client, catalog, args, p)


if __name__ == "__main__":
    main()
