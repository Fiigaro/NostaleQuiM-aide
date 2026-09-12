using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NosSmooth.Core.Packets;
using NosSmooth.Packets.Enums.Battle;
using NosSmooth.Packets.Enums.Entities;
using NosSmooth.Packets.Server.Battle;
using NosSmooth.PacketSerializer.Abstractions.Attributes;
using NosSmoothCustomClient.Responders;
using NosSmoothCustomClient.Configuration;
using NosSmoothCustomClient.Input;
using NosSmoothCustomClient.Orchestration;
using NosSmoothCustomClient.State;

namespace NosSmoothCustomClient.Diagnostics;

/// <summary>
/// Drives the decision matrix by hand and asserts what it decided.
/// </summary>
/// <remarks>
/// The simulator proves the packet path end to end, but the keyboard path is the one that actually
/// runs against the real server, and its two hardest rules are about things that did NOT happen: a
/// skill key pressed into a refused cast must not start a cooldown, and a skill must never be
/// pressed with nothing selected. Those are invisible in a log of what the bot did, so they are
/// checked here instead, one tick at a time with no clock involved.
/// </remarks>
public static class CombatSelfCheck
{
    /// <summary>
    /// Runs the checks.
    /// </summary>
    /// <returns>0 when every check passed, 3 otherwise.</returns>
    public static async Task<int> RunAsync()
    {
        var results = new List<(string Name, bool Passed)>
        {
            await NoTargetPressesOnlyTheAttackKeyAsync().ConfigureAwait(false),
            await RefusedCastDoesNotStartTheCooldownAsync().ConfigureAwait(false),
            await ConfirmedCastStartsTheCooldownAsync().ConfigureAwait(false),
            await QuietTargetIsDroppedAsync().ConfigureAwait(false),
            await PacketPathStillNamesItsTargetAsync().ConfigureAwait(false),
            await QuickBarIsLearnedWithoutSkiAsync().ConfigureAwait(false),
            await ChangingMapClearsStaleStateAsync().ConfigureAwait(false),
            await RouteIsNotWalkedOnTheWrongMapAsync().ConfigureAwait(false)
        };

        var failed = 0;
        foreach (var (name, passed) in results)
        {
            Console.WriteLine($"  [{(passed ? "OK  " : "ECHEC")}] {name}");
            if (!passed)
            {
                failed++;
            }
        }

        Console.WriteLine();
        Console.WriteLine($"{results.Count - failed}/{results.Count} verifications de combat passees.");
        return failed == 0 ? 0 : 3;
    }

    private static async Task<(string, bool)> NoTargetPressesOnlyTheAttackKeyAsync()
    {
        var (loop, _, _, actuator, _) = Build(selectsTargetItself: true);

        // Nothing selected, and an entity table that does know about a monster: the keyboard path
        // must still ask the game rather than trusting the table, and must not spend a skill on a
        // target the client has not confirmed.
        await loop.TickAsync(CancellationToken.None).ConfigureAwait(false);

        return ("sans cible, seule la touche d'attaque est pressee",
            actuator.Calls.Contains("target") && !actuator.Calls.Any(c => c.StartsWith("skill:", StringComparison.Ordinal)));
    }

    private static async Task<(string, bool)> RefusedCastDoesNotStartTheCooldownAsync()
    {
        var (loop, state, rotation, actuator, options) = Build(selectsTargetItself: true);
        options.SkillConfirmationWindow = TimeSpan.FromMilliseconds(150);

        Engage(state);
        await loop.TickAsync(CancellationToken.None).ConfigureAwait(false);

        var pressed = actuator.Calls.Any(c => c.StartsWith("skill:", StringComparison.Ordinal));
        var reservedNow = rotation.Snapshot(long.MaxValue)[0].Remaining > TimeSpan.Zero;

        // The server says nothing, which is what a refused cast looks like from here.
        await Task.Delay(250).ConfigureAwait(false);
        var readyAgain = rotation.Snapshot(long.MaxValue)[0].Remaining == TimeSpan.Zero;

        return ("un sort refuse redevient disponible", pressed && reservedNow && readyAgain);
    }

