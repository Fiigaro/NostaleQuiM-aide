from nqa.behaviors.survival import SurvivalBehavior
from nqa.core.actions import MoveTo, UseItem
from nqa.core.models import MapGrid, Vec2

from .conftest import HP_POTION, MP_POTION, monster

BEHAVIOR = SurvivalBehavior()


def test_healthy_character_wants_nothing(ctx):
    assert BEHAVIOR.decide(ctx) is None


def test_drinks_hp_potion_at_the_threshold(ctx):
    ctx.state.character.hp = 500                       # exactly 50%
    action = BEHAVIOR.decide(ctx)
    assert action == UseItem(vnum=HP_POTION, cooldown=3.0, cooldown_key=f"item:{HP_POTION}")


def test_hp_is_handled_before_mp(ctx):
    ctx.state.character.hp = 100                       # both below threshold
    ctx.state.character.mp = 10
    assert BEHAVIOR.decide(ctx).vnum == HP_POTION


def test_drinks_mp_potion_when_only_mana_is_low(ctx):
    ctx.state.character.mp = 100                       # 20%
    assert BEHAVIOR.decide(ctx).vnum == MP_POTION


def test_an_empty_stack_is_not_drunk(ctx):
    ctx.state.character.hp = 100
    ctx.state.inventory[HP_POTION] = 0
    assert BEHAVIOR.decide(ctx) is None or BEHAVIOR.decide(ctx).vnum != HP_POTION


def test_potion_cooldown_is_respected(ctx, clock):
    ctx.state.character.hp = 100
    ctx.state.character.mp = 50                        # 10%, also below threshold
    ctx.cooldowns.mark_used(f"item:{HP_POTION}", 3.0)
    assert BEHAVIOR.decide(ctx).vnum == MP_POTION      # falls through to mana
    clock.advance(3.0)
    assert BEHAVIOR.decide(ctx).vnum == HP_POTION


def test_critical_and_out_of_potions_retreats_from_the_threat(ctx):
    ctx.state.character.hp = 100                       # 10%, below critical 20
    ctx.state.inventory.clear()
    ctx.state.entities = {1: monster(1, 12, 10)}       # threat to our east
    action = BEHAVIOR.decide(ctx)
    assert isinstance(action, MoveTo)
    assert action.pos.x < ctx.state.character.pos.x    # moving away


def test_retreat_avoids_walls(ctx):
    ctx.state.character.hp = 100
    ctx.state.inventory.clear()
    ctx.state.character.pos = Vec2(10, 10)
    ctx.state.entities = {1: monster(1, 12, 10)}
    # Seal the straight-back tiles; only the diagonal escape remains.
    ctx.state.grid = MapGrid(30, 30, {(9, 10), (9, 9)})
    action = BEHAVIOR.decide(ctx)
    assert action is None or ctx.state.grid.walkable(action.pos)


def test_critical_with_potions_available_drinks_instead_of_fleeing(ctx):
    ctx.state.character.hp = 100
    ctx.state.entities = {1: monster(1, 11, 10)}
    assert isinstance(BEHAVIOR.decide(ctx), UseItem)


def test_a_dead_character_requests_nothing(ctx):
    ctx.state.character.hp = 0
    assert BEHAVIOR.decide(ctx) is None
