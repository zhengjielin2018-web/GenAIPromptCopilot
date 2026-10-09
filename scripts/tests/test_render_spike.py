import base64
import json
import sys
from pathlib import Path

import httpx
import pytest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import render_spike as rs


def client_with(handler, now=None):
    """MockTransport 的 RunPodClient；sleep 不真的睡，時鐘每次 sleep 前進輪詢間隔。"""
    clock = now if now is not None else [0.0]

    def sleep(s):
        clock[0] += s

    http = httpx.Client(base_url="https://api.runpod.ai/v2/ep", transport=httpx.MockTransport(handler))
    return rs.RunPodClient(http, sleep=sleep, clock=lambda: clock[0])


def test_shipped_workflow_has_the_nodes_we_patch_and_its_checkpoint_is_baked_into_the_image():
    template = rs.load_workflow()
    rs.build_workflow(template, "a", "b", 1)  # 節點對不上會丟例外
    assert rs.workflow_checkpoint(template) in rs.dockerfile_checkpoints()


def test_build_workflow_fills_prompts_and_seed_without_touching_the_template():
    template = rs.load_workflow()
    before = json.dumps(template, sort_keys=True)
    wf = rs.build_workflow(template, "1girl, silver hair", "nsfw, lowres", 42)
    assert wf["6"]["inputs"]["text"] == "1girl, silver hair"
    assert wf["7"]["inputs"]["text"] == "nsfw, lowres"
    assert wf["3"]["inputs"]["seed"] == 42
    assert json.dumps(template, sort_keys=True) == before


def test_build_workflow_rejects_a_template_whose_nodes_moved():
    template = rs.load_workflow()
    template["6"]["class_type"] = "KSampler"
    with pytest.raises(ValueError, match="節點 6"):
        rs.build_workflow(template, "a", "b", 1)


def test_dockerfile_checkpoints_reads_filenames_across_line_continuations(tmp_path):
    f = tmp_path / "Dockerfile"
    f.write_text("FROM x\nRUN comfy model download \\\n    --url https://h/a.safetensors \\\n"
                 "    --relative-path models/checkpoints \\\n    --filename a.safetensors\n"
                 "RUN comfy model download --url https://h/l.safetensors --relative-path models/loras "
                 "--filename l.safetensors\n")
    assert rs.dockerfile_checkpoints(f) == ["a.safetensors"]


def test_extract_images_decodes_base64():
    job = {"output": {"images": [{"filename": "render_00001_.png", "type": "base64",
                                  "data": base64.b64encode(b"png-bytes").decode()}]}}
    assert rs.extract_images(job) == [b"png-bytes"]


def test_extract_images_rejects_s3_output_and_missing_images():
    with pytest.raises(ValueError, match="base64"):
        rs.extract_images({"output": {"images": [{"type": "s3_url", "data": "https://bucket/x.png"}]}})
    with pytest.raises(ValueError, match="沒有回圖"):
        rs.extract_images({"output": {"errors": ["Prompt outputs failed validation"]}})


def test_wait_polls_until_the_job_finishes():
    statuses = iter(["IN_QUEUE", "IN_PROGRESS", "COMPLETED"])
    seen = []

    def handler(request):
        seen.append((request.method, request.url.path))
        return httpx.Response(200, json={"id": "j1", "status": next(statuses), "delayTime": 1500})

    job = client_with(handler).wait("j1", poll_s=1, timeout_s=60)
    assert job["status"] == "COMPLETED"
    assert seen == [("GET", "/v2/ep/status/j1")] * 3


def test_wait_cancels_the_job_when_it_times_out():
    seen = []

    def handler(request):
        seen.append((request.method, request.url.path))
        return httpx.Response(200, json={"id": "j1", "status": "IN_QUEUE"})

    with pytest.raises(TimeoutError, match="取消"):
        client_with(handler).wait("j1", poll_s=10, timeout_s=25)
    assert seen[-1] == ("POST", "/v2/ep/cancel/j1")


def read_timeout():
    raise httpx.ReadTimeout("x")


