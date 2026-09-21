"""psycopg 3 連線工廠。所有 DB 存取都經由 connect()，確保 vector 型別已註冊。"""

from __future__ import annotations

import psycopg
from pgvector.psycopg import register_vector

from pipeline.config import settings


def connect() -> psycopg.Connection:
    conn = psycopg.connect(settings.postgres_dsn)
    try:
        register_vector(conn)
    except Exception:
        conn.close()
        raise
    return conn


def db_available() -> bool:
    try:
        with psycopg.connect(settings.postgres_dsn, connect_timeout=2):
            return True
    except psycopg.OperationalError:
        return False
