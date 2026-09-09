"""Buff and consumable upkeep.

Re-applies an effect when it is missing or about to lapse. Working from the
server's reported remaining time (rather than a blind timer started when we
cast) means a resisted cast or a dispel is corrected on the next tick instead
of leaving the buff down for its full nominal duration.
"""

from __future__ import annotations

from ..core.actions import Action, UseItem, UseSkill
from ..core.context import Context
from .base import Behavior


class BuffBehavior(Behavior):
    name = "buffs"
    priority = 50

    def decide(self, ctx: Context) -> Action | None:
        now = ctx.now
        ctx.state.expire_buffs(now)

        for cfg in ctx.profile.buffs:
            if not ctx.cooldowns.ready(cfg.key):
                continue

            remaining = ctx.state.buff_remaining(cfg.buff_id, now)
            if remaining > cfg.refresh_margin:
                continue                      # still comfortably up

            if cfg.kind == "item":
                if ctx.state.item_count(cfg.id) <= 0:
                    continue
                return UseItem(vnum=cfg.id, cooldown=cfg.cooldown, cooldown_key=cfg.key)

            # Skill buffs are gated on mana like any other cast.
            return UseSkill(
                skill_id=cfg.id, target_id=None,
                cooldown=cfg.cooldown, cooldown_key=cfg.key,
            )

        return None
