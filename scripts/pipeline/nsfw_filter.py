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
})

_WORD_RE = re.compile(r"[a-z][a-z\-]*")


def is_nsfw_text(text: str) -> bool:
    lowered = text.lower()
    tokens = set(_WORD_RE.findall(lowered))
    if tokens & NSFW_KEYWORDS:
        return True
    return any(" " in kw and kw in lowered for kw in NSFW_KEYWORDS)
