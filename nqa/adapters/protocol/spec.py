"""Declarative packet map.

The only game-version-specific knowledge in the codebase lives in a YAML file
loaded here: which header carries which event, which field index holds which
value, and how to format outgoing commands. That is deliberate -- those details
differ between emulator forks and between client versions, so filling them in
is data entry against your server's source rather than a code change.

A header can carry several logical events (a movement packet for us and for a
monster, say). ``when`` discriminates on a field value.
"""

from __future__ import annotations

from dataclasses import dataclass, field
from pathlib import Path
from typing import Any

import yaml

from .codec import Packet


class SpecError(ValueError):
    pass


@dataclass(slots=True)
class IncomingSpec:
    event: str                              # logical name, see translate.BUILDERS
    header: str
    fields: dict[str, int] = field(default_factory=dict)
    when: dict[int, str] = field(default_factory=dict)

    def matches(self, packet: Packet) -> bool:
        return all(packet.arg(idx) == value for idx, value in self.when.items())

    def index(self, name: str) -> int | None:
        return self.fields.get(name)


@dataclass(slots=True)
class PacketSpec:
    incoming: list[IncomingSpec] = field(default_factory=list)
    outgoing: dict[str, str] = field(default_factory=dict)
    _by_header: dict[str, list[IncomingSpec]] = field(default_factory=dict)

    def __post_init__(self) -> None:
        self._by_header = {}
        for spec in self.incoming:
            self._by_header.setdefault(spec.header, []).append(spec)
        # Discriminated specs must be tried before the catch-all for a header,
        # or the general case swallows every specific one.
        for specs in self._by_header.values():
            specs.sort(key=lambda s: -len(s.when))

    def match(self, packet: Packet) -> IncomingSpec | None:
        for spec in self._by_header.get(packet.header, ()):
            if spec.matches(packet):
                return spec
        return None

    def command(self, name: str, **params: Any) -> str:
        template = self.outgoing.get(name)
        if template is None:
            raise SpecError(
                f"outgoing.{name} is not defined in the packet spec; "
                "add it from your emulator's command handlers"
            )
        try:
            return template.format(**params)
        except KeyError as exc:
            raise SpecError(
                f"outgoing.{name}: template needs {exc} which was not supplied"
            ) from None

    @classmethod
    def from_dict(cls, raw: dict[str, Any]) -> "PacketSpec":
        if not isinstance(raw, dict):
            raise SpecError("packet spec must be a YAML mapping")

        incoming = []
        for name, node in (raw.get("incoming") or {}).items():
            if not isinstance(node, dict) or "header" not in node:
                raise SpecError(f"incoming.{name}: needs at least a 'header'")
            incoming.append(IncomingSpec(
                event=name,
                header=str(node["header"]),
                fields={k: int(v) for k, v in (node.get("fields") or {}).items()},
                when={int(k): str(v) for k, v in (node.get("when") or {}).items()},
            ))

        outgoing = {str(k): str(v) for k, v in (raw.get("outgoing") or {}).items()}
        return cls(incoming=incoming, outgoing=outgoing)

    @classmethod
    def load(cls, path: str | Path) -> "PacketSpec":
        path = Path(path)
        if not path.is_file():
            raise SpecError(f"packet spec not found: {path}")
        with path.open("r", encoding="utf-8") as fh:
            return cls.from_dict(yaml.safe_load(fh) or {})
