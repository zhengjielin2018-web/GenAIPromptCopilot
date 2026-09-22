"""階段 1：分層拉取 Civitai 圖片 metadata 到 data/raw/images.jsonl。
每層（pipeline.strata.STRATA 的一筆）各自的抓取進度記在 state.json 的
{"version": 2, "strata": {<key>: {cursor, fetched, done}}} 裡，供續跑。"""

from __future__ import annotations

import argparse
import json
from pathlib import Path

from pipeline.civitai_client import default_client
from pipeline.config import RAW_DIR, settings
from pipeline.jsonl import append_jsonl, existing_keys
from pipeline.strata import STRATA

RAW_PATH = RAW_DIR / "images.jsonl"
STATE_PATH = RAW_DIR / "state.json"


def _default_stratum_state() -> dict:
    return {"cursor": None, "fetched": 0, "done": False}


def _migrate_to_v2(raw: dict) -> dict:
    """v1（單層，整個物件就是 baseline 的狀態）→ v2（{version, strata}）。"""
    if raw.get("version") == 2:
        return {"version": 2, "strata": dict(raw.get("strata", {}))}
    if not raw:
        return {"version": 2, "strata": {}}
    return {"version": 2, "strata": {"baseline": raw}}


def _load_state(path: Path) -> dict:
    if path.exists():
        return _migrate_to_v2(json.loads(path.read_text(encoding="utf-8")))
    return {"version": 2, "strata": {}}


def _save_state(path: Path, doc: dict) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(doc), encoding="utf-8")


def _remaining_quota(stratum_quota: int, quota_scale: float, fetched_so_far: int) -> int:
    target = round(stratum_quota * quota_scale)
    return max(0, target - fetched_so_far)


def run_fetch(
    client,
    *,
    max_items: int,
    out_path: Path,
    state_path: Path,
    base_models: list[str] | None = None,
    sort: str = "Most Reactions",
    period: str = "AllTime",
    stratum_key: str = "baseline",
) -> int:
    doc = _load_state(state_path)
    st = doc["strata"].get(stratum_key) or _default_stratum_state()
    if st["done"]:
        return 0
    seen = existing_keys(out_path, "id")
    added = 0
    resume_cursor = st["cursor"]
    for item, page_cursor in client.iter_images(
        cursor=st["cursor"], base_models=base_models, sort=sort, period=period
    ):
        resume_cursor = page_cursor
        if item["id"] not in seen:
            append_jsonl(out_path, item)
            seen.add(item["id"])
            added += 1
        if added >= max_items:
            doc["strata"][stratum_key] = {
                "cursor": resume_cursor, "fetched": st["fetched"] + added, "done": False,
            }
            _save_state(state_path, doc)
            return added
    doc["strata"][stratum_key] = {"cursor": None, "fetched": st["fetched"] + added, "done": True}
    _save_state(state_path, doc)
    return added


def main(argv: list[str] | None = None) -> None:
    ap = argparse.ArgumentParser(description="階段 1：分層拉取 Civitai 圖片 metadata")
    ap.add_argument(
        "--quota-scale", type=float, default=1.0,
        help="乘上 pipeline.strata.STRATA 每層的 quota，四捨五入（用於分階段驗收先跑小規模）",
    )
    args = ap.parse_args(argv)
    client = default_client(settings.civitai_min_interval_s)
    doc = _load_state(STATE_PATH)
    total_added = 0
    for stratum in STRATA:
        st = doc["strata"].get(stratum.key) or _default_stratum_state()
        if st["done"]:
            print(f"fetch[{stratum.key}]: 已耗盡（done），略過")
            continue
        remaining = _remaining_quota(stratum.quota, args.quota_scale, st["fetched"])
        if remaining <= 0:
            print(f"fetch[{stratum.key}]: 已達配額 {st['fetched']}，略過")
            continue
        n = run_fetch(
            client, max_items=remaining, out_path=RAW_PATH, state_path=STATE_PATH,
            base_models=stratum.base_models, sort=stratum.sort, period=stratum.period,
            stratum_key=stratum.key,
        )
        doc = _load_state(STATE_PATH)  # run_fetch 已寫回，重讀取得最新的 doc 供下一層判斷
        total_added += n
        print(f"fetch[{stratum.key}]: +{n} → {RAW_PATH}")
    print(f"fetch: 合計 +{total_added} → {RAW_PATH}")


if __name__ == "__main__":
    main()
