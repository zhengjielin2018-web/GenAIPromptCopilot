"""tag 正規化與 facet 向量的文字（facet 向量設計 §4.2）。

normalize_tag 必須與 C# TagAttribution.Normalize 逐字相同：小寫、底線換空白、連續空白壓成一個、
再反覆剝掉最外層成對括號與「:數字」權重。兩邊用同一組 SHARED_CASES 測。"""

from __future__ import annotations

import re

_SPACES = re.compile(r"\s+")
_WEIGHT = re.compile(r":\s*-?\d*\.?\d+$")

# (原字, 正規化結果)。C# 的 TagAttributionTests.Normalize_matches_the_shared_cases 是同一份，改這裡就要改那裡。
SHARED_CASES: list[tuple[str, str]] = [
    ("(Sandals:1.2)", "sandals"),
    ("platform_sandals", "platform sandals"),
    ("((tag))", "tag"),
    ("(a) (b)", "(a) (b)"),
    ("  Long   Hair ", "long hair"),
    ("(masterpiece:1.2)", "masterpiece"),
    ("tag:0.8", "tag"),
    ("( :1.2)", ""),
    ("sandals", "sandals"),
    ("(Long_Hair:1.2)", "long hair"),
]


def _wrapped_in_parentheses(s: str) -> bool:
    """開頭的 ( 要跟結尾的 ) 成對：「(a) (b)」頭尾都是括號，但不是同一對。"""
    if len(s) < 2 or s[0] != "(" or s[-1] != ")":
        return False
    depth = 0
    for i, ch in enumerate(s):
        if ch == "(":
            depth += 1
        elif ch == ")":
            depth -= 1
            if depth == 0:
                return i == len(s) - 1
    return False


def normalize_tag(tag: str) -> str:
    s = _SPACES.sub(" ", tag.lower().replace("_", " ")).strip()
    while True:
        before = s
        if _wrapped_in_parentheses(s):
            s = s[1:-1].strip()
        s = _WEIGHT.sub("", s).rstrip()
        if s == before:
            return s


def _normalized(tags: list[str]) -> list[str]:
    seen: set[str] = set()
    out: list[str] = []
    for t in tags:
        n = normalize_tag(t)
        if n and n not in seen:
            seen.add(n)
            out.append(n)
    return out


def embedding_text(tags: list[str]) -> str:
    """向量的文字：照原順序（片段作者的排列有語意）。"""
    return ", ".join(_normalized(tags))


def tag_key(tags: list[str]) -> str:
    """去重鍵：排序，順序不同的同一組 tag 同鍵。"""
    return ", ".join(sorted(_normalized(tags)))
