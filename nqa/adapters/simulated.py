"""An in-process fake world.

This exists so the whole perception -> decision -> action loop is runnable and
testable with no game, no server and no network. It is a test harness, not a
toy: every behaviour in the stack is exercised against it, and a regression in
targeting or upkeep shows up here long before it would on a live connection.

Determinism is the point -- seed the RNG and a run replays exactly.
"""

from __future__ import annotations

import asyncio
import random
from dataclasses import dataclass, field

from ..core import events as ev
from ..core.clock import Clock
from ..core.events import EventBus
from ..core.models import Buff, Character, Entity, EntityKind, MapGrid, Vec2
from .base import GameAdapter


@dataclass
class SimConfig:
    width: int = 40
    height: int = 40
    monster_count: int = 8
    monster_hp: int = 120
    monster_damage: int = 9
    monster_attack_interval: float = 1.5
    monster_aggro_radius: int = 8
    monster_move_interval: float = 0.8
    respawn_delay: float = 6.0
    default_skill_damage: int = 35
    skill_damage: dict[int, int] = field(default_factory=dict)
    skill_mp_cost: dict[int, int] = field(default_factory=dict)
    # vnum -> ("hp" | "mp" | "buff", amount)
    item_effects: dict[int, tuple[str, int]] = field(default_factory=dict)
    buff_durations: dict[int, float] = field(default_factory=dict)
    default_buff_duration: float = 120.0
    regen_interval: float = 3.0
    hp_regen: int = 4
    mp_regen: int = 6
    seed: int = 1337
    # Tests drive the world by calling ``step()`` themselves, against a manual
    # clock; the background pump would race that with real time.
    auto_pump: bool = True


@dataclass
class _Monster:
    entity: Entity
    hp: int
    hp_max: int
    dead_since: float | None = None
    next_attack_at: float = 0.0
    next_move_at: float = 0.0


