"""Composition root: build every object and wire them together.

Everything above this file is decoupled; this is the one place that knows the
concrete pieces. Swapping ``SimulatedAdapter`` for ``ProtocolAdapter`` is the
only change needed to point the same brain at a real server.
"""

from __future__ import annotations

from dataclasses import dataclass

from .adapters.base import GameAdapter
from .behaviors.buffs import BuffBehavior
from .behaviors.combat import CombatBehavior
from .behaviors.navigation import NavigationBehavior
from .behaviors.survival import SurvivalBehavior
from .behaviors.targeting import TargetSelector
from .brain.arbiter import Arbiter
from .brain.runner import BotRunner
from .config.profile import Profile
from .core.clock import Clock, SystemClock
from .core.events import EventBus
from .core.scheduler import CooldownRegistry
from .core.state import WorldState, WorldTracker


@dataclass
class Bot:
    bus: EventBus
    state: WorldState
    clock: Clock
    cooldowns: CooldownRegistry
    arbiter: Arbiter
    runner: BotRunner
    adapter: GameAdapter


def build_bot(
    profile: Profile,
    adapter_factory,
    *,
    clock: Clock | None = None,
    sleep=None,
) -> Bot:
    """Assemble a bot.

    ``adapter_factory`` is called with ``(bus, clock)`` so the adapter can
    publish perception events from the moment it is constructed.
    """
    clock = clock or SystemClock()
    bus = EventBus()
    state = WorldState()
    WorldTracker(state, bus, clock)

    cooldowns = CooldownRegistry(clock, global_cooldown=profile.runtime.global_cooldown)
    adapter = adapter_factory(bus, clock)

    targeting = TargetSelector()
    arbiter = Arbiter([
        SurvivalBehavior(),
        BuffBehavior(),
        CombatBehavior(targeting),
        NavigationBehavior(targeting),
    ])

    runner_kwargs = {} if sleep is None else {"sleep": sleep}
    runner = BotRunner(
        adapter=adapter, bus=bus, state=state, cooldowns=cooldowns,
        clock=clock, profile=profile, arbiter=arbiter, **runner_kwargs,
    )
    return Bot(bus, state, clock, cooldowns, arbiter, runner, adapter)
