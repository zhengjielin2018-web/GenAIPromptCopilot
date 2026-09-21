"""關鍵詞層 NSFW 過濾。Civitai 的 nsfw=None 與 nsfwLevel 是第一、二層，這是第三層。
清單刻意保守：寧可誤殺，不可漏放——知識庫乾淨是「合規」賣點的前提。"""

from __future__ import annotations

import re

NSFW_KEYWORDS: frozenset[str] = frozenset({
    "nsfw", "nude", "naked", "topless", "bottomless", "nipples", "areola", "nipple",
    "sex", "sexual", "explicit", "porn", "pornographic", "hentai", "erotic", "erotica",
    "penis", "vagina", "pussy", "cum", "orgasm", "masturbation", "fellatio", "cunnilingus",
    "bondage", "bdsm", "lingerie", "underwear only", "see-through", "cameltoe", "ahegao",
    "loli", "shota", "gore", "guro", "dismemberment",
    # 性暗示形容詞（非解剖學名詞）
    "sexy", "seductive", "cleavage", "busty", "skimpy", "scantily",
    "voluptuous", "lewd", "suggestive", "provocative",
})

_NON_ALNUM_RE = re.compile(r"[^a-z0-9]+")


def _normalized(text: str) -> str:
    """小寫、把所有非英數字元轉成空白，並在頭尾補空白以便做詞組比對。"""
    return " " + _NON_ALNUM_RE.sub(" ", text.lower()).strip() + " "


# 單字關鍵詞比對 token；含空白或連字號的詞組改用正規化後的子字串比對。
_SINGLE_WORDS: frozenset[str] = frozenset(k for k in NSFW_KEYWORDS if k.isalpha())
_PHRASES: tuple[str, ...] = tuple(
    _normalized(k) for k in NSFW_KEYWORDS if not k.isalpha()
)


def is_nsfw_text(text: str) -> bool:
    normalized = _normalized(text)
    if set(normalized.split()) & _SINGLE_WORDS:
        return True
    return any(phrase in normalized for phrase in _PHRASES)
