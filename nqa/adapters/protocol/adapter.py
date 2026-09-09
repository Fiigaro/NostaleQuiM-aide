"""Adapter that speaks a NosTale-family protocol over TCP.

What is complete here: the transport, the read loop, framing, spec-driven
translation into bus events, and the command surface.

What you must supply before it will connect to anything:
  1. ``crypto.py`` -- the encryption your emulator actually uses, ported from
     its source. ``NullEncryption`` only works against a server in plaintext.
  2. A packet spec YAML -- headers and field indices, likewise from that source.
  3. The login handshake, which is server-specific and not modelled here.

Point this at a server you run yourself.
"""

from __future__ import annotations

import asyncio
import logging

from ...core import events as ev
from ...core.clock import Clock
from ...core.events import EventBus
from ...core.models import Vec2
from ..base import GameAdapter
from .codec import decode
from .crypto import Encryption, NullEncryption
from .spec import PacketSpec
from .translate import build

log = logging.getLogger(__name__)


class ProtocolAdapter(GameAdapter):
    def __init__(
        self,
        bus: EventBus,
        clock: Clock,
        spec: PacketSpec,
        *,
        host: str = "127.0.0.1",
        port: int = 4000,
        encryption: Encryption | None = None,
        read_size: int = 4096,
        move_speed: int = 11,
    ) -> None:
        super().__init__(bus)
        self.clock = clock
        self.spec = spec
        self.host = host
        self.port = port
        self.encryption = encryption or NullEncryption()
        self.read_size = read_size
        self.move_speed = move_speed
        self._reader: asyncio.StreamReader | None = None
        self._writer: asyncio.StreamWriter | None = None
        self._task: asyncio.Task | None = None

    # --- lifecycle ----------------------------------------------------------

    async def connect(self) -> None:
        self._reader, self._writer = await asyncio.open_connection(self.host, self.port)
        self._task = asyncio.create_task(self._read_loop())
        await self.bus.publish(ev.Connected())

    async def disconnect(self, reason: str = "closed") -> None:
        if self._task is not None:
            self._task.cancel()
            try:
                await self._task
            except asyncio.CancelledError:
                pass
            self._task = None

        if self._writer is not None:
            self._writer.close()
            try:
                await self._writer.wait_closed()
            except Exception:                 # already torn down at the OS level
                pass
            self._writer = None
        self._reader = None
        await self.bus.publish(ev.Disconnected(reason=reason))

    # --- reading ------------------------------------------------------------

    async def _read_loop(self) -> None:
        assert self._reader is not None
        reason = "closed"
        try:
            while True:
                chunk = await self._reader.read(self.read_size)
                if not chunk:
                    reason = "server closed the connection"
                    break
                for frame in self.encryption.decrypt(chunk):
                    await self._dispatch(frame)
        except asyncio.CancelledError:
            raise
        except Exception as exc:
            log.exception("read loop failed")
            reason = f"read error: {exc}"
        # Let the rest of the bot know its world model is stale.
        await self.bus.publish(ev.Disconnected(reason=reason))

    async def _dispatch(self, frame: str) -> None:
        packet = decode(frame)
        if packet is None:
            return
        spec = self.spec.match(packet)
        if spec is None:
            log.debug("unmapped packet %s", packet.header)
            return
        event = build(packet, spec, self.clock.now())
        if event is not None:
            await self.bus.publish(event)

    # --- writing ------------------------------------------------------------

    async def _send(self, name: str, **params) -> None:
        if self._writer is None:
            log.warning("dropping %s: not connected", name)
            return
        payload = self.spec.command(name, **params)
        log.debug(">> %s", payload)
        self._writer.write(self.encryption.encrypt(payload))
        await self._writer.drain()

    async def use_skill(self, skill_id: int, target_id: int | None = None) -> None:
        if target_id is None:
            await self._send("use_skill_self", skill_id=skill_id)
        else:
            await self._send("use_skill", skill_id=skill_id, target_id=target_id)

    async def use_item(self, vnum: int) -> None:
        await self._send("use_item", vnum=vnum)

    async def attack(self, target_id: int) -> None:
        await self._send("attack", target_id=target_id)

    async def move_to(self, pos: Vec2) -> None:
        await self._send("move", x=pos.x, y=pos.y, speed=self.move_speed)

    async def pick_up(self, entity_id: int) -> None:
        await self._send("pick_up", entity_id=entity_id)
