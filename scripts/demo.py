"""單輪示範：中文描述 → ① 分析 → ② 分維度檢索 → ③ Gemini 組裝 → 英文 prompt + 六維度燈號 + 建議。

這是子專案 1 的成果展示，也是子專案 2 互動流程的縮小版：它只跑一輪、不追問。
用法：
    python demo.py "昏暗雨夜的科幻城市，一個穿皮夾克的短髮女生"
    python demo.py "山上的日出" --k-covered 8 --verbose --no-color
不給描述就進入互動模式，可以連續輸入，Ctrl+C 離開。
設計見 docs/superpowers/specs/2026-09-22-dimension-scoped-retrieval-design.md
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
    BorrowedView,
    DemoView,
    OptionView,
    Palette,
    SuggestionView,
    colors_enabled,
    render,
    render_queries,
    render_retrieval_summary,
    render_verbose,
)
from pipeline.config import FACETS_PATH  # noqa: E402
from pipeline.db import connect  # noqa: E402
from pipeline.facets import FacetCatalog, load_facets  # noqa: E402
from pipeline.retrieval import (  # noqa: E402
    Candidate,
    annotate_coverage,
    dedupe,
    grounded_dimensions,
    normalize_queries,
    retrieve_histories,
    retrieve_presets,
)

Profile = Literal["portrait", "landscape", "object", "vehicle"]
FacetState = Literal["covered", "missing", "notApplicable"]
Dimension = Literal["style", "scene", "camera", "appearance", "pose", "clothing"]


# ---------- ① 分析 ----------


class FacetAssessment(BaseModel):
    facet_id: str = Field(description="必須是清單中的 id")
    state: FacetState


class SubQuery(BaseModel):
    dimension: Dimension
    query: str = Field(description="繁體中文，供向量檢索用")


class AnalysisResult(BaseModel):
    subject_profile: Profile
    facets: list[FacetAssessment]
    queries: list[SubQuery]


# ---------- ③ 組裝 ----------


class BorrowedFrom(BaseModel):
    preset_id: int
    tags: list[str] = Field(description="真的寫進提示詞、且真的來自這筆片段的 tag")


class SuggestionOption(BaseModel):
    label: str = Field(description="繁體中文方向名")
    tags: str = Field(description="可直接貼上的英文 tag")
    preset_id: int = Field(description="來源片段 id")


class DimensionSuggestion(BaseModel):
    dimension: Dimension
    missing_labels: list[str]
    options: list[SuggestionOption] = Field(description="2–3 個不同方向")


class AssemblyResult(BaseModel):
    positive_prompt: str = Field(description="英文，逗號分隔 tag")
    negative_prompt: str = Field(description="英文，逗號分隔 tag")
    borrowed: list[BorrowedFrom]
    suggestions: list[DimensionSuggestion]


# ---------- 純函式：狀態整理與驗證 ----------


def facet_state_map(analysis: AnalysisResult, catalog: FacetCatalog, profile: str) -> dict[str, str]:
    """只留 profile 適用的 facet；LLM 漏判的補 missing（保守：寧可多報缺也不假裝有）。"""
    applicable = catalog.ids_for_profile(profile)
    states = {a.facet_id: a.state for a in analysis.facets if a.facet_id in applicable}
    for fid in applicable:
        states.setdefault(fid, "missing")
    return states


def missing_labels_by_dimension(states: dict[str, str], catalog: FacetCatalog) -> dict[str, list[str]]:
    out: dict[str, list[str]] = {}
    for f in catalog.facets.values():
        if states.get(f.id) == "missing":
            out.setdefault(f.dimension, []).append(f.label)
    return out


def validate_borrowed(assembly: AssemblyResult, by_id: dict[int, Candidate]) -> tuple[list[BorrowedFrom], list[str]]:
    """驗的是「來源」不是「正確性」。只改 borrowed 紀錄，絕不動提示詞。
    每個 tag 必須同時出現在片段（真的來自這裡）與提示詞（真的用了）。"""
    kept: list[BorrowedFrom] = []
    rejected: list[str] = []
    prompt_text = f"{assembly.positive_prompt}\n{assembly.negative_prompt}".lower()
    for b in assembly.borrowed:
        c = by_id.get(b.preset_id)
        if c is None:
            rejected.append(f"id {b.preset_id} 不在本次檢索結果內，整筆不計入借用")
            continue
        if not c.grounded:
            rejected.append(
                f"id {c.id}〈{c.preset['title']}〉屬於使用者未描述的維度（{c.dimension}），只能進建議，不計入借用"
            )
            continue
        source = f"{c.preset['prompt_snippet']}\n{c.preset['negative_snippet'] or ''}".lower()
        ok: list[str] = []
        for raw in b.tags:
            tag = raw.strip()
            if not tag:
                continue
            if tag.lower() not in source:
                rejected.append(f'來源不符：id {c.id}〈{c.preset["title"]}〉沒有 "{tag}"，不計入借用（提示詞不受影響）')
            elif tag.lower() not in prompt_text:
                rejected.append(f'id {c.id}〈{c.preset["title"]}〉的 "{tag}" 未出現在提示詞裡，不計入借用')
            else:
                ok.append(tag)
        if ok:
            kept.append(BorrowedFrom(preset_id=c.id, tags=ok))
    return kept, rejected


def validate_suggestions(
    assembly: AssemblyResult, by_id: dict[int, Candidate], missing_by_dim: dict[str, list[str]]
) -> tuple[list[DimensionSuggestion], list[str]]:
    """來源不受 grounded 限制（閘門只管提示詞）；missing_labels 以 ① 為準覆寫 LLM 給的。"""
    kept: list[DimensionSuggestion] = []
    rejected: list[str] = []
    for s in assembly.suggestions:
        truth = missing_by_dim.get(s.dimension)
        if not truth:
            rejected.append(f"建議：{s.dimension} 沒有缺的 facet，不該有建議，整則移除")
            continue
        options: list[SuggestionOption] = []
        for o in s.options:
            if o.preset_id in by_id:
                options.append(o)
            else:
                rejected.append(f"建議「{o.label}」的來源 id {o.preset_id} 不在本次檢索結果內，移除")
        if options:
            kept.append(DimensionSuggestion(dimension=s.dimension, missing_labels=truth, options=options))
    return kept, rejected


# ---------- prompt ----------

ANALYSIS_TEMPLATE = """你是 AI 生圖提示詞助理的「分析」階段。使用者用繁體中文描述想要的畫面，你只做分析，不產出提示詞。

