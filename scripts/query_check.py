"""驗收用：以繁中口語查詢 presets（混合檢索）與 histories（向量 + profile），印出 Top-K。
用法：python query_check.py "昏暗雨夜的科幻城市" --top 5 [--facet scene.weather] [--tag rain]"""

from __future__ import annotations

import argparse
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

from pgvector import Vector  # noqa: E402

from pipeline.db import connect  # noqa: E402
from pipeline.gemini_client import default_client  # noqa: E402

PRESETS_SQL = """
SELECT title, category, facet_ids, tags, prompt_snippet,
       preset_embedding <=> %(q)s AS dist
FROM prompt_knowledge_presets
WHERE (%(facet)s::text IS NULL OR facet_ids @> ARRAY[%(facet)s]::text[])
  AND (%(tag)s::text IS NULL OR tags @> ARRAY[%(tag)s]::text[])
ORDER BY dist
LIMIT %(k)s
"""

HISTORIES_SQL = """
SELECT user_intent, subject_profile, left(positive_prompt, 80) AS prompt,
       intent_embedding <=> %(q)s AS dist
FROM shared_prompt_histories
ORDER BY dist
LIMIT %(k)s
"""


def main(argv: list[str] | None = None) -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("query")
    ap.add_argument("--top", type=int, default=5)
    ap.add_argument("--facet", default=None)
    ap.add_argument("--tag", default=None)
    args = ap.parse_args(argv)

    qvec = Vector(default_client().embed_batch([args.query], task_type="RETRIEVAL_QUERY")[0])
    with connect() as conn:
        print(f"\n== presets for「{args.query}」 ==")
        for row in conn.execute(PRESETS_SQL, {"q": qvec, "facet": args.facet, "tag": args.tag, "k": args.top}):
            title, category, facets, tags, snippet, dist = row
            print(f"[{dist:.3f}] {title} ({category}) {facets}\n         {snippet[:90]}")
        print("\n== histories ==")
        for intent, profile, prompt, dist in conn.execute(HISTORIES_SQL, {"q": qvec, "k": args.top}):
            print(f"[{dist:.3f}] ({profile}) {intent}\n         {prompt}")


if __name__ == "__main__":
    main()
