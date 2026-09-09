"""The tick loop that ties perception, decision and action together."""

from __future__ import annotations

import asyncio
import logging
from collections import Counter
from dataclasses import dataclass, field

from ..adapters.base import GameAdapter
from ..config.profile import Profile
from ..core.clock import Clock
from ..core.context import Context
from ..core.events import Disconnected, EventBus
from ..core.state import WorldState
from ..core.scheduler import CooldownRegistry
from .arbiter import Arbiter
from .executor import Executor

log = logging.getLogger(__name__)


@dataclass
class RunStats:
    ticks: int = 0
    actions: Counter = field(default_factory=Counter)
    behaviors: Counter = field(default_factory=Counter)
    current_behavior: str = "idle"
    started_at: float = 0.0

    def uptime(self, now: float) -> float:
        return max(0.0, now - self.started_at)


class BotRunner:
    def __init__(
        self,
        *,
        adapter: GameAdapter,
        bus: EventBus,
        state: WorldState,
        cooldowns: CooldownRegistry,
        clock: Clock,
        profile: Profile,
        arbiter: Arbiter,
        sleep=asyncio.sleep,
    ) -> None:
        self.adapter = adapter
        self.state = state
        self.cooldowns = cooldowns
        self.clock = clock
        self.profile = profile
        self.arbiter = arbiter
        self.executor = Executor(adapter, cooldowns, profile.runtime)
        self.stats = RunStats()
        self._sleep = sleep
        self._stopping = asyncio.Event()

        # A dropped connection invalidates every cached decision: the target we
        # were chasing, the path we were walking, and every cooldown we think
        # we know about.
        bus.subscribe(Disconnected, self._on_disconnected)

    def _on_disconnected(self, _: Disconnected) -> None:
        log.warning("disconnected; clearing cached state")
        self.arbiter.reset()
        self.cooldowns.clear()

    def stop(self) -> None:
        self._stopping.set()

    @property
    def stopping(self) -> bool:
        return self._stopping.is_set()

    def _context(self) -> Context:
        return Context(
            state=self.state,
            cooldowns=self.cooldowns,
            clock=self.clock,
            profile=self.profile,
        )

    def tick(self) -> None:
        """One decision cycle, minus the I/O. Split out so it can be tested
        without an event loop."""
        self.stats.ticks += 1
        self.state.expire_buffs(self.clock.now())

    async def run(self) -> RunStats:
        self.stats.started_at = self.clock.now()
        interval = self.profile.runtime.tick_interval
        deadline = (
            self.stats.started_at + self.profile.runtime.max_runtime_minutes * 60.0
            if self.profile.runtime.max_runtime_minutes > 0
            else None
        )

        while not self.stopping:
            started = self.clock.now()

            if deadline is not None and started >= deadline:
                log.info("run time limit reached")
                break

            if not self.state.connected:
                await self._sleep(interval)
                continue

            self.tick()

            if self.state.character.alive:
                decision = self.arbiter.decide(self._context())
                if decision is None:
                    self.stats.current_behavior = "idle"
                else:
                    self.stats.current_behavior = decision.behavior
                    self.stats.behaviors[decision.behavior] += 1
                    self.stats.actions[type(decision.action).__name__] += 1
                    try:
                        await self.executor.execute(decision.action)
                    except Exception:
                        log.exception("executing %r failed", decision.action)
            else:
                self.stats.current_behavior = "dead"

            elapsed = self.clock.now() - started
            await self._sleep(max(0.0, interval - elapsed))

        return self.stats