1. 判斷 subject_profile：portrait（畫面主體是人）、landscape（風景）、object（靜物；**動物也歸在這一類**）、\
vehicle（載具）。
2. 對下面清單裡的**每一個** facet 判斷狀態，一個都不能漏：
   - covered：使用者的描述已經提供了這項資訊
   - missing：這項對本題材有意義，但使用者沒有提到
   - notApplicable：這項對本題材根本不適用（例如風景沒有「人物穿著」）
3. 產出 queries，每筆是 {{dimension, query}}，query 為繁體中文，供向量檢索用：
   - 有任一 facet 為 covered 的維度：給 **1 句**，**逐字使用使用者的原話**（只挑出屬於這個維度的那部分），不改寫、不補充
   - 全部 facet 為 missing 的維度：給 **2 句**，依整體畫面推想這個維度可能的方向，兩句必須是**對比**的方向\
（例如「寫實攝影」對「動漫插畫」），不是同一方向的兩種說法
   - 全部 facet 為 notApplicable 的維度：不給

Facet 清單：
{facets}

使用者的描述：
{query}
"""

ASSEMBLY_TEMPLATE = """你是 AI 生圖提示詞助理的「組裝」階段。分析階段已判定題材與每個 facet 的狀態，\
並從知識庫撈出候選片段。你要產出可直接使用的 Stable Diffusion / SDXL 提示詞（英文、逗號分隔的 tag 風格）。

