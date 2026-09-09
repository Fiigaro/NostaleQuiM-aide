"""The adapter seam.

An adapter is the *only* module that knows how the game is actually reached.
It pushes perception onto the event bus and accepts commands. Swap the
implementation and nothing above this line changes.
"""

from __future__ import annotations

from abc import ABC, abstractmethod

from ..core.events import EventBus
from ..core.models import Vec2


class GameAdapter(ABC):
    """Commands out, events in."""

    def __init__(self, bus: EventBus) -> None:
        self.bus = bus

    # --- lifecycle ----------------------------------------------------------

    @abstractmethod
    async def connect(self) -> None:
        """Establish the session and start any background pump."""

    @abstractmethod
    async def disconnect(self) -> None:
        """Tear down cleanly. Must be safe to call twice."""

    # --- commands -----------------------------------------------------------

    @abstractmethod
    async def use_skill(self, skill_id: int, target_id: int | None = None) -> None:
        ...

    @abstractmethod
    async def use_item(self, vnum: int) -> None:
        ...

    @abstractmethod
    async def attack(self, target_id: int) -> None:
        ...

    @abstractmethod
    async def move_to(self, pos: Vec2) -> None:
        ...

    async def pick_up(self, entity_id: int) -> None:
        """Optional: not every backend models drops."""
        return None
