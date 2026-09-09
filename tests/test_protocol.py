"""Protocol machinery tests.

No real game data is used here, and that is the point: the spec below is
fictional. What is under test is that the framing, the discriminated dispatch
and the command templating all work, so that filling in a real spec is the
only remaining step.
"""

from __future__ import annotations

import asyncio

import pytest

from nqa.adapters.protocol.adapter import ProtocolAdapter
from nqa.adapters.protocol.codec import Packet, decode, encode
from nqa.adapters.protocol.crypto import NullEncryption
from nqa.adapters.protocol.spec import PacketSpec, SpecError
from nqa.core import events as ev
from nqa.core.clock import ManualClock
from nqa.core.events import EventBus
from nqa.core.models import Vec2

SPEC = PacketSpec.from_dict({
    "incoming": {
        "stats": {"header": "st", "fields": {"hp": 1, "hp_max": 2, "mp": 3, "mp_max": 4}},
        # One header, two meanings, split on the entity-type field.
        "self_moved": {"header": "mv", "when": {1: "1"}, "fields": {"x": 3, "y": 4}},
        "entity_moved": {"header": "mv", "when": {1: "3"}, "fields": {"id": 2, "x": 3, "y": 4}},
        "entity_spawn": {"header": "in", "when": {1: "3"},
                         "fields": {"vnum": 2, "id": 3, "x": 4, "y": 5, "level": 6, "hp_pct": 7}},
        "entity_despawn": {"header": "out", "fields": {"id": 1}},
        "buff_gained": {"header": "bf", "fields": {"id": 1, "duration": 2}},
        "inventory": {"header": "ivn", "fields": {"vnum": 1, "count": 2}},
    },
    "outgoing": {
        "move": "walk {x} {y} {speed}",
        "use_skill": "u_s {skill_id} {target_id}",
        "use_item": "u_i {vnum}",
    },
})


# --- codec ------------------------------------------------------------------

def test_decode_splits_header_from_args():
    assert decode("st 100 200") == Packet("st", ("100", "200"))


def test_decode_handles_a_bare_header():
    assert decode("pulse") == Packet("pulse", ())


def test_blank_frames_are_ignored_not_errors():
    assert decode("   \n") is None and decode("") is None


def test_field_access_is_one_based_after_the_header():
    packet = decode("st 10 20 30")
    assert packet.arg(1) == "10" and packet.int_arg(3) == 30


def test_a_short_packet_yields_defaults_rather_than_raising():
    packet = decode("st 10")
    assert packet.arg(9) is None
    assert packet.int_arg(9, default=-1) == -1


def test_a_non_numeric_field_falls_back_to_the_default():
    assert decode("st abc").int_arg(1, default=7) == 7
    assert decode("st abc").float_arg(1, default=1.5) == 1.5


def test_encode_round_trips():
    assert decode(encode("u_s", 237, 3)) == Packet("u_s", ("237", "3"))


# --- spec -------------------------------------------------------------------

def test_a_plain_header_matches():
    assert SPEC.match(decode("st 1 2 3 4")).event == "stats"


def test_a_discriminator_picks_the_right_meaning():
    assert SPEC.match(decode("mv 1 0 10 20")).event == "self_moved"
    assert SPEC.match(decode("mv 3 55 10 20")).event == "entity_moved"


def test_an_unmatched_discriminator_matches_nothing():
    assert SPEC.match(decode("mv 9 0 0 0")) is None


def test_an_unknown_header_matches_nothing():
    assert SPEC.match(decode("wat 1 2")) is None


def test_a_discriminated_spec_is_tried_before_a_catch_all():
    spec = PacketSpec.from_dict({"incoming": {
        "entity_despawn": {"header": "x", "fields": {"id": 1}},
        "stats": {"header": "x", "when": {1: "9"}, "fields": {"hp": 2}},
    }})
    assert spec.match(decode("x 9 5")).event == "stats"
    assert spec.match(decode("x 1 5")).event == "entity_despawn"


def test_command_templates_are_filled():
    assert SPEC.command("move", x=3, y=4, speed=11) == "walk 3 4 11"


def test_an_undefined_command_says_so():
    with pytest.raises(SpecError, match="pick_up"):
        SPEC.command("pick_up", entity_id=1)


def test_a_template_missing_a_parameter_says_which():
    with pytest.raises(SpecError, match="speed"):
        SPEC.command("move", x=1, y=2)


def test_a_spec_entry_without_a_header_is_rejected():
    with pytest.raises(SpecError, match="incoming.broken"):
        PacketSpec.from_dict({"incoming": {"broken": {"fields": {"id": 1}}}})


def test_the_shipped_template_covers_every_known_builder():
    from nqa.adapters.protocol.translate import BUILDERS
    spec = PacketSpec.load("nqa/adapters/protocol/packets.example.yaml")
    assert {s.event for s in spec.incoming} == set(BUILDERS)


# --- encryption -------------------------------------------------------------

def test_null_encryption_reassembles_a_split_frame():
    enc = NullEncryption()
    assert list(enc.decrypt(b"st 1 2")) == []          # incomplete, buffered
    assert list(enc.decrypt(b" 3 4\n")) == ["st 1 2 3 4"]


