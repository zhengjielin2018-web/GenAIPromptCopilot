import httpx

from pipeline.civitai_client import CivitaiClient
from pipeline.ratelimit import RateLimiter


def _page(ids, next_cursor):
    return {
        "items": [{"id": i, "nsfwLevel": "None", "meta": {"prompt": f"p{i}"}} for i in ids],
        "metadata": {"nextCursor": next_cursor} if next_cursor else {},
    }


def _client(handler):
    http = httpx.Client(transport=httpx.MockTransport(handler), base_url="https://civitai.com")
    return CivitaiClient(http, RateLimiter(0, sleep=lambda s: None), sleep=lambda s: None)


def test_iter_images_follows_cursor_and_sends_required_params():
    seen_params = []

    def handler(req: httpx.Request) -> httpx.Response:
        seen_params.append(dict(req.url.params))
        cursor = req.url.params.get("cursor")
        if cursor is None:
            return httpx.Response(200, json=_page([1, 2], "c2"))
        return httpx.Response(200, json=_page([3], None))

    items = list(_client(handler).iter_images(limit=2))
    assert [it["id"] for it, _ in items] == [1, 2, 3]
    assert [c for _, c in items] == ["c2", "c2", None]
    p = seen_params[0]
    assert p["nsfw"] == "None" and p["withMeta"] == "true" and p["type"] == "image"
    assert p["sort"] == "Most Reactions" and p["limit"] == "2"
    assert seen_params[1]["cursor"] == "c2"


def test_iter_images_retries_on_429_then_succeeds():
    calls = {"n": 0}

    def handler(req: httpx.Request) -> httpx.Response:
        calls["n"] += 1
        if calls["n"] == 1:
            return httpx.Response(429, json={"error": "slow down"})
        return httpx.Response(200, json=_page([7], None))

    items = list(_client(handler).iter_images())
    assert [it["id"] for it, _ in items] == [7]
    assert calls["n"] == 2


def test_iter_images_passes_base_models_csv():
    captured = {}

    def handler(req: httpx.Request) -> httpx.Response:
        captured.update(dict(req.url.params))
        return httpx.Response(200, json=_page([], None))

    list(_client(handler).iter_images(base_models=["Illustrious", "SDXL 1.0"]))
    assert captured["baseModels"] == "Illustrious,SDXL 1.0"
