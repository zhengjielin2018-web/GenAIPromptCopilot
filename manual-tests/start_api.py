"""起資料庫、再用 .env 裡的 DB 帳密把 API 跑在固定埠（預設 http://localhost:5000）。

只用標準函式庫。從任何目錄執行都可以：python manual-tests/start_api.py [--port 5000]
Ctrl+C 停 API；資料庫容器留著，要停自己 `docker compose stop db`。
"""

from __future__ import annotations

import argparse
import os
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
API_PROJECT = ROOT / "src" / "PromptCopilot.Api"


def read_env(path: Path) -> dict[str, str]:
    if not path.exists():
        sys.exit(f"找不到 {path}。先從 .env.example 複製一份並填好 POSTGRES_* 。")
    env: dict[str, str] = {}
    for line in path.read_text(encoding="utf-8").splitlines():
        line = line.strip()
        if not line or line.startswith("#") or "=" not in line:
            continue
        key, value = line.split("=", 1)
        env[key.strip()] = value.strip().strip('"').strip("'")
    return env


def has_llm_key() -> bool:
    """只看 user-secrets 有沒有 Llm:ApiKey 這個鍵，不印出值。"""
    r = subprocess.run(["dotnet", "user-secrets", "list", "--project", str(API_PROJECT)],
                       capture_output=True, text=True, encoding="utf-8", errors="replace")
    return any(line.split("=", 1)[0].strip() == "Llm:ApiKey" for line in r.stdout.splitlines())


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--port", type=int, default=5000)
    args = ap.parse_args()

    env = read_env(ROOT / ".env")
    print("起資料庫（等到 healthy）…")
    if subprocess.run(["docker", "compose", "up", "-d", "--wait", "db"], cwd=ROOT).returncode != 0:
        sys.exit("資料庫起不來。Docker Desktop 有開嗎？")

    if not has_llm_key():
        print('警告：user-secrets 裡沒有 Llm:ApiKey，API 會起來但每一輪都會失敗。\n'
              f'  設定：dotnet user-secrets set "Llm:ApiKey" "<GEMINI_API_KEY>" --project "{API_PROJECT}"')

    conn = (f"Host=localhost;Port={env.get('POSTGRES_PORT', '5432')};Database={env.get('POSTGRES_DB', 'prompt_copilot')};"
            f"Username={env.get('POSTGRES_USER', 'postgres')};Password={env.get('POSTGRES_PASSWORD', 'postgres')}")
    child_env = {**os.environ, "ASPNETCORE_ENVIRONMENT": "Development", "Database__ConnectionString": conn}
    url = f"http://localhost:{args.port}"
    print(f"啟動 API：{url}（Swagger：{url}/swagger），Ctrl+C 停止")
    try:
        subprocess.run(["dotnet", "run", "--project", str(API_PROJECT), "--no-launch-profile", "--urls", url],
                       cwd=ROOT / "src", env=child_env)
    except KeyboardInterrupt:
        pass


if __name__ == "__main__":
    main()
