"""Walking: close on a target, or patrol when there is nothing to fight.

Approach range is the *smallest* of the configured skill ranges, not the
largest. Stopping at max range looks smarter but deadlocks: park at range 5
when only the range-1 attack is off cooldown and combat never fires while
navigation considers itself arrived. Walking into melee costs a few tiles and
keeps every skill usable.
"""

from __future__ import annotations

import logging

from ..core.actions import Action, MoveTo
from ..core.context import Context
from ..core.models import Vec2
from ..core.pathfinding import path_into_range, path_to
from .base import Behavior
from .targeting import TargetSelector

# Recompute the path once the goal has drifted further than this from the goal
# the current path was built for. Monsters shuffle constantly; replanning on
# every pixel of movement would burn the tick budget for nothing.
_GOAL_DRIFT_TOLERANCE = 2

MOVE_KEY = "move"

log = logging.getLogger(__name__)


class NavigationBehavior(Behavior):
    name = "navigation"
    priority = 10

    def __init__(self, targeting: TargetSelector) -> None:
        self.targeting = targeting
        self._path: list[Vec2] = []
        self._path_goal: Vec2 | None = None
        self._waypoint_index = 0

    def reset(self) -> None:
        self._path = []
        self._path_goal = None

    # --- main ---------------------------------------------------------------

    def decide(self, ctx: Context) -> Action | None:
        # Movement has its own rate limit and is deliberately not gated by the
        # global cooldown -- walking while a cast is on GCD is normal play.
        if not ctx.cooldowns.is_ready(MOVE_KEY):
            return None

        target = self.targeting.select(ctx)
        if target is not None:
            return self._approach(ctx, target.pos)
        return self._patrol(ctx)

    def _approach(self, ctx: Context, goal: Vec2) -> Action | None:
        rng = self._approach_range(ctx)
        if ctx.state.character.pos.chebyshev(goal) <= rng:
            self.reset()
            return None                       # in range; combat's turn

        self._refresh_path(
            ctx, goal,
            lambda grid, start: path_into_range(grid, start, goal, rng),
        )
        return self._next_step(ctx)

    def _patrol(self, ctx: Context) -> Action | None:
        waypoints = ctx.profile.navigation.waypoints
        if not waypoints:
            return None

        self._waypoint_index %= len(waypoints)
        goal = Vec2(*waypoints[self._waypoint_index])
        here = ctx.state.character.pos

        if here.chebyshev(goal) <= ctx.profile.navigation.arrive_tolerance:
            self._waypoint_index = (self._waypoint_index + 1) % len(waypoints)
            self.reset()
            goal = Vec2(*waypoints[self._waypoint_index])

        self._refresh_path(ctx, goal, lambda grid, start: path_to(grid, start, goal))
        return self._next_step(ctx)

    # --- path cache ---------------------------------------------------------

    @staticmethod
    def _approach_range(ctx: Context) -> int:
        skills = ctx.profile.combat.skills
        return min((s.range for s in skills), default=1)

    def _refresh_path(self, ctx: Context, goal: Vec2, planner) -> None:
        here = ctx.state.character.pos

        stale = (
            not self._path
            or self._path_goal is None
            or self._path_goal.chebyshev(goal) > _GOAL_DRIFT_TOLERANCE
        )

        # Drop steps already taken, then check we are actually adjacent to the
        # head of what is left -- if a move was rejected or we were displaced,
        # the cached path no longer starts where we stand.
        if not stale:
            while self._path and self._path[0] == here:
                self._path.pop(0)
            if not self._path or here.chebyshev(self._path[0]) > 1:
                stale = True

        if stale:
            self._path = planner(ctx.state.grid, here) or []
            self._path_goal = goal
            if not self._path:
                # Unreachable, or we are standing somewhere A* refuses to start
                # from. Silent here would look exactly like 'nothing to do'.
                log.debug("no path from %s to %s", here, goal)

    def _next_step(self, ctx: Context) -> Action | None:
        while self._path and self._path[0] == ctx.state.character.pos:
            self._path.pop(0)
        if not self._path:
            return None                       # unreachable, or already there
        return MoveTo(self._path[0])
