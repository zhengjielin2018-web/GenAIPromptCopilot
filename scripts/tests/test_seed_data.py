from seed_data import STAGES, main, stages_from


def test_stage_order():
    assert STAGES == ["fetch", "clean", "structure", "embed", "load"]


def test_stages_from_returns_suffix():
    assert stages_from("structure") == ["structure", "embed", "load"]
    assert stages_from("fetch") == STAGES


def _record_stage_mains(monkeypatch):
    from pipeline import clean, embed, fetch_civitai, load, structure

    calls: dict[str, list[str]] = {}

    def recorder(name):
        def _main(argv):
            calls[name] = argv
        return _main

    for name, module in (
        ("fetch", fetch_civitai), ("clean", clean), ("structure", structure),
        ("embed", embed), ("load", load),
    ):
        monkeypatch.setattr(module, "main", recorder(name))
    return calls


def test_main_threads_the_right_argv_to_each_stage(monkeypatch):
    calls = _record_stage_mains(monkeypatch)
    main(["--max-items", "7", "--max-records", "3", "--reindex"])
    assert calls == {
        "fetch": ["--max-items", "7"],
        "clean": [],
        "structure": ["--max-records", "3"],
        "embed": ["--reindex"],
        "load": [],
    }


def test_main_from_stage_skips_earlier_stages(monkeypatch):
    calls = _record_stage_mains(monkeypatch)
    main(["--from", "embed"])
    assert set(calls) == {"embed", "load"}
