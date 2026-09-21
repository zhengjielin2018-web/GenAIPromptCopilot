"""階段 2：raw → clean。去掉沒 meta、太短、非英文、NSFW 的紀錄；正規化並依 prompt 去重。"""

from __future__ import annotations

import argparse
import hashlib
import re
from collections.abc import Iterable

from pipeline.config import CLEAN_DIR, RAW_DIR
from pipeline.jsonl import read_jsonl, write_jsonl
from pipeline.nsfw_filter import is_nsfw_text

CLEAN_PATH = CLEAN_DIR / "records.jsonl"
MIN_PROMPT_CHARS = 20
MAX_PROMPT_CHARS = 2000
MAX_NON_ASCII_RATIO = 0.2

_ANGLE_TOKEN_RE = re.compile(r"<[^<>]*>")  # <lora:..>, <lyco:..>, <embedding:..>
# 通用畫質詞／分數標籤／negative embedding 名稱的過濾不在這裡做，見 pipeline/boilerplate.py
_MULTI_COMMA_RE = re.compile(r"(\s*,\s*)+")
_MULTI_SPACE_RE = re.compile(r"\s+")


def normalize_prompt(text: str) -> str:
    text = _ANGLE_TOKEN_RE.sub(" ", text)
    text = _MULTI_SPACE_RE.sub(" ", text)
    text = _MULTI_COMMA_RE.sub(", ", text)
    return text.strip(" ,")


def _prompt_hash(normalized: str) -> str:
    return hashlib.sha1(normalized.lower().encode("utf-8")).hexdigest()


def _non_ascii_ratio(text: str) -> float:
    if not text:
        return 1.0
    return sum(1 for ch in text if ord(ch) > 127) / len(text)


def _to_record(item: dict) -> dict | None:
    meta = item.get("meta") or {}
    prompt = meta.get("prompt")
    if not prompt or item.get("nsfwLevel") != "None":
        return None
    prompt = normalize_prompt(prompt)
    if not (MIN_PROMPT_CHARS <= len(prompt) <= MAX_PROMPT_CHARS):
        return None
    if _non_ascii_ratio(prompt) > MAX_NON_ASCII_RATIO:
        return None
    negative = normalize_prompt(meta.get("negativePrompt") or "")
    if is_nsfw_text(prompt) or is_nsfw_text(negative):
        # 負向詞出現 NSFW 詞很常見（作者在排除），但保守起見一併丟棄
        return None
    return {
        "source_id": item["id"],
        "prompt": prompt,
        "negative_prompt": negative,
        "image_url": item.get("url"),
        "width": item.get("width"),
        "height": item.get("height"),
        "base_model": item.get("baseModel"),
        "like_count": (item.get("stats") or {}).get("likeCount", 0),
        "prompt_hash": _prompt_hash(prompt),
    }


def clean_records(raw: Iterable[dict]) -> list[dict]:
    best: dict[str, dict] = {}
    for item in raw:
        rec = _to_record(item)
        if rec is None:
            continue
        cur = best.get(rec["prompt_hash"])
        if cur is None or rec["like_count"] > cur["like_count"]:
            best[rec["prompt_hash"]] = rec
    return sorted(best.values(), key=lambda r: r["source_id"])


def main(argv: list[str] | None = None) -> None:
    argparse.ArgumentParser(description="階段 2：清洗與去重").parse_args(argv)
    raw = list(read_jsonl(RAW_DIR / "images.jsonl"))
    out = clean_records(raw)
    n = write_jsonl(CLEAN_PATH, out)
    print(f"clean: {len(raw)} → {n} → {CLEAN_PATH}")


if __name__ == "__main__":
    main()
