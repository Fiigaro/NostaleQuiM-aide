"""Turn matched packets into bus events.

One builder per logical event name. A builder reads fields *by name* through
the spec, so the same code works whatever index your server puts them at.
"""

from __future__ import annotations

import logging
from typing import Any, Callable

from ...core import events as ev
from ...core.models import Buff, Entity, EntityKind, Vec2
from .codec import Packet
from .spec import IncomingSpec

log = logging.getLogger(__name__)

Builder = Callable[[Packet, IncomingSpec, float], Any]


def _pos(packet: Packet, spec: IncomingSpec) -> Vec2:
    return Vec2(
        packet.int_arg(spec.index("x") or 0),
        packet.int_arg(spec.index("y") or 0),
    )


def _stats(packet: Packet, spec: IncomingSpec, now: float):
    return ev.StatsChanged(
        hp=packet.int_arg(spec.index("hp") or 0),
        hp_max=packet.int_arg(spec.index("hp_max") or 0, default=1),
        mp=packet.int_arg(spec.index("mp") or 0),
        mp_max=packet.int_arg(spec.index("mp_max") or 0, default=1),
    )


def _self_moved(packet: Packet, spec: IncomingSpec, now: float):
    return ev.SelfMoved(pos=_pos(packet, spec))


def _entity_spawn(packet: Packet, spec: IncomingSpec, now: float):
    return ev.EntitySpawned(Entity(
        id=packet.int_arg(spec.index("id") or 0),
        kind=EntityKind.MONSTER,
        pos=_pos(packet, spec),
        vnum=packet.int_arg(spec.index("vnum") or 0),
        level=packet.int_arg(spec.index("level") or 0, default=1),
        hp_pct=packet.float_arg(spec.index("hp_pct") or 0, default=100.0),
    ))


def _entity_despawn(packet: Packet, spec: IncomingSpec, now: float):
    return ev.EntityDespawned(entity_id=packet.int_arg(spec.index("id") or 0))


def _entity_moved(packet: Packet, spec: IncomingSpec, now: float):
    return ev.EntityMoved(
        entity_id=packet.int_arg(spec.index("id") or 0),
        pos=_pos(packet, spec),
    )


def _entity_hp(packet: Packet, spec: IncomingSpec, now: float):
    return ev.EntityHpChanged(
        entity_id=packet.int_arg(spec.index("id") or 0),
        hp_pct=packet.float_arg(spec.index("hp_pct") or 0, default=100.0),
    )


def _buff_gained(packet: Packet, spec: IncomingSpec, now: float):
    buff_id = packet.int_arg(spec.index("id") or 0)
    return ev.BuffGained(Buff(
        id=buff_id,
        name=str(buff_id),
        expires_at=now + packet.float_arg(spec.index("duration") or 0, default=0.0),
    ))


def _buff_lost(packet: Packet, spec: IncomingSpec, now: float):
    return ev.BuffLost(buff_id=packet.int_arg(spec.index("id") or 0))


def _inventory(packet: Packet, spec: IncomingSpec, now: float):
    return ev.InventoryChanged(
        vnum=packet.int_arg(spec.index("vnum") or 0),
        count=packet.int_arg(spec.index("count") or 0),
    )


BUILDERS: dict[str, Builder] = {
    "stats": _stats,
    "self_moved": _self_moved,
    "entity_spawn": _entity_spawn,
    "entity_despawn": _entity_despawn,
    "entity_moved": _entity_moved,
    "entity_hp": _entity_hp,
    "buff_gained": _buff_gained,
    "buff_lost": _buff_lost,
    "inventory": _inventory,
}


def build(packet: Packet, spec: IncomingSpec, now: float):
    builder = BUILDERS.get(spec.event)
    if builder is None:
        log.warning(
            "incoming.%s has no builder; known events: %s",
            spec.event, ", ".join(sorted(BUILDERS)),
        )
        return None
    return builder(packet, spec, now)
