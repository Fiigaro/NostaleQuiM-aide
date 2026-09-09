"""Skill rotation against the current target.

Combat only fires when something is actually castable *now*. Closing the
distance is navigation's job -- that split keeps the rotation readable and
means an out-of-range target simply falls through to the walker instead of
needing a "chase" state.
"""

from __future__ import annotations

from ..core.actions import Action, UseSkill
from ..core.context import Context
from .base import Behavior
from .targeting import TargetSelector


class CombatBehavior(Behavior):
    name = "combat"
    priority = 30

    def __init__(self, targeting: TargetSelector) -> None:
        self.targeting = targeting

    def decide(self, ctx: Context) -> Action | None:
        target = self.targeting.select(ctx)
        if target is None:
            return None

        char = ctx.state.character
        distance = char.pos.chebyshev(target.pos)

        for skill in ctx.profile.combat.skills_by_priority():
            if distance > skill.range:
                continue
            if char.mp < skill.mp_cost:
                continue
            if not ctx.cooldowns.ready(skill.key):
                continue
            return UseSkill(
                skill_id=skill.id,
                target_id=target.id,
                cooldown=skill.cooldown,
                cooldown_key=skill.key,
            )

        return None

    def reset(self) -> None:
        self.targeting.reset()
