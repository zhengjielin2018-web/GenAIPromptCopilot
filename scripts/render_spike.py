"""RunPod Serverless 生圖 spike：量延遲與成本（docs/ComfyUI整合可行性.md §9；部署步驟見 render/runpod/README.md）。

    python render_spike.py --gpu-price-per-hour 1.10 [--runs 4] [--cases rain-neon,sakura]

讀 .env 的 RUNPOD_API_KEY、RUNPOD_ENDPOINT_ID。每個測試提示詞送一個工作到 /run、輪詢 /status 到結束，一次只送一個
（量的是單張延遲，不是排隊），圖存到 --out（預設 scripts/data/render_spike/<時間>/，不進版控）。最後輸出 Markdown：
每張的 delayTime（RunPod 回報的排隊＋冷啟動）、executionTime、來回總時間、P50／P95 與估計費用，同時寫進 --out。

第一張的 delayTime 含冷啟動。要再量一次冷啟動，等 worker 閒置超過 endpoint 的 idle timeout、縮回 0 之後再跑。
費用只是估計：RunPod 從 worker 啟動算到停止（含載入模型與 idle timeout），準確數字看 RunPod 的帳單。
"""

from __future__ import annotations

import argparse
import base64
import copy
import json
import math
import re
import sys
import time
from collections.abc import Callable
from dataclasses import asdict, dataclass
from datetime import datetime
from pathlib import Path

import httpx

sys.path.insert(0, str(Path(__file__).resolve().parent))

from pipeline.config import DATA_DIR, REPO_ROOT, settings  # noqa: E402

API = "https://api.runpod.ai/v2"
WORKFLOW_PATH = REPO_ROOT / "render" / "workflows" / "txt2img-sdxl.json"
DOCKERFILE_PATH = REPO_ROOT / "render" / "runpod" / "Dockerfile"
# workflow 裡要改的節點：id 與預期的 class_type。範本換了節點編號，這裡一起改（build_workflow 會擋）
NODES = {"positive": ("6", "CLIPTextEncode"), "negative": ("7", "CLIPTextEncode"), "sampler": ("3", "KSampler")}
TERMINAL = {"COMPLETED", "FAILED", "CANCELLED", "TIMED_OUT"}

NEGATIVE = (
    "nsfw, lowres, bad anatomy, bad hands, text, error, missing fingers, extra digit, fewer digits, cropped, "
    "worst quality, low quality, jpeg artifacts, signature, watermark, username, blurry"
)


@dataclass(frozen=True)
class Case:
    name: str
    positive: str
    checks: tuple[str, ...]  # 看圖時要確認畫出來的要素，之後拿來對照自評


CASES = (
    Case("rain-neon", "masterpiece, best quality, 1girl, solo, silver hair, long hair, white raincoat, hood up, "
         "neon lights, city street, rainy night, reflections, looking at viewer, upper body",
         ("銀色長髮", "白色雨衣、兜帽戴上", "霓虹燈", "街道、雨夜", "半身", "看鏡頭")),
    Case("sakura", "masterpiece, best quality, 1girl, solo, black hair, twintails, school uniform, pleated skirt, "
         "cherry blossoms, petals in foreground, from below, smile, spring",
         ("黑髮雙馬尾", "制服、百褶裙", "櫻花", "前景有花瓣", "仰角", "微笑")),
    Case("cafe", "masterpiece, best quality, 1boy, solo, brown hair, short hair, glasses, white shirt, apron, cafe, "
         "indoors, holding cup, coffee, warm lighting, sitting, from side",
         ("男性、棕色短髮", "眼鏡", "白襯衫、圍裙", "咖啡廳室內", "拿著杯子", "暖色光", "坐姿、側面")),
    Case("snow-shrine", "masterpiece, best quality, 1girl, solo, red hair, long hair, miko, hakama, shrine, torii, "
         "snow, snowing, winter, night, lantern, full body, looking back",
         ("紅色長髮", "巫女服", "神社、鳥居", "下雪的夜晚", "燈籠", "全身", "回頭")),
    Case("lake-sunset", "masterpiece, best quality, scenery, no humans, mountains, lake, reflection, sunset, "
         "orange sky, clouds, wide shot",
         ("沒有人物", "山", "湖面倒影", "夕陽、橘色天空", "遠景")),
)


