from nqa.behaviors.buffs import BuffBehavior
from nqa.config.profile import BuffConfig
from nqa.core.actions import UseItem, UseSkill
from nqa.core.models import Buff

BEHAVIOR = BuffBehavior()


def test_missing_buff_is_applied(ctx):
    action = BEHAVIOR.decide(ctx)
    assert action == UseSkill(skill_id=250, target_id=None, cooldown=5.0,
                              cooldown_key="buff:skill:250")


def test_a_fresh_buff_is_left_alone(ctx, clock):
    ctx.state.buffs[250] = Buff(250, "atk", clock.now() + 120.0)
    assert BEHAVIOR.decide(ctx) is None


def test_buff_is_refreshed_inside_the_margin(ctx, clock):
    ctx.state.buffs[250] = Buff(250, "atk", clock.now() + 19.0)   # margin is 20
    assert BEHAVIOR.decide(ctx).skill_id == 250


def test_expired_buff_is_reapplied(ctx, clock):
    ctx.state.buffs[250] = Buff(250, "atk", clock.now() + 5.0)
    clock.advance(10.0)
    assert BEHAVIOR.decide(ctx) is not None
    assert 250 not in ctx.state.buffs                 # expired locally


def test_upkeep_uses_its_own_cooldown_key(ctx, clock):
    """A skill used both to buff and to attack must not share one timer."""
    ctx.cooldowns.mark_used("skill:250", 60.0)        # 'attacked' with it
    assert BEHAVIOR.decide(ctx) is not None           # upkeep still allowed

    ctx.cooldowns.mark_used("buff:skill:250", 5.0)
    assert BEHAVIOR.decide(ctx) is None
    clock.advance(5.0)
    assert BEHAVIOR.decide(ctx) is not None


def test_item_buff_requires_stock(ctx):
    ctx.profile.buffs = [BuffConfig(kind="item", id=1904, buff_id=1904, name="exp",
                                    duration=300.0, refresh_margin=30.0, cooldown=5.0)]
    assert BEHAVIOR.decide(ctx) is None               # none in the bag

    ctx.state.inventory[1904] = 5
    assert BEHAVIOR.decide(ctx) == UseItem(vnum=1904, cooldown=5.0,
                                           cooldown_key="buff:item:1904")


def test_a_blocked_buff_does_not_starve_the_next_one(ctx):
    ctx.profile.buffs.append(
        BuffConfig(kind="skill", id=251, buff_id=251, name="second",
                   duration=60.0, refresh_margin=10.0, cooldown=5.0)
    )
    ctx.cooldowns.mark_used("buff:skill:250", 30.0)
    assert BEHAVIOR.decide(ctx).skill_id == 251
