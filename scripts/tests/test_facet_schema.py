"""子表 DDL 的契約：migration 與 001_schema.sql 要同一份、要冪等、不建 HNSW（facet 向量設計 §4.1、§7）。"""

from __future__ import annotations

import re
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
MIGRATION = ROOT / "db" / "migrations" / "003_preset_facet_embeddings.sql"
SCHEMA = ROOT / "db" / "init" / "001_schema.sql"


def _table_block(sql: str) -> str:
    m = re.search(r"CREATE TABLE IF NOT EXISTS preset_facet_embeddings \((.*?)\);", sql, re.S)
    assert m, "找不到 preset_facet_embeddings 的 CREATE TABLE IF NOT EXISTS"
    return m.group(1)


def test_migration_is_idempotent_and_has_the_agreed_columns():
    sql = MIGRATION.read_text(encoding="utf-8")
    block = _table_block(sql)
    assert "preset_id  BIGINT NOT NULL REFERENCES prompt_knowledge_presets(id) ON DELETE CASCADE" in block
    assert "facet_id   TEXT   NOT NULL" in block
    assert "tag_key    TEXT   NOT NULL" in block
    assert "embedding  VECTOR(768) NOT NULL" in block
    assert "PRIMARY KEY (preset_id, facet_id)" in block
    assert "CREATE INDEX IF NOT EXISTS idx_pfe_facet ON preset_facet_embeddings (facet_id);" in sql


def test_migration_does_not_build_hnsw_on_the_subtable():
    sql = MIGRATION.read_text(encoding="utf-8")
    assert "hnsw" not in sql.lower(), "子表刻意不建 HNSW（設計 §7）；註解也不要寫這個字，用「向量索引」「近似索引」"
    assert "設計 §7" in sql


def test_schema_carries_the_same_table_for_fresh_databases():
    schema = SCHEMA.read_text(encoding="utf-8")
    assert _table_block(schema) == _table_block(MIGRATION.read_text(encoding="utf-8"))
    assert "idx_pfe_facet" in schema
