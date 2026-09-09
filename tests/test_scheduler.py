from nqa.core.clock import ManualClock
from nqa.core.scheduler import CooldownRegistry


def test_unknown_key_is_ready():
    r = CooldownRegistry(ManualClock())
    assert r.ready("never-used")


def test_key_blocks_until_its_cooldown_elapses():
    c = ManualClock()
    r = CooldownRegistry(c)
    r.mark_used("skill:1", 5.0)
    assert not r.is_ready("skill:1")
    c.advance(4.999)
    assert not r.is_ready("skill:1")
    c.advance(0.001)
    assert r.is_ready("skill:1")


def test_global_cooldown_gates_every_key():
    c = ManualClock()
    r = CooldownRegistry(c, global_cooldown=1.0)
    r.mark_used("skill:1", 5.0)
    assert not r.ready("skill:2")           # untouched key, still gated
    assert r.is_ready("skill:2")            # its own timer is clear
    c.advance(1.0)
    assert r.ready("skill:2")


def test_movement_can_opt_out_of_the_global_cooldown():
    c = ManualClock()
    r = CooldownRegistry(c, global_cooldown=1.0)
    r.mark_used("move", 0.3, trigger_global=False)
    assert r.global_ready                   # casting is still allowed
    assert not r.is_ready("move")


def test_remaining_never_goes_negative():
    c = ManualClock()
    r = CooldownRegistry(c)
    r.mark_used("k", 1.0)
    c.advance(10.0)
    assert r.remaining("k") == 0.0


def test_clear_drops_key_and_global_state():
    c = ManualClock()
    r = CooldownRegistry(c, global_cooldown=5.0)
    r.mark_used("skill:1", 60.0)
    r.clear()
    assert r.ready("skill:1")
    assert r.global_ready
