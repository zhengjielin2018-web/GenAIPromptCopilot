"""環境變數與路徑常數。所有階段都從這裡取設定，不各自讀 os.environ。"""

from __future__ import annotations

import os
from pathlib import Path

from dotenv import dotenv_values
from pydantic import BaseModel

REPO_ROOT = Path(__file__).resolve().parents[2]
SCRIPTS_DIR = REPO_ROOT / "scripts"
DATA_DIR = SCRIPTS_DIR / "data"
RAW_DIR = DATA_DIR / "raw"
CLEAN_DIR = DATA_DIR / "clean"
STRUCTURED_DIR = DATA_DIR / "structured"
EMBEDDED_DIR = DATA_DIR / "embedded"
FACETS_PATH = REPO_ROOT / "src" / "PromptCopilot.Api" / "Configuration" / "facets.yaml"


class Settings(BaseModel):
    postgres_user: str = "postgres"
    postgres_password: str = "postgres"
    postgres_db: str = "prompt_copilot"
    postgres_host: str = "localhost"
    postgres_port: int = 5432

    gemini_api_key: str = ""
    gemini_structure_model: str = "gemini-3.5-flash-lite"
    gemini_embedding_model: str = "gemini-embedding-001"
    embedding_dimensions: int = 768

    civitai_min_interval_s: float = 1.0
    gemini_min_interval_s: float = 0.5

    def __init__(self, _env_file: str | Path | None = REPO_ROOT / ".env", **overrides):
        field_names = type(self).model_fields
        file_values = (
            dotenv_values(_env_file) if _env_file is not None and Path(_env_file).is_file() else {}
        )
        env_values = {
            name: os.environ[name.upper()] for name in field_names if name.upper() in os.environ
        }
        values = {
            name: file_values[name.upper()]
            for name in field_names
            if name.upper() in file_values and file_values[name.upper()] is not None
        }
        values.update(env_values)
        values.update(overrides)
        super().__init__(**values)

    @property
    def postgres_dsn(self) -> str:
        return (
            f"postgresql://{self.postgres_user}:{self.postgres_password}"
            f"@{self.postgres_host}:{self.postgres_port}/{self.postgres_db}"
        )


settings = Settings()
