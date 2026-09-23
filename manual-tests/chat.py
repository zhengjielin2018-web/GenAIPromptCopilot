"""PromptCopilot.Api 的終端機對話客戶端：建 session、送訊息、把 SSE 事件印成人看得懂的樣子。

只用標準函式庫。API 要先在跑（python manual-tests/start_api.py，預設 http://localhost:5000）。
用法：python manual-tests/chat.py [--base http://localhost:5000] [--raw] [--no-color]
指令：/new 開新 session　/save <一句話描述> 存到共享庫　/raw 切換原始事件　/quit 離開
"""

from __future__ import annotations

import argparse
import json
import os
import sys
import urllib.error
import urllib.request

if os.name == "nt":
    os.system("")  # 讓舊版主控台吃 ANSI 色碼
if not sys.stdout.isatty():
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")

STATE_MARK = {"covered": "●", "missing": "○", "waived": "–", "notApplicable": "·"}


class Ui:
    def __init__(self, color: bool):
        self.color = color

    def _c(self, code: str, s: str) -> str:
        return f"\033[{code}m{s}\033[0m" if self.color else s

    def dim(self, s): return self._c("2", s)
    def bold(self, s): return self._c("1", s)
    def cyan(self, s): return self._c("36", s)
    def green(self, s): return self._c("32", s)
    def yellow(self, s): return self._c("33", s)
    def red(self, s): return self._c("31", s)


def http_json(method: str, url: str, body: dict | None = None, timeout: float = 30):
    data = json.dumps(body, ensure_ascii=False).encode("utf-8") if body is not None else None
    req = urllib.request.Request(url, data=data, method=method, headers={"content-type": "application/json"})
    with urllib.request.urlopen(req, timeout=timeout) as r:
        return r.status, json.loads(r.read().decode("utf-8") or "null")


def sse_events(url: str, body: dict, timeout: float = 180):
    data = json.dumps(body, ensure_ascii=False).encode("utf-8")
    req = urllib.request.Request(url, data=data, method="POST",
                                 headers={"content-type": "application/json", "accept": "text/event-stream"})
    with urllib.request.urlopen(req, timeout=timeout) as r:
        name, lines = None, []
        for raw in r:
            line = raw.decode("utf-8").rstrip("\r\n")
            if line.startswith("event:"):
                name = line[6:].strip()
            elif line.startswith("data:"):
                lines.append(line[5:].strip())
            elif line == "" and lines:
                yield name, json.loads("\n".join(lines))
                name, lines = None, []


