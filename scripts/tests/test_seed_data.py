from seed_data import STAGES, stages_from


def test_stage_order():
    assert STAGES == ["fetch", "clean", "structure", "embed", "load"]


def test_stages_from_returns_suffix():
    assert stages_from("structure") == ["structure", "embed", "load"]
    assert stages_from("fetch") == STAGES
