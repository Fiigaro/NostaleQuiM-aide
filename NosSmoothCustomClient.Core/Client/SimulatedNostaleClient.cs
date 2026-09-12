using System.Collections.Concurrent;
using System.Globalization;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using NosSmooth.Core.Client;
using NosSmooth.Core.Commands;
using NosSmooth.Core.Extensions;
using NosSmooth.Core.Packets;
using NosSmooth.Packets;
using NosSmooth.Packets.Enums.Battle;
using NosSmooth.Packets.Enums.Entities;
using NosSmooth.Packets.Server.Battle;
using NosSmooth.Packets.Server.Entities;
using NosSmooth.Packets.Server.Maps;
using NosSmooth.Packets.Server.Skills;
using NosSmooth.PacketSerializer;
using NosSmooth.PacketSerializer.Abstractions.Attributes;
using NosSmooth.PacketSerializer.Abstractions.Common;
using NosSmoothCustomClient.Packets;
using Remora.Results;

namespace NosSmoothCustomClient.Client;

/// <summary>
/// An in-process stand-in for the game that speaks the real wire format.
/// </summary>
/// <remarks>
/// Every inbound frame is produced by serializing a real packet record through the same
/// <see cref="IPacketSerializer"/> the production path uses, then fed back in as a string. That
/// makes a run a genuine round-trip test of the generated converters, the type repository, the
/// responder fan-out and the orchestration loop - on any OS, with no game process attached.
/// Outbound frames are parsed back and answered, so the loop closes.
/// </remarks>
public sealed class SimulatedNostaleClient : INostaleClient
{
    private const long OwnCharacterId = 1234;
    private const int MapId = 1;

    private readonly IPacketHandler _packetHandler;
    private readonly IPacketSerializer _serializer;
    private readonly ILogger<SimulatedNostaleClient> _logger;

    private readonly Channel<string> _inbound = Channel.CreateUnbounded<string>();

    // The simulated world.
    private long _playerHp = 2000;
    private long _playerMaxHp = 2000;
    private long _playerMp = 1000;
    private long _playerMaxMp = 1000;
    private int _playerX = 50;
    private int _playerY = 50;

    private long _monsterId;
    private long _monsterHp;
    private long _monsterMaxHp = 1000;
    private int _monsterX;
    private int _monsterY;
    private long _nextMonsterId = 2001;
    private DateTimeOffset _respawnAt = DateTimeOffset.MaxValue;

