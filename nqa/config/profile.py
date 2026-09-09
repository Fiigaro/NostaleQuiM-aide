"""Profile loading and validation.

Everything tunable lives in YAML: skills, cooldowns, thresholds, item vnums,
waypoints. Nothing about a class or a level belongs in code -- the whole point
is that retuning for a new character is editing one file.

Validation is eager and the errors name the exact path (``combat.skills[1].id``)
because a profile typo that only surfaces as a silent no-op three minutes into
a run is miserable to debug.
"""

from __future__ import annotations

from dataclasses import dataclass, field
from pathlib import Path
from typing import Any

import yaml


class ProfileError(ValueError):
    """Raised with a dotted path to the offending key."""


def _get(node: Any, key: str, path: str, *, default: Any = ..., cast: type | None = None) -> Any:
    where = f"{path}.{key}" if path else key
    if not isinstance(node, dict):
        raise ProfileError(f"{path or '<root>'}: expected a mapping, got {type(node).__name__}")
    if key not in node:
        if default is ...:
            raise ProfileError(f"{where}: required key is missing")
        return default
    value = node[key]
    if cast is not None and value is not None:
        try:
            # bool is an int subclass; refuse the silent 'true -> 1' coercion.
            if cast is not bool and isinstance(value, bool):
                raise TypeError
            value = cast(value)
        except (TypeError, ValueError):
            raise ProfileError(
                f"{where}: expected {cast.__name__}, got {value!r}"
            ) from None
    return value


@dataclass(slots=True)
class SkillConfig:
    id: int
    name: str = ""
    cooldown: float = 1.0
    mp_cost: int = 0
    range: int = 1
    priority: int = 0          # higher is tried first

    @property
    def key(self) -> str:
        return f"skill:{self.id}"


@dataclass(slots=True)
class PotionConfig:
    vnum: int
    threshold_pct: float
    cooldown: float = 3.0

    @property
    def key(self) -> str:
        return f"item:{self.vnum}"


@dataclass(slots=True)
class BuffConfig:
    kind: str                  # "skill" or "item"
    id: int                    # skill id or item vnum
    buff_id: int               # id of the effect it grants, as the server reports it
    name: str = ""
    duration: float = 300.0
    refresh_margin: float = 20.0   # re-apply this many seconds before expiry
    cooldown: float = 5.0

    @property
    def key(self) -> str:
        return f"buff:{self.kind}:{self.id}"


@dataclass(slots=True)
class CombatConfig:
    engage_radius: int = 12
    leash_radius: int = 30
    target_vnums: set[int] = field(default_factory=set)   # empty = anything
    skills: list[SkillConfig] = field(default_factory=list)

    def skills_by_priority(self) -> list[SkillConfig]:
        return sorted(self.skills, key=lambda s: -s.priority)


@dataclass(slots=True)
class SurvivalConfig:
    hp_potion: PotionConfig | None = None
    mp_potion: PotionConfig | None = None
    critical_hp_pct: float = 15.0


@dataclass(slots=True)
class NavigationConfig:
    waypoints: list[tuple[int, int]] = field(default_factory=list)
    arrive_tolerance: int = 2


@dataclass(slots=True)
class RuntimeConfig:
    tick_interval: float = 0.25
    global_cooldown: float = 0.9
    move_interval: float = 0.35           # min delay between movement commands
    max_runtime_minutes: float = 0.0      # 0 = no limit


