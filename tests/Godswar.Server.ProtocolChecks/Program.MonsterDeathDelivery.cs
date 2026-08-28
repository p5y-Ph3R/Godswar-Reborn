using Godswar.Server.Game;
using Godswar.Server.Networking;
using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class Program
{
    internal static async Task CheckMonsterDeathDeliveryAsync()
    {
        var transport = new FactionCrierCaptureTransport();
        await using var session = new ClientSession(transport);
        var lateTransport = new FactionCrierCaptureTransport();
        await using var lateSession = new ClientSession(lateTransport);
        var character = CreateCharacter();
        character.CurrentMap = 0;
        character.PositionX = 100;
        character.PositionZ = 100;
        var monster = CreateCapturedMonster(
            10057,
            101,
            101,
            "A_normal_stub_001",
            maximumHealth: 237);
        var startedAt = new DateTimeOffset(
            2026,
            8,
            25,
            0,
            0,
            0,
            TimeSpan.Zero);
        var registry = new GameSessionRegistry();
        try
        {
            registry.InitializeMapMonsters(
                character.CurrentMap,
                [monster],
                startedAt);
            registry.JoinMap(
                session,
                character.AccountId,
                character,
                WorldObjectIds.ForPlayer(character.Id),
                worldReady: true);
            await using (var visibility =
                         await registry.BeginMonsterVisibilityTransitionAsync(
                             session,
                             character.CurrentMap,
                             character.PositionX,
                             character.PositionZ,
                             CancellationToken.None)
                         ?? throw new InvalidOperationException(
                             "death-delivery visibility was unavailable"))
            {
                Check.True(
                    visibility.Delta.Entering.Single().ObjectId ==
                        monster.ObjectId,
                    "death-delivery viewer initially sees the monster");
                visibility.Commit();
            }

            Check.True(
                registry.TryApplyMonsterDamage(
                    character.CurrentMap,
                    monster.ObjectId,
                    damage: 237,
                    attackerCharacterId: character.Id,
                    expectedSpawnGeneration: 1,
                    now: startedAt,
                    out var killed) &&
                killed.Killed,
                "death-delivery fixture kills the visible monster");

            await using (var existingAfterDeath =
                         await registry.BeginMonsterVisibilityTransitionAsync(
                             session,
                             character.CurrentMap,
                             character.PositionX,
                             character.PositionZ,
                             CancellationToken.None)
                         ?? throw new InvalidOperationException(
                             "existing death-delivery visibility was unavailable"))
            {
                Check.Equal(
                    0,
                    existingAfterDeath.Delta.Entering.Count,
                    "death does not replay an appearance to an existing viewer");
                Check.Equal(
                    0,
                    existingAfterDeath.Delta.Leaving.Count,
                    "death retains visibility until ordered damage and death delivery");
                existingAfterDeath.Commit();
            }

            var lateCharacter = CreateCharacter();
            lateCharacter.Id = character.Id + 1;
            lateCharacter.AccountId = character.AccountId + 1;
            lateCharacter.Name = "LateDeathViewer";
            lateCharacter.CurrentMap = character.CurrentMap;
            lateCharacter.PositionX = character.PositionX;
            lateCharacter.PositionZ = character.PositionZ;
            registry.JoinMap(
                lateSession,
                lateCharacter.AccountId,
                lateCharacter,
                WorldObjectIds.ForPlayer(lateCharacter.Id),
                worldReady: true);
            await using (var lateVisibility =
                         await registry.BeginMonsterVisibilityTransitionAsync(
                             lateSession,
                             lateCharacter.CurrentMap,
                             lateCharacter.PositionX,
                             lateCharacter.PositionZ,
                             CancellationToken.None)
                         ?? throw new InvalidOperationException(
                             "late death-delivery visibility was unavailable"))
            {
                Check.Equal(
                    0,
                    lateVisibility.Delta.Entering.Count,
                    "a late viewer never receives a dead monster appearance");
                lateVisibility.Commit();
            }

            var beforeDeath = transport.ReadLegacyPackets().Count;
            await registry.AdvanceMonsterWorldOnceAsync(
                startedAt + MonsterMapRuntime.TickInterval,
                CancellationToken.None);
            var deathPackets = transport.ReadLegacyPackets()
                .Skip(beforeDeath)
                .ToArray();
            Check.Equal(
                0,
                deathPackets.Length,
                "the world tick cannot retire a monster before lethal damage delivery");
            Check.Equal(
                0,
                lateTransport.ReadLegacyPackets().Count,
                "death does not publish an object to the late viewer");

            var delayedDamage = PacketBuilder.SkillDamage(
                WorldObjectIds.ForPlayer(character.Id),
                monster.ObjectId,
                resultFlags: 1,
                damage: 237,
                skillId: 2_000,
                targetX: monster.X,
                targetZ: monster.Z);
            Check.True(
                await registry.DeliverMonsterHealthPacketToViewerAsync(
                    session,
                    character.CurrentMap,
                    monster.ObjectId,
                    delayedDamage,
                     killed.HealthMutation!.Value,
                     CancellationToken.None,
                     "DelayedLethalDamage"),
                "a delayed lethal damage publication retains viewer authority");
            var lethalPackets = transport.ReadLegacyPackets()
                .Skip(beforeDeath)
                .ToArray();
            Check.True(
                lethalPackets.Length == 1 &&
                lethalPackets[0].SequenceEqual(delayedDamage),
                "lethal delivery does not mispublish the first-hit claim packet as a death state");

            var beforeRetirement = transport.ReadLegacyPackets().Count;
            await registry.AdvanceMonsterWorldOnceAsync(
                startedAt + MonsterMapRuntime.DefaultCorpseDespawnDelay,
                CancellationToken.None);
            var retirementPackets = transport.ReadLegacyPackets()
                .Skip(beforeRetirement)
                .ToArray();
            Check.True(
                retirementPackets.Single().SequenceEqual(
                    PacketBuilder.RemoveWorldObjects(monster.ObjectId)),
                "corpse retirement removes the world object");

            registry.Remove(lateSession);
            registry.Remove(session);
        }
        finally
        {
            await registry.DisposeAsync();
        }
    }
}
