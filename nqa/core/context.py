"""Everything a behaviour is allowed to see."""

from __future__ import annotations

from dataclasses import dataclass

from ..config.profile import Profile
from .clock import Clock
from .scheduler import CooldownRegistry
from .state import WorldState


@dataclass(slots=True)
class Context:
    state: WorldState
    cooldowns: CooldownRegistry
    clock: Clock
    profile: Profile

    @property
    def now(self) -> float:
        return self.clock.now()
