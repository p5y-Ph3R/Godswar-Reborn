using System.Buffers.Binary;
using Godswar.Server.Game;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class PlayerRuntimeEcsCutoverChecks
{
    private static readonly DateTimeOffset Start =
        new(2026, 7, 23, 12, 0, 0, TimeSpan.Zero);

    public static async Task RunAsync()
    {
        PlayerRuntimeModeChecks.Run();
        await CheckRecoveryCutoverAsync();
        await CheckStatusCutoverAsync();
        await CheckCommittedOnlineClocksAsync();
    }

    private static async Task CheckRecoveryCutoverAsync()
    {
        await using var socket =
            await RuntimePolicySessionSocket.CreateAsync();
        await using var store = new RuntimePolicyStore();
        var registry = CreateRegistry(store);
        var character = CreateCharacter();
        registry.JoinMap(
            socket.Session,
            character.AccountId,
            character,
            WorldObjectIds.ForPlayer(character.Id),
            joinedAt: Start);

        await registry.AdvancePlayerRecoveryOnceAsync(
            Start.AddSeconds(5),
            CancellationToken.None);
        Check.Equal(1_000, character.CurrentHp, "ECS recovery does not pulse early");
        Check.Equal(0, socket.Available, "early recovery emits no packet");

        store.FailVitalsSave = true;
        await registry.AdvancePlayerRecoveryOnceAsync(
            Start.AddSeconds(6),
            CancellationToken.None);
        var first = await socket.ReadPacketAsync(16);
        Check.Equal((ushort)10097, ReadUInt16(first, 2), "ECS recovery packet opcode");
        Check.Equal(1_076, ReadInt32(first, 8), "ECS recovery packet HP");
        Check.Equal(53, ReadInt32(first, 12), "ECS recovery packet MP");
        Check.Equal(1L, character.VitalsRevision, "ECS recovery advances revision once");
        Check.Equal(1, store.VitalsSaveAttempts, "failed recovery save was attempted");

        await registry.AdvancePlayerRecoveryOnceAsync(
            Start.AddSeconds(6),
            CancellationToken.None);
        Check.Equal(
            0,
            socket.Available,
            "failed persistence does not replay the same recovery pulse");
        Check.Equal(
            1L,
            character.VitalsRevision,
            "failed persistence keeps the authoritative in-memory recovery");

        store.FailVitalsSave = false;
        await registry.AdvancePlayerRecoveryOnceAsync(
            Start.AddSeconds(12),
            CancellationToken.None);
        _ = await socket.ReadPacketAsync(16);
        Check.Equal(2, store.VitalsSaveAttempts, "next due recovery can persist");

        character.CurrentHp = character.MaxHp;
        character.CurrentMp = character.MaxMp;
        var fullRevision = character.VitalsRevision;
        await registry.AdvancePlayerRecoveryOnceAsync(
            Start.AddSeconds(18),
            CancellationToken.None);
        Check.Equal(0, socket.Available, "full vitals emit no recovery packet");
        Check.Equal(
            fullRevision,
            character.VitalsRevision,
            "full vitals do not advance revision");

        character.CurrentHp = 0;
        character.CurrentMp = 1;
        await registry.AdvancePlayerRecoveryOnceAsync(
            Start.AddSeconds(24),
            CancellationToken.None);
        Check.Equal(0, socket.Available, "dead player emits no recovery packet");

        character.CurrentHp = 1_000;
        character.CurrentMp = 9;
        registry.AdvancePlayerLifeRevision(
            socket.Session,
            Start.AddSeconds(25));
        await registry.AdvancePlayerRecoveryOnceAsync(
            Start.AddSeconds(30),
            CancellationToken.None);
        Check.Equal(
            0,
            socket.Available,
            "new life resets recovery to a fresh six-second cadence");
        await registry.AdvancePlayerRecoveryOnceAsync(
            Start.AddSeconds(31),
            CancellationToken.None);
        _ = await socket.ReadPacketAsync(16);

        registry.Remove(socket.Session);
        character.CurrentHp = 1_000;
        character.CurrentMp = 9;
        registry.JoinMap(
            socket.Session,
            character.AccountId,
            character,
            WorldObjectIds.ForPlayer(character.Id),
            joinedAt: Start.AddMinutes(2));
        await registry.AdvancePlayerRecoveryOnceAsync(
            Start.AddMinutes(2).AddSeconds(5),
            CancellationToken.None);
        Check.Equal(
            0,
            socket.Available,
            "same-session rejoin does not retain the old ECS recovery timer");
        await registry.AdvancePlayerRecoveryOnceAsync(
            Start.AddMinutes(2).AddSeconds(6),
            CancellationToken.None);
        _ = await socket.ReadPacketAsync(16);

        var diagnostics =
            registry.GetPlayerRecoveryEcsDiagnostics(socket.Session);
        Check.True(
            diagnostics is { PulsesObserved: 1 },
            "rejoined player owns a fresh ECS recovery lifecycle");
        registry.Remove(socket.Session);
    }

    private static async Task CheckStatusCutoverAsync()
    {
        await using var socket =
            await RuntimePolicySessionSocket.CreateAsync();
        var registry = CreateRegistry(null);
        var character = CreateCharacter();
        character.CurrentHp = character.MaxHp;
        character.CurrentMp = character.MaxMp;
        registry.JoinMap(
            socket.Session,
            character.AccountId,
            character,
            WorldObjectIds.ForPlayer(character.Id),
            joinedAt: Start);

        var laterId = new SkillStatusEffectDefinition(
            1,
            201,
            7,
            1,
            true,
            TimeSpan.FromSeconds(10),
            TimeSpan.Zero,
            20,
            8);
        var earlierId = new SkillStatusEffectDefinition(
            2,
            160,
            6,
            1,
            true,
            TimeSpan.FromSeconds(10),
            TimeSpan.Zero,
            0,
            0);
        await registry.ApplyRuntimeStatusAndPublishAsync(
            socket.Session,
            laterId,
            Start,
            "ecs-status-201",
            CancellationToken.None);
        _ = await socket.ReadPacketAsync(340);
        await registry.ApplyRuntimeStatusAndPublishAsync(
            socket.Session,
            earlierId,
            Start,
            "ecs-status-160",
            CancellationToken.None);
        _ = await socket.ReadPacketAsync(340);

        await registry.SetPersistentRuntimeStatusAndPublishAsync(
            socket.Session,
            MountCatalog.RuntimeStatusKind,
            statusId: 1390,
            priority: 1,
            beneficial: false,
            movementSpeedBonus: 0.24f,
            active: true,
            Start,
            "ecs-mount",
            CancellationToken.None);
        var mounted = await socket.ReadPacketAsync(340);
        var movement = await socket.ReadPacketAsync(236);
        Check.Equal(1.24f, ReadSingle(mounted, 324), "ECS mount status multiplier");
        Check.Equal(1.24f, ReadSingle(movement, 56), "ECS local movement multiplier");

        await registry.AdvancePlayerRecoveryOnceAsync(
            Start.AddSeconds(10),
            CancellationToken.None);
        var expired = await socket.ReadPacketAsync(340);
        Check.Equal(
            1.24f,
            ReadSingle(expired, 324),
            "expiring other statuses preserves mount multiplier");
        var diagnostics =
            registry.GetPlayerStatusEcsDiagnostics(socket.Session)
            ?? throw new InvalidOperationException(
                "Missing player status ECS diagnostics.");
        Check.True(
            diagnostics.ExpiredStatuses
                .Select(static status => status.StatusId)
                .SequenceEqual([160u, 201u]),
            "equal-time status expiry is ordered by status ID");
        Check.True(
            diagnostics.Snapshot.Aggregate.IsRiding,
            "mount remains active after timed statuses expire");

        var stale = await registry.GetStatusSnapshotAsync(
            socket.Session,
            Start.AddSeconds(5),
            CancellationToken.None);
        Check.True(stale.Aggregate.IsRiding, "backwards clock cannot undo mount state");
        Check.True(
            !registry.IsRuntimeStatusActive(
                socket.Session,
                6,
                Start.AddSeconds(5)),
            "backwards query cannot resurrect expired status");

        await registry.SetPersistentRuntimeStatusAndPublishAsync(
            socket.Session,
            MountCatalog.RuntimeStatusKind,
            statusId: 0,
            priority: 0,
            beneficial: false,
            movementSpeedBonus: 0f,
            active: false,
            Start.AddSeconds(11),
            "ecs-dismount",
            CancellationToken.None);
        var dismounted = await socket.ReadPacketAsync(340);
        var normalMovement = await socket.ReadPacketAsync(236);
        Check.Equal(1f, ReadSingle(dismounted, 324), "ECS dismount status multiplier");
        Check.Equal(1f, ReadSingle(normalMovement, 56), "ECS dismount local speed");
        registry.Remove(socket.Session);
    }

    private static async Task CheckCommittedOnlineClocksAsync()
    {
        await using var socket =
            await RuntimePolicySessionSocket.CreateAsync();
        await using var store = new RuntimePolicyStore();
        var registry = CreateRegistry(store);
        var character = CreateCharacter();
        registry.JoinMap(
            socket.Session,
            character.AccountId,
            character,
            WorldObjectIds.ForPlayer(character.Id),
            joinedAt: Start);

        _ = await registry.GetExperienceBoostStateAsync(
            socket.Session,
            character.AccountId,
            character.Id,
            character.Camp,
            character.CurrentMap,
            Start.AddSeconds(10),
            CancellationToken.None);
        var first = registry.GetPlayerOnlineDurationEcsDiagnostics(
            socket.Session);
        Check.Equal(
            TimeSpan.FromSeconds(10).Ticks,
            first.ProgressionElapsedTicks,
            "ECS observes committed progression duration");

        store.FailProgressionSave = true;
        var progressionFailed = false;
        try
        {
            _ = await registry.GetExperienceBoostStateAsync(
                socket.Session,
                character.AccountId,
                character.Id,
                character.Camp,
                character.CurrentMap,
                Start.AddSeconds(20),
                CancellationToken.None);
        }
        catch (InvalidOperationException)
        {
            progressionFailed = true;
        }

        Check.True(
            progressionFailed,
            "failed progression save remains visible to its caller");
        var failed = registry.GetPlayerOnlineDurationEcsDiagnostics(
            socket.Session);
        Check.Equal(
            first.ProgressionElapsedTicks,
            failed.ProgressionElapsedTicks,
            "failed save does not advance ECS online watermark");

        store.FailProgressionSave = false;
        _ = await registry.GetExperienceBoostStateAsync(
            socket.Session,
            character.AccountId,
            character.Id,
            character.Camp,
            character.CurrentMap,
            Start.AddSeconds(25),
            CancellationToken.None);
        var retried = registry.GetPlayerOnlineDurationEcsDiagnostics(
            socket.Session);
        Check.Equal(
            TimeSpan.FromSeconds(25).Ticks,
            retried.ProgressionElapsedTicks,
            "successful retry includes the uncommitted online tail");

        _ = await registry.AdvanceZodiacEnergyAccrualOnceAsync(
            Start.AddSeconds(10),
            CancellationToken.None);
        store.FailZodiacSave = true;
        _ = await registry.AdvanceZodiacEnergyAccrualOnceAsync(
            Start.AddSeconds(20),
            CancellationToken.None);
        var zodiacFailed =
            registry.GetPlayerOnlineDurationEcsDiagnostics(
                socket.Session);
        Check.Equal(
            TimeSpan.FromSeconds(10).Ticks,
            zodiacFailed.ZodiacElapsedTicks,
            "failed Zodiac save does not advance ECS watermark");
        store.FailZodiacSave = false;
        _ = await registry.AdvanceZodiacEnergyAccrualOnceAsync(
            Start.AddSeconds(25),
            CancellationToken.None);
        var zodiacRetried =
            registry.GetPlayerOnlineDurationEcsDiagnostics(
                socket.Session);
        Check.Equal(
            TimeSpan.FromSeconds(25).Ticks,
            zodiacRetried.ZodiacElapsedTicks,
            "Zodiac retry emits the committed full interval");

        await registry.FinishProgressionBoostOnlineSessionAsync(
            socket.Session,
            Start.AddSeconds(30),
            CancellationToken.None);
        await registry.FinishZodiacOnlineSessionAsync(
            socket.Session,
            Start.AddSeconds(30),
            CancellationToken.None);

        var replacement = CreateCharacter();
        replacement.Id++;
        replacement.Name = "RuntimeReplacementHero";
        registry.JoinMap(
            socket.Session,
            replacement.AccountId,
            replacement,
            WorldObjectIds.ForPlayer(replacement.Id),
            joinedAt: Start.AddSeconds(40));
        _ = await registry.GetExperienceBoostStateAsync(
            socket.Session,
            replacement.AccountId,
            replacement.Id,
            replacement.Camp,
            replacement.CurrentMap,
            Start.AddSeconds(45),
            CancellationToken.None);
        var swapped = registry.GetPlayerOnlineDurationEcsDiagnostics(
            socket.Session);
        Check.Equal(
            TimeSpan.FromSeconds(5).Ticks,
            swapped.ProgressionElapsedTicks,
            "same-session character swap resets ECS online diagnostics");
        Check.Equal(
            0L,
            swapped.ZodiacElapsedTicks,
            "character swap cannot retain prior-character Zodiac time");
        registry.Remove(socket.Session);

        await using var reconnect =
            await RuntimePolicySessionSocket.CreateAsync();
        var reconnectAt = Start.AddDays(30);
        registry.JoinMap(
            reconnect.Session,
            character.AccountId,
            character,
            WorldObjectIds.ForPlayer(character.Id),
            joinedAt: reconnectAt);
        _ = await registry.GetExperienceBoostStateAsync(
            reconnect.Session,
            character.AccountId,
            character.Id,
            character.Camp,
            character.CurrentMap,
            reconnectAt.AddSeconds(5),
            CancellationToken.None);
        var lastInterval = store.ProgressionIntervals.Last();
        Check.Equal(
            TimeSpan.FromSeconds(5),
            lastInterval.Until - lastInterval.From,
            "offline month is excluded from online-only duration");
        registry.Remove(reconnect.Session);
    }

    private static GameSessionRegistry CreateRegistry(
        IGameStore? store) =>
        new(
            store,
            null,
            MonsterRuntimeMode.Legacy,
            PlayerRuntimeMode.Ecs);

    private static GameCharacter CreateCharacter() =>
        new()
        {
            Id = 731,
            AccountId = 17,
            Name = "RuntimeCutoverHero",
            CreatedUtc = Start.UtcDateTime,
            Camp = GameDefaults.SpartaCamp,
            CurrentMap = GameDefaults.SpartaCapitalMap,
            Profession = 0,
            Level = 4,
            CurrentHp = 1_000,
            MaxHp = 1_500,
            CurrentMp = 9,
            MaxMp = 177,
            CalculatedStats = new CharacterStats
            {
                HpRecovery = 10,
                MpRecovery = 5
            }
        };

    private static ushort ReadUInt16(byte[] packet, int offset) =>
        BinaryPrimitives.ReadUInt16LittleEndian(
            packet.AsSpan(offset, sizeof(ushort)));

    private static int ReadInt32(byte[] packet, int offset) =>
        BinaryPrimitives.ReadInt32LittleEndian(
            packet.AsSpan(offset, sizeof(int)));

    private static float ReadSingle(byte[] packet, int offset) =>
        BitConverter.Int32BitsToSingle(ReadInt32(packet, offset));

    private sealed class RuntimePolicyStore : GameStoreTestStub
    {
        public bool FailVitalsSave { get; set; }
        public bool FailProgressionSave { get; set; }
        public bool FailZodiacSave { get; set; }
        public int VitalsSaveAttempts { get; private set; }
        public List<(DateTimeOffset From, DateTimeOffset Until)>
            ProgressionIntervals { get; } = [];

        public override Task SaveCharacterVitalsAsync(
            int accountId,
            int characterId,
            int currentHp,
            int currentMp,
            long vitalsRevision,
            CancellationToken cancellationToken = default)
        {
            VitalsSaveAttempts++;
            return FailVitalsSave
                ? Task.FromException(
                    new InvalidOperationException("expected vitals failure"))
                : Task.CompletedTask;
        }

        public override Task ConsumeCharacterBoostOnlineTimeAsync(
            int accountId,
            int characterId,
            DateTimeOffset onlineFrom,
            DateTimeOffset onlineUntil,
            CancellationToken cancellationToken = default)
        {
            if (FailProgressionSave)
            {
                throw new InvalidOperationException(
                    "expected progression failure");
            }

            ProgressionIntervals.Add((onlineFrom, onlineUntil));
            return Task.CompletedTask;
        }

        public override Task<ExperienceBoostState> GetExperienceBoostStateAsync(
            int accountId,
            int characterId,
            byte camp,
            byte mapId,
            DateTimeOffset now,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(ExperienceBoostState.Empty);

        public override Task<ZodiacEnergyAccrualResult?>
            ApplyZodiacOnlineTimeAsync(
                int accountId,
                int characterId,
                DateTimeOffset onlineFrom,
                DateTimeOffset onlineUntil,
                ZodiacEnergyPolicy policy,
                CancellationToken cancellationToken = default)
        {
            if (FailZodiacSave)
            {
                throw new InvalidOperationException(
                    "expected Zodiac failure");
            }

            return Task.FromResult<ZodiacEnergyAccrualResult?>(
                new ZodiacEnergyAccrualResult(
                    GainedEnergyX100: 0,
                    CurrentEnergy: 0,
                    CurrentEnergyRemainderX100: 0,
                    DateOnly.FromDateTime(onlineUntil.UtcDateTime),
                    (onlineUntil - onlineFrom).Ticks,
                    onlineUntil,
                    LastCompensationDay: null,
                    CompensationApplied: false));
        }

    }
}
