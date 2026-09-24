"""把知識庫匯出成公開的種子 dump（給 docker compose 的 seed 服務用）。

做法：開一個用完即丟的 pgvector 容器、套上 db/init/001_schema.sql → 從開發庫 pg_dump 兩張知識表
（唯讀，data-only）直接串流灌進去 → 在丟棄庫裡刪掉使用者自己存的紀錄 → 從丟棄庫 pg_dump → 停掉容器。

開發庫只被讀，不建庫、不刪列，API 開著也沒關係。灌進全新 schema 這一步順便證明
dump 灌得回去（跟 seed 服務走同一條 pg_restore）。

只用標準函式庫：
    python scripts/export_seed.py [--version 1]
產出在 scripts/data/seed/，不進版控；印出的 gh 指令要自己按。
"""

from __future__ import annotations

import argparse
import subprocess
import sys
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
SEED_DIR = ROOT / "scripts" / "data" / "seed"
SCHEMA = ROOT / "db" / "init" / "001_schema.sql"
TABLES = ("prompt_knowledge_presets", "shared_prompt_histories")
EXPORT_IMAGE = "pgvector/pgvector:pg16"        # 跟 docker-compose.yml 的 db 同一個
EXPORT_CONTAINER = "prompt-copilot-seed-export"
EXPORT_DB = "seed_export"
COUNT_SQL = (
    "SELECT 'presets:' || split_part(source_ref, ':', 1), count(*) FROM prompt_knowledge_presets GROUP BY 1 "
    "UNION ALL SELECT 'histories:' || source, count(*) FROM shared_prompt_histories GROUP BY 1"
)
sleep = time.sleep


def asset_name(version: int) -> str:
    return f"prompt_copilot_seed_v{version}.dump"


def release_tag(version: int) -> str:
    return f"seed-v{version}"


def gh_command(version: int, path: Path) -> str:
    return (
        f'gh release create {release_tag(version)} "{path}" '
        f'--title "知識庫種子 v{version}" '
        f'--notes "docker compose 首次啟動灌進知識庫的 pg_dump（data-only）。授權與免責聲明見 docs/資料來源.md。"'
    )


def parse_counts(stdout: str) -> dict[str, int]:
    counts: dict[str, int] = {}
    for line in stdout.splitlines():
        if "|" not in line:
            continue
        key, value = line.rsplit("|", 1)
        counts[key.strip()] = int(value)
    return counts


def run(argv: list[str], *, input: str | None = None, check: bool = True) -> subprocess.CompletedProcess[str]:
    """跑一個指令並收下輸出；測試會換掉它。"""
    return subprocess.run(
        argv, cwd=ROOT, check=check, input=input,
        capture_output=True, text=True, encoding="utf-8", errors="replace",
    )


def pipe(src: list[str], dst: list[str]) -> None:
    """src 的 stdout 以位元組直接接到 dst 的 stdin（dump 不落地）。兩邊的 stderr 照常印到終端機。"""
    p1 = subprocess.Popen(src, cwd=ROOT, stdout=subprocess.PIPE)
    try:
        p2 = subprocess.run(dst, cwd=ROOT, stdin=p1.stdout)
    finally:
        p1.stdout.close()
        rc1 = p1.wait()
    if rc1:
        raise subprocess.CalledProcessError(rc1, src)
    if p2.returncode:
        raise subprocess.CalledProcessError(p2.returncode, dst)


def wait_ready(timeout_s: int = 90) -> None:
    """等丟棄庫起好。走 TCP：官方 image 初始化期間的暫時 server 只聽 socket，TCP 通了才是正式的那個。"""
    for _ in range(timeout_s):
        try:
            run(["docker", "exec", EXPORT_CONTAINER, "pg_isready", "-h", "127.0.0.1",
                 "-U", "postgres", "-d", EXPORT_DB])
            return
        except subprocess.CalledProcessError:
            sleep(1)
    raise SystemExit(f"{EXPORT_CONTAINER} 在 {timeout_s} 秒內沒有起來。")


def _throwaway_sql(statement: str) -> str:
    argv = ["docker", "exec", EXPORT_CONTAINER, "psql", "-U", "postgres", "-d", EXPORT_DB, "-tAc", statement]
    return run(argv).stdout


def export(version: int, user: str, db: str) -> tuple[Path, dict[str, int]]:
    tables = [arg for t in TABLES for arg in ("-t", t)]
    remote = f"/tmp/{asset_name(version)}"
    out = SEED_DIR / asset_name(version)

    run(["docker", "rm", "-f", EXPORT_CONTAINER], check=False)          # 上次中斷留下的
    run(["docker", "run", "-d", "--rm", "--name", EXPORT_CONTAINER,
         "-e", "POSTGRES_PASSWORD=export", "-e", f"POSTGRES_DB={EXPORT_DB}", EXPORT_IMAGE])
    try:
        wait_ready()
        run(["docker", "exec", "-i", EXPORT_CONTAINER, "psql", "-U", "postgres", "-d", EXPORT_DB,
             "-v", "ON_ERROR_STOP=1", "-q"], input=SCHEMA.read_text(encoding="utf-8"))
        pipe(["docker", "compose", "exec", "-T", "db", "pg_dump", "-U", user, "-d", db, "-Fc", "--data-only", *tables],
             ["docker", "exec", "-i", EXPORT_CONTAINER, "pg_restore", "-U", "postgres", "-d", EXPORT_DB,
              "--data-only", "--no-owner", "--single-transaction"])
        _throwaway_sql("DELETE FROM shared_prompt_histories WHERE source = 'user'")
        counts = parse_counts(_throwaway_sql(COUNT_SQL))
        run(["docker", "exec", EXPORT_CONTAINER, "pg_dump", "-U", "postgres", "-d", EXPORT_DB,
             "-Fc", "--data-only", *tables, "-f", remote])
        SEED_DIR.mkdir(parents=True, exist_ok=True)
        run(["docker", "cp", f"{EXPORT_CONTAINER}:{remote}", str(out)])
    finally:
        run(["docker", "stop", EXPORT_CONTAINER], check=False)          # --rm：停了就刪
    return out, counts


def read_env(path: Path) -> dict[str, str]:
    env: dict[str, str] = {}
    if not path.exists():
        return env
    for line in path.read_text(encoding="utf-8").splitlines():
        line = line.strip()
        if not line or line.startswith("#") or "=" not in line:
            continue
        key, value = line.split("=", 1)
        env[key.strip()] = value.strip().strip('"').strip("'")
    return env


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--version", type=int, default=1, help="Release 版本號（tag seed-v<N>）")
    args = ap.parse_args(argv)

    env = read_env(ROOT / ".env")
    user = env.get("POSTGRES_USER", "postgres")
    db = env.get("POSTGRES_DB", "prompt_copilot")

    print(f"開丟棄容器 {EXPORT_CONTAINER}，從 {db} 串流兩張知識表進去、刪除使用者紀錄、再匯出（約 2 分鐘）…")
    out, counts = export(args.version, user, db)

    print("匯出完成：")
    for key, n in sorted(counts.items()):
        print(f"  {key:<22}{n:>8,}")
    print(f"  檔案  {out}（{out.stat().st_size / 1_000_000:.1f} MB）")
    print("\n下一步（手動）：")
    print("  " + gh_command(args.version, out))
    return 0


if __name__ == "__main__":
    sys.exit(main())
