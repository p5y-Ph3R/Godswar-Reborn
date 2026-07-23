using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Godswar.Server.Game;
using Godswar.Server.Networking;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class Program
{
    private static async Task CheckMonsterGenerationReconciliationAsync()
    {
        const int removeLength = 12;
        const int appearanceLength = 108;
        const uint monsterMaximumHealth = 237;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using var viewerOutbound = new TcpClient();
            var viewerAcceptTask = listener.AcceptTcpClientAsync(timeout.Token).AsTask();
            await viewerOutbound.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
            using var viewerInbound = await viewerAcceptTask;
            await using var viewerSession = new ClientSession(viewerOutbound);

            var viewerCharacter = CreateCharacter();
            viewerCharacter.CurrentMap = 0;
            viewerCharacter.PositionX = 100;
            viewerCharacter.PositionZ = 100;
            var monster = CreateCapturedMonster(
                10039,
                viewerCharacter.PositionX + 1,
                viewerCharacter.PositionZ + 1,
                "A_normal_stub_001",
                maximumHealth: monsterMaximumHealth);
            var initializedAt = DateTimeOffset.UtcNow;
            var registry = new GameSessionRegistry();
            registry.InitializeMapMonsters(
                viewerCharacter.CurrentMap,
                [monster],
                initializedAt);
            registry.JoinMap(
                viewerSession,
                viewerCharacter.AccountId,
                viewerCharacter,
                WorldObjectIds.ForPlayer(viewerCharacter.Id),
                worldReady: false);

            await using (var initialTransition =
                         await registry.BeginMonsterVisibilityTransitionAsync(
                             viewerSession,
                             viewerCharacter.CurrentMap,
                             viewerCharacter.PositionX,
                             viewerCharacter.PositionZ,
                             timeout.Token)
                         ?? throw new InvalidOperationException("initial generation transition was unavailable"))
            {
                Check.True(
                    initialTransition.Delta.Entering.Single() is
                    {
                        ObjectId: var objectId,
                        SpawnGeneration: 1
                    } && objectId == monster.ObjectId,
                    "non-ready viewer commits the first monster generation during bootstrap");
                initialTransition.Commit();
            }

            Check.True(
                registry.TryApplyMonsterDamage(
                    viewerCharacter.CurrentMap,
                    monster.ObjectId,
                    damage: 37,
                    attackerCharacterId: viewerCharacter.Id,
                    out _),
                "bootstrap-gap fixture damages generation one");
            var retirementAt = initializedAt + MonsterMapRuntime.TickInterval;
            await registry.AdvanceMonsterWorldOnceAsync(retirementAt, timeout.Token);
            await registry.AdvanceMonsterWorldOnceAsync(
                retirementAt + MonsterMapRuntime.TickInterval,
                timeout.Token);
            Check.Equal(
                0,
                viewerInbound.Available,
                "non-ready viewer receives neither retirement nor respawn ticks");
            Check.True(
                registry.TryGetMonsterSnapshot(
                    viewerCharacter.CurrentMap,
                    monster.ObjectId,
                    out var replacement) &&
                replacement.SpawnGeneration == 2 &&
                replacement.CurrentHealth == monsterMaximumHealth &&
                registry.IsMonsterVisibleTo(viewerSession, monster.ObjectId),
                "viewer still holds generation one while runtime has reused the object ID for generation two");
            Check.True(
                registry.IsMonsterVisibleTo(
                    viewerSession,
                    monster.ObjectId,
                    spawnGeneration: 1) &&
                !registry.IsMonsterVisibleTo(
                    viewerSession,
                    monster.ObjectId,
                    replacement.SpawnGeneration),
                "viewer visibility remains tied to the stale generation until reconciliation");
            Check.True(
                !registry.TryApplyMonsterDamage(
                    viewerCharacter.CurrentMap,
                    monster.ObjectId,
                    damage: 10,
                    attackerCharacterId: viewerCharacter.Id,
                    expectedSpawnGeneration: 1,
                    out _),
                "stale generation-one attack cannot damage the unseen replacement");
            Check.True(
                !registry.TryApplyMonsterStun(
                    viewerCharacter.CurrentMap,
                    monster.ObjectId,
                    viewerCharacter.Id,
                    TimeSpan.FromSeconds(1),
                    expectedSpawnGeneration: 1,
                    now: retirementAt + (MonsterMapRuntime.TickInterval * 2),
                    out _),
                "stale generation-one control cannot stun the unseen replacement");
            Check.True(
                registry.TryGetMonsterSnapshot(
                    viewerCharacter.CurrentMap,
                    monster.ObjectId,
                    out var untouchedReplacement) &&
                untouchedReplacement.SpawnGeneration == 2 &&
                untouchedReplacement.CurrentHealth == monsterMaximumHealth &&
                !untouchedReplacement.IsStunned,
                "generation guard preserves replacement health and control state");

            Check.True(
                registry.TryMarkWorldReady(
                    viewerSession,
                    new Dictionary<uint, long>(),
                    out var unseenPlayers) &&
                unseenPlayers.Count == 0,
                "bootstrap-gap viewer resumes world delivery");

            await using (var reconcileTransition =
                         await registry.BeginMonsterVisibilityTransitionAsync(
                             viewerSession,
                             viewerCharacter.CurrentMap,
                             viewerCharacter.PositionX,
                             viewerCharacter.PositionZ,
                             timeout.Token)
                         ?? throw new InvalidOperationException("generation reconciliation was unavailable"))
            {
                Check.True(
                    reconcileTransition.Delta.Leaving.SequenceEqual([monster.ObjectId]) &&
                    reconcileTransition.Delta.Entering.Single() is
                    {
                        ObjectId: var objectId,
                        SpawnGeneration: 2
                    } && objectId == monster.ObjectId,
                    "generation mismatch is both a removal and a fresh entry despite stable object ID");

                var streamRead = ReadExactlyAsync(
                    viewerInbound.GetStream(),
                    removeLength + appearanceLength,
                    timeout.Token);
                await viewerSession.SendAsync(
                    PacketBuilder.RemoveWorldObjects(
                        reconcileTransition.Delta.Leaving.ToArray()),
                    timeout.Token,
                    "MonsterGenerationReconcileRemove");
                await viewerSession.SendAsync(
                    PacketBuilder.CapturedMonsterSpawns(
                        reconcileTransition.Delta.Entering
                            .Select(entry => entry.Appearance)
                            .ToArray()),
                    timeout.Token,
                    "MonsterGenerationReconcileSpawn",
                    framed: false);
                var stream = await streamRead;
                var receiveCipher = new PacketCipher();
                receiveCipher.Transform(stream);
                Check.Equal((ushort)10024, ReadUInt16(stream, 2), "generation reconcile removes stale entity first");
                Check.Equal(monster.ObjectId, ReadUInt32(stream, 8), "generation reconcile removal object id");
                Check.Equal(
                    (ushort)10020,
                    ReadUInt16(stream, removeLength + 2),
                    "generation reconcile publishes fresh appearance second");
                Check.Equal(
                    monster.ObjectId,
                    ReadUInt32(stream, removeLength + 8),
                    "generation reconcile appearance object id");
                Check.Equal(
                    monsterMaximumHealth,
                    ReadUInt32(stream, removeLength + 20),
                    "generation reconcile appearance has full current health");
                reconcileTransition.Commit();
            }

            await using (var stableTransition =
                         await registry.BeginMonsterVisibilityTransitionAsync(
                             viewerSession,
                             viewerCharacter.CurrentMap,
                             viewerCharacter.PositionX,
                             viewerCharacter.PositionZ,
                             timeout.Token)
                         ?? throw new InvalidOperationException("stable generation transition was unavailable"))
            {
                Check.True(
                    stableTransition.Delta.Entering.Count == 0 &&
                    stableTransition.Delta.Leaving.Count == 0,
                    "committing generation two prevents duplicate reconciliation");
                stableTransition.Commit();
            }

            registry.Remove(viewerSession);
        }
        finally
        {
            listener.Stop();
        }
    }
}
