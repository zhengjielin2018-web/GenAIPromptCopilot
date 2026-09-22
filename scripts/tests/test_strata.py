import dataclasses

import pytest

from pipeline.strata import STRATA, Stratum


def test_strata_keys_are_unique():
    keys = [s.key for s in STRATA]
    assert len(keys) == len(set(keys))


def test_strata_quota_sums_to_nine_thousand():
    assert sum(s.quota for s in STRATA) == 9000


def test_every_stratum_has_a_positive_quota():
    assert all(s.quota > 0 for s in STRATA)


def test_baseline_stratum_matches_the_original_hardcoded_fetch_params():
    """baseline 必須跟現行寫死的抓取參數完全一致，這樣既有的 420 筆 raw 與其 cursor
    才能無縫接續、不會重抓。見 spec 2026-09-22-corpus-expansion-design.md §8。"""
    baseline = next(s for s in STRATA if s.key == "baseline")
    assert baseline.base_models is None
    assert baseline.period == "AllTime"
    assert baseline.sort == "Most Reactions"


def test_stratum_is_frozen():
    s = Stratum(key="x", base_models=None, period="AllTime", quota=1)
    with pytest.raises(dataclasses.FrozenInstanceError):
        s.quota = 2