def test_null_encryption_splits_a_batched_read():
    enc = NullEncryption()
    assert list(enc.decrypt(b"a 1\nb 2\nc 3\n")) == ["a 1", "b 2", "c 3"]


# --- adapter over a real socket ---------------------------------------------

class Loopback:
    """A stand-in server: records what the bot writes, pushes frames back."""

    def __init__(self) -> None:
        self.received = bytearray()
        self.connected = asyncio.Event()
        self._writer: asyncio.StreamWriter | None = None

    async def handle(self, reader, writer) -> None:
        self._writer = writer
        self.connected.set()
        try:
            while data := await reader.read(1024):
                self.received.extend(data)
        except (ConnectionError, asyncio.CancelledError):
            pass

    async def push(self, *frames: str) -> None:
        assert self._writer is not None
        for frame in frames:
            self._writer.write(frame.encode() + b"\n")
        await self._writer.drain()

    def sent_lines(self) -> list[str]:
        return bytes(self.received).decode().strip().splitlines()


@pytest.fixture
async def wired():
    bus, clock = EventBus(), ManualClock()
    seen: list = []
    for event_type in (ev.StatsChanged, ev.SelfMoved, ev.EntityMoved,
                       ev.EntitySpawned, ev.EntityDespawned, ev.BuffGained,
                       ev.InventoryChanged, ev.Disconnected):
        bus.subscribe(event_type, seen.append)

    loopback = Loopback()
    server = await asyncio.start_server(loopback.handle, "127.0.0.1", 0)
    port = server.sockets[0].getsockname()[1]

    adapter = ProtocolAdapter(bus, clock, SPEC, host="127.0.0.1", port=port)
    await adapter.connect()
    await asyncio.wait_for(loopback.connected.wait(), timeout=2.0)
    try:
        yield adapter, loopback, seen, clock
    finally:
        await adapter.disconnect()
        server.close()
        await server.wait_closed()


async def _settle(seen: list, count: int, timeout: float = 2.0) -> None:
    """Wait until the read loop has produced ``count`` events."""
    async with asyncio.timeout(timeout):
        while len(seen) < count:
            await asyncio.sleep(0.005)


async def test_incoming_packets_become_bus_events(wired):
    _, loopback, seen, _ = wired
    await loopback.push("st 150 300 40 80", "mv 1 0 12 34", "mv 3 77 5 6")
    await _settle(seen, 3)

    assert seen[0] == ev.StatsChanged(hp=150, hp_max=300, mp=40, mp_max=80)
    assert seen[1] == ev.SelfMoved(pos=Vec2(12, 34))
    assert seen[2] == ev.EntityMoved(entity_id=77, pos=Vec2(5, 6))


async def test_a_spawn_packet_carries_every_mapped_field(wired):
    _, loopback, seen, _ = wired
    await loopback.push("in 3 1042 501 20 30 42 87.5")
    await _settle(seen, 1)

    entity = seen[0].entity
    assert (entity.id, entity.vnum, entity.level) == (501, 1042, 42)
    assert entity.pos == Vec2(20, 30) and entity.hp_pct == 87.5


async def test_buff_duration_is_anchored_to_the_clock(wired):
    _, loopback, seen, clock = wired
    clock.advance(500.0)
    await loopback.push("bf 250 120")
    await _settle(seen, 1)
    assert seen[0].buff.expires_at == 620.0


async def test_a_wrong_field_index_yields_a_wrong_value_not_an_error(wired):
    """Documenting the failure mode: mis-indexed fields are silent.

    This is why the shipped spec template is all REPLACE_ME rather than
    plausible guesses -- a plausible guess produces a working bot that reads
    the wrong number."""
    spec = PacketSpec.from_dict({"incoming": {
        "buff_gained": {"header": "bf", "fields": {"id": 2, "duration": 3}},
    }})
    from nqa.adapters.protocol.translate import build
    event = build(decode("bf 250 120"), spec.match(decode("bf 250 120")), 0.0)
    assert event.buff.id == 120                    # read the duration as the id
    assert event.buff.expires_at == 0.0            # and found no duration at all


async def test_unmapped_packets_are_skipped_without_breaking_the_stream(wired):
    _, loopback, seen, _ = wired
    await loopback.push("garbage 1 2 3", "", "st 1 2 3 4")
    await _settle(seen, 1)
    assert isinstance(seen[0], ev.StatsChanged)


async def test_commands_are_written_in_the_spec_format(wired):
    adapter, loopback, _, _ = wired
    await adapter.move_to(Vec2(7, 9))
    await adapter.use_skill(237, target_id=42)
    await adapter.use_item(1242)
    await asyncio.sleep(0.05)

    assert loopback.sent_lines() == ["walk 7 9 11", "u_s 237 42", "u_i 1242"]


async def test_a_command_with_no_template_raises_rather_than_sending_nonsense(wired):
    adapter, _, _, _ = wired
    with pytest.raises(SpecError):
        await adapter.pick_up(1)


async def test_the_server_closing_publishes_a_disconnect(wired):
    adapter, loopback, seen, _ = wired
    loopback._writer.close()
    async with asyncio.timeout(2.0):
        while not any(isinstance(e, ev.Disconnected) for e in seen):
            await asyncio.sleep(0.005)
