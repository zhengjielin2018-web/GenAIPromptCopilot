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
    main(["--quota-scale", "0.5", "--max-records", "3", "--reindex"])
    assert calls == {
        "fetch": ["--quota-scale", "0.5"],
        "clean": [],
        "structure": ["--max-records", "3"],
        "embed": ["--reindex"],
        "load": [],
    }


def test_main_from_stage_skips_earlier_stages(monkeypatch):
    calls = _record_stage_mains(monkeypatch)
    main(["--from", "embed"])
    assert set(calls) == {"embed", "load"}


def test_fetch_argv_accepted_by_real_parser(monkeypatch, tmp_path):
    """
    Verifies that the argv seed_data.main() constructs for fetch is actually accepted
    by fetch_civitai.main()'s real argparse parser. The sibling test uses a monkeypatch
    mock and therefore cannot catch argument-name mismatches between seed_data and
    fetch_civitai. This test runs argparse for real, so it fails when the contract drifts
    (e.g., if seed_data.py forwards --quota-scale but fetch_civitai.py accepts --quota_scale,
    or any future rename that updates one but not the other).
    """
    from unittest.mock import MagicMock

    from pipeline import clean, embed, fetch_civitai, load, structure

    # Step 1: Mock only the non-fetch stages to capture what seed_data builds for fetch
    calls: dict[str, list[str]] = {}

    def recorder(name):
        def _main(argv):
            calls[name] = argv
        return _main

    monkeypatch.setattr(clean, "main", recorder("clean"))
    monkeypatch.setattr(structure, "main", recorder("structure"))
    monkeypatch.setattr(embed, "main", recorder("embed"))
    monkeypatch.setattr(load, "main", recorder("load"))

    # Mock fetch_civitai's network and I/O dependencies before calling seed_data.main()
    monkeypatch.setattr(fetch_civitai, "default_client", lambda _: MagicMock())
    monkeypatch.setattr(fetch_civitai, "run_fetch", lambda *a, **kw: 0)
    monkeypatch.setattr(fetch_civitai, "RAW_PATH", tmp_path / "images.jsonl")
    monkeypatch.setattr(fetch_civitai, "STATE_PATH", tmp_path / "state.json")

    # Step 2: Call seed_data.main() with the real fetch_civitai.main() in the call chain
    # This ensures the real argparse in fetch_civitai runs
    main(["--quota-scale", "0.5"])

    # If we reach here without SystemExit, the argv was accepted by the real parser
