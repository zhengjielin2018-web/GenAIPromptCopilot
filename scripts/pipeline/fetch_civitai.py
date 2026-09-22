"""階段 1：分層拉取 Civitai 圖片 metadata 到 data/raw/images.jsonl。
每層（pipeline.strata.STRATA 的一筆）各自的抓取進度記在 state.json 的
{"version": 2, "strata": {<key>: {cursor, fetched, done}}} 裡，供續跑。"""

from __future__ import annotations

import argparse
import json
import os
from pathlib import Path

from pipeline.civitai_client import default_client
from pipeline.config import RAW_DIR, settings
from pipeline.jsonl import append_jsonl, existing_keys
from pipeline.strata import STRATA

RAW_PATH = RAW_DIR / "images.jsonl"
STATE_PATH = RAW_DIR / "state.json"


class StateFileError(RuntimeError):
    """state.json 無法安全讀取，或版本不明時丟出。

    刻意不靜默重置成空 state：那等於幫使用者做了「刪掉 state.json」這個動作，
    會讓所有分層的 fetched 歸零、重新整層抓取（付費結構化呼叫的量級）。"""


def _default_stratum_state() -> dict:
    return {"cursor": None, "fetched": 0, "done": False}


def _migrate_to_v2(raw: dict) -> dict:
    """v1（單層，整個物件就是 baseline 的狀態）→ v2（{version, strata}）。"""
    version = raw.get("version") if raw else None
    if version == 2:
        return {"version": 2, "strata": dict(raw.get("strata", {}))}
    if version is not None:
        raise StateFileError(
            f"state.json 的 version={version!r} 不是已知版本（僅支援缺省欄位視為 v1、或 2）。"
            "為避免把看不懂的格式誤判成 v1 baseline 狀態而覆蓋壞資料，請手動檢查後再重跑，"
            "不要直接刪除 state.json（那會讓所有分層的 fetched 歸零、重新整層抓取）。"
        )
    if not raw:
        return {"version": 2, "strata": {}}
    return {"version": 2, "strata": {"baseline": raw}}


def _load_state(path: Path) -> dict:
    if not path.exists():
        return {"version": 2, "strata": {}}
    text = path.read_text(encoding="utf-8")
    try:
        raw = json.loads(text)
    except json.JSONDecodeError as e:
        raise StateFileError(
            f"{path} 無法解析為 JSON（可能是上次寫入中途被中斷，留下空檔或截斷內容）。"
            "請手動檢查、修復或還原備份後再重跑；不要直接刪除這個檔案——"
            "那會讓所有分層的 fetched 歸零，重新整層抓取一次（付費結構化呼叫的量級）。"
        ) from e
    return _migrate_to_v2(raw)


def _save_state(path: Path, doc: dict) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    tmp_path = path.parent / (path.name + ".tmp")
    tmp_path.write_text(json.dumps(doc), encoding="utf-8")
    os.replace(tmp_path, path)  # 原子換入：中斷只會留下未完成的 .tmp，目標檔案永遠是完整內容


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
    if st.get("done", False):
        return 0
    base_fetched = st.get("fetched", 0)
    start_cursor = st.get("cursor")
    seen = existing_keys(out_path, "id")
    added = 0
    resume_cursor = start_cursor
    prev_page_cursor = start_cursor
    for item, page_cursor in client.iter_images(
        cursor=start_cursor, base_models=base_models, sort=sort, period=period
    ):
        if page_cursor != prev_page_cursor:
            # 跨過頁界：前一頁已經完整寫進 out_path，把目前累積進度存檔，這樣中途
            # 當機時磁碟上的資料最多只有「這一頁尚未走完」的落差，不會整層重抓。
            doc["strata"][stratum_key] = {
                "cursor": page_cursor, "fetched": base_fetched + added, "done": False,
            }
            _save_state(state_path, doc)
            prev_page_cursor = page_cursor
        resume_cursor = page_cursor
        if item["id"] not in seen:
            append_jsonl(out_path, item)
            seen.add(item["id"])
            added += 1
        if added >= max_items:
            doc["strata"][stratum_key] = {
                "cursor": resume_cursor, "fetched": base_fetched + added, "done": False,
            }
            _save_state(state_path, doc)
            return added
    doc["strata"][stratum_key] = {"cursor": None, "fetched": base_fetched + added, "done": True}
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
        if st.get("done", False):
            print(f"fetch[{stratum.key}]: 已耗盡（done），略過")
            continue
        remaining = _remaining_quota(stratum.quota, args.quota_scale, st.get("fetched", 0))
        if remaining <= 0:
            print(f"fetch[{stratum.key}]: 已達配額 {st.get('fetched', 0)}，略過")
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
