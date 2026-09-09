"""Command line entry point."""

from __future__ import annotations

import argparse
import asyncio
import logging
import signal
import sys

from .adapters.simulated import SimConfig, SimulatedAdapter
from .app import build_bot
from .config.profile import Profile, ProfileError


def _build_sim_config(profile: Profile) -> SimConfig:
    """Give the simulator effects matching the profile, so a run exercises the
    real thresholds rather than inert ids."""
    item_effects: dict[int, tuple[str, int]] = {}
    if profile.survival.hp_potion:
        item_effects[profile.survival.hp_potion.vnum] = ("hp", 400)
    if profile.survival.mp_potion:
        item_effects[profile.survival.mp_potion.vnum] = ("mp", 250)

    buff_durations = {b.buff_id: b.duration for b in profile.buffs}
    for b in profile.buffs:
        if b.kind == "item":
            item_effects.setdefault(b.id, ("buff", 0))

    return SimConfig(
        skill_mp_cost={s.id: s.mp_cost for s in profile.combat.skills},
        skill_damage={s.id: 20 + 6 * s.priority for s in profile.combat.skills},
        item_effects=item_effects,
        buff_durations=buff_durations,
    )


def _starting_inventory(profile: Profile) -> dict[int, int]:
    inv: dict[int, int] = {}
    if profile.survival.hp_potion:
        inv[profile.survival.hp_potion.vnum] = 200
    if profile.survival.mp_potion:
        inv[profile.survival.mp_potion.vnum] = 200
    for b in profile.buffs:
        if b.kind == "item":
            inv[b.id] = 20
    return inv


async def _dashboard(bot, interval: float = 2.0) -> None:
    while True:
        await asyncio.sleep(interval)
        s, c = bot.state, bot.state.character
        now = bot.clock.now()
        buffs = ",".join(
            f"{b.name}:{b.remaining(now):.0f}s" for b in s.buffs.values()
        ) or "-"
        print(
            f"[{bot.runner.stats.uptime(now):6.1f}s] "
            f"{bot.runner.stats.current_behavior:<10} "
            f"pos=({c.pos.x:>2},{c.pos.y:>2}) "
            f"hp={c.hp_pct:5.1f}% mp={c.mp_pct:5.1f}% "
            f"mobs={len(s.monsters()):>2} buffs=[{buffs}]",
            flush=True,
        )


async def _run(args: argparse.Namespace) -> int:
    try:
        profile = Profile.load(args.profile)
    except ProfileError as exc:
        print(f"profile error: {exc}", file=sys.stderr)
        return 2

    if args.duration:
        profile.runtime.max_runtime_minutes = args.duration / 60.0

    if args.adapter != "simulated":
        print(
            f"adapter {args.adapter!r} is not wired up in this build.\n"
            "See nqa/adapters/protocol/ -- the transport and packet map have to "
            "be filled in against your emulator before it can connect.",
            file=sys.stderr,
        )
        return 2

    sim_cfg = _build_sim_config(profile)
    inventory = _starting_inventory(profile)
    bot = build_bot(
        profile,
        lambda bus, clock: SimulatedAdapter(
            bus, clock, sim_cfg, inventory=inventory
        ),
    )

    loop = asyncio.get_running_loop()
    for sig in (signal.SIGINT, signal.SIGTERM):
        try:
            loop.add_signal_handler(sig, bot.runner.stop)
        except NotImplementedError:
            pass                              # Windows: fall back to KeyboardInterrupt

    await bot.adapter.connect()
    dash = asyncio.create_task(_dashboard(bot))
    try:
        stats = await bot.runner.run()
    finally:
        dash.cancel()
        await bot.adapter.disconnect()

    print(
        f"\nstopped after {stats.ticks} ticks\n"
        f"  behaviours: {dict(stats.behaviors)}\n"
        f"  actions:    {dict(stats.actions)}"
    )
    return 0


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(prog="nqa", description="NosTale automation core")
    parser.add_argument("--profile", default="profiles/example.yaml")
    parser.add_argument("--adapter", default="simulated", choices=["simulated", "protocol"])
    parser.add_argument("--duration", type=float, default=0.0,
                        help="stop after N seconds (0 = run until interrupted)")
    parser.add_argument("-v", "--verbose", action="store_true")
    args = parser.parse_args(argv)

    logging.basicConfig(
        level=logging.DEBUG if args.verbose else logging.INFO,
        format="%(levelname)-7s %(name)s: %(message)s",
    )
    try:
        return asyncio.run(_run(args))
    except KeyboardInterrupt:
        return 130


if __name__ == "__main__":
    raise SystemExit(main())
