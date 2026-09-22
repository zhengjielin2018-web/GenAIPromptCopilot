import math
from types import SimpleNamespace

import pytest
from pydantic import BaseModel

from pipeline.gemini_client import BATCH_SIZE, GeminiClient
from pipeline.ratelimit import RateLimiter


class Out(BaseModel):
    title: str
    n: int


class FakeModels:
    def __init__(self):
        self.generate_calls = []
        self.embed_calls = []
        self.fail_first_generate = False
        self.fail_generate_with_code = None

    def generate_content(self, *, model, contents, config):
        self.generate_calls.append((model, contents, config))
        if self.fail_generate_with_code is not None:
            raise _api_error(self.fail_generate_with_code)
        if self.fail_first_generate and len(self.generate_calls) == 1:
            raise _api_error(429)
        return SimpleNamespace(text='{"title": "雨夜", "n": 3}')

    def embed_content(self, *, model, contents, config):
        self.embed_calls.append((model, list(contents), config))
        return SimpleNamespace(embeddings=[SimpleNamespace(values=[3.0, 4.0]) for _ in contents])


def _api_error(code):
    from google.genai import errors
    return errors.APIError(code, {"error": {"message": "rate limited", "status": "RESOURCE_EXHAUSTED"}})


def _client(models):
    sdk = SimpleNamespace(models=models)
    return GeminiClient(sdk, structure_model="m-struct", embedding_model="m-embed", dimensions=2,
                        limiter=RateLimiter(0, sleep=lambda s: None), sleep=lambda s: None)


def test_generate_structured_parses_into_schema_and_sets_json_config():
    models = FakeModels()
    out = _client(models).generate_structured("hi", Out)
    assert out == Out(title="雨夜", n=3)
    model, contents, config = models.generate_calls[0]
    assert model == "m-struct" and contents == "hi"
    assert config.response_mime_type == "application/json"
    assert config.response_schema is Out


def test_generate_structured_retries_on_429():
    models = FakeModels()
    models.fail_first_generate = True
    assert _client(models).generate_structured("hi", Out).n == 3
    assert len(models.generate_calls) == 2


def test_generate_structured_does_not_retry_non_transient_error():
    from google.genai import errors

    models = FakeModels()
    models.fail_generate_with_code = 404
    with pytest.raises(errors.APIError):
        _client(models).generate_structured("hi", Out)
    assert len(models.generate_calls) == 1


def test_embed_batch_chunks_normalizes_and_passes_task_type():
    models = FakeModels()
    texts = [f"t{i}" for i in range(BATCH_SIZE + 5)]
    vecs = _client(models).embed_batch(texts, task_type="RETRIEVAL_DOCUMENT")
    assert len(vecs) == len(texts)
    assert [len(c[1]) for c in models.embed_calls] == [BATCH_SIZE, 5]
    assert models.embed_calls[0][2].task_type == "RETRIEVAL_DOCUMENT"
    assert models.embed_calls[0][2].output_dimensionality == 2
    assert math.isclose(sum(v * v for v in vecs[0]), 1.0, rel_tol=1e-6)  # [3,4] → [0.6,0.8]


def test_embed_batch_empty_is_noop():
    models = FakeModels()
    assert _client(models).embed_batch([], task_type="RETRIEVAL_QUERY") == []
    assert models.embed_calls == []


def test_default_client_requires_api_key(monkeypatch):
    from pipeline import gemini_client
    monkeypatch.setattr(gemini_client.settings, "gemini_api_key", "")
    with pytest.raises(RuntimeError, match="GEMINI_API_KEY"):
        gemini_client.default_client()


def test_generate_structured_raises_unusable_response_when_text_is_none():
    """模型回 200 但沒有文字（安全性攔截／MAX_TOKENS）時，必須是可辨識的例外，
    而不是讓 pydantic 丟出看不懂的 json_type 錯誤。"""
    from pipeline.gemini_client import UnusableResponse

    class Resp:
        text = None
        candidates = [SimpleNamespace(finish_reason="SAFETY")]

    class SDK:
        class models:
            @staticmethod
            def generate_content(**kwargs):
                return Resp()

    client = GeminiClient(SDK(), structure_model="m", embedding_model="e", dimensions=4,
                          limiter=RateLimiter(0, sleep=lambda s: None), sleep=lambda s: None)
    with pytest.raises(UnusableResponse) as ei:
        client.generate_structured("p", Out)
    assert "SAFETY" in str(ei.value)


class _Feedback(SimpleNamespace):
    pass


def _blocked_sdk(*, block_reason=None, finish_reason=None, succeed_after=None):
    """回 200 但沒有文字的假 SDK。succeed_after=N 時，第 N+1 次呼叫改回正常文字。"""
    calls = []

    class models:
        @staticmethod
        def generate_content(**kwargs):
            calls.append(kwargs)
            if succeed_after is not None and len(calls) > succeed_after:
                return SimpleNamespace(text='{"title": "雨夜", "n": 3}')
            return SimpleNamespace(
                text=None,
                candidates=([SimpleNamespace(finish_reason=finish_reason)] if finish_reason else None),
                prompt_feedback=(_Feedback(block_reason=block_reason) if block_reason else None),
            )

    return SimpleNamespace(models=models), calls


def test_unusable_response_carries_the_block_reason_instead_of_unknown():
    """Gemini 唯一給出的資訊就是 block_reason；把它吞成 unknown 等於讓使用者無從判斷
    是該換個說法、還是程式壞了。"""
    from pipeline.gemini_client import UnusableResponse

    sdk, _ = _blocked_sdk(block_reason="PROHIBITED_CONTENT")
    with pytest.raises(UnusableResponse) as ei:
        _client(sdk.models).generate_structured("p", Out)
    assert "PROHIBITED_CONTENT" in str(ei.value)
    assert ei.value.block_reason == "PROHIBITED_CONTENT"
    assert ei.value.is_content_block


def test_generate_structured_does_not_retry_a_content_block():
    """攔截是機率性的，重送到過為止等於規避安全判定。只送一次。"""
    from pipeline.gemini_client import UnusableResponse

    sdk, calls = _blocked_sdk(block_reason="PROHIBITED_CONTENT")
    with pytest.raises(UnusableResponse):
        _client(sdk.models).generate_structured("p", Out)
    assert len(calls) == 1


def test_generate_structured_retries_a_truncated_response():
    """MAX_TOKENS 截斷跟內容無關，重送是正當的。"""
    sdk, calls = _blocked_sdk(finish_reason="MAX_TOKENS", succeed_after=2)
    assert _client(sdk.models).generate_structured("p", Out).n == 3
    assert len(calls) == 3


def test_generate_structured_retries_an_empty_response_with_no_block_reason():
    sdk, calls = _blocked_sdk(succeed_after=1)
    assert _client(sdk.models).generate_structured("p", Out).n == 3
    assert len(calls) == 2


def test_generate_structured_gives_up_on_repeated_truncation():
    from pipeline.gemini_client import UnusableResponse

    sdk, calls = _blocked_sdk(finish_reason="MAX_TOKENS")
    with pytest.raises(UnusableResponse) as ei:
        _client(sdk.models).generate_structured("p", Out)
    assert not ei.value.is_content_block
    assert len(calls) > 1


def test_unusable_response_treats_safety_as_a_content_block_too():
    from pipeline.gemini_client import UnusableResponse

    sdk, calls = _blocked_sdk(block_reason="SAFETY")
    with pytest.raises(UnusableResponse) as ei:
        _client(sdk.models).generate_structured("p", Out)
    assert ei.value.is_content_block
    assert len(calls) == 1