class Chat:
    def __init__(self, base: str, ui: Ui, raw: bool):
        self.base, self.ui, self.raw = base.rstrip("/"), ui, raw
        self.labels: dict[str, str] = {}
        self.dims: list[tuple[str, str, list[str]]] = []  # (key, label, facet ids)
        self.dim_labels: dict[str, str] = {}
        self.last_states: dict[str, str] | None = None
        self.last_profile: str | None = None
        self.sid = ""

    def load_facets(self):
        _, cfg = http_json("GET", f"{self.base}/api/config/facets")
        for d in cfg["dimensions"]:
            self.dims.append((d["key"], d["label"], [f["id"] for f in d["facets"]]))
            self.dim_labels[d["key"]] = d["label"]
            for f in d["facets"]:
                self.labels[f["id"]] = f["label"]

    def new_session(self):
        _, body = http_json("POST", f"{self.base}/api/sessions")
        self.sid, self.last_states, self.last_profile = body["sessionId"], None, None
        print(self.ui.dim(f"── 新 session {self.sid} ──"))

    def send(self, text: str):
        try:
            for name, ev in sse_events(f"{self.base}/api/sessions/{self.sid}/messages", {"text": text}):
                if self.raw:
                    print(self.ui.dim(f"  [{name}] {json.dumps(ev, ensure_ascii=False)}"))
                getattr(self, f"on_{name}", self.on_unknown)(ev)
        except urllib.error.HTTPError as e:
            msg = e.read().decode("utf-8", "replace")
            print(self.ui.red(f"  HTTP {e.code}：{msg}"))
            if e.code == 404:
                print(self.ui.dim("  session 過期了，自動開新的；請再送一次。"))
                self.new_session()

    def save(self, intent: str):
        if not intent:
            print(self.ui.yellow("  用法：/save 一句話描述這張圖（例：銀髮少女站在雨夜霓虹街頭）"))
            return
        try:
            _, body = http_json("POST", f"{self.base}/api/sessions/{self.sid}/save-to-shared", {"intent": intent})
            print(self.ui.green(f"  已存到共享庫，id = {body['id']}"))
        except urllib.error.HTTPError as e:
            print(self.ui.red(f"  HTTP {e.code}：{e.read().decode('utf-8', 'replace')}"))

    # ---- 事件 ----
    def on_session(self, ev):
        print(self.ui.dim(f"  第 {ev['turnIndex']} 輪（開始時狀態 {ev['status']}）"))

    def on_tool_call(self, ev):
        summary = ev.get("argsSummary") or ""
        print(self.ui.dim(f"  ⋯ {ev['name']}  {summary}"))

    def on_tool_result(self, ev):
        print(self.ui.dim(f"    ↳ {ev.get('summary', '')}"))
        for p in (ev.get("presets") or [])[:5]:
            print(self.ui.dim(f"       #{p['id']} {p['title']}"))

    def on_dimensions(self, ev):
        states: dict[str, str] = ev.get("facetStates") or {}
        had_prev = self.last_states is not None
        changed = {k for k, v in states.items() if not had_prev or self.last_states.get(k) != v}
        self.last_states = dict(states)
        profile = ev.get("profile")
        if had_prev and not changed and profile == self.last_profile:
            return  # 每輪收尾都會再送一次；沒變就不重印
        self.last_profile = profile
        print(self.ui.cyan(f"  儀表板  profile = {profile or '（未定）'}"))
        for _, label, ids in self.dims:
            present = [i for i in ids if i in states]
            if not present:
                continue
            marks = "".join(STATE_MARK.get(states[i], "?") for i in present)
            missing = [self.labels[i] for i in present if states[i] == "missing"]
            waived = [self.labels[i] for i in present if states[i] == "waived"]
            flag = self.ui.yellow(" *") if had_prev and any(i in changed for i in present) else ""
            extra = ""
            if missing:
                extra += "  缺：" + "、".join(missing)
            if waived:
                extra += "  不指定：" + "、".join(waived)
            print(f"    {label:<4} {marks}{flag}{self.ui.dim(extra)}")

    def on_final(self, ev):
        kind = ev["kind"]
        if kind == "ask":
            if ev.get("preamble"):
                print(self.ui.bold(f"\n助手：{ev['preamble']}"))
            for n, a in enumerate(ev.get("asks") or [], 1):
                dim = self.dim_labels.get(a["dimension"], a["dimension"])
                print(self.ui.bold(f"  {n}. [{dim}] {a['question']}"))
                for m, o in enumerate(a.get("options") or [], 1):
                    pid = f" #{o['presetId']}" if o.get("presetId") else ""
                    print(f"     {chr(96 + m)}) {o['label']}" + self.ui.dim(f"  {o.get('tags', '')}{pid}"))
        elif kind == "message":
            print(self.ui.bold(f"\n助手：{ev.get('message', '')}"))
            for m, o in enumerate(ev.get("options") or [], 1):
                print(f"     {chr(96 + m)}) {o['label']}" + self.ui.dim(f"  {o.get('tags', '')}"))
        elif kind == "finalized":
            print(self.ui.green("\n✔ 定稿"))
            print(self.ui.bold("  Positive: ") + (ev.get("positive") or ""))
            print(self.ui.bold("  Negative: ") + (ev.get("negative") or ""))
            if ev.get("tips"):
                print(self.ui.dim(f"  Tips: {ev['tips']}"))
        elif kind == "save_consent_requested":
            print(self.ui.yellow("\n助手想把這次的定稿存進共享庫。要存就輸入：/save 一句話描述這張圖"))
        else:
            print(json.dumps(ev, ensure_ascii=False))
        print()

    def on_blocked(self, ev):
        print(self.ui.red(f"\n⛔ 被攔下（{ev['reason']}）：{ev['message']}\n"))

    def on_error(self, ev):
        print(self.ui.red(f"\n✖ 錯誤（{ev['code']}）：{ev['message']}\n"))

    def on_unknown(self, ev):
        print(self.ui.dim(f"  ? {json.dumps(ev, ensure_ascii=False)}"))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--base", default="http://localhost:5000")
    ap.add_argument("--raw", action="store_true", help="同時印出原始事件 JSON")
    ap.add_argument("--no-color", action="store_true")
    args = ap.parse_args()

    chat = Chat(args.base, Ui(not args.no_color), args.raw)
    try:
        chat.load_facets()
    except (urllib.error.URLError, ConnectionError) as e:
        sys.exit(f"連不到 {args.base}：{e}。API 有在跑嗎？（python manual-tests/start_api.py）")
    chat.new_session()
    print(chat.ui.dim("指令：/new 開新 session　/save <描述> 存到共享庫　/raw 切換原始事件　/quit 離開\n"))

    while True:
        try:
            text = input("你：").strip()
        except (EOFError, KeyboardInterrupt):
            print()
            break
        if not text:
            continue
        if text in ("/quit", "/exit"):
            break
        if text == "/new":
            chat.new_session()
        elif text == "/raw":
            chat.raw = not chat.raw
            print(chat.ui.dim(f"  原始事件：{'開' if chat.raw else '關'}"))
        elif text.startswith("/save"):
            chat.save(text[5:].strip())
        else:
            try:
                chat.send(text)
            except KeyboardInterrupt:
                print(chat.ui.dim("\n  （中斷這一輪；伺服器會回滾）"))


if __name__ == "__main__":
    main()
