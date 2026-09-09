"""Cooldown bookkeeping.

One registry tracks every per-key cooldown (skills, items, behaviour-level
rate limits) plus a global cooldown that gates *all* actions. The global one
matters: without it the bot fires as fast as the tick loop runs, which is both
useless (the server drops the extra actions) and the single most obvious
behavioural tell there is.
"""

from __future__ import annotations

from .clock import Clock


class CooldownRegistry:
    def __init__(self, clock: Clock, global_cooldown: float = 0.0) -> None:
        self.clock = clock
        self.global_cooldown = global_cooldown
        self._ready_at: dict[str, float] = {}
        self._global_ready_at: float = 0.0

    # --- per-key ------------------------------------------------------------

    def mark_used(self, key: str, cooldown: float, *, trigger_global: bool = True) -> None:
        now = self.clock.now()
        self._ready_at[key] = now + max(0.0, cooldown)
        if trigger_global and self.global_cooldown > 0:
            self._global_ready_at = now + self.global_cooldown

    def remaining(self, key: str) -> float:
        return max(0.0, self._ready_at.get(key, 0.0) - self.clock.now())

    def is_ready(self, key: str) -> bool:
        return self.remaining(key) <= 0.0

    def reset(self, key: str) -> None:
        self._ready_at.pop(key, None)

    def clear(self) -> None:
        """Drop every cooldown. Used on reconnect, where our view is stale."""
        self._ready_at.clear()
        self._global_ready_at = 0.0

    # --- global -------------------------------------------------------------

    @property
    def global_remaining(self) -> float:
        return max(0.0, self._global_ready_at - self.clock.now())

    @property
    def global_ready(self) -> bool:
        return self.global_remaining <= 0.0

    def ready(self, key: str) -> bool:
        """Both gates: the key's own cooldown and the global one."""
        return self.is_ready(key) and self.global_ready
