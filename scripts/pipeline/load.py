"""階段 5：embedded → PostgreSQL。以 source_ref 做 upsert，重跑不會重複。"""

from __future__ import annotations

import argparse
from collections.abc import Iterable
from pathlib import Path

import psycopg
from pgvector import Vector

from pipeline.config import EMBEDDED_DIR
from pipeline.jsonl import read_jsonl

HISTORIES_SQL = """
INSERT INTO shared_prompt_histories
    (source_ref, user_intent, positive_prompt, negative_prompt, subject_profile, source, image_url, intent_embedding)
VALUES (%(source_ref)s, %(user_intent)s, %(positive_prompt)s, %(negative_prompt)s,
        %(subject_profile)s, 'civitai', %(image_url)s, %(embedding)s)
ON CONFLICT (source_ref) DO UPDATE SET
    user_intent      = EXCLUDED.user_intent,
    positive_prompt  = EXCLUDED.positive_prompt,
    negative_prompt  = EXCLUDED.negative_prompt,
    subject_profile  = EXCLUDED.subject_profile,
    image_url        = EXCLUDED.image_url,
    intent_embedding = EXCLUDED.intent_embedding
"""

PRESETS_SQL = """
INSERT INTO prompt_knowledge_presets
    (source_ref, title, category, description, tags, facet_ids,
     prompt_snippet, negative_snippet, image_url, preset_embedding)
VALUES (%(source_ref)s, %(title)s, %(category)s, %(description)s, %(tags)s, %(facet_ids)s,
        %(prompt_snippet)s, %(negative_snippet)s, %(image_url)s, %(embedding)s)
ON CONFLICT (source_ref) DO UPDATE SET
    title            = EXCLUDED.title,
    category         = EXCLUDED.category,
    description      = EXCLUDED.description,
    tags             = EXCLUDED.tags,
    facet_ids        = EXCLUDED.facet_ids,
    prompt_snippet   = EXCLUDED.prompt_snippet,
    negative_snippet = EXCLUDED.negative_snippet,
    image_url        = EXCLUDED.image_url,
    preset_embedding = EXCLUDED.preset_embedding
"""


def _with_vector(row: dict) -> dict:
    return {**row, "embedding": Vector(row["embedding"])}


def _upsert(conn: psycopg.Connection, sql: str, rows: Iterable[dict]) -> int:
    n = 0
    with conn.cursor() as cur:
        for row in rows:
            cur.execute(sql, _with_vector(row))
            n += 1
    return n


def upsert_histories(conn: psycopg.Connection, rows: Iterable[dict]) -> int:
    return _upsert(conn, HISTORIES_SQL, rows)


def upsert_presets(conn: psycopg.Connection, rows: Iterable[dict]) -> int:
    return _upsert(conn, PRESETS_SQL, rows)


def run_load(conn: psycopg.Connection, *, histories_path: Path, presets_path: Path) -> tuple[int, int]:
    h = upsert_histories(conn, read_jsonl(histories_path))
    p = upsert_presets(conn, read_jsonl(presets_path))
    conn.commit()
    return h, p


def main(argv: list[str] | None = None) -> None:
    argparse.ArgumentParser(description="階段 5：寫入 PostgreSQL").parse_args(argv)
    from pipeline.db import connect

    with connect() as conn:
        h, p = run_load(conn, histories_path=EMBEDDED_DIR / "histories.jsonl",
                        presets_path=EMBEDDED_DIR / "presets.jsonl")
    print(f"load: {h} histories, {p} presets upserted")


if __name__ == "__main__":
    main()
