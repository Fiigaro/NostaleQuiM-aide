"""Keeping the character alive. Highest priority in the stack.

Order inside the behaviour matters as much as its priority: HP before MP,
because dying with a full mana bar is still dying.
"""

from __future__ import annotations

from ..core.actions import Action, MoveTo, UseItem
from ..core.context import Context
from ..core.models import Vec2
from .base import Behavior


class SurvivalBehavior(Behavior):
    name = "survival"
    priority = 100

    def decide(self, ctx: Context) -> Action | None:
        char = ctx.state.character
        if not char.alive:
            return None                      # nothing to do; the runner handles death

        cfg = ctx.profile.survival

        # HP first.
        if (potion := cfg.hp_potion) is not None:
            if char.hp_pct <= potion.threshold_pct and self._usable(ctx, potion):
                return UseItem(vnum=potion.vnum, cooldown=potion.cooldown, cooldown_key=potion.key)

        # Then MP.
        if (potion := cfg.mp_potion) is not None:
            if char.mp_pct <= potion.threshold_pct and self._usable(ctx, potion):
                return UseItem(vnum=potion.vnum, cooldown=potion.cooldown, cooldown_key=potion.key)

        # Critically low and nothing left to drink: break contact rather than
        # stand there trading hits. Only reachable once potions are exhausted or
        # still cooling down, because the checks above would have fired first.
        if char.hp_pct <= cfg.critical_hp_pct:
            if (step := self._retreat_step(ctx)) is not None:
                return MoveTo(step)

        return None

    @staticmethod
    def _usable(ctx: Context, potion) -> bool:
        return (
            ctx.state.item_count(potion.vnum) > 0
            and ctx.cooldowns.ready(potion.key)
        )

    @staticmethod
    def _retreat_step(ctx: Context) -> Vec2 | None:
        """One step directly away from the nearest monster.

        Deliberately greedy rather than a full flee path: it runs every tick, so
        the character keeps backing off as the threat follows, and a single step
        is always cheap to compute.
        """
        state = ctx.state
        threat = state.nearest_monster()
        if threat is None:
            return None

        here = state.character.pos
        dx = _sign(here.x - threat.pos.x)
        dy = _sign(here.y - threat.pos.y)
        if dx == 0 and dy == 0:
            dx = 1                            # stacked on the mob; any direction

        for candidate in (Vec2(here.x + dx, here.y + dy),
                          Vec2(here.x + dx, here.y),
                          Vec2(here.x, here.y + dy)):
            if state.grid.walkable(candidate):
                return candidate
        return None


def _sign(value: int) -> int:
    return (value > 0) - (value < 0)
