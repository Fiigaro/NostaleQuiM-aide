"""Time source abstraction.

Every module that needs the current time takes a ``Clock`` instead of calling
``time.monotonic()`` directly. Tests drive a ``ManualClock`` and get
deterministic cooldown/buff behaviour with no sleeping.
"""

from __future__ import annotations

import time
from typing import Protocol


class Clock(Protocol):
    def now(self) -> float:
        """Monotonic seconds. Only differences are meaningful."""
        ...


class SystemClock:
    """Production clock, backed by ``time.monotonic``."""

    def now(self) -> float:
        return time.monotonic()


class ManualClock:
    """Test clock. Time only moves when you move it."""

    def __init__(self, start: float = 0.0) -> None:
        self._now = start

    def now(self) -> float:
        return self._now

    def advance(self, seconds: float) -> float:
        if seconds < 0:
            raise ValueError("cannot move a monotonic clock backwards")
        self._now += seconds
        return self._now
