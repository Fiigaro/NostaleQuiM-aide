"""Priority arbitration.

Ask each behaviour in descending priority order, take the first non-None
answer. That is the whole decision engine.

This is subsumption rather than a finite state machine on purpose. An FSM
needs an explicit edge for every interruption -- combat->drink->back to combat,
walking->drink->back to walking, buffing->drink->... -- and the edge you forget
is the one that gets you killed. Here, survival simply outranks everything, so
preemption is a property of the ordering instead of something to be enumerated.
The "current state" reported to the UI is just whichever behaviour won.
"""

from __future__ import annotations

from dataclasses import dataclass
from typing import Iterable

from ..behaviors.base import Behavior
from ..core.actions import Action
from ..core.context import Context


@dataclass(frozen=True, slots=True)
class Decision:
    behavior: str
    action: Action


class Arbiter:
    def __init__(self, behaviors: Iterable[Behavior]) -> None:
        self.behaviors: list[Behavior] = sorted(behaviors, key=lambda b: -b.priority)

    def decide(self, ctx: Context) -> Decision | None:
        for behavior in self.behaviors:
            action = behavior.decide(ctx)
            if action is not None:
                return Decision(behavior.name, action)
        return None

    def reset(self) -> None:
        for behavior in self.behaviors:
            behavior.reset()