    private static async Task<(string, bool)> ConfirmedCastStartsTheCooldownAsync()
    {
        var (loop, state, rotation, _, options) = Build(selectsTargetItself: true);
        options.SkillConfirmationWindow = TimeSpan.FromMilliseconds(150);

        Engage(state);
        await loop.TickAsync(CancellationToken.None).ConfigureAwait(false);

        // The server confirms the cast, the way su does.
        rotation.ConfirmCast(options.Skills[0].CastId);

        await Task.Delay(250).ConfigureAwait(false);
        var remaining = rotation.Snapshot(long.MaxValue)[0].Remaining;

        return ("un sort confirme demarre son vrai cooldown", remaining > TimeSpan.FromSeconds(1));
    }

    private static async Task<(string, bool)> QuietTargetIsDroppedAsync()
    {
        var (loop, state, _, _, options) = Build(selectsTargetItself: true);
        options.TargetStaleAfter = TimeSpan.FromMilliseconds(120);

        Engage(state);
        await Task.Delay(200).ConfigureAwait(false);
        await loop.TickAsync(CancellationToken.None).ConfigureAwait(false);

        return ("une cible muette est abandonnee", state.Target is null);
    }

    private static async Task<(string, bool)> PacketPathStillNamesItsTargetAsync()
    {
        var (loop, state, _, actuator, _) = Build(selectsTargetItself: false);

        // The packet path has no key to ask with, so it must go on finding its target in the table.
        state.TrackEntity(new TrackedEntity(77, EntityType.Monster, 51, 50, 100));
        await loop.TickAsync(CancellationToken.None).ConfigureAwait(false);

        return ("le mode paquets cible toujours par lui-meme", state.Target?.EntityId == 77 && actuator.Calls.Count > 0);
    }

    private static async Task<(string, bool)> QuickBarIsLearnedWithoutSkiAsync()
    {
        var (loop, state, rotation, _, options) = Build(selectsTargetItself: true);
        var bar = new SkillBarMap();
        var responder = new SkillResponder(rotation, bar, state, NullLogger<SkillResponder>.Instance);

        Engage(state);
        await loop.TickAsync(CancellationToken.None).ConfigureAwait(false);

        // No ski has been seen, so the bar is empty and the VNum means nothing yet. The server
        // reporting our own cast, with a cooldown of its own, is what identifies the slot.
        await responder.Respond(Su(vnum: 4242, cooldown: 120, casterId: state.OwnCharacterId)).ConfigureAwait(false);

        var learned = bar.TryGetCastId(4242, out var castId) && castId == options.Skills[0].CastId;
        var onCooldown = rotation.Snapshot(long.MaxValue)[0].Remaining > TimeSpan.FromSeconds(1);

        // And a plain swing must not be mistaken for a cast: no cooldown of its own, nothing learned.
        var (loop2, state2, rotation2, _, _) = Build(selectsTargetItself: true);
        var bar2 = new SkillBarMap();
        var responder2 = new SkillResponder(rotation2, bar2, state2, NullLogger<SkillResponder>.Instance);

        Engage(state2);
        await loop2.TickAsync(CancellationToken.None).ConfigureAwait(false);
        await responder2.Respond(Su(vnum: 7, cooldown: 0, casterId: state2.OwnCharacterId)).ConfigureAwait(false);

        var swingIgnored = !bar2.TryGetCastId(7, out _);

        return ("la barre de sorts s'apprend sans ski", learned && onCooldown && swingIgnored);
    }

    private static Task<(string, bool)> ChangingMapClearsStaleStateAsync()
    {
        var (_, state, _, _, _) = Build(selectsTargetItself: true);

        state.EnterMap(1);
        Engage(state);

        var hadState = state.Target is not null && state.KnownEntities.Count > 0;
        var changed = state.EnterMap(2);

        return Task.FromResult(("changer de carte purge cible et entites",
            hadState && changed && state.Target is null && state.KnownEntities.Count == 0 && state.CurrentMapId == 2));
    }

    private static async Task<(string, bool)> RouteIsNotWalkedOnTheWrongMapAsync()
    {
        var (loop, state, _, actuator, options) = Build(selectsTargetItself: true);
        options.Waypoints = new List<Waypoint> { new(80, 80, 400, 300) };
        options.RouteMapId = 9;
        options.SearchInterval = TimeSpan.FromHours(1); // keep the probe out of the way

        state.EnterMap(3);
        await loop.TickAsync(CancellationToken.None).ConfigureAwait(false);
        var heldElsewhere = !actuator.Calls.Any(c => c.StartsWith("waypoint:", StringComparison.Ordinal));

        state.EnterMap(9);
        await loop.TickAsync(CancellationToken.None).ConfigureAwait(false);
        var walksOnItsOwnMap = actuator.Calls.Any(c => c.StartsWith("waypoint:", StringComparison.Ordinal));

        return ("la route n'est marchee que sur sa carte", heldElsewhere && walksOnItsOwnMap);
    }

