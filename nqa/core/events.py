"""Typed async event bus.

The adapter publishes perception events; the world tracker and any observers
subscribe. This is the seam that lets a behaviour stack run unchanged against
the simulator and against a real server.
"""

from __future__ import annotations

import inspect
import logging
from collections import defaultdict
from dataclasses import dataclass
from typing import Any, Callable, TypeVar

from .models import Buff, Character, Entity, MapGrid, Vec2

log = logging.getLogger(__name__)


# --- perception events -------------------------------------------------------

@dataclass(frozen=True, slots=True)
class Connected:
    pass


@dataclass(frozen=True, slots=True)
class Disconnected:
    reason: str = ""


@dataclass(frozen=True, slots=True)
class CharacterUpdated:
    """Full snapshot of our character. Sent on login and after big changes."""
    character: Character


@dataclass(frozen=True, slots=True)
class StatsChanged:
    hp: int
    hp_max: int
    mp: int
    mp_max: int


@dataclass(frozen=True, slots=True)
class SelfMoved:
    pos: Vec2


@dataclass(frozen=True, slots=True)
class MapChanged:
    map_id: int
    grid: MapGrid


@dataclass(frozen=True, slots=True)
class EntitySpawned:
    entity: Entity


@dataclass(frozen=True, slots=True)
class EntityDespawned:
    entity_id: int


@dataclass(frozen=True, slots=True)
class EntityMoved:
    entity_id: int
    pos: Vec2


@dataclass(frozen=True, slots=True)
class EntityHpChanged:
    entity_id: int
    hp_pct: float


@dataclass(frozen=True, slots=True)
class BuffGained:
    buff: Buff


@dataclass(frozen=True, slots=True)
class BuffLost:
    buff_id: int


@dataclass(frozen=True, slots=True)
class InventoryChanged:
    vnum: int
    count: int


E = TypeVar("E")
Handler = Callable[[Any], Any]


class EventBus:
    """Minimal type-keyed pub/sub.

    A handler raising does not stop the other handlers: perception must not be
    taken down by one bad subscriber.
    """

    def __init__(self) -> None:
        self._handlers: dict[type, list[Handler]] = defaultdict(list)

    def subscribe(self, event_type: type[E], handler: Callable[[E], Any]) -> None:
        self._handlers[event_type].append(handler)

    def unsubscribe(self, event_type: type[E], handler: Callable[[E], Any]) -> None:
        try:
            self._handlers[event_type].remove(handler)
        except ValueError:
            pass

    async def publish(self, event: Any) -> None:
        for handler in list(self._handlers.get(type(event), ())):
            try:
                result = handler(event)
                if inspect.isawaitable(result):
                    await result
            except Exception:
                log.exception("handler %r failed on %r", handler, event)