class SimulatedAdapter(GameAdapter):
    def __init__(
        self,
        bus: EventBus,
        clock: Clock,
        config: SimConfig | None = None,
        *,
        character: Character | None = None,
        inventory: dict[int, int] | None = None,
    ) -> None:
        super().__init__(bus)
        self.clock = clock
        self.cfg = config or SimConfig()
        self.rng = random.Random(self.cfg.seed)
        self.grid = self._build_grid()
        self.character = character or Character(
            id=1, name="Sim", pos=Vec2(self.cfg.width // 2, self.cfg.height // 2),
            level=30, hp=1200, hp_max=1200, mp=600, mp_max=600,
        )
        # A character standing inside geometry can never path anywhere, and the
        # failure is silent: every A* call just returns None. Nudge to open
        # ground instead of shipping a world that deadlocks on tick one.
        if not self.grid.walkable(self.character.pos):
            self.character.pos = self._nearest_walkable(self.character.pos)
        self.inventory: dict[int, int] = dict(inventory or {})
        self.monsters: dict[int, _Monster] = {}
        self._next_id = 1000
        self._next_regen_at = 0.0
        self._pump: asyncio.Task | None = None
        self._running = False
        self.command_log: list[tuple[str, tuple]] = []
        self.kills = 0

    # --- world construction -------------------------------------------------

    def _build_grid(self) -> MapGrid:
        cfg = self.cfg
        blocked: set[tuple[int, int]] = set()
        # A border plus a few interior walls, so pathfinding has to do real work.
        for x in range(cfg.width):
            blocked.add((x, 0))
            blocked.add((x, cfg.height - 1))
        for y in range(cfg.height):
            blocked.add((0, y))
            blocked.add((cfg.width - 1, y))
        for y in range(5, cfg.height - 10):
            blocked.add((cfg.width // 3, y))
        for x in range(cfg.width // 2, cfg.width - 6):
            blocked.add((x, cfg.height // 2))
        return MapGrid(cfg.width, cfg.height, blocked)

    def _nearest_walkable(self, origin: Vec2) -> Vec2:
        for radius in range(1, max(self.cfg.width, self.cfg.height)):
            for dx in range(-radius, radius + 1):
                for dy in (-radius, radius):
                    for p in (Vec2(origin.x + dx, origin.y + dy),
                              Vec2(origin.x + dy, origin.y + dx)):
                        if self.grid.walkable(p):
                            return p
        raise RuntimeError("simulated map has no walkable tile")

    def _random_walkable(self) -> Vec2:
        for _ in range(500):
            p = Vec2(self.rng.randrange(self.cfg.width), self.rng.randrange(self.cfg.height))
            if self.grid.walkable(p):
                return p
        return self.character.pos

    def _spawn_monster(self) -> _Monster:
        self._next_id += 1
        entity = Entity(
            id=self._next_id, kind=EntityKind.MONSTER, pos=self._random_walkable(),
            vnum=self.rng.choice([100, 101]), name="Sim Mob",
            level=self.rng.randint(25, 35), hp_pct=100.0,
        )
        mob = _Monster(entity=entity, hp=self.cfg.monster_hp, hp_max=self.cfg.monster_hp)
        self.monsters[entity.id] = mob
        return mob

    # --- lifecycle ----------------------------------------------------------

    async def connect(self) -> None:
        self._running = True
        await self.bus.publish(ev.Connected())
        await self.bus.publish(ev.MapChanged(map_id=1, grid=self.grid))
        await self.bus.publish(ev.CharacterUpdated(character=self.character))
        for vnum, count in self.inventory.items():
            await self.bus.publish(ev.InventoryChanged(vnum=vnum, count=count))
        for _ in range(self.cfg.monster_count):
            mob = self._spawn_monster()
            await self.bus.publish(ev.EntitySpawned(entity=mob.entity))
        if self.cfg.auto_pump:
            self._pump = asyncio.create_task(self._pump_loop())

    async def disconnect(self) -> None:
        self._running = False
        if self._pump is not None:
            self._pump.cancel()
            try:
                await self._pump
            except asyncio.CancelledError:
                pass
            self._pump = None
        await self.bus.publish(ev.Disconnected(reason="simulator stopped"))

    async def _pump_loop(self) -> None:
        while self._running:
            await self.step()
            await asyncio.sleep(0.1)

    # --- simulation ---------------------------------------------------------

    async def step(self) -> None:
        """Advance the world one slice. Called by the pump, or directly in tests."""
        now = self.clock.now()
        await self._step_respawns(now)
        await self._step_monster_ai(now)
        await self._step_regen(now)

    async def _step_respawns(self, now: float) -> None:
        for mob_id, mob in list(self.monsters.items()):
            if mob.dead_since is not None and now - mob.dead_since >= self.cfg.respawn_delay:
                del self.monsters[mob_id]
                fresh = self._spawn_monster()
                await self.bus.publish(ev.EntitySpawned(entity=fresh.entity))

    async def _step_monster_ai(self, now: float) -> None:
        for mob in list(self.monsters.values()):
            if mob.dead_since is not None:
                continue
            distance = mob.entity.pos.chebyshev(self.character.pos)

            if distance <= self.cfg.monster_aggro_radius and now >= mob.next_move_at:
                mob.next_move_at = now + self.cfg.monster_move_interval
                if distance > 1:
                    step = self._step_toward(mob.entity.pos, self.character.pos)
                    if step is not None:
                        mob.entity.pos = step
                        await self.bus.publish(ev.EntityMoved(mob.entity.id, step))
                        distance = step.chebyshev(self.character.pos)

            if distance <= 1 and now >= mob.next_attack_at:
                mob.next_attack_at = now + self.cfg.monster_attack_interval
                await self._damage_character(self.cfg.monster_damage)

    def _step_toward(self, src: Vec2, dst: Vec2) -> Vec2 | None:
        dx = (dst.x > src.x) - (dst.x < src.x)
        dy = (dst.y > src.y) - (dst.y < src.y)
        for candidate in (Vec2(src.x + dx, src.y + dy), Vec2(src.x + dx, src.y), Vec2(src.x, src.y + dy)):
            if candidate != src and self.grid.walkable(candidate):
                return candidate
        return None

    async def _step_regen(self, now: float) -> None:
        if now < self._next_regen_at:
            return
        self._next_regen_at = now + self.cfg.regen_interval
        before = (self.character.hp, self.character.mp)
        self.character.hp = min(self.character.hp_max, self.character.hp + self.cfg.hp_regen)
        self.character.mp = min(self.character.mp_max, self.character.mp + self.cfg.mp_regen)
        if (self.character.hp, self.character.mp) != before:
            await self._publish_stats()

    async def _damage_character(self, amount: int) -> None:
        self.character.hp = max(0, self.character.hp - amount)
        await self._publish_stats()

    async def _publish_stats(self) -> None:
        c = self.character
        await self.bus.publish(ev.StatsChanged(c.hp, c.hp_max, c.mp, c.mp_max))

    async def _kill(self, mob: _Monster, now: float) -> None:
        self.kills += 1
        mob.dead_since = now
        mob.entity.hp_pct = 0.0
        await self.bus.publish(ev.EntityDespawned(entity_id=mob.entity.id))

    # --- commands -----------------------------------------------------------

    async def use_skill(self, skill_id: int, target_id: int | None = None) -> None:
        self.command_log.append(("use_skill", (skill_id, target_id)))
        now = self.clock.now()

        cost = self.cfg.skill_mp_cost.get(skill_id, 0)
        if cost > self.character.mp:
            return                            # server would refuse the cast
        if cost:
            self.character.mp -= cost
            await self._publish_stats()

        if target_id is None:
            duration = self.cfg.buff_durations.get(skill_id, self.cfg.default_buff_duration)
            await self.bus.publish(ev.BuffGained(
                Buff(id=skill_id, name=f"skill:{skill_id}", expires_at=now + duration)
            ))
            return

        mob = self.monsters.get(target_id)
        if mob is None or mob.dead_since is not None:
            return

        damage = self.cfg.skill_damage.get(skill_id, self.cfg.default_skill_damage)
        mob.hp = max(0, mob.hp - damage)
        mob.entity.hp_pct = 100.0 * mob.hp / mob.hp_max
        if mob.hp <= 0:
            await self._kill(mob, now)
        else:
            await self.bus.publish(ev.EntityHpChanged(mob.entity.id, mob.entity.hp_pct))

    async def attack(self, target_id: int) -> None:
        self.command_log.append(("attack", (target_id,)))
        await self.use_skill(0, target_id)

    async def use_item(self, vnum: int) -> None:
        self.command_log.append(("use_item", (vnum,)))
        if self.inventory.get(vnum, 0) <= 0:
            return

        self.inventory[vnum] -= 1
        await self.bus.publish(ev.InventoryChanged(vnum=vnum, count=self.inventory[vnum]))

        kind, amount = self.cfg.item_effects.get(vnum, ("buff", 0))
        if kind == "hp":
            self.character.hp = min(self.character.hp_max, self.character.hp + amount)
            await self._publish_stats()
        elif kind == "mp":
            self.character.mp = min(self.character.mp_max, self.character.mp + amount)
            await self._publish_stats()
        else:
            duration = self.cfg.buff_durations.get(vnum, self.cfg.default_buff_duration)
            await self.bus.publish(ev.BuffGained(
                Buff(id=vnum, name=f"item:{vnum}", expires_at=self.clock.now() + duration)
            ))

    async def move_to(self, pos: Vec2) -> None:
        self.command_log.append(("move_to", (pos.x, pos.y)))
        # Reject anything that is not a legal single step, exactly as a server
        # would: this is what makes the navigator's desync handling testable.
        if self.character.pos.chebyshev(pos) > 1 or not self.grid.walkable(pos):
            return
        self.character.pos = pos
        await self.bus.publish(ev.SelfMoved(pos=pos))
