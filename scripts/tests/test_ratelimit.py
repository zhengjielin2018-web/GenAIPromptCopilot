import pytest

from pipeline.ratelimit import RateLimiter, retry


def test_rate_limiter_sleeps_only_when_called_too_soon():
    clock = [100.0]
    slept: list[float] = []
    rl = RateLimiter(1.0, sleep=slept.append, now=lambda: clock[0])
    rl.wait()  # 第一次不等
    clock[0] += 0.3
    rl.wait()  # 太快 → 等 0.7
    clock[0] += 5
    rl.wait()  # 夠久 → 不等
    assert [round(s, 3) for s in slept] == [0.7]


def test_retry_succeeds_after_transient_failures():
    calls = {"n": 0}
    slept: list[float] = []

    def flaky():
        calls["n"] += 1
        if calls["n"] < 3:
            raise TimeoutError("boom")
        return "ok"

    out = retry(flaky, attempts=5, base_delay_s=1.0,
                should_retry=lambda e: isinstance(e, TimeoutError), sleep=slept.append)
    assert out == "ok"
    assert calls["n"] == 3
    assert slept == [1.0, 2.0]  # 指數退避


def test_retry_gives_up_and_reraises():
    def always():
        raise TimeoutError("boom")

    with pytest.raises(TimeoutError):
        retry(always, attempts=3, base_delay_s=0.0,
              should_retry=lambda e: True, sleep=lambda s: None)


def test_retry_does_not_retry_non_matching_errors():
    calls = {"n": 0}

    def bad():
        calls["n"] += 1
        raise ValueError("nope")

    with pytest.raises(ValueError):
        retry(bad, attempts=5, base_delay_s=0.0,
              should_retry=lambda e: isinstance(e, TimeoutError), sleep=lambda s: None)
    assert calls["n"] == 1