規則：
1. 使用者已描述的內容必須**完整**反映在提示詞裡。
2. **複合屬性用複合 tag 完整表達**：雙色髮、半邊、漸層、混色這類，要寫成像 `split-color hair, two-tone hair, \
purple hair, pink hair` 這種能讓生圖模型理解結構的組合，不可被候選片段裡的單色詞（如 `pink hair`）取代或吃掉一半。\
知識庫沒有的詞由你自己翻譯，不因為片段裡沒有就省略。
3. 標為 missing 的 facet **不要自行發明**，留白交給生圖模型；基礎畫質詞與基礎負向詞例外（慣例 boilerplate）。
4. 候選片段每筆都標了「可借入提示詞」或「僅供建議」，以及它涉及的 facet 對本次使用者是 covered 還是 missing：
   - 「僅供建議」的片段，任何詞都不可寫進提示詞
   - 「可借入提示詞」的片段，標 missing 的 facet 對應的詞也不可寫進提示詞，只可進建議
   - 相似度「低」的片段仍可借用其中與使用者描述相符的詞（例如 `sitting on the mushroom` 裡的 `sitting`），\
但不可借入與描述矛盾的詞
5. borrowed 只列**真的寫進提示詞**且**真的來自該片段**的詞；每個詞要跟片段裡的寫法一致。
6. suggestions：每個有 missing facet 的維度都要給一則，missing_labels 逐一點名該維度缺的 facet，\
options 給 2–3 個**不同方向**的選項，每個選項的 tags 是可直接貼的英文、preset_id 是來源片段。\
沒有 missing facet 的維度不要給。

題材：{profile}

Facet 狀態：
{facet_states}

候選片段（依維度分組，相似度依向量距離分級）：
{candidates}

類似的既有作品（僅供參考風格，不要照抄）：
{histories}

