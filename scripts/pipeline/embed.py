"""階段 4：structured → embedded。以 source_ref 續跑；--reindex 全部重算（換 embedding 模型時用）。"""

from __future__ import annotations

import argparse
from collections.abc import Callable
from pathlib import Path

from pipeline.config import EMBEDDED_DIR, STRUCTURED_DIR
from pipeline.jsonl import append_jsonl, existing_keys, read_jsonl, write_jsonl

HISTORIES_IN = STRUCTURED_DIR / "histories.jsonl"
PRESETS_IN = STRUCTURED_DIR / "presets.jsonl"
HISTORIES_OUT = EMBEDDED_DIR / "histories.jsonl"
PRESETS_OUT = EMBEDDED_DIR / "presets.jsonl"


def history_text(r: dict) -> str:
    return r["user_intent"]


def preset_text(r: dict) -> str:
    return f"{r['title']}。{r['description']}。標籤：{', '.join(r['tags'])}"


def run_embed(
    client,
    *,
    in_path: Path,
    out_path: Path,
    text_fn: Callable[[dict], str],
    reindex: bool = False,
    chunk: int = 64,
) -> int:
    if reindex:
        write_jsonl(out_path, [])
    done = existing_keys(out_path, "source_ref")
    pending = [r for r in read_jsonl(in_path) if r["source_ref"] not in done]
    n = 0
    for i in range(0, len(pending), chunk):
        batch = pending[i : i + chunk]
        vectors = client.embed_batch([text_fn(r) for r in batch], task_type="RETRIEVAL_DOCUMENT")
        for r, v in zip(batch, vectors, strict=True):
            append_jsonl(out_path, {**r, "embedding": v})
            n += 1
    return n


def main(argv: list[str] | None = None) -> None:
    ap = argparse.ArgumentParser(description="階段 4：向量化")
    ap.add_argument("--reindex", action="store_true", help="丟棄既有向量全部重算")
    args = ap.parse_args(argv)
    from pipeline.gemini_client import default_client

    client = default_client()
    h = run_embed(client, in_path=HISTORIES_IN, out_path=HISTORIES_OUT, text_fn=history_text, reindex=args.reindex)
    p = run_embed(client, in_path=PRESETS_IN, out_path=PRESETS_OUT, text_fn=preset_text, reindex=args.reindex)
    print(f"embed: +{h} histories, +{p} presets → {EMBEDDED_DIR}")


if __name__ == "__main__":
    main()
