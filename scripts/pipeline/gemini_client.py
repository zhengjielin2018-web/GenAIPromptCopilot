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


# Gemini 判定「這個內容不該生成」的理由。可能出現在 prompt_feedback.block_reason
# （擋輸入）或 candidates[0].finish_reason（擋輸出）。
CONTENT_BLOCK_REASONS: frozenset[str] = frozenset({
    "PROHIBITED_CONTENT", "SAFETY", "IMAGE_SAFETY", "BLOCKLIST", "JAILBREAK", "MODEL_ARMOR",
})
UNUSABLE_ATTEMPTS = 3  # 非內容攔截的空回應重試次數（傳輸錯誤另有 _call 的 6 次）
CONTENT_BLOCK_ATTEMPTS = 1  # 內容攔截的重試次數。只一次：會誤擋 SFW，但重送到過為止等於規避安全判定


class UnusableResponse(Exception):
    """模型回了 200 但沒有可用的文字。

    `is_content_block` 為真代表 Gemini 判定這個內容不該生成。**這種只重試一次**：
    攔截是機率性的（實測同一份 SFW 輸入也會被誤擋），送進去的內容是我們自己判定
    可接受的，容忍上游一次誤判合理；但重送到過為止等於利用分類器的不確定性規避
    安全判定，所以第二次仍被擋就交給呼叫端告訴使用者換個說法。

    其餘情形（MAX_TOKENS 截斷、空 candidate）與內容無關，重試是正當的，
    由 generate_structured 自己重試，呼叫端不必處理。
    """

    def __init__(self, message: str, *, block_reason: str | None = None,
                 finish_reason: str | None = None):
        super().__init__(message)
        self.block_reason = block_reason
        self.finish_reason = finish_reason

    @property
    def is_content_block(self) -> bool:
        return bool({self.block_reason, self.finish_reason} & CONTENT_BLOCK_REASONS)



def _is_transient(e: Exception) -> bool:
    if isinstance(e, errors.APIError):
        return e.code in (429, 500, 502, 503, 504)
    return isinstance(e, (httpx.TimeoutException, httpx.TransportError))


def _name(value) -> str | None:
    """列舉轉成純字串（BlockedReason.PROHIBITED_CONTENT → "PROHIBITED_CONTENT"）。"""
    if value is None:
        return None
    return str(getattr(value, "value", value))


def _response_problem(resp) -> tuple[str | None, str | None]:
    """挖出 (block_reason, finish_reason)。被擋掉的輸入 candidates 會是 None，
    真正的理由只在 prompt_feedback 裡——早期版本只看 candidates，等於把它丟掉。
    取診斷資訊本身不得再炸一次，所以全程 getattr。"""
    feedback = getattr(resp, "prompt_feedback", None)
    block = _name(getattr(feedback, "block_reason", None)) if feedback is not None else None
    candidates = getattr(resp, "candidates", None) or ()
    finish = _name(getattr(candidates[0], "finish_reason", None)) if candidates else None
    return block, finish


def _unusable_message(block: str | None, finish: str | None) -> str:
    if block:
        return f"Gemini 攔截了這次請求的內容（block_reason={block}）"
    return f"回應沒有文字內容（finish_reason={finish or 'unknown'}）"


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

        def attempt() -> M:
            resp = self._call(
                lambda: self._sdk.models.generate_content(
                    model=self._structure_model, contents=prompt, config=config
                )
            )
            if not resp.text:
                block, finish = _response_problem(resp)
                raise UnusableResponse(_unusable_message(block, finish),
                                       block_reason=block, finish_reason=finish)
            return schema.model_validate_json(resp.text)

        content_blocks = 0

        def should_retry(e: Exception) -> bool:
            nonlocal content_blocks
            if not isinstance(e, UnusableResponse):
                return False
            if not e.is_content_block:
                return True
            content_blocks += 1
            return content_blocks <= CONTENT_BLOCK_ATTEMPTS

        return retry(
            attempt, attempts=UNUSABLE_ATTEMPTS, base_delay_s=2.0,
            should_retry=should_retry, sleep=self._sleep,
        )

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