@dataclass
class Result:
    case: str
    run: int
    seed: int
    status: str
    delay_ms: int | None
    execution_ms: int | None
    wall_ms: int
    image: str | None = None
    error: str | None = None


# ---------- workflow ----------


def load_workflow(path: Path = WORKFLOW_PATH) -> dict:
    return json.loads(path.read_text(encoding="utf-8"))


def build_workflow(template: dict, positive: str, negative: str, seed: int) -> dict:
    """複製範本、填入提示詞與 seed。範本的節點對不上 NODES 就丟例外：送出去才發現會白花一次冷啟動。"""
    wf = copy.deepcopy(template)
    for role, (node_id, class_type) in NODES.items():
        actual = (wf.get(node_id) or {}).get("class_type")
        if actual != class_type:
            raise ValueError(f"workflow 的節點 {node_id}（{role}）應該是 {class_type}，實際是 {actual}")
    wf[NODES["positive"][0]]["inputs"]["text"] = positive
    wf[NODES["negative"][0]]["inputs"]["text"] = negative
    wf[NODES["sampler"][0]]["inputs"]["seed"] = seed
    return wf


def workflow_checkpoint(workflow: dict) -> str | None:
    loaders = (n for n in workflow.values() if n.get("class_type") == "CheckpointLoaderSimple")
    return next((n["inputs"]["ckpt_name"] for n in loaders), None)


def dockerfile_checkpoints(path: Path = DOCKERFILE_PATH) -> list[str]:
    """Dockerfile 裡 comfy model download 下載到 checkpoints 的檔名。"""
    text = path.read_text(encoding="utf-8").replace("\\\n", " ")
    return [m.group(2) for m in re.finditer(r"--relative-path\s+models/(checkpoints)\b.*?--filename\s+(\S+)", text)]


# ---------- RunPod ----------


def extract_images(job: dict) -> list[bytes]:
    """worker-comfyui 5.x 的輸出：output.images[{filename, type, data}]。只收 base64（endpoint 沒設 S3 時的預設）。"""
    output = job.get("output") or {}
    images = output.get("images") or []
    if not images:
        raise ValueError(f"工作沒有回圖：{output.get('errors') or job.get('error') or output}")
    for img in images:
        if img.get("type") != "base64":
            raise ValueError(f"只支援 base64 輸出，收到 {img.get('type')}；endpoint 不要設 S3 相關的環境變數")
    return [base64.b64decode(img["data"]) for img in images]


class RunPodClient:
    def __init__(self, http: httpx.Client, sleep: Callable[[float], None] = time.sleep,
                 clock: Callable[[], float] = time.monotonic):
        self.http, self.sleep, self.clock = http, sleep, clock

    @classmethod
    def create(cls, api_key: str, endpoint_id: str) -> RunPodClient:
        return cls(httpx.Client(base_url=f"{API}/{endpoint_id}", headers={"Authorization": f"Bearer {api_key}"},
                                timeout=30))

    def submit(self, workflow: dict) -> str:
        # 送出失敗不重送：結果不明時可能已經建立了工作，重送會多跑（多付）一次
        r = self.http.post("/run", json={"input": {"workflow": workflow}})
        r.raise_for_status()
        return r.json()["id"]

    def wait(self, job_id: str, poll_s: float = 1.0, timeout_s: float = 300) -> dict:
        """輪詢到終止狀態。超過 timeout_s 就取消工作（盡力而為）再丟 TimeoutError，不讓它在背景繼續計費。"""
        start = self.clock()
        while True:
            r = self.http.get(f"/status/{job_id}")
            r.raise_for_status()
            job = r.json()
            if job.get("status") in TERMINAL:
                return job
            if self.clock() - start > timeout_s:
                try:
                    self.http.post(f"/cancel/{job_id}")
                except httpx.HTTPError:
                    pass
                raise TimeoutError(f"工作 {job_id} 超過 {timeout_s:g} 秒沒結束，已要求取消")
            self.sleep(poll_s)


