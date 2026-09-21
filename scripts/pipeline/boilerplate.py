"""通用畫質詞／分數標籤／negative embedding 名稱過濾器。

獨立成模組是因為這個過濾邏輯跨三個階段邊界生效：structure 階段對 LLM 產出的
history 與 preset 欄位套用，資料修補腳本也直接匯入它來重新處理既有的 jsonl，
而不必重跑 Gemini。"""

from __future__ import annotations

import re

_SCORE_TAG_RE = re.compile(r"^score_\d+(_up)?$")
_EMBEDDING_TAG_RE = re.compile(r"^\w*_neg$")
_BOILERPLATE_TAGS: frozenset[str] = frozenset({
    "masterpiece", "best quality", "high quality", "normal quality",
    "worst quality", "low quality", "highly detailed", "ultra detailed",
    "absurdres", "highres", "lowres", "bad anatomy", "bad hands",
    "jpeg artifacts", "signature", "watermark", "username", "artist name",
    "text", "error", "cropped", "out of frame", "subtitle", "subtitles",
})

_BREAK_RE = re.compile(r"\bBREAK\b")
_WEIGHT_RE = re.compile(r"^[(\[{\s]*(.*?)\s*(?::[\d.]+)?[)\]}\s]*$")


def strip_boilerplate(snippet: str) -> str:
    """逐個逗號分隔標籤過濾：移除通用畫質詞、分數標籤與 negative embedding 名稱，
    但保留有風格意義的負向詞（如 censored、furry、chibi、3d）。
    比對前會剝掉 SD 的權重語法（括號與 :1.2），並把 BREAK 當成分隔符號。"""
    kept: list[str] = []
    for raw in _BREAK_RE.sub(",", snippet).split(","):
        tag = raw.strip()
        if not tag:
            continue
        low = _WEIGHT_RE.match(tag).group(1).strip().lower()
        if not low:
            continue
        if low in _BOILERPLATE_TAGS or _SCORE_TAG_RE.match(low) or _EMBEDDING_TAG_RE.match(low):
            continue
        kept.append(tag)
    return ", ".join(kept)
