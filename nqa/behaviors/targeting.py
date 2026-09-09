"""Shared target selection.

Combat and navigation must agree on what we are fighting: if each picked its
own nearest monster every tick, the character would walk toward one mob while
casting at another, and switch both every time something moved. One selector,
one target, held until it is dead, gone, or out of leash range.
"""

from __future__ import annotations

from ..core.context import Context
from ..core.models import Entity


class TargetSelector:
    def __init__(self) -> None:
        self._target_id: int | None = None
        self._anchor = None            # where we stood when we engaged

    @property
    def target_id(self) -> int | None:
        return self._target_id

    def reset(self) -> None:
        self._target_id = None
        self._anchor = None

    def select(self, ctx: Context) -> Entity | None:
        state = ctx.state
        cfg = ctx.profile.combat
        vnums = cfg.target_vnums or None

        # Stay on the current target while it is still a valid one.
        if self._target_id is not None:
            current = state.entity(self._target_id)
            if (
                current is not None
                and current.alive
                and (vnums is None or current.vnum in vnums)
                and self._anchor is not None
                and self._anchor.chebyshev(current.pos) <= cfg.leash_radius
            ):
                return current
            self.reset()

        # Otherwise engage the closest acceptable monster in range.
        candidates = state.monsters_within(cfg.engage_radius, vnums)
        if not candidates:
            return None

        chosen = candidates[0]
        self._target_id = chosen.id
        self._anchor = state.character.pos
        return chosen