def run_case(client: RunPodClient, template: dict, case: Case, run: int, seed: int, out: Path,
             poll_s: float, timeout_s: float, clock: Callable[[], float] = time.monotonic) -> Result:
    start = clock()
    job: dict = {}
    try:
        job_id = client.submit(build_workflow(template, case.positive, NEGATIVE, seed))
        job = client.wait(job_id, poll_s, timeout_s)
        image = None
        if job.get("status") == "COMPLETED":
            path = out / f"{case.name}-{run}.png"
            path.write_bytes(extract_images(job)[0])
            image = path.name
        error = None if image else str(job.get("error") or job.get("output"))
        return Result(case.name, run, seed, job.get("status", "?"), job.get("delayTime"), job.get("executionTime"),
                      int((clock() - start) * 1000), image, error)
    except (httpx.HTTPError, TimeoutError, ValueError, KeyError) as e:
        # 一律記 ERROR：RunPod 回 COMPLETED 但取不到圖的，不能算進成功張數與 P50
        return Result(case.name, run, seed, "ERROR", job.get("delayTime"), job.get("executionTime"),
                      int((clock() - start) * 1000), None, f"{type(e).__name__}: {e}")


# ---------- 報表 ----------


def percentile(values: list[float], p: float) -> float | None:
    """nearest-rank；樣本少（spike 只有幾十張）時比內插直觀：P95 就是排第 ceil(0.95n) 的那張。"""
    if not values:
        return None
    s = sorted(values)
    return s[max(0, math.ceil(p / 100 * len(s)) - 1)]


def _sec(ms: float | None) -> str:
    return "-" if ms is None else f"{ms / 1000:.1f}"


def summarize(results: list[Result], price_per_hour: float, idle_timeout_s: float, twd_rate: float) -> str:
    ok = [r for r in results if r.status == "COMPLETED"]
    lines = ["| 提示詞 | 第幾次 | 狀態 | delayTime（秒） | executionTime（秒） | 來回總時間（秒） | 圖 |",
             "| :--- | ---: | :--- | ---: | ---: | ---: | :--- |"]
    for r in results:
        lines.append(f"| {r.case} | {r.run} | {r.status} | {_sec(r.delay_ms)} | {_sec(r.execution_ms)} | "
                     f"{_sec(r.wall_ms)} | {r.image or r.error or '-'} |")
    lines += ["", f"成功 {len(ok)}／{len(results)} 張。", "",
              "| | P50（秒） | P95（秒） |", "| :--- | ---: | ---: |"]
    for label, values in (("delayTime（排隊＋冷啟動）", [r.delay_ms for r in ok]),
                          ("executionTime", [r.execution_ms for r in ok]),
                          ("來回總時間", [r.wall_ms for r in ok])):
        vs = [v for v in values if v is not None]
        lines.append(f"| {label} | {_sec(percentile(vs, 50))} | {_sec(percentile(vs, 95))} |")
    if results:
        lines += ["", f"第一張的 delayTime：{_sec(results[0].delay_ms)} 秒（worker 原本縮在 0 的話，這就是冷啟動）。"]
    if ok:
        per_s = price_per_hour / 3600
        low = sum((r.execution_ms or 0) / 1000 for r in ok) * per_s / len(ok)
        high = sum(((r.execution_ms or 0) + (r.delay_ms or 0)) / 1000 + idle_timeout_s for r in ok) * per_s / len(ok)
        lines += ["", f"估計每張費用（GPU 每小時 US${price_per_hour:g}）：下限 US${low:.4f}（只算執行）、"
                      f"上限 US${high:.4f}（加上 delayTime 與 {idle_timeout_s:g} 秒 idle timeout）。",
                  f"換算每月 500 張：約 NT${low * 500 * twd_rate:.0f}–{high * 500 * twd_rate:.0f}"
                  f"（1 美元 = {twd_rate:g} 台幣；不含存放映像檔與審圖的費用）。"]
    return "\n".join(lines)


