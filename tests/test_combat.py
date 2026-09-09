from nqa.behaviors.combat import CombatBehavior
from nqa.behaviors.targeting import TargetSelector
from nqa.core.actions import UseSkill
from nqa.core.models import Vec2

from .conftest import monster


def make():
    selector = TargetSelector()
    return CombatBehavior(selector), selector


def test_no_monsters_means_no_action(ctx):
    behavior, _ = make()
    assert behavior.decide(ctx) is None


def test_highest_priority_usable_skill_wins(ctx):
    behavior, _ = make()
    ctx.state.entities = {1: monster(1, 12, 10)}       # 2 tiles away
    assert behavior.decide(ctx) == UseSkill(
        skill_id=237, target_id=1, cooldown=6.0, cooldown_key="skill:237"
    )


def test_falls_back_when_the_good_skill_is_cooling(ctx):
    behavior, _ = make()
    ctx.state.entities = {1: monster(1, 11, 10)}       # 1 tile: both in range
    ctx.cooldowns.mark_used("skill:237", 6.0)
    assert behavior.decide(ctx).skill_id == 0


def test_falls_back_when_mana_is_short(ctx):
    behavior, _ = make()
    ctx.state.entities = {1: monster(1, 11, 10)}
    ctx.state.character.mp = 5                          # big skill costs 30
    assert behavior.decide(ctx).skill_id == 0


def test_out_of_range_defers_to_the_walker(ctx):
    behavior, _ = make()
    ctx.state.entities = {1: monster(1, 18, 10)}       # 8 tiles: no skill reaches
    assert behavior.decide(ctx) is None


def test_a_target_beyond_engage_radius_is_not_picked_up(ctx):
    behavior, selector = make()
    ctx.state.entities = {1: monster(1, 25, 10)}       # 15 > engage_radius 10
    assert behavior.decide(ctx) is None
    assert selector.target_id is None


def test_target_is_held_rather_than_swapped_to_whatever_is_nearest(ctx):
    behavior, selector = make()
    ctx.state.entities = {1: monster(1, 12, 10)}
    behavior.decide(ctx)
    assert selector.target_id == 1

    ctx.state.entities[2] = monster(2, 10, 11)         # closer newcomer
    assert behavior.decide(ctx).target_id == 1         # still on the first


def test_target_is_dropped_when_it_dies(ctx):
    behavior, selector = make()
    ctx.state.entities = {1: monster(1, 12, 10), 2: monster(2, 13, 10)}
    behavior.decide(ctx)
    assert selector.target_id == 1

    ctx.state.entities[1].hp_pct = 0.0
    assert behavior.decide(ctx).target_id == 2


def test_target_is_dropped_when_it_despawns(ctx):
    behavior, selector = make()
    ctx.state.entities = {1: monster(1, 12, 10)}
    behavior.decide(ctx)
    del ctx.state.entities[1]
    assert behavior.decide(ctx) is None
    assert selector.target_id is None


def test_a_target_that_flees_past_the_leash_is_abandoned(ctx):
    behavior, selector = make()
    ctx.state.entities = {1: monster(1, 12, 10)}
    behavior.decide(ctx)                                # anchor at (10,10)

    ctx.state.entities[1].pos = Vec2(10, 40)            # 30 > leash 20
    behavior.decide(ctx)
    assert selector.target_id is None


def test_the_vnum_whitelist_is_honoured(ctx):
    behavior, _ = make()
    ctx.profile.combat.target_vnums = {555}
    ctx.state.entities = {1: monster(1, 11, 10, vnum=100), 2: monster(2, 12, 10, vnum=555)}
    assert behavior.decide(ctx).target_id == 2


def test_global_cooldown_holds_the_rotation(ctx, clock):
    ctx.cooldowns.global_cooldown = 1.0
    behavior, _ = make()
    ctx.state.entities = {1: monster(1, 11, 10)}

    assert behavior.decide(ctx) is not None
    ctx.cooldowns.mark_used("skill:237", 6.0)
    assert behavior.decide(ctx) is None                 # every skill gated
    clock.advance(1.0)
    assert behavior.decide(ctx).skill_id == 0
