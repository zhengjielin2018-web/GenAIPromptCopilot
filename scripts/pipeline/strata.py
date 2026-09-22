"""分層抓取的配額表：baseModels x period 決定 Civitai 抓取的題材傾向。
數字依據見 docs/superpowers/specs/2026-09-22-corpus-expansion-design.md §4.2、§6
（唯讀公開 API 探測 + 100 筆抽樣關鍵字啟發式）。"""

from __future__ import annotations

from dataclasses import dataclass


@dataclass(frozen=True)
class Stratum:
    key: str                       # state.json 的鍵；一旦寫入就不可更名（改名=整層重抓）
    base_models: list[str] | None  # None = 不過濾（僅 baseline 使用）
    period: str                    # "AllTime" | "Year" | "Month" | "Week"
    quota: int                     # 這一層目標新增幾筆 raw（累積值，非單次呼叫上限）
    sort: str = "Most Reactions"


STRATA: tuple[Stratum, ...] = (
    Stratum(key="baseline", base_models=None, period="AllTime", quota=600),
    Stratum(key="sd15/year", base_models=["SD 1.5"], period="Year", quota=1600),
    Stratum(key="sdxl10/alltime", base_models=["SDXL 1.0"], period="AllTime", quota=1600),
    Stratum(key="sdxl10/year", base_models=["SDXL 1.0"], period="Year", quota=1200),
    Stratum(key="noobai/year", base_models=["NoobAI"], period="Year", quota=1200),
    Stratum(key="noobai/alltime", base_models=["NoobAI"], period="AllTime", quota=800),
    Stratum(key="sd15/alltime", base_models=["SD 1.5"], period="AllTime", quota=800),
    Stratum(key="illustrious/alltime", base_models=["Illustrious"], period="AllTime", quota=700),
    Stratum(key="pony/alltime", base_models=["Pony"], period="AllTime", quota=500),
)
