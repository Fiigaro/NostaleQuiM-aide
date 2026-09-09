"""Round-trip proof for the NosCore crypto port.

The oracle below is a faithful transcription of the NosCore *server* codecs
(NosCore.Networking/Encoding/*.cs). The client-side routines in
nqa.adapters.protocol.noscore.crypto must be their exact inverse, so
server_decode(client_encode(x)) == x is the correctness criterion -- and it
holds with no server running, because the oracle *is* the server's logic.

Any divergence here is exactly the silent-wrong-value failure mode that a live
connection would otherwise surface only as corrupted packets.
"""

from __future__ import annotations

import pytest

from nqa.adapters.protocol.noscore import crypto as c

ENC = "latin-1"


# ===========================================================================
# ORACLE: transcribed from NosCore server source (the ground truth)
# ===========================================================================

def srv_login_decode(data: bytes) -> str:
    # LoginDecoder.Decode
    return "".join(chr(((b - 15) & 0xFF) ^ 195) for b in data)


def srv_login_encode(packet: str) -> bytes:
    # LoginEncoder.Encode: bytes of (packet + " "), +15 on content, last = 25
    tmp = bytearray((packet + " ").encode(ENC))
    for i in range(len(packet)):
        tmp[i] = (tmp[i] + 15) & 0xFF
    tmp[-1] = 25
    return bytes(tmp)


def srv_world_encode(packet: str) -> bytes:
    # WorldEncoder.Encode
    str_bytes = packet.encode(ENC)
    length = len(str_bytes)
    import math
    out = bytearray(length + math.ceil(length / 0x7E) + 1)
    j = 0
    for i in range(length):
        if i % 0x7E == 0:
            out[i + j] = 0x7E if (length - i) > 0x7E else (length - i)
            j += 1
        out[i + j] = (~str_bytes[i]) & 0xFF
    out[-1] = 0xFF
    return bytes(out)


def _case_transform(byte: int, session_id: int) -> int:
    # WorldDecoder switch, server side
    key = session_id & 0xFF
    first = (key + 0x40) & 0xFF
    case = (session_id >> 6) & 0x03
    if case == 0:
        return (byte - first) & 0xFF
    if case == 1:
        return (byte + first) & 0xFF
    if case == 2:
        return ((byte - first) ^ 0xC3) & 0xFF
    return ((byte + first) ^ 0xC3) & 0xFF


def _decrypt_private(s: str) -> str:
    # WorldDecoder.DecryptPrivate
    table = [' ', '-', '.', '0', '1', '2', '3', '4', '5', '6', '7', '8', '9', 'n']
    out = bytearray()
    count = 0
    n = len(s)
    while count < n:
        head = ord(s[count])
        if head <= 0x7A:
            length = head
            for _ in range(length):
                count += 1
                out.append((ord(s[count]) ^ 0xFF) & 0xFF if count < n else 255)
        else:
            length = head & 0x7F
            i = 0
            while i < length:
                count += 1
                val = ord(s[count]) if count < n else 0
                highbyte = (val & 0xF0) >> 4
                lowbyte = val & 0x0F
                if highbyte not in (0x0, 0xF):
                    out.append(ord(table[highbyte - 1]) & 0xFF)
                    i += 1
                if lowbyte not in (0x0, 0xF):
                    out.append(ord(table[lowbyte - 1]) & 0xFF)
                i += 1
        count += 1
    return out.decode(ENC)


def srv_world_decode(frame_with_delim: bytes, session_id: int) -> list[str]:
    # Pipeline strips the trailing delimiter, then WorldDecoder.Decode runs.
    delim = c.frame_delimiter(session_id)
    assert frame_with_delim[-1] == delim, "frame not terminated by the session delimiter"
    body = frame_with_delim[:-1]
    s = "".join(chr(_case_transform(b, session_id)) for b in body)
    return [_decrypt_private(seg) for seg in s.split("\xFF") if seg]


def srv_world_decode_session(frame_with_delim: bytes) -> str:
    # WorldDecoder.DecryptCustomParameter (first packet). Pipeline strips 0x0E.
    assert frame_with_delim[-1] == 0x0E
    data = frame_with_delim[:-1]
    table_special = {0: " ", 1: " ", 2: "-", 3: "."}
    out = []
    for i in range(1, len(data)):            # str[0] skipped
        first = data[i] - 0xF
        second = first & 0xF0
        first = first - second
        second >>= 4
        for nib in (second, first):
            out.append(table_special.get(nib, chr((nib + 0x2C) & 0xFF)) if nib in table_special
                       else chr((nib + 0x2C) & 0xFF))
    return "".join(out)


# ===========================================================================
# LOGIN
# ===========================================================================

