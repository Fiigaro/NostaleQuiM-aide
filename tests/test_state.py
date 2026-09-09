import pytest

from nqa.core import events as ev
from nqa.core.clock import ManualClock
from nqa.core.events import EventBus
from nqa.core.models import Buff, Character, MapGrid, Vec2
from nqa.core.state import WorldState, WorldTracker

from .conftest import monster


@pytest.fixture
def wired():
    bus, clock = EventBus(), ManualClock()
    state = WorldState()
    WorldTracker(state, bus, clock)
    return state, bus, clock


async def test_tracker_applies_spawn_move_and_despawn(wired):
    state, bus, _ = wired
    await bus.publish(ev.EntitySpawned(monster(7, 3, 3)))
    assert state.entity(7).pos == Vec2(3, 3)

    await bus.publish(ev.EntityMoved(7, Vec2(4, 4)))
    assert state.entity(7).pos == Vec2(4, 4)

    await bus.publish(ev.EntityDespawned(7))
    assert state.entity(7) is None


async def test_events_for_unknown_entities_are_ignored(wired):
    state, bus, _ = wired
    await bus.publish(ev.EntityMoved(999, Vec2(1, 1)))
    await bus.publish(ev.EntityHpChanged(999, 50.0))
    assert state.entities == {}


async def test_map_change_invalidates_every_tracked_entity(wired):
    state, bus, _ = wired
    await bus.publish(ev.EntitySpawned(monster(1, 2, 2)))
    await bus.publish(ev.MapChanged(map_id=5, grid=MapGrid(10, 10)))
    assert state.entities == {}
    assert state.character.map_id == 5


async def test_disconnect_clears_volatile_knowledge(wired):
    state, bus, clock = wired
    await bus.publish(ev.Connected())
    await bus.publish(ev.EntitySpawned(monster(1, 2, 2)))
    await bus.publish(ev.BuffGained(Buff(9, "x", clock.now() + 100)))

    await bus.publish(ev.Disconnected("test"))
    assert not state.connected
    assert state.entities == {} and state.buffs == {}


async def test_inventory_zero_removes_the_entry(wired):
    state, bus, _ = wired
    await bus.publish(ev.InventoryChanged(vnum=1242, count=3))
    assert state.item_count(1242) == 3
    await bus.publish(ev.InventoryChanged(vnum=1242, count=0))
    assert state.item_count(1242) == 0 and 1242 not in state.inventory


async def test_a_raising_handler_does_not_stop_the_others():
    bus = EventBus()
    seen = []
    bus.subscribe(ev.Connected, lambda e: (_ for _ in ()).throw(RuntimeError("boom")))
    bus.subscribe(ev.Connected, lambda e: seen.append(e))
    await bus.publish(ev.Connected())
    assert len(seen) == 1


def test_monsters_within_filters_by_radius_and_sorts_by_distance():
    state = WorldState()
    state.character = Character(pos=Vec2(0, 0))
    state.entities = {
        1: monster(1, 5, 0),
        2: monster(2, 2, 0),
        3: monster(3, 20, 0),          # out of radius
    }
    found = state.monsters_within(10)
    assert [e.id for e in found] == [2, 1]


def test_monsters_within_honours_the_vnum_whitelist():
    state = WorldState()
    state.character = Character(pos=Vec2(0, 0))
    state.entities = {1: monster(1, 1, 0, vnum=100), 2: monster(2, 2, 0, vnum=999)}
    assert [e.id for e in state.monsters_within(10, {999})] == [2]


def test_dead_monsters_are_excluded():
    state = WorldState()
    state.character = Character(pos=Vec2(0, 0))
    state.entities = {1: monster(1, 1, 0, hp_pct=0.0)}
    assert state.monsters() == []
    assert state.nearest_monster() is None


def test_buffs_expire_locally_when_the_server_never_says_so():
    state = WorldState()
    state.buffs = {1: Buff(1, "a", 10.0), 2: Buff(2, "b", 30.0)}
    assert state.expire_buffs(15.0) == [1]
    assert set(state.buffs) == {2}
    assert state.buff_remaining(2, 15.0) == 15.0
    assert state.buff_remaining(404, 15.0) == 0.0
