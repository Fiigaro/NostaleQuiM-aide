"""Transport encryption hook.

NosTale-family servers use custom, session-keyed encryption that differs
between the login and world channels *and* between emulator forks and game
versions. There is no correct implementation to ship here -- port the one your
server actually uses from its own source and plug it in.

``NullEncryption`` covers emulators configured to speak plaintext, which is the
usual dev-mode setting and the right place to start.
"""

from __future__ import annotations

from typing import Iterator, Protocol


class Encryption(Protocol):
    def encrypt(self, payload: str) -> bytes:
        """Serialise one outgoing packet to wire bytes."""
        ...

    def decrypt(self, chunk: bytes) -> Iterator[str]:
        """Feed raw bytes in, yield complete frames out.

        Stateful by contract: a chunk may hold several packets or split one
        across a TCP read, so implementations buffer the remainder.
        """
        ...


class NullEncryption:
    """Newline-delimited plaintext. Handles partial reads."""

    DELIMITER = b"\n"

    def __init__(self, encoding: str = "utf-8") -> None:
        self.encoding = encoding
        self._buffer = bytearray()

    def encrypt(self, payload: str) -> bytes:
        return payload.encode(self.encoding) + self.DELIMITER

    def decrypt(self, chunk: bytes) -> Iterator[str]:
        self._buffer.extend(chunk)
        while self.DELIMITER in self._buffer:
            frame, _, rest = bytes(self._buffer).partition(self.DELIMITER)
            self._buffer = bytearray(rest)
            yield frame.decode(self.encoding, errors="replace")
