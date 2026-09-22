"""關鍵詞層 NSFW 過濾。Civitai 的 nsfw=None 與 nsfwLevel 是第一、二層，這是第三層。
清單刻意保守：寧可誤殺，不可漏放——知識庫乾淨是「合規」賣點的前提。"""

from __future__ import annotations

import re
from collections.abc import Callable

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


def make_matcher(keywords: frozenset[str]) -> Callable[[str], bool]:
    """由一份關鍵詞清單造出比對器。單字比對 token；含空白或連字號的詞組改用
    正規化後的子字串比對。獨立成工廠是為了讓特定來源能在不動 NSFW_KEYWORDS
    的前提下用收窄的清單——civitai 路徑的判準必須維持原樣。"""
    single_words = frozenset(k for k in keywords if k.isalpha())
    phrases = tuple(_normalized(k) for k in keywords if not k.isalpha())

    def matches(text: str) -> bool:
        normalized = _normalized(text)
        if set(normalized.split()) & single_words:
            return True
        return any(phrase in normalized for phrase in phrases)

    return matches


is_nsfw_text = make_matcher(NSFW_KEYWORDS)
