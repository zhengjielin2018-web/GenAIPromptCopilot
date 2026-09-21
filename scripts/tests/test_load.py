import pytest
from pgvector import Vector

from pipeline import db
from pipeline.jsonl import write_jsonl
from pipeline.load import run_load, upsert_histories, upsert_presets

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
    hits = {
        r[0]
        for r in conn.execute(
            "SELECT source_ref FROM prompt_knowledge_presets WHERE facet_ids && %s::text[]",
            (["scene.location"],),
        ).fetchall()
    }
    assert "test:h1:0" in hits


def test_upsert_presets_refreshes_every_column_on_conflict(conn):
    upsert_presets(conn, [PRESET])
    conn.commit()
    changed = {
        **PRESET,
        "title": "改過的標題",
        "category": "Style",
        "description": "改過的描述",
        "tags": ["x", "y"],
        "facet_ids": ["scene.weather"],
        "prompt_snippet": "changed snippet",
        "negative_snippet": "3d",
        "image_url": "https://example.invalid/changed.png",
        "embedding": [0.0, 1.0] + [0.0] * 766,
    }
    assert upsert_presets(conn, [changed]) == 1
    conn.commit()
    row = conn.execute(
        "SELECT title, category, description, tags, facet_ids, prompt_snippet, "
        "negative_snippet, image_url, preset_embedding <=> %s AS dist "
        "FROM prompt_knowledge_presets WHERE source_ref = %s",
        (Vector(changed["embedding"]), "test:h1:0"),
    ).fetchone()
    assert row[:8] == (
        "改過的標題", "Style", "改過的描述", ["x", "y"], ["scene.weather"],
        "changed snippet", "3d", "https://example.invalid/changed.png",
    )
    assert row[8] < 1e-6
    count = conn.execute(
        "SELECT count(*) FROM prompt_knowledge_presets WHERE source_ref = %s", ("test:h1:0",)
    ).fetchone()[0]
    assert count == 1


def test_upsert_histories_refreshes_the_embedding_on_conflict(conn):
    upsert_histories(conn, [HIST])
    conn.commit()
    other = [0.0, 1.0] + [0.0] * 766
    upsert_histories(conn, [{**HIST, "embedding": other}])
    conn.commit()
    dist = conn.execute(
        "SELECT intent_embedding <=> %s FROM shared_prompt_histories WHERE source_ref = %s",
        (Vector(other), "test:h1"),
    ).fetchone()[0]
    assert dist < 1e-6


def test_upsert_histories_refreshes_every_column_on_conflict(conn):
    upsert_histories(conn, [HIST])
    conn.commit()
    changed = {
        **HIST,
        "user_intent": "改過的意圖",
        "positive_prompt": "changed positive",
        "negative_prompt": "changed negative",
        "subject_profile": "landscape",
        "image_url": "https://example.invalid/changed.png",
        "embedding": [0.0, 1.0] + [0.0] * 766,
    }
    assert upsert_histories(conn, [changed]) == 1
    conn.commit()
    row = conn.execute(
        "SELECT user_intent, positive_prompt, negative_prompt, subject_profile, image_url, "
        "intent_embedding <=> %s AS dist "
        "FROM shared_prompt_histories WHERE source_ref = %s",
        (Vector(changed["embedding"]), "test:h1"),
    ).fetchone()
    assert row[:5] == (
        "改過的意圖", "changed positive", "changed negative", "landscape",
        "https://example.invalid/changed.png",
    )
    assert row[5] < 1e-6
    count = conn.execute(
        "SELECT count(*) FROM shared_prompt_histories WHERE source_ref = %s", ("test:h1",)
    ).fetchone()[0]
    assert count == 1


def test_run_load_reads_both_files_and_persists(conn, tmp_path):
    h, p = tmp_path / "histories.jsonl", tmp_path / "presets.jsonl"
    write_jsonl(h, [HIST])
    write_jsonl(p, [PRESET])
    assert run_load(conn, histories_path=h, presets_path=p) == (1, 1)
    assert conn.execute(
        "SELECT user_intent, source FROM shared_prompt_histories WHERE source_ref = %s",
        ("test:h1",),
    ).fetchone() == ("測試", "civitai")
    assert conn.execute(
        "SELECT count(*) FROM prompt_knowledge_presets WHERE source_ref = %s", ("test:h1:0",)
    ).fetchone()[0] == 1
