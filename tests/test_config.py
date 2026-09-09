import textwrap

import pytest

from nqa.config.profile import Profile, ProfileError

MINIMAL = """
combat:
  skills:
    - id: 1
      cooldown: 2.0
      range: 3
"""


def load(text: str) -> Profile:
    import yaml
    return Profile.from_dict(yaml.safe_load(textwrap.dedent(text)))


def test_minimal_profile_fills_in_defaults():
    p = load(MINIMAL)
    assert p.combat.skills[0].id == 1
    assert p.combat.engage_radius == 12
    assert p.runtime.tick_interval == 0.25
    assert p.survival.hp_potion is None


def test_skills_sort_by_descending_priority():
    p = load("""
    combat:
      skills:
        - {id: 1, priority: 5}
        - {id: 2, priority: 50}
        - {id: 3, priority: 20}
    """)
    assert [s.id for s in p.combat.skills_by_priority()] == [2, 3, 1]


def test_a_profile_with_no_skills_is_rejected():
    with pytest.raises(ProfileError, match="combat.skills"):
        load("combat: {skills: []}")


def test_a_missing_required_key_names_its_path():
    with pytest.raises(ProfileError, match=r"combat\.skills\[0\]\.id"):
        load("combat: {skills: [{cooldown: 1.0}]}")


def test_a_wrong_type_names_its_path():
    with pytest.raises(ProfileError, match=r"combat\.skills\[0\]\.id"):
        load("combat: {skills: [{id: not-a-number}]}")


def test_booleans_are_not_silently_coerced_to_numbers():
    with pytest.raises(ProfileError):
        load("combat: {skills: [{id: true}]}")


def test_an_unknown_buff_kind_is_rejected():
    with pytest.raises(ProfileError, match=r"buffs\[0\]\.kind"):
        load(MINIMAL + "buffs:\n  - {kind: potion, id: 1, buff_id: 1}\n")


def test_a_malformed_waypoint_is_rejected():
    with pytest.raises(ProfileError, match=r"navigation\.waypoints\[1\]"):
        load(MINIMAL + "navigation:\n  waypoints: [[1, 2], [3]]\n")


def test_a_zero_tick_interval_is_rejected():
    with pytest.raises(ProfileError, match="tick_interval"):
        load(MINIMAL + "runtime: {tick_interval: 0}\n")


def test_a_missing_file_is_reported_clearly():
    with pytest.raises(ProfileError, match="not found"):
        Profile.load("/nonexistent/profile.yaml")


def test_the_shipped_example_profile_is_valid():
    p = Profile.load("profiles/example.yaml")
    assert p.combat.skills and p.buffs and p.navigation.waypoints
    # A free range-1 fallback keeps the rotation from stalling.
    fallback = min(p.combat.skills, key=lambda s: s.priority)
    assert fallback.mp_cost == 0 and fallback.range == 1
