"""Regression coverage for the NosCore packet map.

Every sample is a real NosCore packet line; each asserts the field mapping
lands where the source says it does. If someone renumbers an index while
'tidying up' the YAML, this fails loudly instead of silently reading the
wrong field on a live server.
"""

from __future__ import annotations

import pytest

from nqa.adapters.protocol.codec import decode
from nqa.adapters.protocol.spec import PacketSpec, SpecError
from nqa.adapters.protocol.translate import build
from nqa.core import events as ev

SPEC = PacketSpec.load("nqa/adapters/protocol/noscore/packets.noscore.yaml")


def decode_line(line: str):
    packet = decode(line)
    match = SPEC.match(packet)
    return build(packet, match, now=1000.0) if match else None


def test_own_stats_reads_absolute_hp_and_mp():
    e = decode_line("st 1 1 40 0 100 100 4200 1800 5000 3000 0")
    assert e == ev.StatsChanged(hp=4200, hp_max=5000, mp=1800, mp_max=3000)


def test_monster_st_is_an_hp_update_not_our_stats():
    e = decode_line("st 3 4042 30 0 62 0 620 0 1000 0 0")
    assert e == ev.EntityHpChanged(entity_id=4042, hp_pct=62.0)


def test_self_move():
    assert decode_line("mv 1 1 55 92 11") == ev.SelfMoved(pos=__import__(
        "nqa.core.models", fromlist=["Vec2"]).Vec2(55, 92))


def test_monster_move_keeps_the_entity_id():
    e = decode_line("mv 3 4042 56 92 11")
    assert e.entity_id == 4042 and (e.pos.x, e.pos.y) == (56, 92)


def test_monster_spawn_maps_vnum_id_and_position():
    e = decode_line("in 3 SomeMob 2612 4042 55 92 2")
    ent = e.entity
    assert (ent.vnum, ent.id, ent.pos.x, ent.pos.y) == (2612, 4042, 55, 92)


def test_despawn():
    assert decode_line("out 3 4042") == ev.EntityDespawned(entity_id=4042)


def test_player_move_and_monster_move_are_discriminated_on_visualtype():
    assert isinstance(decode_line("mv 1 1 5 6 11"), ev.SelfMoved)
    assert isinstance(decode_line("mv 3 9 5 6 11"), ev.EntityMoved)


def test_use_skill_targets_a_monster():
    assert SPEC.command("use_skill", skill_id=237, target_id=4042) == "u_s 237 3 4042"


def test_computed_field_commands_are_left_undefined_on_purpose():
    # move / u_i / self-cast need computed values; calling them must fail loud.
    for name in ("move", "use_item", "use_skill_self"):
        with pytest.raises(SpecError):
            SPEC.command(name, x=1, y=2, speed=11, vnum=1, slot=1, skill_id=1)
