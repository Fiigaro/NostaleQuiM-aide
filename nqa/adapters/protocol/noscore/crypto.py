"""NosCore transport encryption, from the client's side.

Every routine is the inverse of a NosCore server codec. References below point
at the exact source file each one mirrors. Byte math uses ``& 0xFF`` wherever
the C# used ``unchecked((byte))``; a NosTale packet body is region-encoded
text, and we map bytes to codepoints 1:1 with latin-1 (only non-ASCII names
are affected, and the region encoding would refine those).

The delimiter identity is the load-bearing invariant that ties the two layers
together: ``frame_delimiter(session)`` equals the encrypted form of 0xFF under
the world case-cipher, so appending the delimiter is the same as encrypting a
0xFF terminator. A test asserts it rather than trusting the comment.
"""

from __future__ import annotations

from typing import Iterator

ENCODING = "latin-1"

# --- login channel ----------------------------------------------------------
# LoginDecoder.cs:  plain = ((enc - 15) & 0xFF) ^ 195   (server reads client)
# LoginEncoder.cs:  enc = plain + 15, frame terminated by byte 25 (server->client)
# The login server runs with UseDelimiter = false: it reads the whole buffer as
# one packet, so a client send needs no frame delimiter of its own.

_LOGIN_XOR = 0xC3        # 195
_LOGIN_OFFSET = 0x0F     # 15
_LOGIN_TERMINATOR = 25   # 0x19, server->client frame terminator


def login_encrypt(packet: str, *, encoding: str = ENCODING) -> bytes:
    """Encrypt one client->login packet (inverse of LoginDecoder)."""
    return bytes(
        ((byte ^ _LOGIN_XOR) + _LOGIN_OFFSET) & 0xFF
        for byte in packet.encode(encoding)
    )


class LoginRecvStream:
    """Decrypt the login server->client byte stream (inverse of LoginEncoder).

    Stateful: server frames are terminated by byte 25, and a TCP read may split
    or batch them, so the remainder is buffered between calls.
    """

    def __init__(self, encoding: str = ENCODING) -> None:
        self.encoding = encoding
        self._buffer = bytearray()

    def feed(self, chunk: bytes) -> Iterator[str]:
        self._buffer.extend(chunk)
        while _LOGIN_TERMINATOR in self._buffer:
            idx = self._buffer.index(_LOGIN_TERMINATOR)
            frame, self._buffer = self._buffer[:idx], self._buffer[idx + 1:]
            yield bytes((b - _LOGIN_OFFSET) & 0xFF for b in frame).decode(
                self.encoding, errors="replace"
            )


# --- world channel: delimiter -----------------------------------------------
# FrameDelimiter.cs

def frame_delimiter(session_id: int, is_first_packet: bool = False) -> int:
    """The byte that terminates a client->world frame for this session."""
    stype = -1 if is_first_packet else (session_id >> 6) & 3
    key = session_id & 0xFF
    if stype == 0:
        return (0xFF + key + 0x40) & 0xFF
    if stype == 1:
        return (0xFF - key - 0x40) & 0xFF
    if stype == 2:
        return ((0xFF ^ 0xC3) + key + 0x40) & 0xFF
    if stype == 3:
        return ((0xFF ^ 0xC3) - key - 0x40) & 0xFF
    return (0xFF + 0x0F) & 0xFF          # first packet -> 0x0E


# --- world channel: first packet (session id) -------------------------------
# WorldDecoder.DecryptCustomParameter: two chars packed per byte via nibbles,
# str[0] skipped, terminated by 0x0E. Digits map through the default branch
# (nibble = char - 0x2C); space/-/./ have dedicated nibble codes.

_NIBBLE_SPECIAL = {" ": 1, "-": 2, ".": 3}


def _char_to_nibble(ch: str) -> int:
    if ch in _NIBBLE_SPECIAL:
        return _NIBBLE_SPECIAL[ch]
    value = ord(ch) - 0x2C
    if not 0 <= value <= 0xF:
        raise ValueError(f"character {ch!r} is not representable in the session encoding")
    return value


def world_encrypt_session(session_text: str) -> bytes:
    """Encode the first world packet: the session id (inverse of
    DecryptCustomParameter). A leading pad byte stands in for the skipped
    str[0]; the frame ends with 0x0E, which is also the first-packet delimiter.
    """
    chars = session_text if len(session_text) % 2 == 0 else session_text + " "
    out = bytearray([0x00])              # skipped prefix byte
    for i in range(0, len(chars), 2):
        high = _char_to_nibble(chars[i])
        low = _char_to_nibble(chars[i + 1])
        out.append((((high << 4) | low) + 0x0F) & 0xFF)
    out.append(0x0E)
    return bytes(out)


# --- world channel: client -> server (subsequent packets) -------------------
# Two layers, inverting WorldDecoder: an inner "private" chunk encoding
# (DecryptPrivate) wrapped by the outer per-byte case cipher, then the
# delimiter (= encrypted 0xFF).

_PRIVATE_CHUNK = 0x7A                    # DecryptPrivate raw-mode max length


def _private_encrypt(text: bytes) -> bytes:
    """Inverse of DecryptPrivate, raw mode only (always valid; skips the
    optional digit-compression table)."""
    out = bytearray()
    for start in range(0, len(text), _PRIVATE_CHUNK):
        chunk = text[start:start + _PRIVATE_CHUNK]
        out.append(len(chunk))
        out.extend((b ^ 0xFF) & 0xFF for b in chunk)
    return bytes(out)


def _case_encrypt_byte(byte: int, session_id: int) -> int:
    key = (session_id & 0xFF)
    first = (key + 0x40) & 0xFF
    case = (session_id >> 6) & 0x03
    if case == 0:
        return (byte + first) & 0xFF
    if case == 1:
        return (byte - first) & 0xFF
    if case == 2:
        return ((byte ^ 0xC3) + first) & 0xFF
    return ((byte ^ 0xC3) - first) & 0xFF   # case 3


def world_encrypt(packet: str, session_id: int, *, encoding: str = ENCODING) -> bytes:
    """Encrypt one client->world packet (inverse of WorldDecoder, session known)."""
    inner = _private_encrypt(packet.encode(encoding))
    body = bytes(_case_encrypt_byte(b, session_id) for b in inner)
    return body + bytes([frame_delimiter(session_id)])


# --- world channel: server -> client ----------------------------------------
# Inverse of WorldEncoder: packets terminated by 0xFF; within a packet, chunks
# of up to 0x7E bytes each prefixed by a length byte, every payload byte ~b.

class WorldRecvStream:
    """Decrypt the world server->client byte stream (inverse of WorldEncoder)."""

    def __init__(self, encoding: str = ENCODING) -> None:
        self.encoding = encoding
        self._buffer = bytearray()

    def feed(self, chunk: bytes) -> Iterator[str]:
        self._buffer.extend(chunk)
        while 0xFF in self._buffer:
            idx = self._buffer.index(0xFF)
            frame, self._buffer = self._buffer[:idx], self._buffer[idx + 1:]
            yield self._decode_frame(frame)

    def _decode_frame(self, frame: bytes) -> str:
        out = bytearray()
        i = 0
        n = len(frame)
        while i < n:
            length = frame[i] & 0x7F           # chunk length (<= 0x7E)
            i += 1
            for _ in range(length):
                if i >= n:
                    break
                out.append((~frame[i]) & 0xFF)
                i += 1
        return out.decode(self.encoding, errors="replace")
