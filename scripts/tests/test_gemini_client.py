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