# ---------- main ----------


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--gpu-price-per-hour", type=float, required=True,
                    help="endpoint 選的 GPU 每小時價格（美元），例如 4090 填 1.10；只用來估費用")
    ap.add_argument("--runs", type=int, default=1, help="每個提示詞跑幾次（預設 1，五個提示詞共 5 張）")
    ap.add_argument("--cases", help=f"只跑這些，逗號分隔：{','.join(c.name for c in CASES)}")
    ap.add_argument("--seed", type=int, default=1234, help="第一次的 seed；第 n 次用 seed+n-1")
    ap.add_argument("--out", type=Path, help="存圖與報表的資料夾")
    ap.add_argument("--idle-timeout", type=float, default=5, help="endpoint 的 idle timeout（秒），只用來估費用")
    ap.add_argument("--twd-rate", type=float, default=31.8, help="1 美元換多少台幣")
    ap.add_argument("--poll", type=float, default=1.0, help="輪詢間隔（秒）")
    ap.add_argument("--timeout", type=float, default=300, help="單張逾時（秒），含冷啟動")
    args = ap.parse_args(argv)

    if not settings.runpod_api_key or not settings.runpod_endpoint_id:
        print("缺 RUNPOD_API_KEY 或 RUNPOD_ENDPOINT_ID：寫進 repo 根目錄的 .env（見 render/runpod/README.md）",
              file=sys.stderr)
        return 2
    cases = CASES
    if args.cases:
        wanted = {c.strip() for c in args.cases.split(",") if c.strip()}
        cases = tuple(c for c in CASES if c.name in wanted)
        if unknown := wanted - {c.name for c in cases}:
            print(f"沒有這些提示詞：{', '.join(sorted(unknown))}", file=sys.stderr)
            return 2

    template = load_workflow()
    ckpt, baked = workflow_checkpoint(template), dockerfile_checkpoints()
    if ckpt not in baked:
        # 只是提醒：模型放在網路磁碟上的部署（render/runpod/README.md 做法 A）不看 Dockerfile
        print(f"注意：workflow 用的 checkpoint {ckpt} 不在 Dockerfile 下載的清單 {baked} 裡；"
              "endpoint 用網路磁碟的話，確認磁碟上有這個檔", file=sys.stderr)

    out = args.out or DATA_DIR / "render_spike" / datetime.now().strftime("%Y%m%d-%H%M%S")
    out.mkdir(parents=True, exist_ok=True)
    client = RunPodClient.create(settings.runpod_api_key, settings.runpod_endpoint_id)
    results: list[Result] = []
    for run in range(1, args.runs + 1):
        for case in cases:
            r = run_case(client, template, case, run, args.seed + run - 1, out, args.poll, args.timeout)
            results.append(r)
            print(f"{case.name} #{run}: {r.status} delay={_sec(r.delay_ms)}s exec={_sec(r.execution_ms)}s "
                  f"wall={_sec(r.wall_ms)}s {r.error or ''}", file=sys.stderr)

    report = summarize(results, args.gpu_price_per_hour, args.idle_timeout, args.twd_rate)
    checks = "\n".join(f"- {c.name}：{'、'.join(c.checks)}" for c in cases)
    (out / "report.md").write_text(f"{report}\n\n## 看圖時要確認的要素\n\n{checks}\n", encoding="utf-8")
    (out / "results.json").write_text(json.dumps([asdict(r) for r in results], ensure_ascii=False, indent=2),
                                      encoding="utf-8")
    print(report)
    print(f"\n圖與報表在 {out}", file=sys.stderr)
    return 0 if len(results) == sum(r.status == "COMPLETED" for r in results) else 1


if __name__ == "__main__":
    sys.exit(main())
