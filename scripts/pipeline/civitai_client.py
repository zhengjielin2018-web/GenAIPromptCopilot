"""Civitai 公開 REST API：GET /api/v1/images，cursor 分頁。
匿名呼叫被平台壓在公開瀏覽等級，再加 nsfw=None 只取純 SFW。"""

from __future__ import annotations

import time
from collections.abc import Callable, Iterator

import httpx

from pipeline.ratelimit import RateLimiter, retry

BASE_URL = "https://civitai.com"
IMAGES_PATH = "/api/v1/images"


def _is_transient(e: Exception) -> bool:
    if isinstance(e, httpx.HTTPStatusError):
        return e.response.status_code in (429, 500, 502, 503, 504)
    return isinstance(e, (httpx.TimeoutException, httpx.TransportError))


class CivitaiClient:
    def __init__(
        self,
        http: httpx.Client,
        limiter: RateLimiter,
        sleep: Callable[[float], None] = time.sleep,
    ):
        self._http = http
        self._limiter = limiter
        self._sleep = sleep

    def _get_page(self, params: dict) -> dict:
        def call() -> dict:
            self._limiter.wait()
            r = self._http.get(IMAGES_PATH, params=params, timeout=30)
            r.raise_for_status()
            return r.json()

        return retry(call, attempts=6, base_delay_s=3.0, should_retry=_is_transient, sleep=self._sleep)

    def iter_images(
        self,
        *,
        limit: int = 200,
        cursor: str | None = None,
        base_models: list[str] | None = None,
    ) -> Iterator[tuple[dict, str | None]]:
        params: dict = {
            "limit": limit,
            "nsfw": "None",
            "withMeta": "true",
            "type": "image",
            "sort": "Most Reactions",
            "period": "AllTime",
        }
        if base_models:
            params["baseModels"] = ",".join(base_models)
        while True:
            if cursor is not None:
                params["cursor"] = cursor
            page = self._get_page(params)
            next_cursor = page.get("metadata", {}).get("nextCursor") or None
            for item in page.get("items", []):
                yield item, next_cursor
            if next_cursor is None:
                return
            cursor = next_cursor


def default_client(min_interval_s: float) -> CivitaiClient:
    http = httpx.Client(base_url=BASE_URL, headers={"User-Agent": "GenAIPromptCopilot-pipeline/0.1"})
    return CivitaiClient(http, RateLimiter(min_interval_s))