    // Packets the fake server owes the client later: skill-ready notifications, mostly.
    private readonly ConcurrentQueue<(DateTimeOffset Due, IPacket Packet)> _deferred = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="SimulatedNostaleClient"/> class.
    /// </summary>
    /// <param name="packetHandler">The managed packet handler.</param>
    /// <param name="serializer">The packet serializer.</param>
    /// <param name="logger">The logger.</param>
    public SimulatedNostaleClient
    (
        IPacketHandler packetHandler,
        IPacketSerializer serializer,
        ILogger<SimulatedNostaleClient> logger
    )
    {
        _packetHandler = packetHandler;
        _serializer = serializer;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<Result> RunAsync(CancellationToken stopRequested = default)
    {
        _logger.LogInformation("Simulated client running. No game process is attached; frames are synthesised.");

        Seed();

        var pump = Task.Run(() => RespawnPumpAsync(stopRequested), stopRequested);

        try
        {
            await foreach (var packetString in _inbound.Reader.ReadAllAsync(stopRequested).ConfigureAwait(false))
            {
                _logger.LogInformation("[IN ] {Packet}", packetString);

                var handled = await _packetHandler
                    .HandlePacketAsync(this, PacketSource.Server, packetString, stopRequested)
                    .ConfigureAwait(false);

                if (!handled.IsSuccess)
                {
                    _logger.LogWarning("Handling '{Packet}' failed: {Error}", packetString, handled.ToFullString());
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }

        await SwallowAsync(pump).ConfigureAwait(false);
        return Result.FromSuccess();
    }

    /// <inheritdoc />
    public Task<Result> SendPacketAsync(string packetString, CancellationToken ct = default)
    {
        _logger.LogInformation("[OUT] {Packet}", packetString);
        React(packetString);
        return Task.FromResult(Result.FromSuccess());
    }

    /// <inheritdoc />
    public async Task<Result> ReceivePacketAsync(string packetString, CancellationToken ct = default)
    {
        await _inbound.Writer.WriteAsync(packetString, ct).ConfigureAwait(false);
        return Result.FromSuccess();
    }

    /// <inheritdoc />
    public Task<Result> SendCommandAsync(ICommand command, CancellationToken ct = default)
    {
        // The command handlers belong to the local (attached) client. Nothing drives them here.
        _logger.LogInformation("[CMD] {Command}", command.GetType().Name);
        return Task.FromResult(Result.FromSuccess());
    }

    // A synthetic skill bar. The cast id is the slot, so VNum 9000 + slot is the skill sitting
    // there - which is all the confirmation path needs to turn su's VNum back into a cast id.
    private const int SkillVNumBase = 9000;

    private void Seed()
    {
        Enqueue(new AtPacket(OwnCharacterId, MapId, (short)_playerX, (short)_playerY));
        EnqueueStat();
        EnqueueSkillBar();

        // Exercises the custom generated converter end to end.
        Enqueue(new QuiMStatPacket(OwnCharacterId, 1, 4200, "NosSmoothCustomClient"));

        SpawnMonster(55, 52);
    }

    private void EnqueueSkillBar()
    {
        // Ten slots: the whole quick bar, so any configured cast id resolves without the simulator
        // having to know what is configured.
        var bar = Enumerable.Range(0, 10)
            .Select(slot => new SkiSubPacket(SkillVNumBase + slot, 0))
            .ToArray();

        Enqueue(new SkiPacket(bar[0].SkillVNum, bar[0].SkillVNum, 0, bar));
    }

    private void React(string packetString)
    {
        var parts = packetString.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return;
        }

        switch (parts[0])
        {
            case "u_s":
                HandleAttack(parts);
                break;

            case "walk":
                HandleWalk(parts);
                break;

            case "u_i":
                HandleItemUse();
                break;
        }
    }

    private void HandleAttack(IReadOnlyList<string> parts)
    {
        if (parts.Count < 4 || !long.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var targetId))
        {
            return;
        }

        if (targetId != _monsterId || _monsterHp <= 0)
        {
            return;
        }

        // Charge the cast and promise an sr once its cooldown elapses, so the rotation is driven
        // by the server signal exactly as it would be in game.
        int? castVNum = null;
        if (short.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var castId) && castId != 0)
        {
            _playerMp = Math.Max(0, _playerMp - 40);
            _deferred.Enqueue((DateTimeOffset.UtcNow.AddSeconds(4), new SrPacket(castId)));

            // Name the skill in su, the way a real server does. Without it there is no way to tell
            // a cast that happened from a key press the client threw away.
            castVNum = SkillVNumBase + castId;
        }

        const int damage = 250;
        _monsterHp = Math.Max(0, _monsterHp - damage);
        var percentage = (byte)(_monsterMaxHp > 0 ? _monsterHp * 100 / _monsterMaxHp : 0);

        Enqueue(new SuPacket
        (
            CasterEntityType: EntityType.Player,
            CasterEntityId: OwnCharacterId,
            TargetEntityType: EntityType.Monster,
            TargetEntityId: _monsterId,
            SkillVNum: castVNum,
            SkillCooldown: 40,
            AttackAnimation: 11,
            SkillEffect: 0,
            PositionX: (short)_monsterX,
            PositionY: (short)_monsterY,
            TargetIsAlive: _monsterHp > 0,
            HpPercentage: percentage,
            Damage: damage,
            HitMode: HitMode.SuccessfulAttack,
            SkillTypeMinusOne: 0,
            Hp: (int)_monsterHp,
            MaxHp: (int)_monsterMaxHp
        ));

        if (_monsterHp <= 0)
        {
            Enqueue(new DiePacket(EntityType.Player, OwnCharacterId, EntityType.Monster, _monsterId));
            Enqueue(new OutPacket(EntityType.Monster, _monsterId));
            _monsterId = 0;

            // Leave a quiet window so the navigation priority gets to run before the next spawn.
            _respawnAt = DateTimeOffset.UtcNow.AddSeconds(6);
            return;
        }

        // The monster hits back, which eventually drives the survival priority.
        _playerHp = Math.Max(1, _playerHp - 300);
        EnqueueStat();
    }

