"""載入 facets.yaml。管線用它驗證 LLM 回傳的 facet_ids，並產生給 LLM 看的清單文字。"""

from __future__ import annotations

from dataclasses import dataclass, field
from pathlib import Path

import yaml


@dataclass(frozen=True)
class Facet:
    id: str
    label: str
    hint: str
    dimension: str


@dataclass
class FacetCatalog:
    dimensions: dict[str, str] = field(default_factory=dict)  # key -> label
    facets: dict[str, Facet] = field(default_factory=dict)  # id -> Facet
    profiles: dict[str, dict[str, list[str]]] = field(default_factory=dict)  # profile -> dim -> ids
    profile_labels: dict[str, dict[str, str]] = field(default_factory=dict)  # profile -> dim -> 顯示名稱覆寫

    @property
    def all_ids(self) -> frozenset[str]:
        return frozenset(self.facets)

    def dimension_of(self, facet_id: str) -> str:
        return self.facets[facet_id].dimension

    def ids_for_profile(self, profile: str) -> frozenset[str]:
        dims = self.profiles[profile]
        return frozenset(i for ids in dims.values() for i in ids)

    def dimension_label(self, dimension: str, profile: str) -> str:
        """該 profile 的顯示名稱覆寫優先於全域名稱（例：object/vehicle 的 appearance → 主體外觀）。"""
        override = self.profile_labels.get(profile, {})
        return override.get(dimension, self.dimensions.get(dimension, dimension))

    def prompt_listing(self) -> str:
        lines: list[str] = []
        for key, label in self.dimensions.items():
            lines.append(f"[{key}] {label}")
            for f in self.facets.values():
                if f.dimension == key:
                    lines.append(f"  - {f.id}：{f.label}（例：{f.hint}）")
        return "\n".join(lines)


def load_facets(path: Path) -> FacetCatalog:
    raw = yaml.safe_load(path.read_text(encoding="utf-8"))
    cat = FacetCatalog()
    for dim in raw["dimensions"]:
        cat.dimensions[dim["key"]] = dim["label"]
        for f in dim["facets"]:
            cat.facets[f["id"]] = Facet(
                id=f["id"], label=f["label"], hint=f["hint"], dimension=dim["key"]
            )
    for name, body in raw["profiles"].items():
        cat.profiles[name] = {k: list(v) for k, v in body["dimensions"].items()}
        cat.profile_labels[name] = dict(body.get("labels", {}))
    unknown = {i for dims in cat.profiles.values() for ids in dims.values() for i in ids} - cat.all_ids
    if unknown:
        raise ValueError(f"facets.yaml profiles 引用了不存在的 facet id: {sorted(unknown)}")
    return cat
