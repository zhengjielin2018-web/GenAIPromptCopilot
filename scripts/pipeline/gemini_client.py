"""Gemini 呼叫的唯一出口：結構化生成與批次 embedding，含節流與 429/5xx 重試。
SDK 物件可注入，測試不打網路。"""

from __future__ import annotations

import math
import time
from collections.abc import Callable
from typing import Literal

import httpx
from google import genai
from google.genai import errors, types
from pydantic import BaseModel

from pipeline.config import settings
from pipeline.ratelimit import RateLimiter, retry

BATCH_SIZE = 32
TaskType = Literal["RETRIEVAL_DOCUMENT", "RETRIEVAL_QUERY"]


class UnusableResponse(Exception):
    """模型回了 200 但沒有可用的文字（安全性攔截、MAX_TOKENS 截斷、空 candidate）。
    這不是傳輸錯誤，retry 沒有意義——同一份輸入重送結果一樣，呼叫端應該跳過這筆。"""



def _is_transient(e: Exception) -> bool:
    if isinstance(e, errors.APIError):
        return e.code in (429, 500, 502, 503, 504)
    return isinstance(e, (httpx.TimeoutException, httpx.TransportError))


def _finish_reason(resp) -> str:
    """從回應裡挖出 finish_reason 供錯誤訊息使用；挖不到就回 unknown，不要因為
    取錯誤訊息本身再炸一次。"""
    try:
        return str(resp.candidates[0].finish_reason)
    except Exception:  # noqa: BLE001 - 純診斷用途
        return "unknown"


def _l2_normalize(v: list[float]) -> list[float]:
    norm = math.sqrt(sum(x * x for x in v)) or 1.0
    return [x / norm for x in v]


class GeminiClient:
    def __init__(
        self,
        sdk,
        *,
        structure_model: str,
        embedding_model: str,
        dimensions: int,
        limiter: RateLimiter,
        sleep: Callable[[float], None] = time.sleep,
    ):
        self._sdk = sdk
        self._structure_model = structure_model
        self._embedding_model = embedding_model
        self._dimensions = dimensions
        self._limiter = limiter
        self._sleep = sleep

    def _call(self, fn):
        def wrapped():
            self._limiter.wait()
            return fn()

        return retry(wrapped, attempts=6, base_delay_s=4.0, should_retry=_is_transient, sleep=self._sleep)

    def generate_structured[M: BaseModel](
        self, prompt: str, schema: type[M], *, temperature: float = 0.2
    ) -> M:
        config = types.GenerateContentConfig(
            response_mime_type="application/json",
            response_schema=schema,
            temperature=temperature,
        )
        resp = self._call(
            lambda: self._sdk.models.generate_content(
                model=self._structure_model, contents=prompt, config=config
            )
        )
        if not resp.text:
            raise UnusableResponse(f"回應沒有文字內容（finish_reason={_finish_reason(resp)}）")
        return schema.model_validate_json(resp.text)

    def _embed_chunk(self, chunk: list[str], task_type: TaskType) -> list[list[float]]:
        config = types.EmbedContentConfig(task_type=task_type, output_dimensionality=self._dimensions)
        resp = self._call(
            lambda: self._sdk.models.embed_content(
                model=self._embedding_model, contents=chunk, config=config
            )
        )
        return [_l2_normalize(list(e.values)) for e in resp.embeddings]

    def embed_batch(self, texts: list[str], *, task_type: TaskType) -> list[list[float]]:
        out: list[list[float]] = []
        for i in range(0, len(texts), BATCH_SIZE):
            out.extend(self._embed_chunk(texts[i : i + BATCH_SIZE], task_type))
        return out


def default_client() -> GeminiClient:
    if not settings.gemini_api_key:
        raise RuntimeError("GEMINI_API_KEY 未設定（見 .env.example）")
    return GeminiClient(
        genai.Client(api_key=settings.gemini_api_key),
        structure_model=settings.gemini_structure_model,
        embedding_model=settings.gemini_embedding_model,
        dimensions=settings.embedding_dimensions,
        limiter=RateLimiter(settings.gemini_min_interval_s),
    )
