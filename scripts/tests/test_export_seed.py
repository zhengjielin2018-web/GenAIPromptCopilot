"""export_seed 不碰 docker：run()／pipe() 被換成記錄器，驗證指令順序、只讀開發庫、失敗時的收尾。
pipe() 本身用兩個真的 Python 子行程測。"""

from __future__ import annotations

import subprocess
import sys

import pytest

import export_seed
from export_seed import (
    EXPORT_CONTAINER,
    EXPORT_DB,
    EXPORT_IMAGE,
    asset_name,
    export,
    gh_command,
    parse_counts,
    pipe,
    release_tag,
    wait_ready,
)


def test_asset_and_tag_follow_the_version():
    assert asset_name(1) == "prompt_copilot_seed_v1.dump"
    assert release_tag(3) == "seed-v3"


def test_gh_command_uploads_the_dump_to_the_versioned_release(tmp_path):
    cmd = gh_command(2, tmp_path / "prompt_copilot_seed_v2.dump")
    assert cmd.startswith("gh release create seed-v2 ")
    assert "prompt_copilot_seed_v2.dump" in cmd
    assert "--title" in cmd


def test_parse_counts_reads_psql_tuples_only_output():
    out = "presets:civitai|18977\npresets:kisegae|377\nhistories:civitai|6294\n"
    assert parse_counts(out) == {"presets:civitai": 18977, "presets:kisegae": 377, "histories:civitai": 6294}


class Recorder:
    """假的 run() 與 pipe()：依序記下每個 argv；可指定某個關鍵字的呼叫要炸。"""

    def __init__(self, fail_on: str | None = None):
        self.calls: list[list[str]] = []
        self.inputs: dict[int, str] = {}
        self.pipes: list[tuple[list[str], list[str]]] = []
        self.fail_on = fail_on

    def _maybe_fail(self, argv):
        if self.fail_on and self.fail_on in " ".join(argv):
            raise subprocess.CalledProcessError(1, argv, stderr="boom")

    def run(self, argv, *, input=None, check=True):
        self.calls.append(list(argv))
        if input is not None:
            self.inputs[len(self.calls) - 1] = input
        if check:
            self._maybe_fail(argv)
        stdout = "presets:civitai|2\nhistories:civitai|1\n" if "UNION ALL" in " ".join(argv) else ""
        return subprocess.CompletedProcess(argv, 0, stdout=stdout, stderr="")

    def pipe(self, src, dst):
        self.calls.append(["<pipe>"])
        self.pipes.append((list(src), list(dst)))
        self._maybe_fail(src + dst)


@pytest.fixture
def rec(monkeypatch, tmp_path):
    def make(fail_on=None):
        r = Recorder(fail_on)
        monkeypatch.setattr(export_seed, "run", r.run)
        monkeypatch.setattr(export_seed, "pipe", r.pipe)
        monkeypatch.setattr(export_seed, "SEED_DIR", tmp_path)
        return r
    return make


def _index(calls, *needles):
    return next(i for i, c in enumerate(calls) if all(n in c for n in needles))


def test_export_only_reads_the_dev_database(rec):
    """開發庫只被 pg_dump 讀：不建庫、不刪列、不需要先停 API。"""
    r = rec()
    export(version=1, user="postgres", db="prompt_copilot")

    compose_argvs = [c for c in r.calls if c[:2] == ["docker", "compose"]] + [s for s, _ in r.pipes]
    assert compose_argvs, "應該有一次從開發庫讀資料"
    for argv in compose_argvs:
        assert argv[:2] == ["docker", "compose"]
        assert "pg_dump" in argv and "psql" not in argv


def test_export_filters_inside_a_throwaway_and_dumps_from_it(rec, tmp_path):
    r = rec()
    out, counts = export(version=1, user="postgres", db="prompt_copilot")

    assert out == tmp_path / "prompt_copilot_seed_v1.dump"
    assert counts == {"presets:civitai": 2, "histories:civitai": 1}

    c = r.calls
    assert c[0] == ["docker", "rm", "-f", EXPORT_CONTAINER]           # 上次中斷留下的先清掉
    start = _index(c, "run", EXPORT_IMAGE)
    assert c[start][:2] == ["docker", "run"] and "--rm" in c[start]
    ready = _index(c, "pg_isready")
    schema = _index(c, "psql", "ON_ERROR_STOP=1")
    assert "CREATE TABLE prompt_knowledge_presets" in r.inputs[schema]
    piped = c.index(["<pipe>"])
    delete = next(i for i, x in enumerate(c) if x[-1] == "DELETE FROM shared_prompt_histories WHERE source = 'user'")
    dump = _index(c, "exec", EXPORT_CONTAINER, "pg_dump")
    copy = _index(c, "cp")
    assert start < ready < schema < piped < delete < dump < copy
    assert c[-1] == ["docker", "stop", EXPORT_CONTAINER]

    src, dst = r.pipes[0]
    assert src[src.index("-d") + 1] == "prompt_copilot"
    assert "--data-only" in src and "-Fc" in src and src.count("-t") == 2
    assert dst[:4] == ["docker", "exec", "-i", EXPORT_CONTAINER]
    assert "pg_restore" in dst and "--single-transaction" in dst and dst[dst.index("-d") + 1] == EXPORT_DB
    assert "--data-only" in c[dump] and c[dump].count("-t") == 2
    assert c[copy][2] == f"{EXPORT_CONTAINER}:/tmp/{asset_name(1)}"


def test_export_stops_the_throwaway_even_when_the_restore_fails(rec):
    r = rec(fail_on="pg_restore")
    with pytest.raises(subprocess.CalledProcessError):
        export(version=1, user="postgres", db="prompt_copilot")
    assert r.calls[-1] == ["docker", "stop", EXPORT_CONTAINER]


def test_wait_ready_gives_up_with_a_readable_message(monkeypatch):
    def never_ready(argv, *, input=None, check=True):
        raise subprocess.CalledProcessError(2, argv)

    monkeypatch.setattr(export_seed, "run", never_ready)
    monkeypatch.setattr(export_seed, "sleep", lambda s: None)
    with pytest.raises(SystemExit) as e:
        wait_ready(timeout_s=3)
    assert EXPORT_CONTAINER in str(e.value)


def test_pipe_streams_binary_from_one_process_into_the_next(tmp_path):
    target = tmp_path / "out.bin"
    payload = bytes(range(256)) * 1000
    src = [sys.executable, "-c", "import sys; sys.stdout.buffer.write(bytes(range(256)) * 1000)"]
    dst = [sys.executable, "-c", f"import sys; open({str(target)!r}, 'wb').write(sys.stdin.buffer.read())"]
    pipe(src, dst)
    assert target.read_bytes() == payload


def test_pipe_raises_when_the_source_fails():
    src = [sys.executable, "-c", "import sys; sys.exit(3)"]
    dst = [sys.executable, "-c", "import sys; sys.stdin.buffer.read()"]
    with pytest.raises(subprocess.CalledProcessError) as e:
        pipe(src, dst)
    assert e.value.returncode == 3
