"""NosCore-specific protocol layer.

Ported directly from the NosCore emulator source (MIT, github.com/NosCoreIO):
  - NosCore.Networking/Encoding/{Login,World}{Encoder,Decoder}.cs
  - NosCore.Networking/Encoding/FrameDelimiter.cs

The bot is a *client*, so each function here is the inverse of the matching
server codec: what we send is what the server's decoder consumes, and what we
receive is what the server's encoder produced. Correctness is proven in
tests/test_noscore_crypto.py by round-tripping against a faithful transcription
of those same server codecs.
"""

from .crypto import (
    frame_delimiter,
    login_encrypt,
    LoginRecvStream,
    world_encrypt_session,
    world_encrypt,
    WorldRecvStream,
)

__all__ = [
    "frame_delimiter",
    "login_encrypt",
    "LoginRecvStream",
    "world_encrypt_session",
    "world_encrypt",
    "WorldRecvStream",
]
