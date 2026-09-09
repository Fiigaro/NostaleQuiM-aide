from nqa.behaviors.navigation import NavigationBehavior
from nqa.behaviors.targeting import TargetSelector
from nqa.core.actions import MoveTo
from nqa.core.models import MapGrid, Vec2

from .conftest import monster


def make():
    selector = TargetSelector()
    return NavigationBehavior(selector), selector


def test_idle_with_nothing_to_chase_and_no_waypoints(ctx):
    ctx.profile.navigation.waypoints = []
    behavior, _ = make()
    assert behavior.decide(ctx) is None


def test_steps_toward_a_monster_that_is_out_of_reach(ctx):
    behavior, _ = make()
    ctx.state.entities = {1: monster(1, 18, 10)}       # inside engage, out of range
    action = behavior.decide(ctx)
    assert isinstance(action, MoveTo)
    assert action.pos.x == 11                           # one step east


def test_returns_one_step_not_the_whole_path(ctx):
    behavior, _ = make()
    ctx.state.entities = {1: monster(1, 18, 10)}
    action = behavior.decide(ctx)
    assert ctx.state.character.pos.chebyshev(action.pos) == 1


def test_stops_once_inside_approach_range(ctx):
    """Approach range is the *smallest* skill range, so melee stays usable."""
    behavior, _ = make()
    ctx.state.entities = {1: monster(1, 11, 10)}       # already adjacent
    assert behavior.decide(ctx) is None


def test_does_not_stop_at_the_longest_skill_range(ctx):
    # The range-3 skill could reach from here, but stopping now would deadlock
    # the moment it goes on cooldown, so we keep closing.
    behavior, _ = make()
    ctx.state.entities = {1: monster(1, 13, 10)}
    assert isinstance(behavior.decide(ctx), MoveTo)


def test_patrols_toward_the_first_waypoint(ctx):
    behavior, _ = make()
    action = behavior.decide(ctx)                       # (10,10) -> (5,5)
    assert action.pos == Vec2(9, 9)


def test_reaching_a_waypoint_advances_to_the_next(ctx):
    behavior, _ = make()
    ctx.state.character.pos = Vec2(5, 5)                # arrived at waypoint 0
    action = behavior.decide(ctx)
    assert action.pos.x == 6                            # heading to (15,5)


def test_waypoints_wrap_around(ctx):
    behavior, _ = make()
    behavior._waypoint_index = len(ctx.profile.navigation.waypoints) - 1
    ctx.state.character.pos = Vec2(*ctx.profile.navigation.waypoints[-1])
    assert behavior.decide(ctx) is not None
    assert behavior._waypoint_index == 0


def test_a_monster_preempts_the_patrol(ctx):
    behavior, _ = make()
    behavior.decide(ctx)                                # walking to (5,5), i.e. west
    ctx.state.entities = {1: monster(1, 18, 10)}        # spawn to the east
    assert behavior.decide(ctx).pos.x == 11             # turns around


def test_move_rate_limit_is_respected(ctx, clock):
    behavior, _ = make()
    ctx.cooldowns.mark_used("move", 0.5, trigger_global=False)
    assert behavior.decide(ctx) is None
    clock.advance(0.5)
    assert behavior.decide(ctx) is not None


def test_an_unreachable_waypoint_yields_no_action(ctx):
    behavior, _ = make()
    ctx.state.grid = MapGrid(30, 30, {(x, 8) for x in range(30)})   # sealed off
    ctx.profile.navigation.waypoints = [(10, 2)]
    assert behavior.decide(ctx) is None


def test_a_rejected_move_forces_a_replan(ctx):
    """If the server refuses a step, the cached path no longer starts where we
    stand and must be rebuilt rather than blindly continued."""
    behavior, _ = make()
    ctx.state.entities = {1: monster(1, 18, 10)}
    behavior.decide(ctx)
    assert behavior._path                                # a path is cached

    # Simulate being displaced far from the cached path head.
    ctx.state.character.pos = Vec2(2, 25)
    action = behavior.decide(ctx)
    assert isinstance(action, MoveTo)
    assert ctx.state.character.pos.chebyshev(action.pos) == 1


def test_path_is_reused_while_the_target_barely_moves(ctx):
    behavior, _ = make()
    ctx.state.entities = {1: monster(1, 20, 10)}
    behavior.decide(ctx)
    first_goal = behavior._path_goal

    ctx.state.entities[1].pos = Vec2(21, 10)            # drift of 1, tolerance 2
    ctx.state.character.pos = behavior._path[0]
    behavior.decide(ctx)
    assert behavior._path_goal == first_goal            # not replanned
