using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NosSmooth.Core.Packets;
using NosSmooth.Packets.Enums.Battle;
using NosSmooth.Packets.Enums.Entities;
using NosSmooth.Packets.Server.Battle;
using NosSmooth.Packets.Server.Maps;
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
            await RouteIsNotWalkedOnTheWrongMapAsync().ConfigureAwait(false),
            await WalkingIsNotRestartedEveryTickAsync().ConfigureAwait(false),
            await StalledWalkIsSentAgainAsync().ConfigureAwait(false),
            await IgnoredClicksChangeHowWeClickAsync().ConfigureAwait(false),
            await UnknownMapDoesNotBlockTheRouteAsync().ConfigureAwait(false),
            await StartingOnAWaypointStillWalksAsync().ConfigureAwait(false),
            await UnknownPositionStillStartsWalkingAsync().ConfigureAwait(false),
            await RouteGoingNowhereIsReportedAsync().ConfigureAwait(false),
            await TheGamesOwnTargetIsAdoptedAsync().ConfigureAwait(false),
            await UnreachableWaypointIsSkippedAsync().ConfigureAwait(false),
            await WorkingClicksAreNotBlamedWithoutAPositionAsync().ConfigureAwait(false),
            await LeavingEntityReleasesTheTargetAsync().ConfigureAwait(false),
            await ClearedRoomHeadsForTheExitAsync().ConfigureAwait(false),
            await ClearedRoomStopsHuntingAsync().ConfigureAwait(false),
            await BasicAttackFiresEvenWithSkillsReadyAsync().ConfigureAwait(false),
            RouteIsStampedWhereItsPointsWereTaken(),
            await AStalledFightGivesWayToMovingAsync().ConfigureAwait(false),
            TheMinimapIsMeasuredFromTheRoute(),
            await TheExitIsNotAStopOnTheRoundAsync().ConfigureAwait(false),
            await DistantMonstersAreWalkedToAsync().ConfigureAwait(false)
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

    private static async Task<(string, bool)> WalkingIsNotRestartedEveryTickAsync()
    {
        var (loop, state, _, actuator, options) = Build(selectsTargetItself: true);
        options.Waypoints = new List<Waypoint> { new(80, 80, 400, 300) };
        options.SearchInterval = TimeSpan.FromHours(1);

        // Ticks here run back to back, so the per-tick walk gate has to be opened for the check to
        // model a real run, where a full tick period elapses between them and the gate is always
        // free. Without this the gate hides the very behaviour under test.
        options.TickInterval = TimeSpan.Zero;

        // Four ticks of a character that is walking. Exactly one order should have been sent.
        for (var step = 0; step < 4; step++)
        {
            state.UpdatePosition(50 + step, 50 + step);
            await loop.TickAsync(CancellationToken.None).ConfigureAwait(false);
        }

        var orders = actuator.Calls.Count(c => c.StartsWith("waypoint:", StringComparison.Ordinal));
        return ("marcher n'est pas relance a chaque tick", orders == 1);
    }

    private static async Task<(string, bool)> StalledWalkIsSentAgainAsync()
    {
        var (loop, state, _, actuator, options) = Build(selectsTargetItself: true);
        options.Waypoints = new List<Waypoint> { new(80, 80, 400, 300) };
        options.SearchInterval = TimeSpan.FromHours(1);
        options.WalkReissueInterval = TimeSpan.FromMilliseconds(150);
        options.TickInterval = TimeSpan.Zero;

        // Same position throughout: the click went nowhere, so it has to be sent again - but only
        // once the window has passed, not on the very next tick.
        state.UpdatePosition(50, 50);
        await loop.TickAsync(CancellationToken.None).ConfigureAwait(false);
        await loop.TickAsync(CancellationToken.None).ConfigureAwait(false);
        var heldAtFirst = actuator.Calls.Count(c => c.StartsWith("waypoint:", StringComparison.Ordinal)) == 1;

        await Task.Delay(250).ConfigureAwait(false);
        await loop.TickAsync(CancellationToken.None).ConfigureAwait(false);
        var sentAgain = actuator.Calls.Count(c => c.StartsWith("waypoint:", StringComparison.Ordinal)) == 2;

        return ("un trajet bloque est relance", heldAtFirst && sentAgain);
    }

    private static async Task<(string, bool)> TheGamesOwnTargetIsAdoptedAsync()
    {
        var (loop, state, rotation, actuator, options) = Build(selectsTargetItself: true);
        var responder = new TargetHpResponder(state, new RunJournal(), NullLogger<TargetHpResponder>.Instance);

        state.SetOwnCharacterId(1234);

        // Nothing on our side picks a target when the game does its own selecting, so the only way
        // to learn what it chose is the server reporting our character hitting it. Without this the
        // lock stays empty and the rotation never casts anything at all.
        await responder.Respond(Su(vnum: null, cooldown: 0, casterId: 1234)).ConfigureAwait(false);

        var locked = state.Target?.EntityId == 42;

        await loop.TickAsync(CancellationToken.None).ConfigureAwait(false);
        var cast = actuator.Calls.Any(c => c.StartsWith("skill:", StringComparison.Ordinal));

        return ("la cible choisie par le jeu est adoptee", locked && cast && rotation is not null);
    }

    private static (OrchestrationBackgroundService Loop, ProtocolStateManager State, RecordingActuator Actuator, BotOptions Options)
        BuildInstance()
    {
        var (loop, state, _, actuator, options) = Build(selectsTargetItself: true);

        // The two jobs a room's route has: somewhere to pull from, and the way out.
        options.Waypoints = new List<Waypoint> { new(13, 14, 1351, 100), new(14, 1, 1354, 36) };
        options.InstanceMode = true;
        options.SearchInterval = TimeSpan.Zero;
        options.TickInterval = TimeSpan.Zero;

        state.EnterMap(4103);
        state.UpdatePosition(13, 14);

        return (loop, state, actuator, options);
    }

    private static async Task<(string, bool)> AStalledFightGivesWayToMovingAsync()
    {
        var (loop, state, _, actuator, options) = Build(selectsTargetItself: true);
        options.Waypoints = new List<Waypoint> { new(13, 14, 1351, 100), new(14, 1, 1354, 36) };
        options.SearchInterval = TimeSpan.FromHours(1);
        options.TickInterval = TimeSpan.Zero;
        options.RepositionAfter = TimeSpan.FromMilliseconds(120);

        state.EnterMap(4103);
        state.UpdatePosition(13, 14);
        Engage(state);

        // A live target and nothing else happening, which in a room full of monsters is every tick.
        // Attacking must not be allowed to claim them all, or the character never leaves the spot
        // and the rest of the room is never reached.
        await loop.TickAsync(CancellationToken.None).ConfigureAwait(false);
        var heldTheTickWhileProgressing = !actuator.Calls.Any(c => c.StartsWith("waypoint:", StringComparison.Ordinal));

        await Task.Delay(200).ConfigureAwait(false);
        await loop.TickAsync(CancellationToken.None).ConfigureAwait(false);

        var attacked = actuator.Calls.Contains("basic");
        var moved = actuator.Calls.Any(c => c.StartsWith("waypoint:", StringComparison.Ordinal));

        return ("un combat qui n'avance plus laisse la place au déplacement",
            heldTheTickWhileProgressing && attacked && moved);
    }

    private static (string, bool) TheMinimapIsMeasuredFromTheRoute()
    {
        // The two points actually recorded in the room. Recording a waypoint captures the same place
        // in both coordinate systems, so a route is also a measurement of the minimap.
        var projection = MinimapProjection.Build(new[] { new Waypoint(13, 14, 1351, 100), new Waypoint(14, 1, 1354, 36) });

        if (projection is null)
        {
            return ("la minimap se mesure depuis la route", false);
        }

        var a = projection.Project(13, 14);
        var b = projection.Project(14, 1);

        // It has to reproduce the points it was built from, and it has to admit that one cell of
        // spread in X measures nothing - a confidently wrong projection sends the character
        // somewhere nobody asked for.
        var faithful = a == (1351, 100) && b == (1354, 36);
        var honest = !projection.IsWellSpread && projection.SpreadY == 13 && projection.SpreadX == 1;

        return ("la minimap se mesure depuis la route", faithful && honest);
    }

    private static async Task<(string, bool)> TheExitIsNotAStopOnTheRoundAsync()
    {
        var (loop, state, actuator, options) = BuildInstance();
        options.WaypointArrivalRadius = 3;

        // Without this the target probe claims every tick and navigation is never reached, which
        // makes the whole check pass without exercising anything.
        options.SearchInterval = TimeSpan.FromHours(1);

        // Standing on the roaming point, which is where arriving at it leaves you. Advancing from
        // there must not walk into the way out: that ends the run with the room still full, which is
        // exactly what it did.
        state.UpdatePosition(13, 14);

        for (var tick = 0; tick < 6; tick++)
        {
            await loop.TickAsync(CancellationToken.None).ConfigureAwait(false);
        }

        return ("la sortie n'est pas une étape du circuit", !actuator.Calls.Contains("waypoint:1"));
    }

    private static async Task<(string, bool)> DistantMonstersAreWalkedToAsync()
    {
        var (loop, state, actuator, options) = BuildInstance();
        options.RepositionAfter = TimeSpan.Zero;
        options.EngagementRadius = 40;

        // The probe takes the first tick to ask the game whether anything is close; only after that
        // does the answer - nothing within reach - leave the tick to walking.
        options.SearchInterval = TimeSpan.FromHours(1);

        // A monster the server named, too far for the attack key to find. Its coordinates came in a
        // packet; the route says where that is on the minimap.
        state.UpdatePosition(13, 25);
        state.TrackEntity(new TrackedEntity(77, EntityType.Monster, 13, 4, 100));

        await loop.TickAsync(CancellationToken.None).ConfigureAwait(false);
        await loop.TickAsync(CancellationToken.None).ConfigureAwait(false);

        return ("un monstre lointain est rejoint", actuator.Calls.Any(c => c.StartsWith("approach:", StringComparison.Ordinal)));
    }

    private static (string, bool) RouteIsStampedWhereItsPointsWereTaken()
    {
        var options = new BotOptions { Waypoints = new List<Waypoint>() };
        var state = new ProtocolStateManager(options);
        var recorder = new WaypointRecorder(new CaptureTarget(), state, options, NullLogger<WaypointRecorder>.Instance);

        // Recorded in the room...
        state.EnterMap(4103);
        state.UpdatePosition(13, 14);
        recorder.Capture(1351, 100);
        state.UpdatePosition(14, 1);
        recorder.Capture(1354, 36);

        // ...and saved back in town, which is where a run naturally ends. Taking the map at save
        // time made the route belong to the town: refused where its points mean something, walked
        // where they mean nothing.
        state.EnterMap(2628);
        state.UpdatePosition(107, 96);
        recorder.Apply();

        var stamped = options.RouteMapId == 4103;
        var kept = options.Waypoints.Count == 2 && options.Waypoints[1].X == 14 && options.Waypoints[1].Y == 1;

        return ("la route est estampillée là où ses points ont été pris", stamped && kept);
    }

    private static async Task<(string, bool)> BasicAttackFiresEvenWithSkillsReadyAsync()
    {
        var (loop, state, rotation, actuator, options) = Build(selectsTargetItself: true);

        // Two skills, both ready, both affordable - the state a rotation spends nearly all its time
        // in. Treating the attack key as what to press when nothing else is ready means never
        // pressing it, which is most of the damage gone.
        options.Skills = new List<SkillDefinition>
        {
            new(1, "A", 0, TimeSpan.FromSeconds(10)),
            new(2, "B", 0, TimeSpan.FromSeconds(10))
        };

        Engage(state);

        await loop.TickAsync(CancellationToken.None).ConfigureAwait(false);
        var both = actuator.Calls.Contains("basic") && actuator.Calls.Any(c => c.StartsWith("skill:", StringComparison.Ordinal));

        // And it keeps swinging on the frames that follow, while the skill waits for its answer.
        actuator.Calls.Clear();
        await loop.TickAsync(CancellationToken.None).ConfigureAwait(false);
        var keptSwinging = actuator.Calls.Contains("basic");

        return ("l'attaque de base part même avec des sorts prêts", both && keptSwinging && rotation is not null);
    }

    private static async Task<(string, bool)> ClearedRoomHeadsForTheExitAsync()
    {
        var (loop, state, actuator, _) = BuildInstance();

        state.MarkRoomCleared();
        await loop.TickAsync(CancellationToken.None).ConfigureAwait(false);

        // Waypoint 2 is the portal, and nothing else will do: a cleared room has one destination.
        return ("une salle terminée mène à la sortie", actuator.Calls.Contains("waypoint:1"));
    }

    private static async Task<(string, bool)> ClearedRoomStopsHuntingAsync()
    {
        var (loop, state, actuator, _) = BuildInstance();

        // A monster still in the table, and the room announced as wiped. The announcement wins:
        // probing for a target is asking a question the server has already answered.
        state.TrackEntity(new TrackedEntity(42, EntityType.Monster, 13, 15, 100));
        state.MarkRoomCleared();

        await loop.TickAsync(CancellationToken.None).ConfigureAwait(false);

        var hunted = actuator.Calls.Contains("target")
                     || actuator.Calls.Any(c => c.StartsWith("skill:", StringComparison.Ordinal));

        // And a fresh room puts it back to work.
        state.EnterMap(4104);
        state.UpdatePosition(13, 14);
        await loop.TickAsync(CancellationToken.None).ConfigureAwait(false);

        return ("une salle terminée arrête la chasse", !hunted && !state.RoomCleared && actuator.Calls.Contains("target"));
    }

    private static async Task<(string, bool)> LeavingEntityReleasesTheTargetAsync()
    {
        var (loop, state, _, actuator, _) = Build(selectsTargetItself: true);
        var journal = new RunJournal();
        var responder = new EntitySpawnResponder(state, new BotOptions(), journal, NullLogger<EntitySpawnResponder>.Instance);

        Engage(state);

        // The packet a monster leaves by, death included. The lock has to go with the table entry,
        // or the bot keeps hitting something that is no longer there until the silence timeout.
        await responder.Respond(new PacketEventArgs<OutPacket>
        (
            PacketSource.Server,
            new OutPacket(EntityType.Monster, 42),
            "out"
        )).ConfigureAwait(false);

        var released = state.Target is null;

        await loop.TickAsync(CancellationToken.None).ConfigureAwait(false);
        var lookedForAnother = actuator.Calls.Contains("target");

        return ("une entité qui part libère la cible", released && lookedForAnother);
    }

    private static async Task<(string, bool)> WorkingClicksAreNotBlamedWithoutAPositionAsync()
    {
        var (loop, _, _, actuator, options) = Build(selectsTargetItself: true, withPosition: false);
        options.Waypoints = new List<Waypoint> { new(80, 80, 400, 300), new(120, 120, 410, 310) };
        options.SearchInterval = TimeSpan.FromHours(1);
        options.TickInterval = TimeSpan.Zero;
        options.WalkReissueInterval = TimeSpan.FromMilliseconds(50);

        // The server never says where the character is, so the position holds at (0,0) whatever
        // happens. Read as "has not moved", that condemns a click the client is obeying: the bot
        // changes how it clicks, then abandons the waypoint, over something it cannot see.
        for (var attempt = 0; attempt < 10; attempt++)
        {
            await loop.TickAsync(CancellationToken.None).ConfigureAwait(false);
            await Task.Delay(70).ConfigureAwait(false);
        }

        var stayedOnCourse = !actuator.Calls.Contains("waypoint:1");
        return ("sans position, un clic qui marche n'est pas accuse", actuator.Escalations == 0 && stayedOnCourse);
    }

    private static async Task<(string, bool)> UnreachableWaypointIsSkippedAsync()
    {
        var (loop, state, _, actuator, options) = Build(selectsTargetItself: true);
        options.Waypoints = new List<Waypoint> { new(200, 200, 400, 300), new(80, 80, 410, 310) };
        options.SearchInterval = TimeSpan.FromHours(1);
        options.TickInterval = TimeSpan.Zero;
        options.WalkReissueInterval = TimeSpan.FromMilliseconds(60);

        // Every order is accepted, the character never moves, and no other way of clicking helps.
        // Parking there forever is the one outcome that is never right.
        state.UpdatePosition(50, 50);

        // Enough ticks for a lost click, a change of method, and the repeats after it to all have
        // had their turn before giving up is the only thing left.
        for (var attempt = 0; attempt < 14; attempt++)
        {
            await loop.TickAsync(CancellationToken.None).ConfigureAwait(false);
            await Task.Delay(90).ConfigureAwait(false);
        }

        return ("un waypoint inatteignable est abandonne", actuator.Calls.Contains("waypoint:1"));
    }

    private static async Task<(string, bool)> StartingOnAWaypointStillWalksAsync()
    {
        var (loop, state, _, actuator, options) = Build(selectsTargetItself: true);

        // Two points within the arrival radius of the start, then one that is actually elsewhere.
        // Stepping past a single point is not enough: the bot has to keep going until it finds
        // somewhere worth walking to, rather than setting off for a point it is already standing on.
        options.Waypoints = new List<Waypoint> { new(50, 50, 400, 300), new(51, 50, 405, 300), new(80, 80, 410, 310) };
        options.SearchInterval = TimeSpan.FromHours(1);
        options.TickInterval = TimeSpan.Zero;

        // Standing exactly on the first waypoint, which is where a run started from the spot the
        // route was recorded on always begins.
        state.UpdatePosition(50, 50);

        await loop.TickAsync(CancellationToken.None).ConfigureAwait(false);
        await loop.TickAsync(CancellationToken.None).ConfigureAwait(false);

        return ("partir depuis un waypoint ne bloque pas", actuator.Calls.Contains("waypoint:2"));
    }

    private static async Task<(string, bool)> UnknownPositionStillStartsWalkingAsync()
    {
        var (loop, state, _, actuator, options) = Build(selectsTargetItself: true, withPosition: false);
        options.Waypoints = new List<Waypoint> { new(50, 50, 400, 300), new(80, 80, 410, 310) };
        options.SearchInterval = TimeSpan.FromHours(1);
        options.TickInterval = TimeSpan.Zero;

        // No position has ever been reported, so the state reads (0,0) - which must not be taken
        // for a real coordinate that happens to sit on a waypoint.
        await loop.TickAsync(CancellationToken.None).ConfigureAwait(false);
        await loop.TickAsync(CancellationToken.None).ConfigureAwait(false);

        return ("sans position connue il marche quand meme",
            !state.HasPosition && actuator.Calls.Any(c => c.StartsWith("waypoint:", StringComparison.Ordinal)));
    }

    private static async Task<(string, bool)> RouteGoingNowhereIsReportedAsync()
    {
        var (loop, state, _, actuator, options) = Build(selectsTargetItself: true);
        options.Waypoints = new List<Waypoint> { new(0, 0, 400, 300), new(0, 0, 410, 310) };
        options.SearchInterval = TimeSpan.FromHours(1);
        options.TickInterval = TimeSpan.Zero;

        state.UpdatePosition(0, 0);
        await loop.TickAsync(CancellationToken.None).ConfigureAwait(false);
        await loop.TickAsync(CancellationToken.None).ConfigureAwait(false);

        var walkedNowhere = !actuator.Calls.Any(c => c.StartsWith("waypoint:", StringComparison.Ordinal));
        var loopSaysSo = loop.LastDecision.Contains("ne mène nulle part", StringComparison.Ordinal);
        var windowSaysSo = !BotReadiness.Describe(options, state, null).First(i => i.Name == "Déplacement").Ready;

        return ("une route qui ne mene nulle part est signalee", walkedNowhere && loopSaysSo && windowSaysSo);
    }

    private static async Task<(string, bool)> UnknownMapDoesNotBlockTheRouteAsync()
    {
        var (loop, state, _, actuator, options) = Build(selectsTargetItself: true);
        options.Waypoints = new List<Waypoint> { new(80, 80, 400, 300) };
        options.RouteMapId = 9;
        options.SearchInterval = TimeSpan.FromHours(1);
        options.TickInterval = TimeSpan.Zero;

        // The map is never announced, which is the normal case for a bot started while the
        // character is already standing somewhere: at and c_map only arrive on entering a map.
        // Reading that silence as "the wrong map" stops the bot walking for the whole session.
        await loop.TickAsync(CancellationToken.None).ConfigureAwait(false);
        await loop.TickAsync(CancellationToken.None).ConfigureAwait(false);

        var walked = actuator.Calls.Any(c => c.StartsWith("waypoint:", StringComparison.Ordinal));

        // And the window must agree with the loop, or a green line covers a bot that is refusing.
        var readinessAgrees = BotReadiness.Describe(options, state, null)
            .First(i => i.Name == "Déplacement").Ready;

        return ("une carte inconnue ne bloque pas la route", walked && readinessAgrees);
    }

    private static async Task<(string, bool)> IgnoredClicksChangeHowWeClickAsync()
    {
        var (loop, state, _, actuator, options) = Build(selectsTargetItself: true);
        options.Waypoints = new List<Waypoint> { new(80, 80, 400, 300) };
        options.SearchInterval = TimeSpan.FromHours(1);
        options.TickInterval = TimeSpan.Zero;
        options.WalkReissueInterval = TimeSpan.FromMilliseconds(80);

        // The order is accepted every time and the character never moves: repeating it harder is
        // not the answer, changing how it is sent is.
        state.UpdatePosition(50, 50);

        // Five, not three: the first tick goes to the one-off target probe, so only the rest of
        // them reach navigation at all.
        for (var attempt = 0; attempt < 5; attempt++)
        {
            await loop.TickAsync(CancellationToken.None).ConfigureAwait(false);
            await Task.Delay(120).ConfigureAwait(false);
        }

        return ("des clics ignores font changer de methode", actuator.Escalations > 0);
    }

    private static PacketEventArgs<SuPacket> Su(int? vnum, short cooldown, long casterId)
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
        Build(bool selectsTargetItself, bool withPosition = true)
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

        if (withPosition)
        {
            state.UpdatePosition(50, 50);
        }

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

        // The keyboard actuator can approach too, once the minimap has been measured.
        public bool SupportsApproach => true;

        public bool SelectsTargetItself { get; }

        public bool WalkIsSustained => SelectsTargetItself;

        public bool TryAnotherWayToMove(out string what)
        {
            what = "test";
            return Escalations++ == 0;
        }

        public int Escalations { get; private set; }

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
