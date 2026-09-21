import pytest
from pgvector import Vector

from pipeline import db
from pipeline.load import upsert_histories, upsert_presets

pytestmark = pytest.mark.integration

VEC = [1.0] + [0.0] * 767
HIST = {"source_ref": "test:h1", "user_intent": "測試", "positive_prompt": "p", "negative_prompt": "n",
        "subject_profile": "portrait", "image_url": None, "embedding": VEC}
PRESET = {"source_ref": "test:h1:0", "title": "t", "category": "Scene", "description": "d",
          "tags": ["a", "b"], "facet_ids": ["scene.location"], "prompt_snippet": "s",
          "negative_snippet": None, "image_url": None, "embedding": VEC}


@pytest.fixture
def conn():
    if not db.db_available():
        pytest.skip("PostgreSQL 未啟動")
    c = db.connect()
    yield c
    c.rollback()
    c.execute("DELETE FROM shared_prompt_histories WHERE source_ref LIKE 'test:%'")
    c.execute("DELETE FROM prompt_knowledge_presets WHERE source_ref LIKE 'test:%'")
    c.commit()
    c.close()


def test_upsert_histories_is_idempotent(conn):
    assert upsert_histories(conn, [HIST]) == 1
    assert upsert_histories(conn, [{**HIST, "user_intent": "改過"}]) == 1
    conn.commit()
    row = conn.execute(
        "SELECT user_intent, source, intent_embedding FROM shared_prompt_histories WHERE source_ref = %s",
        ("test:h1",),
    ).fetchone()
    assert row[0] == "改過" and row[1] == "civitai"
    assert row[2].dimensions() == 768
    assert conn.execute("SELECT count(*) FROM shared_prompt_histories WHERE source_ref = 'test:h1'").fetchone()[0] == 1


def test_upsert_presets_arrays_and_vector_search(conn):
    upsert_presets(conn, [PRESET])
    conn.commit()
    row = conn.execute(
        "SELECT tags, facet_ids, preset_embedding <=> %s AS dist "
        "FROM prompt_knowledge_presets WHERE source_ref = %s",
        (Vector(VEC), "test:h1:0"),
    ).fetchone()
    assert row[0] == ["a", "b"] and row[1] == ["scene.location"]
    assert row[2] < 1e-6
    hit = conn.execute(
        "SELECT source_ref FROM prompt_knowledge_presets WHERE facet_ids && %s::text[]",
        (["scene.location"],),
    ).fetchone()
    assert hit[0] == "test:h1:0"
