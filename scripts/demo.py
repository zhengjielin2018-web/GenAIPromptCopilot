"""單輪示範：中文描述 → ① 分析 → ② 分維度檢索 → ③ Gemini 組裝 → 英文 prompt + 六維度燈號 + 建議。

這是子專案 1 的成果展示，也是子專案 2 互動流程的縮小版：它只跑一輪、不追問。
用法：
    python demo.py "昏暗雨夜的科幻城市，一個穿皮夾克的短髮女生"
    python demo.py "山上的日出" --k-covered 8 --verbose --no-color
不給描述就進入互動模式，可以連續輸入，Ctrl+C 離開。
設計見 docs/superpowers/specs/2026-09-22-dimension-scoped-retrieval-design.md
"""

from __future__ import annotations

import logging
import sys
from pathlib import Path
from typing import Literal

# google-genai 每次 generate_content 都會印一段 AFC 的提醒；對這支 demo 是純噪音。
logging.getLogger("google_genai").setLevel(logging.ERROR)

sys.path.insert(0, str(Path(__file__).resolve().parent))

from pydantic import BaseModel, Field  # noqa: E402

from pipeline.facets import FacetCatalog  # noqa: E402
from pipeline.retrieval import Candidate  # noqa: E402

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
