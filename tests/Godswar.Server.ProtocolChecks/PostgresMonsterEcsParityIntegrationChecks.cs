using Godswar.Server.Game;
using Godswar.Server.Infrastructure.WorldContent;
using Godswar.Server.State;
using Godswar.Server.World.Systems.Monsters;

namespace Godswar.Server.ProtocolChecks;

/// <summary>
/// Exercises the ECS cutover against every captured production monster
/// definition, not only the compact synthetic fixtures used by unit parity
/// checks. The check is read-only apart from the store's normal forward
/// migration/seed initialization.
/// </summary>
internal static class PostgresMonsterEcsParityIntegrationChecks
{
    private const string ConnectionStringVariable =
        "GODSWAR_TEST_POSTGRES_CONNECTION_STRING";

    private static readonly DateTimeOffset Start =
        new(2026, 7, 23, 0, 0, 0, TimeSpan.Zero);

    public static async Task RunAsync()
    {
        var connectionString =
            Environment.GetEnvironmentVariable(ConnectionStringVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Console.WriteLine(
                $"SKIP PostgreSQL captured-monster ECS parity " +
                $"({ConnectionStringVariable} is not set)");
            return;
        }

        await using var store = new PostgresGameStore(connectionString);
        await store.EnsureSeedDataAsync();
        var worldContent =
            await PostgresWorldContentReaderLoader.LoadAsync(connectionString);
        var definitions = (await worldContent.ReadMapAsync(mapId: 0))
            .Monsters;
        Check.True(
            definitions.Count > 0,
            "captured production monster corpus is available");
        foreach (var definition in definitions)
        {
            definition.Validate(expectedMapId: 0);
        }

        var legacy = new MonsterMapRuntime(0, definitions, Start);
        var ecs = new EcsMonsterMapRuntime(0, definitions, Start);
        AssertEquivalent(legacy, ecs, "captured initial state");

        var now = Start;
        for (var tick = 0; tick < 180; tick++)
        {
            now += MonsterMapRuntime.TickInterval;
            AssertEquivalent(
                legacy.Advance(now),
                ecs.Advance(now),
                $"captured patrol tick {tick}");
            AssertEquivalent(
                legacy,
                ecs,
                $"captured patrol snapshot {tick}");
        }

        var damaged = definitions[0];
        var target = new MonsterCombatTarget(
            CharacterId: 731,
            X: damaged.AppearanceX + 10f,
            Z: damaged.AppearanceZ,
            IsAlive: true);
        Check.Equal(
            legacy.TryApplyDamage(
                damaged.ObjectId,
                damage: 1,
                attackerCharacterId: target.CharacterId,
                now,
                out var legacyDamage),
            ecs.TryApplyDamage(
                damaged.ObjectId,
                damage: 1,
                attackerCharacterId: target.CharacterId,
                now,
                out var ecsDamage),
            "captured damage acceptance parity");
        Check.True(
            legacyDamage == ecsDamage,
            "captured damage result parity");

        for (var tick = 0; tick < 40; tick++)
        {
            now += MonsterMapRuntime.TickInterval;
            AssertEquivalent(
                legacy.Advance(now, [target]),
                ecs.Advance(now, [target]),
                $"captured combat tick {tick}");
            AssertEquivalent(
                legacy,
                ecs,
                $"captured combat snapshot {tick}");
        }

        var killed = definitions.Count > 1 ? definitions[1] : definitions[0];
        Check.Equal(
            legacy.TryApplyDamage(
                killed.ObjectId,
                uint.MaxValue,
                attackerCharacterId: target.CharacterId,
                now,
                out var legacyDeath),
            ecs.TryApplyDamage(
                killed.ObjectId,
                uint.MaxValue,
                attackerCharacterId: target.CharacterId,
                now,
                out var ecsDeath),
            "captured lethal-damage acceptance parity");
        Check.True(
            legacyDeath == ecsDeath,
            "captured lethal-damage result parity");
        AssertEquivalent(
            legacy.Advance(now, [target]),
            ecs.Advance(now, [target]),
            "captured death publication");
        AssertEquivalent(
            legacy.Advance(
                now + MonsterMapRuntime.DefaultCorpseDespawnDelay,
                [target]),
            ecs.Advance(
                now + MonsterMapRuntime.DefaultCorpseDespawnDelay,
                [target]),
            "captured corpse despawn");
        AssertEquivalent(
            legacy.Advance(
                now + MonsterMapRuntime.DefaultRespawnDelay,
                [target]),
            ecs.Advance(
                now + MonsterMapRuntime.DefaultRespawnDelay,
                [target]),
            "captured full-health respawn");
        AssertEquivalent(legacy, ecs, "captured final state");
    }

    private static void AssertEquivalent(
        IMonsterMapRuntime legacy,
        IMonsterMapRuntime ecs,
        string scope)
    {
        var legacySnapshots = legacy.Snapshot();
        var ecsSnapshots = ecs.Snapshot();
        Check.Equal(
            legacySnapshots.Count,
            ecsSnapshots.Count,
            $"{scope} count");
        for (var index = 0; index < legacySnapshots.Count; index++)
        {
            Check.True(
                legacySnapshots[index] == ecsSnapshots[index],
                $"{scope} object {legacySnapshots[index].ObjectId}");
        }
    }

    private static void AssertEquivalent(
        MonsterRuntimeTick legacy,
        MonsterRuntimeTick ecs,
        string scope)
    {
        Check.Equal(
            legacy.PositionsChanged,
            ecs.PositionsChanged,
            $"{scope} position flag");
        Check.Equal(
            legacy.Updates.Count,
            ecs.Updates.Count,
            $"{scope} update count");
        for (var index = 0; index < legacy.Updates.Count; index++)
        {
            Check.True(
                legacy.Updates[index] == ecs.Updates[index],
                $"{scope} update {index}");
        }
    }
}
