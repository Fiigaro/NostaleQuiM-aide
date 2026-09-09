"""The perceived world, and the subscriber that keeps it current.

``WorldState`` is a plain data holder with query helpers. It never talks to the
network and never decides anything -- that keeps it trivial to build by hand in
tests. ``WorldTracker`` is the only thing that mutates it, driven by events.
"""

from __future__ import annotations

from dataclasses import dataclass, field

from . import events as ev
from .clock import Clock
from .models import Buff, Character, Entity, EntityKind, MapGrid, Vec2


@dataclass
class WorldState:
    character: Character = field(default_factory=Character)
    entities: dict[int, Entity] = field(default_factory=dict)
    buffs: dict[int, Buff] = field(default_factory=dict)
    inventory: dict[int, int] = field(default_factory=dict)   # vnum -> count
    grid: MapGrid = field(default_factory=lambda: MapGrid(0, 0))
    connected: bool = False

    # --- queries ------------------------------------------------------------

    def monsters(self) -> list[Entity]:
        return [
            e for e in self.entities.values()
            if e.kind is EntityKind.MONSTER and e.alive
        ]

    def monsters_within(self, radius: int, vnums: set[int] | None = None) -> list[Entity]:
        """Live monsters within ``radius`` tiles, nearest first.

        ``vnums`` optionally restricts to a whitelist of monster templates, so a
        profile can farm one species and ignore everything else.
        """
        origin = self.character.pos
        found = [
            e for e in self.monsters()
            if origin.chebyshev(e.pos) <= radius
            and (vnums is None or e.vnum in vnums)
        ]
        found.sort(key=lambda e: origin.euclidean(e.pos))
        return found

    def nearest_monster(self, vnums: set[int] | None = None) -> Entity | None:
        candidates = self.monsters()
        if vnums is not None:
            candidates = [e for e in candidates if e.vnum in vnums]
        if not candidates:
            return None
        return min(candidates, key=lambda e: self.character.pos.euclidean(e.pos))

    def entity(self, entity_id: int) -> Entity | None:
        return self.entities.get(entity_id)

    def has_buff(self, buff_id: int) -> bool:
        return buff_id in self.buffs

    def buff_remaining(self, buff_id: int, now: float) -> float:
        buff = self.buffs.get(buff_id)
        return buff.remaining(now) if buff else 0.0

    def item_count(self, vnum: int) -> int:
        return self.inventory.get(vnum, 0)

    def expire_buffs(self, now: float) -> list[int]:
        """Drop buffs whose timer ran out. Returns the ids removed.

        Servers do send an explicit "buff ended" packet, but it can be missed on
        a lossy link; expiring locally keeps upkeep correct either way.
        """
        dead = [bid for bid, b in self.buffs.items() if b.expires_at <= now]
        for bid in dead:
            del self.buffs[bid]
        return dead


class WorldTracker:
    """Applies perception events to a ``WorldState``. The only mutator."""

    def __init__(self, state: WorldState, bus, clock: Clock) -> None:
        self.state = state
        self.clock = clock
        bus.subscribe(ev.Connected, self._on_connected)
        bus.subscribe(ev.Disconnected, self._on_disconnected)
        bus.subscribe(ev.CharacterUpdated, self._on_character)
        bus.subscribe(ev.StatsChanged, self._on_stats)
        bus.subscribe(ev.SelfMoved, self._on_self_moved)
        bus.subscribe(ev.MapChanged, self._on_map)
        bus.subscribe(ev.EntitySpawned, self._on_spawn)
        bus.subscribe(ev.EntityDespawned, self._on_despawn)
        bus.subscribe(ev.EntityMoved, self._on_entity_moved)
        bus.subscribe(ev.EntityHpChanged, self._on_entity_hp)
        bus.subscribe(ev.BuffGained, self._on_buff_gained)
        bus.subscribe(ev.BuffLost, self._on_buff_lost)
        bus.subscribe(ev.InventoryChanged, self._on_inventory)

    def _on_connected(self, _: ev.Connected) -> None:
        self.state.connected = True

    def _on_disconnected(self, _: ev.Disconnected) -> None:
        # Everything we knew about the map is stale now; keep only our identity.
        self.state.connected = False
        self.state.entities.clear()
        self.state.buffs.clear()

    def _on_character(self, e: ev.CharacterUpdated) -> None:
        self.state.character = e.character

    def _on_stats(self, e: ev.StatsChanged) -> None:
        c = self.state.character
        c.hp, c.hp_max, c.mp, c.mp_max = e.hp, e.hp_max, e.mp, e.mp_max

    def _on_self_moved(self, e: ev.SelfMoved) -> None:
        self.state.character.pos = e.pos

    def _on_map(self, e: ev.MapChanged) -> None:
        # A map change invalidates every entity we were tracking.
        self.state.character.map_id = e.map_id
        self.state.grid = e.grid
        self.state.entities.clear()

    def _on_spawn(self, e: ev.EntitySpawned) -> None:
        self.state.entities[e.entity.id] = e.entity

    def _on_despawn(self, e: ev.EntityDespawned) -> None:
        self.state.entities.pop(e.entity_id, None)

    def _on_entity_moved(self, e: ev.EntityMoved) -> None:
        if (entity := self.state.entities.get(e.entity_id)) is not None:
            entity.pos = e.pos

    def _on_entity_hp(self, e: ev.EntityHpChanged) -> None:
        if (entity := self.state.entities.get(e.entity_id)) is not None:
            entity.hp_pct = e.hp_pct

    def _on_buff_gained(self, e: ev.BuffGained) -> None:
        self.state.buffs[e.buff.id] = e.buff

    def _on_buff_lost(self, e: ev.BuffLost) -> None:
        self.state.buffs.pop(e.buff_id, None)

    def _on_inventory(self, e: ev.InventoryChanged) -> None:
        if e.count <= 0:
            self.state.inventory.pop(e.vnum, None)
        else:
            self.state.inventory[e.vnum] = e.count
