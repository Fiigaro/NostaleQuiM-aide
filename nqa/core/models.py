"""Domain models: the vocabulary shared by every layer.

These types are deliberately free of any transport or game-client detail so
the same behaviours run against the simulator and against a live server.
"""

from __future__ import annotations

import math
from dataclasses import dataclass, field
from enum import Enum


@dataclass(frozen=True, slots=True)
class Vec2:
    """Integer grid position. NosTale-family maps are tile grids."""

    x: int
    y: int

    def manhattan(self, other: "Vec2") -> int:
        return abs(self.x - other.x) + abs(self.y - other.y)

    def chebyshev(self, other: "Vec2") -> int:
        """Steps needed with 8-way movement -- the game's real notion of range."""
        return max(abs(self.x - other.x), abs(self.y - other.y))

    def euclidean(self, other: "Vec2") -> float:
        return math.hypot(self.x - other.x, self.y - other.y)

    def __add__(self, other: "Vec2") -> "Vec2":
        return Vec2(self.x + other.x, self.y + other.y)


class EntityKind(str, Enum):
    MONSTER = "monster"
    NPC = "npc"
    PLAYER = "player"
    DROP = "drop"


@dataclass(slots=True)
class Entity:
    """Anything on the map that is not us."""

    id: int
    kind: EntityKind
    pos: Vec2
    vnum: int = 0          # template id: which monster/item this is
    name: str = ""
    level: int = 1
    hp_pct: float = 100.0

    @property
    def alive(self) -> bool:
        return self.hp_pct > 0.0


@dataclass(slots=True)
class Character:
    """Our own character."""

    id: int = 0
    name: str = ""
    pos: Vec2 = Vec2(0, 0)
    map_id: int = 0
    level: int = 1
    hp: int = 1
    hp_max: int = 1
    mp: int = 1
    mp_max: int = 1

    @property
    def hp_pct(self) -> float:
        return 100.0 * self.hp / self.hp_max if self.hp_max > 0 else 0.0

    @property
    def mp_pct(self) -> float:
        return 100.0 * self.mp / self.mp_max if self.mp_max > 0 else 0.0

    @property
    def alive(self) -> bool:
        return self.hp > 0


@dataclass(slots=True)
class Buff:
    """An active effect on our character, with the time it runs out."""

    id: int
    name: str
    expires_at: float      # Clock timestamp, not wall time

    def remaining(self, now: float) -> float:
        return max(0.0, self.expires_at - now)


@dataclass(slots=True)
class MapGrid:
    """Walkability mask for the current map.

    ``blocked`` holds the impassable tiles. Emulators expose this as a packed
    bitmap per map id; the simulator builds it directly.
    """

    width: int
    height: int
    blocked: set[tuple[int, int]] = field(default_factory=set)

    def in_bounds(self, p: Vec2) -> bool:
        return 0 <= p.x < self.width and 0 <= p.y < self.height

    def walkable(self, p: Vec2) -> bool:
        return self.in_bounds(p) and (p.x, p.y) not in self.blocked