使用者的描述：
{query}
"""


def format_facet_states(states: dict[str, str], catalog: FacetCatalog, profile: str) -> str:
    lines: list[str] = []
    for dim, ids in catalog.profiles[profile].items():
        lines.append(f"[{dim}] {catalog.dimensions[dim]}")
        for fid in ids:
            lines.append(f"  - {fid}（{catalog.facets[fid].label}）：{states.get(fid, 'missing')}")
    return "\n".join(lines)


def format_candidates(cands: list[Candidate]) -> str:
    if not cands:
        return "  （無）"
    lines: list[str] = []
    for c in cands:
        usage = "可借入提示詞" if c.grounded else "僅供建議"
        coverage = ", ".join(f"{k}={v}" for k, v in c.facet_coverage.items()) or "(無)"
        lines.append(f"  id={c.id} 〈{c.preset['title']}〉[{c.dimension}] 相似度：{c.band}（{c.dist:.3f}） {usage}")
        lines.append(f"      facets: {coverage}")
        lines.append(f"      positive: {c.preset['prompt_snippet']}")
        lines.append(f"      negative: {c.preset['negative_snippet'] or '(無)'}")
    return "\n".join(lines)


def format_histories(histories: list[dict]) -> str:
    if not histories:
        return "  （無）"
    return "\n".join(
        f"  ({x['subject_profile']}) {x['user_intent']}\n      {x['positive_prompt'][:150]}" for x in histories
    )


def build_analysis_prompt(query: str, catalog: FacetCatalog) -> str:
    return ANALYSIS_TEMPLATE.format(facets=catalog.prompt_listing(), query=query)


def build_assembly_prompt(
    query: str,
    profile: str,
    states: dict[str, str],
    cands: list[Candidate],
    histories: list[dict],
    catalog: FacetCatalog,
) -> str:
    return ASSEMBLY_TEMPLATE.format(
        profile=profile,
        facet_states=format_facet_states(states, catalog, profile),
        candidates=format_candidates(cands),
        histories=format_histories(histories),
        query=query,
    )


# ---------- 主流程 ----------


def build_view(
    profile: str,
    states: dict[str, str],
    assembly: AssemblyResult,
    kept_borrowed: list[BorrowedFrom],
    rejections: list[str],
    kept_suggestions: list[DimensionSuggestion],
    by_id: dict[int, Candidate],
    catalog: FacetCatalog,
) -> DemoView:
    borrowed = [
        BorrowedView(
            band=by_id[b.preset_id].band, dist=by_id[b.preset_id].dist,
            title=by_id[b.preset_id].preset["title"], category=by_id[b.preset_id].preset["category"], tags=b.tags,
        )
        for b in kept_borrowed
    ]
    suggestions = [
        SuggestionView(
            dimension_label=catalog.dimensions[s.dimension],
            missing_labels=s.missing_labels,
            options=[OptionView(o.label, o.tags, by_id[o.preset_id].preset["title"]) for o in s.options],
        )
        for s in kept_suggestions
    ]
    return DemoView(
        profile=profile, facet_states=states,
        positive_prompt=assembly.positive_prompt, negative_prompt=assembly.negative_prompt,
        borrowed=borrowed, rejections=rejections, suggestions=suggestions,
    )


def run_once(query: str, conn, client, catalog: FacetCatalog, args, p: Palette) -> None:
    print(p.dim(f"\n[1/3] 分析：{query}"))
    analysis = client.generate_structured(build_analysis_prompt(query, catalog), AnalysisResult)
    profile = analysis.subject_profile
    states = facet_state_map(analysis, catalog, profile)
    grounded = grounded_dimensions(states, catalog)
    queries = normalize_queries(
        [(q.dimension, q.query) for q in analysis.queries], profile, grounded, catalog, query,
        k_covered=args.k_covered, k_missing=args.k_missing,
    )
    print(p.dim(f"      題材 {profile}"))
    print(p.dim(render_queries(queries, catalog)))

    print(p.dim("[2/3] 檢索"))
    vectors = client.embed_batch([query] + [q.query for q in queries], task_type="RETRIEVAL_QUERY")
    qvec, subvecs = Vector(vectors[0]), [Vector(v) for v in vectors[1:]]
    hits = retrieve_presets(conn, queries, subvecs, catalog, profile)
    cands = dedupe([c for dh in hits for c in dh.hits])
    annotate_coverage(cands, states)
    histories = retrieve_histories(conn, qvec, profile, args.top_histories)
    print(p.dim(render_retrieval_summary(hits, catalog, len(histories), profile)))
    if args.verbose:
        print(render_verbose(cands, catalog, p))

    print(p.dim("[3/3] 交給 Gemini 組裝提示詞…"))
    assembly = client.generate_structured(
        build_assembly_prompt(query, profile, states, cands, histories, catalog), AssemblyResult
    )
    by_id = {c.id: c for c in cands}
    kept_borrowed, rejected_b = validate_borrowed(assembly, by_id)
    kept_suggestions, rejected_s = validate_suggestions(assembly, by_id, missing_labels_by_dimension(states, catalog))
    view = build_view(
        profile, states, assembly, kept_borrowed, rejected_b + rejected_s, kept_suggestions, by_id, catalog
    )
    print(render(view, catalog, p))


def main(argv: list[str] | None = None) -> None:
    ap = argparse.ArgumentParser(description="單輪示範：中文描述 → 英文生圖提示詞")
    ap.add_argument("query", nargs="?", help="中文描述；省略則進入互動模式")
    ap.add_argument("--k-covered", type=int, default=5, help="使用者有描述的維度，每維撈幾筆")
    ap.add_argument("--k-missing", type=int, default=3, help="使用者未描述的維度，每句推想子查詢撈幾筆")
    ap.add_argument("--top-histories", type=int, default=3)
    ap.add_argument("--verbose", action="store_true", help="印出每維度全部候選與分級")
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
