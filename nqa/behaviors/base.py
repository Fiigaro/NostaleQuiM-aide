"""Behaviour contract.

A behaviour answers one question: "given the world right now, do I want to do
something?" It returns an ``Action`` if yes, ``None`` to pass.

Behaviours are pure with respect to the game: no I/O, no adapter, no sleeping.
That is what lets every one of them be tested by handing it a hand-built
``WorldState`` and a ``ManualClock``.

Priority ordering is subsumption, not a state machine: the arbiter asks the
highest-priority behaviour first and takes the first non-None answer. Survival
outranks combat, so drinking a potion preempts a skill rotation mid-fight
without any explicit transition having to be written for it.
"""

from __future__ import annotations

from abc import ABC, abstractmethod

from ..core.actions import Action
from ..core.context import Context


class Behavior(ABC):
    name: str = "behavior"
    priority: int = 0

    @abstractmethod
    def decide(self, ctx: Context) -> Action | None:
        """Return an action to take, or None to defer to a lower priority."""

    def reset(self) -> None:
        """Drop any cached state. Called on disconnect and map change."""
        return None
