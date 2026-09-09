from nqa.core.models import MapGrid, Vec2
from nqa.core.pathfinding import path_into_range, path_to


def test_open_field_takes_the_diagonal():
    path = path_to(MapGrid(10, 10), Vec2(0, 0), Vec2(3, 3))
    assert path == [Vec2(1, 1), Vec2(2, 2), Vec2(3, 3)]


def test_path_excludes_the_start_tile():
    path = path_to(MapGrid(5, 5), Vec2(0, 0), Vec2(0, 2))
    assert Vec2(0, 0) not in path


def test_already_there_is_an_empty_path_not_none():
    assert path_to(MapGrid(5, 5), Vec2(2, 2), Vec2(2, 2)) == []


def test_detours_around_a_wall_with_one_gap():
    grid = MapGrid(10, 10, {(5, y) for y in range(9)})
    path = path_to(grid, Vec2(0, 0), Vec2(9, 0))
    assert path is not None
    assert Vec2(5, 9) in path                      # forced through the gap
    assert all(grid.walkable(p) for p in path)


def test_sealed_wall_is_unreachable():
    grid = MapGrid(10, 10, {(5, y) for y in range(10)})
    assert path_to(grid, Vec2(0, 0), Vec2(9, 0)) is None


def test_start_inside_geometry_is_unreachable():
    grid = MapGrid(5, 5, {(1, 1)})
    assert path_to(grid, Vec2(1, 1), Vec2(4, 4)) is None


def test_diagonal_cannot_cut_a_corner():
    # Both orthogonal neighbours blocked: the diagonal would clip the corner.
    grid = MapGrid(3, 3, {(1, 0), (0, 1)})
    assert path_to(grid, Vec2(0, 0), Vec2(1, 1)) is None


def test_one_blocked_side_forces_a_detour_rather_than_a_squeeze():
    # Strict corner rule: a diagonal needs BOTH orthogonal neighbours open.
    # The squeeze past (1,0) is refused, but the goal is still reachable by
    # stepping orthogonally around it.
    grid = MapGrid(3, 3, {(1, 0)})
    assert path_to(grid, Vec2(0, 0), Vec2(1, 1)) == [Vec2(0, 1), Vec2(1, 1)]


def test_into_range_stops_at_the_edge_of_range():
    path = path_into_range(MapGrid(20, 20), Vec2(0, 0), Vec2(10, 0), 3)
    assert path[-1].chebyshev(Vec2(10, 0)) == 3
    assert len(path) == 7                          # 10 - 3, no wasted steps


def test_into_range_is_a_noop_when_already_close_enough():
    assert path_into_range(MapGrid(20, 20), Vec2(9, 0), Vec2(10, 0), 3) == []


def test_into_range_works_when_the_target_tile_is_blocked():
    # A monster's own tile is not walkable; 'walk onto it' would find nothing.
    grid = MapGrid(20, 20, {(10, 0)})
    assert path_to(grid, Vec2(0, 0), Vec2(10, 0)) is None
    assert path_into_range(grid, Vec2(0, 0), Vec2(10, 0), 1) is not None


def test_node_budget_bounds_a_hopeless_search():
    grid = MapGrid(200, 200, {(100, y) for y in range(200)})
    assert path_to(grid, Vec2(0, 0), Vec2(199, 0), max_nodes=500) is None