LOGIN_SAMPLES = [
    "NoS0575 admin password 0043BCC207",
    "4229 user pass",
    "0 test",
    "a",
]


@pytest.mark.parametrize("packet", LOGIN_SAMPLES)
def test_login_send_is_decoded_by_the_server(packet):
    assert srv_login_decode(c.login_encrypt(packet)) == packet


@pytest.mark.parametrize("packet", LOGIN_SAMPLES)
def test_login_recv_decrypts_what_the_server_sent(packet):
    stream = c.LoginRecvStream()
    assert list(stream.feed(srv_login_encode(packet))) == [packet]


def test_login_recv_reassembles_a_split_frame():
    stream = c.LoginRecvStream()
    wire = srv_login_encode("NsTeST 1 2 3")
    cut = len(wire) // 2
    assert list(stream.feed(wire[:cut])) == []          # no terminator yet
    assert list(stream.feed(wire[cut:])) == ["NsTeST 1 2 3"]


def test_login_recv_splits_batched_frames():
    stream = c.LoginRecvStream()
    wire = srv_login_encode("aaa 1") + srv_login_encode("bbb 2")
    assert list(stream.feed(wire)) == ["aaa 1", "bbb 2"]


# ===========================================================================
# WORLD: first packet (session id)
# ===========================================================================

@pytest.mark.parametrize("session", ["12345", "9", "1073741", "42"])
def test_world_first_packet_carries_the_session_id(session):
    frame = c.world_encrypt_session(session)
    assert frame[-1] == 0x0E                             # first-packet delimiter
    decoded = srv_world_decode_session(frame)
    assert decoded.startswith(session)                  # trailing pad tolerated


# ===========================================================================
# WORLD: client -> server, across all four session cases
# ===========================================================================

# session ids chosen so (session >> 6) & 3 hits 0,1,2,3 respectively
SESSIONS_BY_CASE = {0: 0x00 << 6, 1: 0x01 << 6, 2: 0x02 << 6, 3: 0x03 << 6}
# add a non-zero key component to each
SESSIONS = [case_base | 0x25 for case_base in SESSIONS_BY_CASE.values()]

WORLD_SAMPLES = [
    "walk 55 92 0 11",
    "u_s 1 3 4042",
    "0",
    "say hello world this is a longer packet " * 5,     # forces multiple chunks
]


@pytest.mark.parametrize("session_id", SESSIONS)
@pytest.mark.parametrize("packet", WORLD_SAMPLES)
def test_world_send_is_decoded_by_the_server(packet, session_id):
    frame = c.world_encrypt(packet, session_id)
    assert srv_world_decode(frame, session_id) == [packet]


def test_all_four_session_cases_are_actually_exercised():
    seen = {(sid >> 6) & 3 for sid in SESSIONS}
    assert seen == {0, 1, 2, 3}


def test_the_delimiter_is_the_encrypted_form_of_0xFF():
    # The invariant the whole scheme rests on, asserted rather than trusted.
    for session_id in SESSIONS:
        assert c.frame_delimiter(session_id) == c._case_encrypt_byte(0xFF, session_id)


def test_a_long_packet_spans_multiple_private_chunks():
    packet = "x" * 300                                   # > 0x7A, several chunks
    session_id = SESSIONS[0]
    assert srv_world_decode(c.world_encrypt(packet, session_id), session_id) == [packet]


# ===========================================================================
# WORLD: server -> client
# ===========================================================================

@pytest.mark.parametrize("packet", [
    "st 1 1 40 0 100 100 5000 3000 5000 3000 0",
    "mv 3 4042 55 92 11",
    "in 3 - 2 4042 55 92 2",
    "c_close",
])
def test_world_recv_decrypts_what_the_server_sent(packet):
    stream = c.WorldRecvStream()
    assert list(stream.feed(srv_world_encode(packet))) == [packet]


def test_world_recv_reassembles_a_split_frame():
    stream = c.WorldRecvStream()
    wire = srv_world_encode("st 1 1 40 0 100 100 5000 3000 5000 3000 0")
    cut = len(wire) // 2
    assert list(stream.feed(wire[:cut])) == []
    assert list(stream.feed(wire[cut:])) == ["st 1 1 40 0 100 100 5000 3000 5000 3000 0"]


def test_world_recv_splits_batched_frames():
    stream = c.WorldRecvStream()
    wire = srv_world_encode("mv 3 1 5 6 11") + srv_world_encode("out 3 1")
    assert list(stream.feed(wire)) == ["mv 3 1 5 6 11", "out 3 1"]


def test_world_recv_round_trips_a_long_packet():
    stream = c.WorldRecvStream()
    packet = "in 2 " + "z" * 400
    assert list(stream.feed(srv_world_encode(packet))) == [packet]
