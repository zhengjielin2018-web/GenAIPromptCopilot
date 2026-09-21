import pytest
from pgvector import Vector

from pipeline import db


@pytest.mark.integration
def test_connect_registers_vector_and_tables_exist():
    if not db.db_available():
        pytest.skip("PostgreSQL 未啟動")
    with db.connect() as conn:
        names = {
            r[0]
            for r in conn.execute(
                "SELECT tablename FROM pg_tables WHERE schemaname = 'public'"
            ).fetchall()
        }
        assert {"shared_prompt_histories", "prompt_knowledge_presets", "audit_logs"} <= names
        dim = conn.execute(
            "SELECT atttypmod FROM pg_attribute "
            "WHERE attrelid = 'prompt_knowledge_presets'::regclass AND attname = 'preset_embedding'"
        ).fetchone()[0]
        assert dim == 768


@pytest.mark.integration
def test_connect_round_trips_a_vector():
    if not db.db_available():
        pytest.skip("PostgreSQL 未啟動")
    with db.connect() as conn:
        sent = [0.5] * 768
        got = conn.execute("SELECT %s::vector", (Vector(sent),)).fetchone()[0]
        assert not isinstance(got, str), "register_vector did not deserialize the vector"
        assert got.dimensions() == 768
        assert all(abs(a - b) < 1e-6 for a, b in zip(got.to_list(), sent, strict=True))
