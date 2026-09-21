import os

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


def test_settings_does_not_mutate_os_environ(tmp_path, monkeypatch):
    monkeypatch.delenv("POSTGRES_DB", raising=False)
    env_file = tmp_path / "custom.env"
    env_file.write_text("POSTGRES_DB=from_file\n", encoding="utf-8")
    s = Settings(_env_file=env_file)
    assert s.postgres_db == "from_file"          # file value is read
    assert "POSTGRES_DB" not in os.environ        # but never leaked into the process


def test_os_environ_beats_env_file(tmp_path, monkeypatch):
    env_file = tmp_path / "custom.env"
    env_file.write_text("POSTGRES_DB=from_file\n", encoding="utf-8")
    monkeypatch.setenv("POSTGRES_DB", "from_environ")
    assert Settings(_env_file=env_file).postgres_db == "from_environ"
