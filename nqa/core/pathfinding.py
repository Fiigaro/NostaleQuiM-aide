"""A* over the tile grid, 8-way movement.

Two entry points, because the bot has two different questions:
  * ``path_to`` -- "walk onto this exact tile" (waypoints, gathering).
  * ``path_into_range`` -- "get close enough to hit this" (combat). Stopping at
    the edge of skill range instead of walking onto the target saves a lot of
    pointless steps, and it is the only correct goal when the target tile is
    itself occupied and therefore unwalkable.
"""

from __future__ import annotations

import heapq
import math
from typing import Callable, Iterator

from .models import MapGrid, Vec2

DIAGONAL_COST = math.sqrt(2.0)
_STEPS: tuple[tuple[int, int], ...] = (
    (0, 1), (1, 0), (0, -1), (-1, 0),
    (1, 1), (1, -1), (-1, 1), (-1, -1),
)


def _neighbours(grid: MapGrid, node: Vec2) -> Iterator[tuple[Vec2, float]]:
    for dx, dy in _STEPS:
        nxt = Vec2(node.x + dx, node.y + dy)
        if not grid.walkable(nxt):
            continue
        if dx and dy:
            # No cutting corners: both orthogonal neighbours must be open, or
            # the path clips the corner of a wall and the server rejects it.
            if not grid.walkable(Vec2(node.x + dx, node.y)):
                continue
            if not grid.walkable(Vec2(node.x, node.y + dy)):
                continue
            yield nxt, DIAGONAL_COST
        else:
            yield nxt, 1.0


def _octile(a: Vec2, b: Vec2) -> float:
    dx, dy = abs(a.x - b.x), abs(a.y - b.y)
    return (dx + dy) + (DIAGONAL_COST - 2.0) * min(dx, dy)


def astar(
    grid: MapGrid,
    start: Vec2,
    is_goal: Callable[[Vec2], bool],
    heuristic: Callable[[Vec2], float],
    *,
    max_nodes: int = 20_000,
) -> list[Vec2] | None:
    """Generic A*. Returns the path excluding ``start``, or None.

    ``max_nodes`` bounds the search so an unreachable goal on a large map costs
    a bounded amount of time instead of stalling the tick loop.
    """
    if is_goal(start):
        return []
    if not grid.walkable(start):
        return None

    counter = 0
    open_heap: list[tuple[float, int, Vec2]] = [(heuristic(start), 0, start)]
    came_from: dict[Vec2, Vec2] = {}
    g_score: dict[Vec2, float] = {start: 0.0}
    closed: set[Vec2] = set()

    while open_heap:
        _, _, current = heapq.heappop(open_heap)
        if current in closed:
            continue
        closed.add(current)

        if is_goal(current):
            path = [current]
            while current in came_from:
                current = came_from[current]
                path.append(current)
            path.reverse()
            return path[1:]

        if len(closed) > max_nodes:
            return None

        for nxt, step_cost in _neighbours(grid, current):
            if nxt in closed:
                continue
            tentative = g_score[current] + step_cost
            if tentative < g_score.get(nxt, math.inf):
                g_score[nxt] = tentative
                came_from[nxt] = current
                counter += 1
                heapq.heappush(open_heap, (tentative + heuristic(nxt), counter, nxt))

    return None


def path_to(grid: MapGrid, start: Vec2, goal: Vec2, **kw) -> list[Vec2] | None:
    """Path onto ``goal`` itself."""
    return astar(grid, start, lambda n: n == goal, lambda n: _octile(n, goal), **kw)


def path_into_range(
    grid: MapGrid, start: Vec2, target: Vec2, rng: int, **kw
) -> list[Vec2] | None:
    """Path to any tile within ``rng`` (Chebyshev) of ``target``.

    The heuristic is ``chebyshev - rng`` clamped at zero: every step cuts the
    Chebyshev distance by at most one and costs at least one, so it never
    overestimates and A* stays optimal.
    """
    return astar(
        grid,
        start,
        lambda n: n.chebyshev(target) <= rng,
        lambda n: float(max(0, n.chebyshev(target) - rng)),
        **kw,
    )
