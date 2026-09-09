"""Packet framing for the NosTale family of protocols.

The wire format is text: one packet per frame, fields separated by spaces, the
first field being the header (opcode). That much is stable across the emulator
forks. What is *not* stable -- and what this module deliberately does not
hardcode -- is which field index means what, and which headers exist at all.
Those live in a spec file you fill in from your own server's source.
"""

from __future__ import annotations

from dataclasses import dataclass


@dataclass(frozen=True, slots=True)
class Packet:
    header: str
    args: tuple[str, ...] = ()

    def __str__(self) -> str:
        return " ".join((self.header, *self.args))

    def arg(self, index: int) -> str | None:
        """Field by position, or None if the packet is shorter than expected.

        Returning None rather than raising matters: field counts differ between
        server versions, and one short packet should not kill the reader.
        """
        return self.args[index - 1] if 0 < index <= len(self.args) else None

    def int_arg(self, index: int, default: int = 0) -> int:
        raw = self.arg(index)
        if raw is None:
            return default
        try:
            return int(raw)
        except ValueError:
            return default

    def float_arg(self, index: int, default: float = 0.0) -> float:
        raw = self.arg(index)
        if raw is None:
            return default
        try:
            return float(raw)
        except ValueError:
            return default


def decode(frame: str) -> Packet | None:
    """Parse one frame. Empty frames are normal keepalive noise, not errors."""
    frame = frame.strip()
    if not frame:
        return None
    header, _, rest = frame.partition(" ")
    return Packet(header=header, args=tuple(rest.split(" ")) if rest else ())


def encode(header: str, *args: object) -> str:
    return " ".join((header, *(str(a) for a in args)))
