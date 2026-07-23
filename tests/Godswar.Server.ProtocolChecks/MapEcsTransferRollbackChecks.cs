using System.Buffers.Binary;
using System.Text;
using Godswar.Server.Game;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class MapEcsTransferRollbackChecks
{
    private const int AccountId = 91;
    private const int InvalidAccountId = 92;
    private const int CharacterId = 941;
    private const uint PlayerObjectId = 0x6601;
    private const uint MonsterObjectId = 10_901;
    private static readonly DateTimeOffset Start =
        new(2026, 7, 23, 13, 0, 0, TimeSpan.Zero);

    public static async Task RunAsync()
    {
        await CheckFailedTransferAsync(useJoinMap: false);
        await CheckFailedTransferAsync(useJoinMap: true);
    }

    private static async Task CheckFailedTransferAsync(
        bool useJoinMap)
    {
        await using var socket =
            await RuntimePolicySessionSocket.CreateAsync();
        var registry = new GameSessionRegistry(
            store: null,
            zodiacEnergyOptions: null,
            MonsterRuntimeMode.Ecs,
            PlayerRuntimeMode.Ecs);
        var character = CreateCharacter(
            mapId: 0,
            accountId: AccountId,
            x: 120f,
            z: -80f);
        var invalidMovedCharacter = CreateCharacter(
            mapId: 1,
            accountId: InvalidAccountId,
            x: -45f,
            z: 80f);
        registry.InitializeMapMonsters(
            mapId: 0,
            [CreateMonster(character.PositionX, character.PositionZ)],
            Start);
        registry.JoinMap(
            socket.Session,
            AccountId,
            character,
            PlayerObjectId,
            worldReady: true,
            joinedAt: Start);

        Check.True(
            registry.TryApplyMonsterDamage(
                mapId: 0,
                MonsterObjectId,
                damage: 1,
                attackerCharacterId: character.Id,
                now: Start,
                out _),
            $"{Describe(useJoinMap)} fixture establishes aggro");
        await using (var initial = await registry
                         .BeginMonsterVisibilityTransitionAsync(
                             socket.Session,
                             mapId: 0,
                             character.PositionX,
                             character.PositionZ,
                             CancellationToken.None))
        {
            Check.True(
                initial is not null,
                $"{Describe(useJoinMap)} fixture opens initial visibility");
            Check.Equal(
                1,
                initial!.Delta.Entering.Count,
                $"{Describe(useJoinMap)} initial monster enters once");
            Check.Equal(
                0,
                initial.Delta.Leaving.Count,
                $"{Describe(useJoinMap)} initial transition has no leaving monster");
            initial.Commit();
        }

        var before = registry
            .GetMapMonsterSnapshots(0)
            .Single();
        Check.True(
            registry.IsMonsterVisibleTo(
                socket.Session,
                MonsterObjectId),
            $"{Describe(useJoinMap)} fixture commits monster visibility");

        Check.Throws<InvalidOperationException>(
            () =>
            {
                if (useJoinMap)
                {
                    registry.JoinMap(
                        socket.Session,
                        AccountId,
                        invalidMovedCharacter,
                        PlayerObjectId,
                        worldReady: true,
                        joinedAt: Start.AddMinutes(1));
                }
                else
                {
                    registry.UpdateCharacter(
                        socket.Session,
                        invalidMovedCharacter);
                }
            },
            $"{Describe(useJoinMap)} rejects destination hydration");

        Check.Equal(
            1,
            registry.GetMapPopulation(0),
            $"{Describe(useJoinMap)} retains old-map population");
        Check.Equal(
            0,
            registry.GetMapPopulation(1),
            $"{Describe(useJoinMap)} leaves destination empty");
        var retained = registry.GetMapSessions(0).Single();
        Check.True(
            ReferenceEquals(character, retained.Character),
            $"{Describe(useJoinMap)} retains old authoritative character");
        Check.True(
            registry.IsMonsterVisibleTo(
                socket.Session,
                MonsterObjectId),
            $"{Describe(useJoinMap)} retains old viewer state");

        await using (var afterFailure = await registry
                         .BeginMonsterVisibilityTransitionAsync(
                             socket.Session,
                             mapId: 0,
                             character.PositionX,
                             character.PositionZ,
                             CancellationToken.None))
        {
            Check.True(
                afterFailure is not null,
                $"{Describe(useJoinMap)} opens post-failure visibility");
            Check.Equal(
                0,
                afterFailure!.Delta.Entering.Count,
                $"{Describe(useJoinMap)} emits no duplicate entering monster");
            Check.Equal(
                0,
                afterFailure.Delta.Leaving.Count,
                $"{Describe(useJoinMap)} emits no spurious leaving monster");
            afterFailure.Commit();
        }

        var after = registry
            .GetMapMonsterSnapshots(0)
            .Single();
        Check.Equal(
            before.ObjectId,
            after.ObjectId,
            $"{Describe(useJoinMap)} preserves monster identity");
        Check.Equal(
            before.SpawnGeneration,
            after.SpawnGeneration,
            $"{Describe(useJoinMap)} preserves spawn generation");
        Check.Equal(
            before.HealthRevision,
            after.HealthRevision,
            $"{Describe(useJoinMap)} preserves health revision");
        Check.Equal(
            before.CurrentHealth,
            after.CurrentHealth,
            $"{Describe(useJoinMap)} preserves monster health");
        Check.True(
            after.CombatPhase == before.CombatPhase,
            $"{Describe(useJoinMap)} preserves aggro combat phase");

        await registry.AdvanceMonsterWorldOnceAsync(
            Start,
            CancellationToken.None);
        Check.True(
            registry.GetMapMonsterSnapshots(0).Single().CombatPhase ==
                MonsterCombatPhase.Attacking,
            $"{Describe(useJoinMap)} retained aggro advances to attacking");
        Check.Equal(
            0,
            socket.Available,
            $"{Describe(useJoinMap)} failure emits no network side effects");
        registry.Remove(socket.Session);
    }

    private static string Describe(bool useJoinMap) =>
        useJoinMap
            ? "failed cross-map JoinMap"
            : "failed cross-map UpdateCharacter";

    private static GameCharacter CreateCharacter(
        byte mapId,
        int accountId,
        float x,
        float z) =>
        new()
        {
            Id = CharacterId,
            AccountId = accountId,
            Name = "TransferRollbackHero",
            CreatedUtc = Start.UtcDateTime,
            Camp = mapId == 0
                ? GameDefaults.SpartaCamp
                : GameDefaults.AthensCamp,
            CurrentMap = mapId,
            PositionX = x,
            PositionZ = z,
            Level = 20,
            CurrentHp = 2_000,
            MaxHp = 2_500,
            CurrentMp = 1_000,
            MaxMp = 1_500,
            Equipment = string.Empty,
            KitBag = string.Empty
        };

    private static CapturedMonsterSpawn CreateMonster(
        float x,
        float z)
    {
        const string templateKey = "TransferRollbackMonster";
        var packet = new byte[108];
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(0, 2),
            (ushort)packet.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(2, 2),
            10020);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(4, 4),
            0x00000212);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(8, 4),
            MonsterObjectId);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(12, 4),
            1);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(20, 4),
            237);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(24, 4),
            237);
        BinaryPrimitives.WriteSingleLittleEndian(
            packet.AsSpan(28, 4),
            x);
        BinaryPrimitives.WriteSingleLittleEndian(
            packet.AsSpan(32, 4),
            2f);
        BinaryPrimitives.WriteSingleLittleEndian(
            packet.AsSpan(36, 4),
            z);
        BinaryPrimitives.WriteSingleLittleEndian(
            packet.AsSpan(40, 4),
            1f);
        Encoding.ASCII.GetBytes(templateKey)
            .CopyTo(packet.AsSpan(44));
        return new CapturedMonsterSpawn(
            MapId: 0,
            SceneKey: "Sparta",
            templateKey,
            templateKey,
            MonsterObjectId,
            x,
            z,
            packet);
    }
}
