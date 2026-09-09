from __future__ import annotations

import pytest

from nqa.config.profile import (
    BuffConfig, CombatConfig, NavigationConfig, PotionConfig, Profile,
    RuntimeConfig, SkillConfig, SurvivalConfig,
)
from nqa.core.clock import ManualClock
from nqa.core.context import Context
from nqa.core.models import Character, Entity, EntityKind, MapGrid, Vec2
from nqa.core.scheduler import CooldownRegistry
from nqa.core.state import WorldState

HP_POTION = 1242
MP_POTION = 1244


@pytest.fixture
def clock() -> ManualClock:
    return ManualClock()


@pytest.fixture
def profile() -> Profile:
    return Profile(
        name="test",
        combat=CombatConfig(
            engage_radius=10,
            leash_radius=20,
            skills=[
                SkillConfig(id=237, name="big", priority=20, cooldown=6.0, mp_cost=30, range=3),
                SkillConfig(id=0, name="basic", priority=0, cooldown=1.0, mp_cost=0, range=1),
            ],
        ),
        survival=SurvivalConfig(
            hp_potion=PotionConfig(vnum=HP_POTION, threshold_pct=50.0, cooldown=3.0),
            mp_potion=PotionConfig(vnum=MP_POTION, threshold_pct=30.0, cooldown=3.0),
            critical_hp_pct=20.0,
        ),
        buffs=[
            BuffConfig(kind="skill", id=250, buff_id=250, name="atk",
                       duration=120.0, refresh_margin=20.0, cooldown=5.0),
        ],
        navigation=NavigationConfig(waypoints=[(5, 5), (15, 5)], arrive_tolerance=1),
        runtime=RuntimeConfig(tick_interval=0.2, global_cooldown=0.0, move_interval=0.0),
    )


@pytest.fixture
def state() -> WorldState:
    s = WorldState()
    s.connected = True
    s.grid = MapGrid(30, 30)
    s.character = Character(id=1, name="T", pos=Vec2(10, 10),
                            hp=1000, hp_max=1000, mp=500, mp_max=500)
    s.inventory = {HP_POTION: 10, MP_POTION: 10}
    return s


@pytest.fixture
def ctx(state, clock, profile) -> Context:
    return Context(
        state=state,
        cooldowns=CooldownRegistry(clock, global_cooldown=profile.runtime.global_cooldown),
        clock=clock,
        profile=profile,
    )


def monster(eid: int, x: int, y: int, *, vnum: int = 100, hp_pct: float = 100.0) -> Entity:
    return Entity(id=eid, kind=EntityKind.MONSTER, pos=Vec2(x, y),
                  vnum=vnum, name="mob", hp_pct=hp_pct)
