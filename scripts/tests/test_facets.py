import pytest

from pipeline.config import FACETS_PATH
from pipeline.facets import load_facets


def test_loads_all_six_dimensions():
    cat = load_facets(FACETS_PATH)
    assert set(cat.dimensions) == {"style", "scene", "camera", "appearance", "pose", "clothing"}


def test_all_ids_include_pose_and_footwear():
    cat = load_facets(FACETS_PATH)
    assert "pose.main" in cat.all_ids
    assert "clothing.footwear" in cat.all_ids
    assert cat.dimension_of("clothing.footwear") == "clothing"


def test_landscape_profile_has_no_person_dimensions():
    cat = load_facets(FACETS_PATH)
    ids = cat.ids_for_profile("landscape")
    assert "scene.season" in ids
    assert not any(i.startswith(("appearance.", "pose.", "clothing.")) for i in ids)


def test_every_profile_id_exists_in_dimensions():
    cat = load_facets(FACETS_PATH)
    for profile in ("portrait", "landscape", "object", "vehicle"):
        assert cat.ids_for_profile(profile) <= cat.all_ids


def test_prompt_listing_mentions_ids_and_hints():
    listing = load_facets(FACETS_PATH).prompt_listing()
    assert "scene.weather" in listing
    assert "rain, fog" in listing


def test_facet_counts_are_pinned():
    cat = load_facets(FACETS_PATH)
    per_dimension = {key: sum(1 for f in cat.facets.values() if f.dimension == key)
                     for key in cat.dimensions}
    assert per_dimension == {
        "style": 4, "scene": 7, "camera": 5,
        "appearance": 8, "pose": 7, "clothing": 6,
    }
    assert len(cat.all_ids) == 37
    assert {p: len(cat.ids_for_profile(p)) for p in
            ("portrait", "landscape", "object", "vehicle")} == {
        "portrait": 31, "landscape": 16, "object": 18, "vehicle": 20,
    }


def test_dimension_label_uses_the_profile_override_for_object_and_vehicle():
    cat = load_facets(FACETS_PATH)
    assert cat.dimension_label("appearance", "object") == "主體外觀"
    assert cat.dimension_label("appearance", "vehicle") == "主體外觀"
    assert cat.dimension_label("pose", "vehicle") == "運動狀態"


def test_dimension_label_falls_back_to_the_global_label_when_no_override():
    cat = load_facets(FACETS_PATH)
    assert cat.dimension_label("appearance", "portrait") == "人物樣貌"
    assert cat.dimension_label("appearance", "landscape") == "人物樣貌"
    assert cat.dimension_label("style", "object") == "風格"  # object 只覆寫 appearance，其餘用全域


def test_load_facets_rejects_profile_referencing_unknown_facet(tmp_path):
    bad = tmp_path / "bad.yaml"
    bad.write_text(
        "dimensions:\n"
        "  - key: style\n"
        "    label: 風格\n"
        "    facets:\n"
        "      - { id: style.genre, label: 流派, hint: anime }\n"
        "profiles:\n"
        "  portrait:\n"
        "    dimensions:\n"
        "      style: [style.genre, style.ghost]\n",
        encoding="utf-8",
    )
    with pytest.raises(ValueError, match="style.ghost"):
        load_facets(bad)
