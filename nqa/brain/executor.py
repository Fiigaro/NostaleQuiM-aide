"""The one place intent becomes a command on the wire.

Cooldowns are armed here, not in the behaviours: a behaviour that armed its own
timer would mark a skill used even when the adapter rejected the command.
"""

from __future__ import annotations

import logging

from ..adapters.base import GameAdapter
from ..behaviors.navigation import MOVE_KEY
from ..config.profile import RuntimeConfig
from ..core.actions import Action, Attack, Idle, MoveTo, PickUp, UseItem, UseSkill
from ..core.scheduler import CooldownRegistry

log = logging.getLogger(__name__)


class Executor:
    def __init__(
        self,
        adapter: GameAdapter,
        cooldowns: CooldownRegistry,
        runtime: RuntimeConfig,
    ) -> None:
        self.adapter = adapter
        self.cooldowns = cooldowns
        self.runtime = runtime

    async def execute(self, action: Action) -> None:
        match action:
            case Idle():
                return

            case UseSkill(skill_id=sid, target_id=tid, cooldown=cd, cooldown_key=key):
                await self.adapter.use_skill(sid, tid)
                self.cooldowns.mark_used(key or f"skill:{sid}", cd)

            case UseItem(vnum=vnum, cooldown=cd, cooldown_key=key):
                await self.adapter.use_item(vnum)
                self.cooldowns.mark_used(key or f"item:{vnum}", cd)

            case Attack(target_id=tid):
                await self.adapter.attack(tid)

            case MoveTo(pos=pos):
                await self.adapter.move_to(pos)
                # Movement is rate-limited on its own key and must not consume
                # the global cooldown, or walking would block casting.
                self.cooldowns.mark_used(
                    MOVE_KEY, self.runtime.move_interval, trigger_global=False
                )

            case PickUp(entity_id=eid):
                await self.adapter.pick_up(eid)

            case _:
                log.warning("no executor branch for %r", action)