@dataclass(slots=True)
class Profile:
    name: str = "default"
    combat: CombatConfig = field(default_factory=CombatConfig)
    survival: SurvivalConfig = field(default_factory=SurvivalConfig)
    buffs: list[BuffConfig] = field(default_factory=list)
    navigation: NavigationConfig = field(default_factory=NavigationConfig)
    runtime: RuntimeConfig = field(default_factory=RuntimeConfig)

    @classmethod
    def from_dict(cls, raw: dict[str, Any]) -> "Profile":
        if not isinstance(raw, dict):
            raise ProfileError("<root>: profile must be a YAML mapping")

        combat_raw = _get(raw, "combat", "", default={}) or {}
        skills = []
        for i, s in enumerate(_get(combat_raw, "skills", "combat", default=[]) or []):
            p = f"combat.skills[{i}]"
            skills.append(SkillConfig(
                id=_get(s, "id", p, cast=int),
                name=_get(s, "name", p, default="", cast=str),
                cooldown=_get(s, "cooldown", p, default=1.0, cast=float),
                mp_cost=_get(s, "mp_cost", p, default=0, cast=int),
                range=_get(s, "range", p, default=1, cast=int),
                priority=_get(s, "priority", p, default=0, cast=int),
            ))
        if not skills:
            raise ProfileError("combat.skills: at least one skill is required")

        combat = CombatConfig(
            engage_radius=_get(combat_raw, "engage_radius", "combat", default=12, cast=int),
            leash_radius=_get(combat_raw, "leash_radius", "combat", default=30, cast=int),
            target_vnums=set(_get(combat_raw, "target_vnums", "combat", default=[]) or []),
            skills=skills,
        )

        surv_raw = _get(raw, "survival", "", default={}) or {}

        def _potion(key: str) -> PotionConfig | None:
            node = _get(surv_raw, key, "survival", default=None)
            if not node:
                return None
            p = f"survival.{key}"
            return PotionConfig(
                vnum=_get(node, "vnum", p, cast=int),
                threshold_pct=_get(node, "threshold_pct", p, cast=float),
                cooldown=_get(node, "cooldown", p, default=3.0, cast=float),
            )

        survival = SurvivalConfig(
            hp_potion=_potion("hp_potion"),
            mp_potion=_potion("mp_potion"),
            critical_hp_pct=_get(surv_raw, "critical_hp_pct", "survival", default=15.0, cast=float),
        )

        buffs = []
        for i, b in enumerate(_get(raw, "buffs", "", default=[]) or []):
            p = f"buffs[{i}]"
            kind = _get(b, "kind", p, cast=str)
            if kind not in ("skill", "item"):
                raise ProfileError(f"{p}.kind: expected 'skill' or 'item', got {kind!r}")
            buffs.append(BuffConfig(
                kind=kind,
                id=_get(b, "id", p, cast=int),
                buff_id=_get(b, "buff_id", p, cast=int),
                name=_get(b, "name", p, default="", cast=str),
                duration=_get(b, "duration", p, default=300.0, cast=float),
                refresh_margin=_get(b, "refresh_margin", p, default=20.0, cast=float),
                cooldown=_get(b, "cooldown", p, default=5.0, cast=float),
            ))

        nav_raw = _get(raw, "navigation", "", default={}) or {}
        waypoints = []
        for i, w in enumerate(_get(nav_raw, "waypoints", "navigation", default=[]) or []):
            if not (isinstance(w, (list, tuple)) and len(w) == 2):
                raise ProfileError(f"navigation.waypoints[{i}]: expected [x, y]")
            waypoints.append((int(w[0]), int(w[1])))
        navigation = NavigationConfig(
            waypoints=waypoints,
            arrive_tolerance=_get(nav_raw, "arrive_tolerance", "navigation", default=2, cast=int),
        )

        rt_raw = _get(raw, "runtime", "", default={}) or {}
        runtime = RuntimeConfig(
            tick_interval=_get(rt_raw, "tick_interval", "runtime", default=0.25, cast=float),
            global_cooldown=_get(rt_raw, "global_cooldown", "runtime", default=0.9, cast=float),
            move_interval=_get(rt_raw, "move_interval", "runtime", default=0.35, cast=float),
            max_runtime_minutes=_get(rt_raw, "max_runtime_minutes", "runtime", default=0.0, cast=float),
        )
        if runtime.tick_interval <= 0:
            raise ProfileError("runtime.tick_interval: must be > 0")

        return cls(
            name=_get(raw, "name", "", default="default", cast=str),
            combat=combat, survival=survival, buffs=buffs,
            navigation=navigation, runtime=runtime,
        )

    @classmethod
    def load(cls, path: str | Path) -> "Profile":
        path = Path(path)
        if not path.is_file():
            raise ProfileError(f"profile not found: {path}")
        with path.open("r", encoding="utf-8") as fh:
            return cls.from_dict(yaml.safe_load(fh) or {})