    private static PacketEventArgs<SuPacket> Su(int vnum, short cooldown, long casterId)
        => new
        (
            PacketSource.Server,
            new SuPacket
            (
                CasterEntityType: EntityType.Player,
                CasterEntityId: casterId,
                TargetEntityType: EntityType.Monster,
                TargetEntityId: 42,
                SkillVNum: vnum,
                SkillCooldown: cooldown,
                AttackAnimation: 11,
                SkillEffect: 0,
                PositionX: 51,
                PositionY: 50,
                TargetIsAlive: true,
                HpPercentage: 80,
                Damage: 100,
                HitMode: HitMode.SuccessfulAttack,
                SkillTypeMinusOne: 0,
                Hp: 800,
                MaxHp: 1000
            ),
            "su"
        );

    private static void Engage(ProtocolStateManager state)
    {
        state.TrackEntity(new TrackedEntity(42, EntityType.Monster, 51, 50, 100));
        state.AcquireTarget(42, EntityType.Monster);
        state.UpdateTargetVitals(42, 900, 90);
    }

    private static (OrchestrationBackgroundService Loop, ProtocolStateManager State, SkillRotation Rotation, RecordingActuator Actuator, BotOptions Options)
        Build(bool selectsTargetItself)
    {
        var options = new BotOptions
        {
            Waypoints = new List<Waypoint>(),
            Buffs = new List<BuffDefinition>(),
            Skills = new List<SkillDefinition> { new(1, "Test", 0, TimeSpan.FromSeconds(10)) },
            AttackInterval = TimeSpan.Zero,
            SearchInterval = TimeSpan.Zero
        };

        var state = new ProtocolStateManager(options);
        state.UpdateVitals(2000, 2000, 1000, 1000);
        state.UpdatePosition(50, 50);

        var rotation = new SkillRotation(options, NullLogger<SkillRotation>.Instance);
        var actuator = new RecordingActuator(selectsTargetItself);

        var loop = new OrchestrationBackgroundService
        (
            state,
            actuator,
            rotation,
            new BuffTracker(options, NullLogger<BuffTracker>.Instance),
            new BotController(),
            options,
            NullLogger<OrchestrationBackgroundService>.Instance
        );

        return (loop, state, rotation, actuator, options);
    }

    /// <summary>
    /// An actuator that carries nothing out and remembers what it was asked to do.
    /// </summary>
    private sealed class RecordingActuator : IBotActuator
    {
        public RecordingActuator(bool selectsTargetItself)
            => SelectsTargetItself = selectsTargetItself;

        public List<string> Calls { get; } = new();

        public string Description => "recording";

        public bool SupportsApproach => !SelectsTargetItself;

        public bool SelectsTargetItself { get; }

        public bool TryPrepare(out string error)
        {
            error = string.Empty;
            return true;
        }

        public Task<bool> UseHpPotionAsync(CancellationToken ct = default) => Record("hp");

        public Task<bool> UseMpPotionAsync(CancellationToken ct = default) => Record("mp");

        public Task<bool> ApplyBuffAsync(BuffDefinition buff, CancellationToken ct = default) => Record("buff:" + buff.Name);

        public Task<bool> TargetNearestAsync(CancellationToken ct = default) => Record("target");

        public Task<bool> CastSkillAsync(SkillDefinition? skill, long targetEntityId, CancellationToken ct = default)
            => Record(skill is null ? "basic" : "skill:" + skill.Name);

        public Task<bool> LootAsync(CancellationToken ct = default) => Record("loot");

        public Task<bool> ApproachAsync(int x, int y, CancellationToken ct = default) => Record($"approach:{x},{y}");

        public Task<bool> GoToWaypointAsync(int index, CancellationToken ct = default) => Record("waypoint:" + index);

        private Task<bool> Record(string call)
        {
            Calls.Add(call);
            return Task.FromResult(true);
        }
    }
}
