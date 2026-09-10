# NosSmoothCustomClient

A .NET 8 packet-driven automation assembly built on the NosSmooth framework: typed packet
interception, a singleton state engine that survives NosSmooth's per-packet responder scopes, and a
priority-based orchestration loop on a 300 ms clock.

```bash
dotnet build
dotnet run              # simulated transport, runs on any OS
dotnet run -- --verbose # adds per-tick debug output
dotnet run -- --attach  # binds to a running NosTale process (Windows x86 only)
```

Press **ESC** or **Q** to stop (Ctrl+C also works, and is the only option when stdin is redirected).

## Corrections to the original brief

Four items in the specification do not exist as written. Each was resolved against the actual
published packages and the decompiled assemblies rather than guessed at.

| Specified | Reality | Used instead |
|---|---|---|
| `NosSmooth.Local` 5.0.0 | No such package ID on NuGet | **`NosSmooth.LocalClient` 2.2.0** — depends on `NosSmooth.Core` 5.0.0 exactly, so the pin is consistent |
| `NosSmooth.PacketSerializersGenerator` 2.2.7 | That package tops out at 1.1.1; **2.2.7 is `NosSmooth.PacketSerializer`** | Both, at their real versions: `NosSmooth.PacketSerializer` 2.2.7 + `NosSmooth.PacketSerializersGenerator` 1.1.1 |
| `services.AddManagedNosSmoothCore()` | Method does not exist | **`services.AddManagedNostaleCore()`** |
| `services.AddNosSmoothPackets()` | Method does not exist | **`services.AddPacketSerialization()`** |
| `services.AddPacketTypes(assembly)` | Not an `IServiceCollection` extension — it extends `IPacketTypesRepository` | Resolved from the container after build, in `PacketTypeRegistrar` |

Two protocol-level corrections:

- **Movement is `walk`, not `mv`.** `mv` is a *server→client* packet (`MovePacket`: an entity moved).
  The client sends `walk <x> <y> <checksum> <speed>`. The brief's `"mv <X> <Y> <Speed>"` would never
  have been accepted outbound. `mv` is instead consumed inbound to track position.
- **Targeted attack is `u_s`, not `u_as`.** `u_as` is the *area* skill packet and carries no target
  id, so it cannot express "attack this entity". `UseSkillPacket` (`u_s`) does.

### The walk checksum

NosSmooth models `WalkPacket.CheckSum` but never computes it — its local client walks by calling the
game's own movement routine instead of emitting packets. The value is client-build specific.
`WalkChecksumCalculator` holds the common private-server formula in one place and **must be
validated against the target server build** before the packet path is trusted. On `--attach` this is
moot: `CommandWalkStrategy` issues a `WalkCommand` and the game builds a valid frame itself.

## Architecture

```
inbound frame ─► ManagedPacketHandler ─► IPacketResponder<T>  ─┐
                 (deserialises)          (per-packet scope)    │ writes
                                                               ▼
                                                    ProtocolStateManager  (singleton)
                                                               │ reads
        outbound frame ◄─ PacketDispatcher ◄─ OrchestrationBackgroundService (300 ms)
                          (serialises)         P1 survival ▸ P2 engagement ▸ P3 navigation
```

`ProtocolStateManager` is the only cross-packet state. Responders are resolved per packet, so a
responder instance never sees the packet before it — anything that must persist lives there. Reads
and writes are safe from the packet threads and the orchestration loop at once: `Interlocked` for
independent scalars, a short lock for pairs that must stay coherent (X/Y, target id/HP), a
`ConcurrentDictionary` for the entity table, and a `SemaphoreSlim` that serialises whole decision
cycles.

Responders are edge-triggered (act on the frame that just arrived); the loop is level-triggered
(keeps acting while a condition holds). Both go through the same cooldown gates on the state
manager, so a potion or attack frame is never dispatched twice for one event.

| File | Role |
|---|---|
| `State/ProtocolStateManager.cs` | Vitals, position, lock-on target, entity table, waypoint ring, cooldown gates, cycle lock |
| `Packets/QuiMStatPacket.cs` | Custom `quim` broadcast — source-generated converter |
| `Packets/QuiMTargetPacket.cs` | Custom `quimtg` lock-on model, with an optional trailing index |
| `Packets/PacketTypeRegistrar.cs` | Stock + custom type registration, with explicit per-type fallback |
| `Responders/PlayerStatsResponder.cs` | `stat` → vitals; injects a consumable at the threshold |
| `Responders/EntitySpawnResponder.cs` | `in`/`out`/`mapclear` → entity table; locks the nearest monster |
| `Responders/TargetHpResponder.cs` | `su`/`st`/`die`/`quimtg` → target vitals; clears the lock on death |
| `Responders/PositionTrackingResponder.cs` | `at`/`mv`/`tp` → own and entity coordinates |
| `Orchestration/OrchestrationBackgroundService.cs` | The decision matrix |
| `Client/SimulatedNostaleClient.cs` | In-process stand-in that speaks the real wire format |

## Deliberate behavioural deviation

Step 5 says movement halts outright once a target is locked. Taken literally that strands the loop
on any monster that spawns outside skill range: it never closes the gap and never lands a hit. The
loop therefore approaches a target beyond `BotOptions.AttackRange`, then holds position once inside
it. Set `ApproachTargetOutOfRange = false` for the literal reading.

Ground loot is never touched, per the brief — server-side auto-loot is assumed active.

## Simulated mode

`--attach` needs Windows x86 and a live NosTale process; `LocalClient`/`LocalBinding` sigscan and
hook the client through `Reloaded.Hooks`. So that the pipeline stays testable everywhere, the
default transport is `SimulatedNostaleClient`, which builds every inbound frame by serialising a
real packet record through the same `IPacketSerializer` the production path uses, then feeds it back
as a string. A run is a genuine round-trip test of the generated converters, the type repository,
the responder fan-out and the orchestration loop — it is not a mock of them.

It scripts spawn → engage → kill → patrol → respawn, and drains HP during combat so the survival
priority fires on its own.

To inspect what the source generator emitted, add to the csproj and rebuild:

```xml
<EmitCompilerGeneratedFiles>true</EmitCompilerGeneratedFiles>
<CompilerGeneratedFilesOutputPath>generated</CompilerGeneratedFilesOutputPath>
```

## Note

Automating a commercial game client violates NosTale's terms of service and risks an account ban.
