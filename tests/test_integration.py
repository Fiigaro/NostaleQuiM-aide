"""End-to-end runs of the whole stack against the simulator.

Time is driven by a ManualClock advanced from the runner's own sleep hook, so
a 'one minute' run executes in milliseconds and replays identically.
"""

from __future__ import annotations

from dataclasses import dataclass

from nqa.adapters.simulated import SimConfig, SimulatedAdapter
from nqa.app import build_bot
from nqa.config.profile import Profile
from nqa.core.clock import ManualClock

HP_POTION = 1242
MP_POTION = 1244


def build(profile: Profile, sim_cfg: SimConfig, inventory: dict[int, int]):
    clock = ManualClock()
    holder: dict = {}

    async def fake_sleep(seconds: float) -> None:
        clock.advance(max(seconds, profile.runtime.tick_interval))
        await holder["adapter"].step()

    bot = build_bot(
        profile,
        lambda bus, c: SimulatedAdapter(bus, c, sim_cfg, inventory=inventory),
        clock=clock,
        sleep=fake_sleep,
    )
    holder["adapter"] = bot.adapter
    return bot, clock


def farming_profile(minutes: float = 1.0) -> Profile:
    profile = Profile.load("profiles/example.yaml")
    profile.runtime.max_runtime_minutes = minutes
    return profile


def sim_config(**overrides) -> SimConfig:
    profile = farming_profile()
    base = dict(
        auto_pump=False,
        skill_mp_cost={s.id: s.mp_cost for s in profile.combat.skills},
        skill_damage={s.id: 20 + 6 * s.priority for s in profile.combat.skills},
        item_effects={HP_POTION: ("hp", 400), MP_POTION: ("mp", 250), 1904: ("buff", 0)},
        buff_durations={b.buff_id: b.duration for b in profile.buffs},
    )
    base.update(overrides)
    return SimConfig(**base)


@dataclass
class Snapshot:
    """State captured *before* teardown.

    Disconnecting deliberately wipes buffs and entities, so anything asserted
    about the end of a run has to be read while the session is still up.
    """
    alive: bool
    hp_pct: float
    buff_remaining: dict[int, float]
    inventory: dict[int, int]


async def run(bot, clock) -> Snapshot:
    await bot.adapter.connect()
    try:
        await bot.runner.run()
        now = clock.now()
        return Snapshot(
            alive=bot.state.character.alive,
            hp_pct=bot.state.character.hp_pct,
            buff_remaining={b: bot.state.buff_remaining(b, now) for b in (250, 1904)},
            inventory=dict(bot.state.inventory),
        )
    finally:
        await bot.adapter.disconnect()


async def test_a_full_run_walks_fights_and_kills():
    profile = farming_profile(1.0)
    bot, clock = build(profile, sim_config(), {HP_POTION: 50, MP_POTION: 50, 1904: 5})
    snap = await run(bot, clock)

    stats = bot.runner.stats
    assert stats.ticks > 100
    assert bot.adapter.kills > 0, "never killed anything"
    assert stats.behaviors["combat"] > 0
    assert stats.behaviors["navigation"] > 0
    assert snap.alive


async def test_buffs_are_applied_and_kept_up():
    profile = farming_profile(3.0)
    # Short durations so upkeep has to fire repeatedly inside the run.
    cfg = sim_config(buff_durations={250: 20.0, 1904: 25.0})
    for buff in profile.buffs:
        buff.duration = 20.0 if buff.kind == "skill" else 25.0
        buff.refresh_margin = 5.0

    bot, clock = build(profile, cfg, {HP_POTION: 50, MP_POTION: 50, 1904: 99})
    snap = await run(bot, clock)

    # 180s of run time against 20s and 25s buffs: upkeep has to fire many times.
    assert bot.runner.stats.behaviors["buffs"] >= 10, "upkeep did not re-fire"
    assert snap.buff_remaining[250] > 0, "attack buff was allowed to lapse"
    assert snap.buff_remaining[1904] > 0, "exp boost was allowed to lapse"


async def test_potions_keep_the_character_alive_under_heavy_pressure():
    profile = farming_profile(2.0)
    # Sustained pressure the potion throughput can actually answer: ~3 mobs at
    # 35 damage/s is ~105 dps against 400 HP every 3s. Tight, but survivable --
    # which is the point. An unwinnable fight would prove nothing.
    cfg = sim_config(
        monster_count=3,
        monster_damage=35,
        monster_attack_interval=1.0,
        monster_aggro_radius=25,
        hp_regen=0,
        mp_regen=0,
    )
    bot, clock = build(profile, cfg, {HP_POTION: 500, MP_POTION: 500, 1904: 5})
    snap = await run(bot, clock)

    assert bot.runner.stats.behaviors["survival"] > 0, "never reacted to damage"
    assert snap.alive, "died with potions in the bag"
    assert snap.inventory[HP_POTION] < 500, "potions were never consumed"


async def test_with_no_potions_left_the_character_disengages():
    profile = farming_profile(2.0)
    cfg = sim_config(
        monster_damage=40, monster_attack_interval=0.8,
        monster_aggro_radius=25, hp_regen=0, mp_regen=0,
    )
    bot, clock = build(profile, cfg, {1904: 5})       # no potions at all
    await run(bot, clock)

    moves = [c for c in bot.adapter.command_log if c[0] == "move_to"]
    assert moves, "never moved"
    assert bot.runner.stats.behaviors["survival"] > 0, "never tried to retreat"


async def test_a_disconnect_clears_cached_decisions():
    from nqa.core.events import Disconnected

    profile = farming_profile(0.2)
    bot, _ = build(profile, sim_config(), {HP_POTION: 50, MP_POTION: 50, 1904: 5})
    await bot.adapter.connect()
    await bot.runner.run()

    bot.cooldowns.mark_used("skill:237", 999.0)
    await bot.bus.publish(Disconnected("test"))

    assert bot.cooldowns.ready("skill:237")
    assert bot.state.entities == {}
