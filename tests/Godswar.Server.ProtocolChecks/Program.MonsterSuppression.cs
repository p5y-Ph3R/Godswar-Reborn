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
    private static async Task CheckMonsterOldGenerationEventSuppressionAsync()
    {
        const uint maximumHealth = 237;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using var selfOutbound = new TcpClient();
            var selfAcceptTask = listener.AcceptTcpClientAsync(timeout.Token).AsTask();
            await selfOutbound.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
            using var selfInbound = await selfAcceptTask;
            await using var selfSession =
                new ClientSession(new RawTcpLegacyTransport(selfOutbound));

            using var worldOutbound = new TcpClient();
            var worldAcceptTask = listener.AcceptTcpClientAsync(timeout.Token).AsTask();
            await worldOutbound.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
            using var worldInbound = await worldAcceptTask;
            await using var worldSession =
                new ClientSession(new RawTcpLegacyTransport(worldOutbound));

            var selfCharacter = CreateCharacter();
            selfCharacter.CurrentMap = 0;
            selfCharacter.PositionX = 100;
            selfCharacter.PositionZ = 100;
            var worldCharacter = CreateCharacter();
            worldCharacter.Id += 1;
            worldCharacter.AccountId += 1;
            worldCharacter.Name = "GenerationWorldViewer";
            worldCharacter.CurrentMap = 0;
            worldCharacter.PositionX = 102;
            worldCharacter.PositionZ = 100;
            var monster = CreateCapturedMonster(
                10046,
                101,
                101,
                "A_normal_stub_001",
                maximumHealth: maximumHealth);
            var start = new DateTimeOffset(2026, 5, 12, 19, 0, 0, TimeSpan.FromHours(12));
            var registry = new GameSessionRegistry();
            registry.InitializeMapMonsters(selfCharacter.CurrentMap, [monster], start);
            registry.JoinMap(
                selfSession,
                selfCharacter.AccountId,
                selfCharacter,
                WorldObjectIds.ForPlayer(selfCharacter.Id),
                worldReady: false);
            registry.JoinMap(
                worldSession,
                worldCharacter.AccountId,
                worldCharacter,
                WorldObjectIds.ForPlayer(worldCharacter.Id),
                worldReady: false);

            foreach (var (session, character) in new[]
                     {
                         (selfSession, selfCharacter),
                         (worldSession, worldCharacter)
                     })
            {
                await using var initialTransition =
                    await registry.BeginMonsterVisibilityTransitionAsync(
                        session,
                        character.CurrentMap,
                        character.PositionX,
                        character.PositionZ,
                        timeout.Token)
                    ?? throw new InvalidOperationException("old-generation initial transition was unavailable");
                Check.True(
                    initialTransition.Delta.Entering.Single().SpawnGeneration == 1,
                    "old-generation viewer initially commits generation one");
                initialTransition.Commit();
            }

            Check.True(
                registry.TryApplyMonsterDamage(
                    selfCharacter.CurrentMap,
                    monster.ObjectId,
                    maximumHealth,
                    attackerCharacterId: null,
                    expectedSpawnGeneration: 1,
                    now: start,
                    out var lethalDamage) &&
                lethalDamage.Killed,
                "old-generation event fixture kills generation one");
            await registry.AdvanceMonsterWorldOnceAsync(
                start + TimeSpan.FromSeconds(11),
                timeout.Token);
            await registry.AdvanceMonsterWorldOnceAsync(
                start + TimeSpan.FromSeconds(11) + MonsterMapRuntime.TickInterval,
                timeout.Token);
            await registry.AdvanceMonsterWorldOnceAsync(
                start + TimeSpan.FromSeconds(11) + (MonsterMapRuntime.TickInterval * 2),
                timeout.Token);
            Check.True(
                registry.TryGetMonsterSnapshot(
                    selfCharacter.CurrentMap,
                    monster.ObjectId,
                    out var replacement) &&
                replacement.SpawnGeneration == 2 &&
                replacement.CurrentHealth == maximumHealth,
                "old-generation packet fixture has a full-health replacement");

            Check.True(
                registry.TryMarkWorldReady(
                    selfSession,
                    new Dictionary<uint, long>(),
                    out var selfUnseen) &&
                selfUnseen.Count == 0,
                "self viewer activates before old-generation packet checks");
            var worldKnownRevisions = new Dictionary<uint, long>();
            while (!registry.TryMarkWorldReady(
                       worldSession,
                       worldKnownRevisions,
                       out var worldUnseen))
            {
                Check.True(worldUnseen.Count > 0, "world viewer activation has a resolvable player delta");
                foreach (var unseen in worldUnseen)
                {
                    worldKnownRevisions[unseen.ObjectId] = unseen.WorldRevision;
                }
            }

            foreach (var (session, character) in new[]
                     {
                         (selfSession, selfCharacter),
                         (worldSession, worldCharacter)
                     })
            {
                await using var replacementTransition =
                    await registry.BeginMonsterVisibilityTransitionAsync(
                        session,
                        character.CurrentMap,
                        character.PositionX,
                        character.PositionZ,
                        timeout.Token)
                    ?? throw new InvalidOperationException("replacement transition was unavailable");
                Check.True(
                    replacementTransition.Delta.Leaving.SequenceEqual([monster.ObjectId]) &&
                    replacementTransition.Delta.Entering.Single().SpawnGeneration == 2,
                    "viewer replaces generation one before delayed packets arrive");
                replacementTransition.Commit();
            }

            var oldGenerationPackets = new (string Label, byte[] Packet)[]
            {
                (
                    "SkillImpact",
                    PacketBuilder.SkillCastImpact(
                        0x1448u,
                        monster.ObjectId,
                        2000,
                        monster.X,
                        monster.Z)),
                (
                    "StunStatus",
                    PacketBuilder.WorldObjectStatusEffects(
                        monster.ObjectId,
                        [new ClientStatusEffect(4001, 1)])),
                (
                    "DeathProgression",
                    PacketBuilder.MonsterDeathReward(
                        monster.ObjectId,
                        0x1448u,
                        currentExperience: 80,
                        currentTalentExperience: 2,
                        currentTalentPoints: 0))
            };
            foreach (var (label, eventPacket) in oldGenerationPackets)
            {
                Check.Equal(
                    false,
                    await registry.DeliverMonsterPacketToViewerAsync(
                        selfSession,
                        selfCharacter.CurrentMap,
                        monster.ObjectId,
                        eventPacket,
                        expectedSpawnGeneration: 1,
                        timeout.Token,
                        $"DelayedOldGeneration{label}Self"),
                    $"delayed old-generation {label} is suppressed for self");
                Check.Equal(
                    0,
                    await registry.BroadcastToMonsterViewersAsync(
                        selfCharacter.CurrentMap,
                        monster.ObjectId,
                        eventPacket,
                        timeout.Token,
                        excludeSession: selfSession,
                        label: $"DelayedOldGeneration{label}World",
                        expectedSpawnGeneration: 1),
                    $"delayed old-generation {label} is suppressed for world viewers");
            }

            Check.Equal(0, selfInbound.Available, "self receives no generation-one event bytes on replacement");
            Check.Equal(0, worldInbound.Available, "world receives no generation-one event bytes on replacement");
            var replacementMarker = PacketBuilder.MonsterLifecycleMarker(monster.ObjectId);
            var selfRead = ReadExactlyAsync(
                selfInbound.GetStream(),
                replacementMarker.Length,
                timeout.Token);
            var worldRead = ReadExactlyAsync(
                worldInbound.GetStream(),
                replacementMarker.Length,
                timeout.Token);
            Check.Equal(
                true,
                await registry.DeliverMonsterPacketToViewerAsync(
                    selfSession,
                    selfCharacter.CurrentMap,
                    monster.ObjectId,
                    replacementMarker,
                    expectedSpawnGeneration: 2,
                    timeout.Token,
                    "CurrentGenerationMarkerSelf"),
                "current-generation ordinary self packet still delivers");
            Check.Equal(
                1,
                await registry.BroadcastToMonsterViewersAsync(
                    selfCharacter.CurrentMap,
                    monster.ObjectId,
                    replacementMarker,
                    timeout.Token,
                    excludeSession: selfSession,
                    label: "CurrentGenerationMarkerWorld",
                    expectedSpawnGeneration: 2),
                "current-generation ordinary world packet still delivers");
            var selfFrame = await selfRead;
            var selfCipher = new PacketCipher();
            selfCipher.Transform(selfFrame);
            var worldFrame = await worldRead;
            var worldCipher = new PacketCipher();
            worldCipher.Transform(worldFrame);
            Check.Equal((ushort)10023, ReadUInt16(selfFrame, 2), "current-generation self marker opcode");
            Check.Equal((ushort)10023, ReadUInt16(worldFrame, 2), "current-generation world marker opcode");

            registry.Remove(selfSession);
            registry.Remove(worldSession);
        }
        finally
        {
            listener.Stop();
        }
    }
}