    private void HandleWalk(IReadOnlyList<string> parts)
    {
        if (parts.Count < 3
            || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var x)
            || !int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var y))
        {
            return;
        }

        _playerX = x;
        _playerY = y;
        Enqueue(new MovePacket(EntityType.Player, OwnCharacterId, (short)x, (short)y, 11));
    }

    private void HandleItemUse()
    {
        _playerHp = _playerMaxHp;
        _playerMp = _playerMaxMp;
        EnqueueStat();
    }

    private async Task RespawnPumpAsync(CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                DrainDeferred();

                if (_monsterId != 0 || DateTimeOffset.UtcNow < _respawnAt)
                {
                    continue;
                }

                _respawnAt = DateTimeOffset.MaxValue;
                SpawnMonster(_playerX + 4, _playerY + 2);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    private void DrainDeferred()
    {
        var now = DateTimeOffset.UtcNow;
        var pending = new List<(DateTimeOffset Due, IPacket Packet)>();

        while (_deferred.TryDequeue(out var item))
        {
            if (item.Due <= now)
            {
                Enqueue(item.Packet);
            }
            else
            {
                pending.Add(item);
            }
        }

        foreach (var item in pending)
        {
            _deferred.Enqueue(item);
        }
    }

    private void SpawnMonster(int x, int y)
    {
        _monsterId = _nextMonsterId++;
        _monsterHp = _monsterMaxHp;
        _monsterX = x;
        _monsterY = y;

        Enqueue(new InPacket
        (
            EntityType: EntityType.Monster,
            Name: NameString.FromString("Fibroid"),
            VNum: 58,
            Unknown: "-",
            EntityId: _monsterId,
            PositionX: (short)x,
            PositionY: (short)y,
            Direction: 2,
            PlayerSubPacket: null!,
            ItemSubPacket: null!,
            NonPlayerSubPacket: new InNonPlayerSubPacket
            (
                HpPercentage: 100,
                MpPercentage: 100,
                Dialog: -1,
                Faction: FactionType.Neutral,
                GroupEffect: 0,
                OwnerId: null,
                SpawnEffect: SpawnEffect.NoEffect,
                IsSitting: false,
                MorphVNum: null,
                Name: NameString.FromString("Fibroid"),
                PartnerMask: 0,
                Unknown2: "-1",
                Unknown3: "-1",
                Skill1: 0,
                Skill2: 0,
                Skill3: 0,
                SkillRank1: 0,
                SkillRank2: 0,
                SkillRank3: 0,
                IsInvisible: false,
                Unknown4: "-1",
                Unknown5: "-1"
            )
        ));
    }

    private void EnqueueStat()
        => Enqueue(new StatPacket(_playerHp, _playerMaxHp, _playerMp, _playerMaxMp, 0, 0));

    private void Enqueue(IPacket packet)
    {
        var serialized = _serializer.Serialize(packet);
        if (!serialized.IsDefined(out var packetString))
        {
            _logger.LogError
            (
                "The simulator could not serialize {Packet}: {Error}",
                packet.GetType().Name,
                serialized.ToFullString()
            );

            return;
        }

        if (!_inbound.Writer.TryWrite(packetString))
        {
            _logger.LogWarning("Dropped simulated packet '{Packet}'.", packetString);
        }
    }

    private static async Task SwallowAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown.
        }
    }
}
