from pipeline.jsonl import append_jsonl, existing_keys, read_jsonl, write_jsonl


def test_roundtrip_preserves_unicode(tmp_path):
    p = tmp_path / "a" / "b.jsonl"
    append_jsonl(p, {"id": 1, "text": "雨夜"})
    append_jsonl(p, {"id": 2, "text": "霓虹"})
    assert list(read_jsonl(p)) == [{"id": 1, "text": "雨夜"}, {"id": 2, "text": "霓虹"}]
    assert "雨夜" in p.read_text(encoding="utf-8")  # 不是 \uXXXX


def test_read_missing_file_yields_nothing(tmp_path):
    assert list(read_jsonl(tmp_path / "nope.jsonl")) == []


def test_write_overwrites_and_returns_count(tmp_path):
    p = tmp_path / "x.jsonl"
    append_jsonl(p, {"id": 9})
    n = write_jsonl(p, [{"id": 1}, {"id": 2}])
    assert n == 2
    assert [r["id"] for r in read_jsonl(p)] == [1, 2]


def test_existing_keys(tmp_path):
    p = tmp_path / "x.jsonl"
    write_jsonl(p, [{"k": "a"}, {"k": "b"}, {"other": 1}])
    assert existing_keys(p, "k") == {"a", "b"}
