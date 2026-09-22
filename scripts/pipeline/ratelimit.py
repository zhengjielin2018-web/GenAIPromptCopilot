"""最小間隔節流與指數退避重試。時間函式可注入，測試不用真的 sleep。"""

from __future__ import annotations

import threading
import time
from collections.abc import Callable


class RateLimiter:
    def __init__(
        self,
        min_interval_s: float,
        sleep: Callable[[float], None] = time.sleep,
        now: Callable[[], float] = time.monotonic,
    ):
        self._min = min_interval_s
        self._sleep = sleep
        self._now = now
        self._last: float | None = None
        self._lock = threading.Lock()

    def wait(self) -> None:
        """間隔是從「上一次請求開始」起算（_last 在送出前就更新）。
        必須互斥：多執行緒同時進來時，若各自讀到同一個 _last 再睡同樣長度，
        會一起衝出去，最小間隔就形同虛設。"""
        with self._lock:
            if self._last is not None:
                elapsed = self._now() - self._last
                if elapsed < self._min:
                    self._sleep(self._min - elapsed)
            self._last = self._now()


def retry[T](
    fn: Callable[[], T],
    *,
    attempts: int = 5,
    base_delay_s: float = 2.0,
    should_retry: Callable[[Exception], bool],
    sleep: Callable[[float], None] = time.sleep,
) -> T:
    for i in range(attempts):
        try:
            return fn()
        except Exception as e:  # noqa: BLE001 - 由 should_retry 決定
            if i == attempts - 1 or not should_retry(e):
                raise
            sleep(base_delay_s * (2**i))
    raise AssertionError("unreachable")
