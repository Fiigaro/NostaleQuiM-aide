import pytest

from nqa.adapters.base import GameAdapter
from nqa.behaviors.base import Behavior
from nqa.brain.arbiter import Arbiter
from nqa.brain.executor import Executor
from nqa.config.profile import RuntimeConfig
from nqa.core.actions import Idle, MoveTo, UseItem, UseSkill
from nqa.core.clock import ManualClock
from nqa.core.events import EventBus
from nqa.core.models import Vec2
from nqa.core.scheduler import CooldownRegistry


class Fixed(Behavior):
    def __init__(self, name, priority, action):
        self.name, self.priority, self._action = name, priority, action
        self.reset_calls = 0

    def decide(self, ctx):
        return self._action

    def reset(self):
        self.reset_calls += 1


class RecordingAdapter(GameAdapter):
    def __init__(self):
        super().__init__(EventBus())
        self.calls = []

    async def connect(self): ...
    async def disconnect(self): ...
    async def use_skill(self, skill_id, target_id=None):
        self.calls.append(("use_skill", skill_id, target_id))
    async def use_item(self, vnum):
        self.calls.append(("use_item", vnum))
    async def attack(self, target_id):
        self.calls.append(("attack", target_id))
    async def move_to(self, pos):
        self.calls.append(("move_to", pos))


# --- arbiter ----------------------------------------------------------------

def test_highest_priority_behavior_wins(ctx):
    arbiter = Arbiter([
        Fixed("low", 1, Idle("low")),
        Fixed("high", 100, Idle("high")),
    ])
    assert arbiter.decide(ctx).behavior == "high"


def test_a_passing_behavior_defers_to_the_next(ctx):
    arbiter = Arbiter([Fixed("high", 100, None), Fixed("low", 1, Idle("low"))])
    assert arbiter.decide(ctx).behavior == "low"


def test_all_passing_means_no_decision(ctx):
    assert Arbiter([Fixed("a", 1, None)]).decide(ctx) is None


def test_survival_preempts_combat_without_an_explicit_transition(ctx):
    """The point of subsumption: no combat->heal->combat edge is written
    anywhere, yet a mid-fight heal takes over."""
    from nqa.behaviors.combat import CombatBehavior
    from nqa.behaviors.survival import SurvivalBehavior
    from nqa.behaviors.targeting import TargetSelector
    from .conftest import monster

    arbiter = Arbiter([SurvivalBehavior(), CombatBehavior(TargetSelector())])
    ctx.state.entities = {1: monster(1, 11, 10)}

    assert arbiter.decide(ctx).behavior == "combat"
    ctx.state.character.hp = 100                     # takes a hit
    assert arbiter.decide(ctx).behavior == "survival"


def test_reset_propagates_to_every_behavior(ctx):
    behaviors = [Fixed("a", 1, None), Fixed("b", 2, None)]
    Arbiter(behaviors).reset()
    assert all(b.reset_calls == 1 for b in behaviors)


# --- executor ---------------------------------------------------------------

@pytest.fixture
def executor_setup():
    clock = ManualClock()
    cooldowns = CooldownRegistry(clock, global_cooldown=1.0)
    adapter = RecordingAdapter()
    return Executor(adapter, cooldowns, RuntimeConfig(move_interval=0.3)), adapter, cooldowns, clock


async def test_skill_is_dispatched_and_its_timer_armed(executor_setup):
    executor, adapter, cooldowns, _ = executor_setup
    await executor.execute(UseSkill(skill_id=7, target_id=3, cooldown=5.0))
    assert adapter.calls == [("use_skill", 7, 3)]
    assert not cooldowns.is_ready("skill:7")


async def test_an_explicit_cooldown_key_overrides_the_default(executor_setup):
    executor, _, cooldowns, _ = executor_setup
    await executor.execute(
        UseSkill(skill_id=7, cooldown=5.0, cooldown_key="buff:skill:7")
    )
    assert not cooldowns.is_ready("buff:skill:7")
    assert cooldowns.is_ready("skill:7")             # the attack timer is untouched


async def test_item_arms_its_own_key(executor_setup):
    executor, adapter, cooldowns, _ = executor_setup
    await executor.execute(UseItem(vnum=1242, cooldown=3.0))
    assert adapter.calls == [("use_item", 1242)]
    assert not cooldowns.is_ready("item:1242")


async def test_moving_does_not_consume_the_global_cooldown(executor_setup):
    executor, adapter, cooldowns, _ = executor_setup
    await executor.execute(MoveTo(Vec2(1, 1)))
    assert adapter.calls == [("move_to", Vec2(1, 1))]
    assert cooldowns.global_ready                    # casting still allowed
    assert not cooldowns.is_ready("move")


async def test_casting_does_consume_the_global_cooldown(executor_setup):
    executor, _, cooldowns, _ = executor_setup
    await executor.execute(UseSkill(skill_id=7, target_id=1, cooldown=5.0))
    assert not cooldowns.global_ready


async def test_idle_touches_nothing(executor_setup):
    executor, adapter, cooldowns, _ = executor_setup
    await executor.execute(Idle("nothing to do"))
    assert adapter.calls == [] and cooldowns.global_ready
