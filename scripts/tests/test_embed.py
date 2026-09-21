from pipeline.embed import history_text, preset_text, run_embed
from pipeline.jsonl import read_jsonl, write_jsonl


class FakeGemini:
    def __init__(self):
        self.batches = []

    def embed_batch(self, texts, *, task_type):
        assert task_type == "RETRIEVAL_DOCUMENT"
        self.batches.append(list(texts))
        return [[float(len(t)), 0.0] for t in texts]


def test_text_builders():
    assert history_text({"user_intent": "雨夜"}) == "雨夜"
    assert preset_text({"title": "T", "description": "D", "tags": ["a", "b"]}) == "T。D。標籤：a, b"


def test_run_embed_writes_embeddings_and_resumes(tmp_path):
    inp, out = tmp_path / "in.jsonl", tmp_path / "out.jsonl"
    write_jsonl(inp, [{"source_ref": "a", "user_intent": "xx"}, {"source_ref": "b", "user_intent": "yyy"}])
    g = FakeGemini()
    assert run_embed(g, in_path=inp, out_path=out, text_fn=history_text) == 2
    rows = list(read_jsonl(out))
    assert rows[0]["embedding"] == [2.0, 0.0] and rows[1]["embedding"] == [3.0, 0.0]
    assert rows[0]["user_intent"] == "xx"  # 原欄位保留

    write_jsonl(inp, [{"source_ref": "a", "user_intent": "xx"},
                      {"source_ref": "b", "user_intent": "yyy"},
                      {"source_ref": "c", "user_intent": "z"}])
    assert run_embed(g, in_path=inp, out_path=out, text_fn=history_text) == 1
    assert g.batches[-1] == ["z"]
    assert [r["source_ref"] for r in read_jsonl(out)] == ["a", "b", "c"]


def test_run_embed_reindex_recomputes_everything(tmp_path):
    inp, out = tmp_path / "in.jsonl", tmp_path / "out.jsonl"
    write_jsonl(inp, [{"source_ref": "a", "user_intent": "xx"}])
    g = FakeGemini()
    run_embed(g, in_path=inp, out_path=out, text_fn=history_text)
    assert run_embed(g, in_path=inp, out_path=out, text_fn=history_text, reindex=True) == 1
    assert len(list(read_jsonl(out))) == 1
    assert len(g.batches) == 2


def test_run_embed_chunks_requests(tmp_path):
    inp, out = tmp_path / "in.jsonl", tmp_path / "out.jsonl"
    write_jsonl(inp, [{"source_ref": str(i), "user_intent": "t"} for i in range(5)])
    g = FakeGemini()
    run_embed(g, in_path=inp, out_path=out, text_fn=history_text, chunk=2)
    assert [len(b) for b in g.batches] == [2, 2, 1]
