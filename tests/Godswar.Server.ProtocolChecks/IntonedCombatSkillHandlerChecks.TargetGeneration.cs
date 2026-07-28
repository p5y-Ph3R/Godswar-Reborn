using System.Buffers.Binary;
using Godswar.Server.Game;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class IntonedCombatSkillHandlerChecks
{
    private static async Task
        CheckTargetGenerationRefreshInterruptionAsync(
            SkillCombatDefinition combat)
    {
        await using var fixture =
            await Fixture.CreateAsync("GenerationRefreshThunder");
        await fixture.BeginCastAsync();

        var killedAt = DateTimeOffset.UtcNow;
        Check.True(
            fixture.Registry.TryApplyMonsterDamage(
                fixture.Character.CurrentMap,
                MonsterObjectId,
                InitialMonsterHealth,
                fixture.Character.Id,
                expectedSpawnGeneration: 1,
                killedAt,
                out var killed) &&
            killed.Killed &&
            killed.Monster.SpawnGeneration == 1,
            "Thunder generation fixture retires its original target");

        await fixture.Registry.AdvanceMonsterWorldOnceAsync(
            killedAt + MonsterMapRuntime.DefaultRespawnDelay,
            CancellationToken.None);
        Check.Equal(
            0,
            fixture.Socket.Available,
            "Thunder generation fixture drains death before disposal");

        await fixture.Registry.AdvanceMonsterWorldOnceAsync(
            killedAt +
            MonsterMapRuntime.DefaultRespawnDelay +
            MonsterMapRuntime.TickInterval,
            CancellationToken.None);
        var despawn = await fixture.Socket.ReadPacketAsync(8);
        Check.Equal(
            (ushort)10023,
            ReadOpcode(despawn),
            "Thunder generation fixture publishes old-generation disposal");

        await fixture.Registry.AdvanceMonsterWorldOnceAsync(
            killedAt +
            MonsterMapRuntime.DefaultRespawnDelay +
            (MonsterMapRuntime.TickInterval * 2),
            CancellationToken.None);
        var respawnMarker = await fixture.Socket.ReadPacketAsync(8);
        var respawnAppearance =
            await fixture.Socket.ReadPacketAsync(108);
        Check.Equal(
            (ushort)10023,
            ReadOpcode(respawnMarker),
            "Thunder generation fixture publishes replacement marker");
        Check.Equal(
            (ushort)10020,
            ReadOpcode(respawnAppearance),
            "Thunder generation fixture publishes replacement appearance");
        Check.Equal(
            MonsterObjectId,
            BinaryPrimitives.ReadUInt32LittleEndian(
                respawnAppearance.AsSpan(8, 4)),
            "Thunder replacement reuses the original object ID");

        Check.True(
            fixture.Registry.TryGetMonsterSnapshot(
                fixture.Character.CurrentMap,
                MonsterObjectId,
                out var replacement) &&
            replacement.SpawnGeneration == 2 &&
            replacement.IsSpawned &&
            replacement.IsAlive &&
            replacement.CurrentHealth == InitialMonsterHealth,
            "Thunder target refresh creates a full-health second generation");
        Check.True(
            fixture.Registry.IsMonsterVisibleTo(
                fixture.Socket.Session,
                MonsterObjectId,
                spawnGeneration: 2),
            "Thunder viewer reconciles to the replacement generation");

        var interrupted = await fixture.Socket.ReadPacketAsync(8);
        Check.Equal(
            "0800BB2748140000",
            Convert.ToHexString(interrupted),
            "target generation refresh interrupts the original Thunder cast");
        await Task.Delay(50);
        Check.Equal(
            0,
            fixture.Socket.Available,
            "generation-interrupted Thunder emits no stale effect packets");
        Check.Equal(
            InitialMana,
            fixture.Character.CurrentMp,
            "generation-interrupted Thunder consumes no MP");
        Check.Equal(
            InitialMonsterHealth,
            fixture.CurrentMonsterHealth(),
            "generation-interrupted Thunder does not damage the replacement");
        Check.Equal(
            0,
            fixture.Store.VitalsWrites,
            "generation-interrupted Thunder persists no resource mutation");
        Check.True(
            combat.CastTime > TimeSpan.Zero,
            "generation-refresh regression covers an intoned skill");
    }
}
