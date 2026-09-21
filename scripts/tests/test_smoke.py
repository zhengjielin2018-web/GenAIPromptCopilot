from pipeline.config import DATA_DIR, EMBEDDED_DIR, RAW_DIR, REPO_ROOT, Settings


def test_repo_root_contains_docs():
    assert (REPO_ROOT / "docs").is_dir()


def test_data_dirs_are_under_scripts_data():
    assert RAW_DIR == DATA_DIR / "raw"
    assert EMBEDDED_DIR == DATA_DIR / "embedded"


def test_settings_defaults_without_env(monkeypatch):
    monkeypatch.delenv("GEMINI_API_KEY", raising=False)
    monkeypatch.delenv("EMBEDDING_DIMENSIONS", raising=False)
    monkeypatch.delenv("GEMINI_EMBEDDING_MODEL", raising=False)
    s = Settings(_env_file=None)
    assert s.embedding_dimensions == 768
    assert s.gemini_embedding_model == "gemini-embedding-001"
    assert s.postgres_dsn.startswith("postgresql://")
