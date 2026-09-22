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


def test_rate_limiter_serializes_concurrent_waits():
    """併發呼叫下 wait() 必須互斥，否則多個執行緒會讀到同一個 _last、睡同樣長度後
    一起衝出去，最小間隔形同虛設。用真實時鐘量總耗時：5 次 wait 若真的被序列化，
    至少要花 4 個間隔；沒有鎖的話幾乎瞬間就全部返回。"""
    import threading
    import time

    interval = 0.05
    limiter = RateLimiter(interval)
    t0 = time.monotonic()
    threads = [threading.Thread(target=limiter.wait) for _ in range(5)]
    for t in threads:
        t.start()
    for t in threads:
        t.join()
    elapsed = time.monotonic() - t0
    assert elapsed >= interval * 4 * 0.8, f"wait() 沒有互斥，5 次只花了 {elapsed:.3f}s"
