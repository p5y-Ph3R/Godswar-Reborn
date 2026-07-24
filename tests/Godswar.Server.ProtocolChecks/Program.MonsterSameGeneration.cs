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
    private static async Task CheckMonsterSameGenerationActivationRefreshAsync()
    {
        const int removeLength = 12;
        const int appearanceLength = 108;
        const uint monsterMaximumHealth = 237;
        const uint damage = 37;
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
            await using var viewerSession =
                new ClientSession(new RawTcpLegacyTransport(viewerOutbound));

            var viewerCharacter = CreateCharacter();
            viewerCharacter.CurrentMap = 0;
            viewerCharacter.PositionX = 100;
            viewerCharacter.PositionZ = 100;
            var monster = CreateCapturedMonster(
                10040,
                viewerCharacter.PositionX + 1,
                viewerCharacter.PositionZ + 1,
                "A_normal_stub_001",
                maximumHealth: monsterMaximumHealth);
            var registry = new GameSessionRegistry();
            registry.InitializeMapMonsters(
                viewerCharacter.CurrentMap,
                [monster],
                DateTimeOffset.UtcNow);
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
                         ?? throw new InvalidOperationException("initial health-gap transition was unavailable"))
            {
                Check.True(
                    initialTransition.Delta.Entering.Single() is
                    {
                        ObjectId: var objectId,
                        SpawnGeneration: 1,
                        CurrentHealth: monsterMaximumHealth
                    } && objectId == monster.ObjectId,
                    "non-ready viewer captures generation one at full health");
                initialTransition.Commit();
            }

            Check.True(
                registry.TryApplyMonsterDamage(
                    viewerCharacter.CurrentMap,
                    monster.ObjectId,
                    damage,
                    out var damageResult) &&
                damageResult.AfterHealth == monsterMaximumHealth - damage,
                "monster health changes while the bootstrap viewer is hidden");
            Check.True(
                registry.TryGetMonsterSnapshot(
                    viewerCharacter.CurrentMap,
                    monster.ObjectId,
                    out var damagedMonster) &&
                damagedMonster.SpawnGeneration == 1 &&
                damagedMonster.CurrentHealth == monsterMaximumHealth - damage,
                "bootstrap health drift remains within the already-committed generation");
            Check.Equal(
                0,
                viewerInbound.Available,
                "non-ready viewer receives no health-change broadcast");
            Check.True(
                registry.TryMarkWorldReady(
                    viewerSession,
                    new Dictionary<uint, long>(),
                    out var unseenPlayers) &&
                unseenPlayers.Count == 0,
                "same-generation fixture reaches the activation handoff");

            await using (var activationTransition =
                         await registry.BeginMonsterVisibilityTransitionAsync(
                             viewerSession,
                             viewerCharacter.CurrentMap,
                             viewerCharacter.PositionX,
                             viewerCharacter.PositionZ,
                             timeout.Token,
                             forceRefreshVisible: true)
                         ?? throw new InvalidOperationException("activation health refresh was unavailable"))
            {
                Check.True(
                    activationTransition.Delta.Leaving.SequenceEqual([monster.ObjectId]) &&
                    activationTransition.Delta.Entering.Single() is
                    {
                        ObjectId: var objectId,
                        SpawnGeneration: 1,
                        CurrentHealth: var currentHealth
                    } &&
                    objectId == monster.ObjectId &&
                    currentHealth == monsterMaximumHealth - damage,
                    "activation forcibly replaces a stale same-generation appearance");

                var streamRead = ReadExactlyAsync(
                    viewerInbound.GetStream(),
                    removeLength + appearanceLength,
                    timeout.Token);
                await viewerSession.SendAsync(
                    PacketBuilder.RemoveWorldObjects(
                        activationTransition.Delta.Leaving.ToArray()),
                    timeout.Token,
                    "MonsterActivationHealthRefreshRemove");
                await viewerSession.SendAsync(
                    PacketBuilder.CapturedMonsterSpawns(
                        activationTransition.Delta.Entering
                            .Select(entry => entry.Appearance)
                            .ToArray()),
                    timeout.Token,
                    "MonsterActivationHealthRefreshSpawn",
                    framed: false);
                var stream = await streamRead;
                var receiveCipher = new PacketCipher();
                receiveCipher.Transform(stream);
                Check.Equal(
                    (ushort)10024,
                    ReadUInt16(stream, 2),
                    "activation health refresh removes stale appearance first");
                Check.Equal(
                    (ushort)10020,
                    ReadUInt16(stream, removeLength + 2),
                    "activation health refresh sends a fresh appearance second");
                Check.Equal(
                    monsterMaximumHealth - damage,
                    ReadUInt32(stream, removeLength + 20),
                    "fresh activation appearance carries authoritative current health");
                Check.Equal(
                    monsterMaximumHealth,
                    ReadUInt32(stream, removeLength + 24),
                    "fresh activation appearance preserves maximum health");
                activationTransition.Commit();
            }

            await using (var stableTransition =
                         await registry.BeginMonsterVisibilityTransitionAsync(
                             viewerSession,
                             viewerCharacter.CurrentMap,
                             viewerCharacter.PositionX,
                             viewerCharacter.PositionZ,
                             timeout.Token)
                         ?? throw new InvalidOperationException("stable health transition was unavailable"))
            {
                Check.True(
                    stableTransition.Delta.Entering.Count == 0 &&
                    stableTransition.Delta.Leaving.Count == 0,
                    "forced activation refresh commits back to stable normal AOI tracking");
                stableTransition.Commit();
            }

            registry.Remove(viewerSession);
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task CheckMonsterEnteringViewerDamageLeaseAsync()
    {
        const int appearanceLength = 108;
        const uint monsterMaximumHealth = 237;
        const uint damage = 37;
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
            await using var viewerSession =
                new ClientSession(new RawTcpLegacyTransport(viewerOutbound));

            var viewerCharacter = CreateCharacter();
            viewerCharacter.CurrentMap = 0;
            viewerCharacter.PositionX = 100;
            viewerCharacter.PositionZ = 100;
            var monster = CreateCapturedMonster(
                10041,
                viewerCharacter.PositionX + 1,
                viewerCharacter.PositionZ + 1,
                "A_normal_stub_001",
                maximumHealth: monsterMaximumHealth);
            var registry = new GameSessionRegistry();
            registry.InitializeMapMonsters(
                viewerCharacter.CurrentMap,
                [monster],
                DateTimeOffset.UtcNow);
            registry.JoinMap(
                viewerSession,
                viewerCharacter.AccountId,
                viewerCharacter,
                WorldObjectIds.ForPlayer(viewerCharacter.Id));

            var enteringTransition =
                await registry.BeginMonsterVisibilityTransitionAsync(
                    viewerSession,
                    viewerCharacter.CurrentMap,
                    viewerCharacter.PositionX,
                    viewerCharacter.PositionZ,
                    timeout.Token)
                ?? throw new InvalidOperationException("entering-viewer transition was unavailable");
            try
            {
                var enteringMonster = enteringTransition.Delta.Entering.Single();
                Check.True(
                    enteringMonster.ObjectId == monster.ObjectId &&
                    enteringMonster.CurrentHealth == monsterMaximumHealth &&
                    !registry.IsMonsterVisibleTo(viewerSession, monster.ObjectId),
                    "entering snapshot is full health and remains uncommitted during its send lease");
                Check.True(
                    registry.TryApplyMonsterDamage(
                        viewerCharacter.CurrentMap,
                        monster.ObjectId,
                        damage,
                        out var damageResult) &&
                    damageResult.AfterHealth == monsterMaximumHealth - damage,
                    "another actor can damage the monster while the appearance send is in flight");

                var damagePacket = PacketBuilder.PhysicalDamage(
                    WorldObjectIds.ForPlayer(viewerCharacter.Id),
                    monster.X,
                    0f,
                    monster.Z,
                    monster.ObjectId,
                    damage,
                    result: 1);
                var broadcastTask = registry.BroadcastToMonsterViewersAsync(
                    viewerCharacter.CurrentMap,
                    monster.ObjectId,
                    damagePacket,
                    timeout.Token,
                    label: "MonsterEnteringViewerDamageRace",
                    healthMutation: damageResult.HealthMutation);
                Check.True(
                    !broadcastTask.IsCompleted,
                    "damage broadcast waits behind the entering viewer transition lease");

                var streamRead = ReadExactlyAsync(
                    viewerInbound.GetStream(),
                    appearanceLength + damagePacket.Length,
                    timeout.Token);
                await viewerSession.SendAsync(
                    PacketBuilder.CapturedMonsterSpawns(
                        [enteringMonster.Appearance]),
                    timeout.Token,
                    "MonsterEnteringViewerAppearance",
                    framed: false);
                enteringTransition.Commit();
                await enteringTransition.DisposeAsync();

                Check.Equal(
                    1,
                    await broadcastTask,
                    "damage broadcast re-checks committed visibility and reaches the entering viewer");
                var stream = await streamRead;
                var receiveCipher = new PacketCipher();
                receiveCipher.Transform(stream);
                Check.Equal(
                    (ushort)10020,
                    ReadUInt16(stream, 2),
                    "stale full-health appearance is delivered before its queued damage");
                Check.Equal(
                    monsterMaximumHealth,
                    ReadUInt32(stream, 20),
                    "race fixture appearance captured the pre-damage health");
                Check.Equal(
                    (ushort)10026,
                    ReadUInt16(stream, appearanceLength + 2),
                    "queued damage follows appearance commit");
                Check.Equal(
                    monster.ObjectId,
                    ReadUInt32(stream, appearanceLength + 20),
                    "queued damage targets the entering monster");
                Check.Equal(
                    damage,
                    ReadUInt32(stream, appearanceLength + 24),
                    "queued damage preserves its authoritative amount");
            }
            finally
            {
                await enteringTransition.DisposeAsync();
            }

            Check.True(
                registry.TryGetMonsterSnapshot(
                    viewerCharacter.CurrentMap,
                    monster.ObjectId,
                    out var damagedMonster) &&
                damagedMonster.CurrentHealth == monsterMaximumHealth - damage,
                "runtime health matches the appearance-then-damage delivery sequence");
            registry.Remove(viewerSession);
        }
        finally
        {
            listener.Stop();
        }
    }
}
