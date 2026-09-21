"""跨階段契約測試：確保 load.py 的 SQL 參數是 embed 階段輸出紀錄形狀的子集。
不需要資料庫，故不標記為 integration，在預設（非 integration）selection 下就會跑。"""

import re

from pipeline import load as load_module


def test_load_sql_params_are_a_subset_of_the_embedded_record_shape():
    history_fields = {
        "source_ref", "user_intent", "positive_prompt", "negative_prompt",
        "subject_profile", "image_url", "embedding",
    }
    preset_fields = {
        "source_ref", "title", "category", "description", "tags", "facet_ids",
        "prompt_snippet", "negative_snippet", "image_url", "embedding",
    }
    for sql, fields in (
        (load_module.HISTORIES_SQL, history_fields),
        (load_module.PRESETS_SQL, preset_fields),
    ):
        assert set(re.findall(r"%\((\w+)\)s", sql)) <= fields