@pytest.mark.parametrize("failure", [lambda: httpx.Response(503), read_timeout], ids=["status-503", "read-timeout"])
def test_wait_cancels_the_job_when_polling_fails(failure):
    seen = []

    def handler(request):
        seen.append((request.method, request.url.path))
        if request.method == "POST":
            return httpx.Response(200, json={"id": "j1", "status": "CANCELLED"})
        if len(seen) == 1:
            return httpx.Response(200, json={"id": "j1", "status": "IN_QUEUE"})
        return failure()

    with pytest.raises(httpx.HTTPError):
        client_with(handler).wait("j1", poll_s=1, timeout_s=60)
    assert seen[-1] == ("POST", "/v2/ep/cancel/j1")


def test_run_case_saves_the_image_and_records_runpod_timings(tmp_path):
    png = base64.b64encode(b"png").decode()

    def handler(request):
        if request.url.path.endswith("/run"):
            body = json.loads(request.content)
            assert body["input"]["workflow"]["3"]["inputs"]["seed"] == 7
            return httpx.Response(200, json={"id": "j1", "status": "IN_QUEUE"})
        return httpx.Response(200, json={"id": "j1", "status": "COMPLETED", "delayTime": 30000, "executionTime": 5000,
                                         "output": {"images": [{"type": "base64", "data": png}]}})

    case = rs.CASES[0]
    r = rs.run_case(client_with(handler), rs.load_workflow(), case, 1, 7, tmp_path, 1, 60, clock=lambda: 0.0)
    assert (r.status, r.delay_ms, r.execution_ms, r.image) == ("COMPLETED", 30000, 5000, f"{case.name}-1.png")
    assert (tmp_path / r.image).read_bytes() == b"png"


def test_run_case_records_a_failed_job_without_raising(tmp_path):
    def handler(request):
        if request.url.path.endswith("/run"):
            return httpx.Response(200, json={"id": "j1", "status": "IN_QUEUE"})
        return httpx.Response(200, json={"id": "j1", "status": "FAILED", "error": "ckpt not found"})

    r = rs.run_case(client_with(handler), rs.load_workflow(), rs.CASES[0], 1, 7, tmp_path, 1, 60, clock=lambda: 0.0)
    assert (r.status, r.image, r.error) == ("FAILED", None, "ckpt not found")


def test_run_case_does_not_count_a_completed_job_without_an_image_as_success(tmp_path):
    def handler(request):
        if request.url.path.endswith("/run"):
            return httpx.Response(200, json={"id": "j1", "status": "IN_QUEUE"})
        return httpx.Response(200, json={"id": "j1", "status": "COMPLETED", "delayTime": 100, "executionTime": 900,
                                         "output": {"errors": ["Prompt outputs failed validation"]}})

    r = rs.run_case(client_with(handler), rs.load_workflow(), rs.CASES[0], 1, 7, tmp_path, 1, 60, clock=lambda: 0.0)
    assert (r.status, r.image, r.execution_ms) == ("ERROR", None, 900)
    assert "沒有回圖" in r.error


def test_percentile_is_nearest_rank():
    assert rs.percentile([], 50) is None
    assert rs.percentile([3, 1, 2], 50) == 2
    assert rs.percentile(list(range(1, 21)), 95) == 19


def test_summarize_reports_cold_start_and_cost_bounds():
    results = [rs.Result("a", 1, 1, "COMPLETED", 40000, 6000, 47000, "a-1.png"),
               rs.Result("b", 1, 2, "COMPLETED", 200, 6000, 7000, "b-1.png"),
               rs.Result("c", 1, 3, "FAILED", 100, None, 500, None, "boom")]
    text = rs.summarize(results, price_per_hour=3.6, idle_timeout_s=5, twd_rate=30)
    assert "成功 2／3 張" in text
    assert "第一張的 delayTime：40.0 秒" in text
    # 每秒 US$0.001：下限只算執行 6 秒；零星使用每張各自加 delayTime 與 idle，平均 (6+40+5 + 6+0.2+5)/2 = 31.1 秒
    assert "下限 US$0.0060" in text and "零星使用 US$0.0311" in text
    assert "NT$90–466" in text
    # 這一輪是連續跑的，idle 只算一次；失敗的那張也用了 GPU：47 + 7 + 0.5 + 5 = 59.5 秒
    assert "這一輪估計 US$0.0595" in text
