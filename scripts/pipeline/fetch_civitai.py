"""階段 1：拉 Civitai 圖片 metadata 到 data/raw/images.jsonl，state.json 記 cursor 供續跑。"""

from __future__ import annotations

import argparse
import json
from pathlib import Path

from pipeline.civitai_client import default_client
from pipeline.config import RAW_DIR, settings
from pipeline.jsonl import append_jsonl, existing_keys

RAW_PATH = RAW_DIR / "images.jsonl"
STATE_PATH = RAW_DIR / "state.json"


def _load_state(path: Path) -> dict:
    if path.exists():
        return json.loads(path.read_text(encoding="utf-8"))
    return {"cursor": None, "fetched": 0, "done": False}


def _save_state(path: Path, state: dict) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(state), encoding="utf-8")


def run_fetch(client, *, max_items: int, out_path: Path, state_path: Path,
              base_models: list[str] | None = None) -> int:
    state = _load_state(state_path)
    if state["done"]:
        return 0
    seen = existing_keys(out_path, "id")
    added = 0
    last_cursor = state["cursor"]
    for item, next_cursor in client.iter_images(cursor=state["cursor"], base_models=base_models):
        if item["id"] not in seen:
            append_jsonl(out_path, item)
            seen.add(item["id"])
            added += 1
        last_cursor = next_cursor
        if added >= max_items:
            _save_state(state_path, {"cursor": last_cursor, "fetched": len(seen), "done": last_cursor is None})
            return added
    _save_state(state_path, {"cursor": None, "fetched": len(seen), "done": True})
    return added


def main(argv: list[str] | None = None) -> None:
    ap = argparse.ArgumentParser(description="階段 1：拉取 Civitai 圖片 metadata")
    ap.add_argument("--max-items", type=int, default=3000, help="本次最多新增筆數")
    ap.add_argument("--base-models", default="", help="逗號分隔，如 'Illustrious,SDXL 1.0'；空=不過濾")
    args = ap.parse_args(argv)
    base_models = [b.strip() for b in args.base_models.split(",") if b.strip()] or None
    client = default_client(settings.civitai_min_interval_s)
    n = run_fetch(client, max_items=args.max_items, out_path=RAW_PATH, state_path=STATE_PATH,
                  base_models=base_models)
    print(f"fetch: +{n} → {RAW_PATH}")


if __name__ == "__main__":
    main()
