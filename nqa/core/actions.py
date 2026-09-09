"""Actions a behaviour can request.

Behaviours never touch the adapter. They return one of these, the arbiter picks
a winner, and the executor is the single place that turns intent into a command
on the wire. That indirection is what makes behaviours unit-testable with no
transport at all.
"""

from __future__ import annotations

from dataclasses import dataclass

from .models import Vec2


@dataclass(frozen=True, slots=True)
class Idle:
    reason: str = ""


@dataclass(frozen=True, slots=True)
class UseSkill:
    skill_id: int
    target_id: int | None = None
    cooldown: float = 0.0
    # Which timer to arm. Defaults to the skill's own, but a buff rotation
    # tracks its upkeep under a separate key so a skill used both offensively
    # and as a buff does not share one cooldown.
    cooldown_key: str | None = None


@dataclass(frozen=True, slots=True)
class UseItem:
    vnum: int
    cooldown: float = 0.0
    cooldown_key: str | None = None


@dataclass(frozen=True, slots=True)
class Attack:
    target_id: int


@dataclass(frozen=True, slots=True)
class MoveTo:
    """Step toward ``pos``. Navigation supplies the next waypoint, not the
    final destination, so obstacle avoidance stays on our side."""
    pos: Vec2


@dataclass(frozen=True, slots=True)
class PickUp:
    entity_id: int


Action = Idle | UseSkill | UseItem | Attack | MoveTo | PickUp
