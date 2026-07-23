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
    private static async Task CheckMonsterAreaDamageDeliveryAsync()
    {
        const int appearanceLength = 108;
        const int markerLength = 8;
        const int oneHitClusterLength = 29;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using var partialOutbound = new TcpClient();
            var partialAcceptTask = listener.AcceptTcpClientAsync(timeout.Token).AsTask();
            await partialOutbound.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
            using var partialInbound = await partialAcceptTask;
            await using var partialSession = new ClientSession(partialOutbound);

            using var farOutbound = new TcpClient();
            var farAcceptTask = listener.AcceptTcpClientAsync(timeout.Token).AsTask();
            await farOutbound.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
            using var farInbound = await farAcceptTask;
            await using var farSession = new ClientSession(farOutbound);

            var partialCharacter = CreateCharacter();
            partialCharacter.CurrentMap = 0;
            partialCharacter.PositionX = 70;
            partialCharacter.PositionZ = 100;
            var farCharacter = CreateCharacter();
            farCharacter.Id += 1;
            farCharacter.AccountId += 1;
            farCharacter.Name = "AreaFarViewer";
            farCharacter.CurrentMap = 0;
            farCharacter.PositionX = 500;
            farCharacter.PositionZ = 500;
            var firstMonster = CreateCapturedMonster(
                10043,
                100,
                100,
                "A_normal_stub_001");
            var secondMonster = CreateCapturedMonster(
                10044,
                164,
                100,
                "A_normal_stub_002");
            var registry = new GameSessionRegistry();
            registry.InitializeMapMonsters(
                partialCharacter.CurrentMap,
                [firstMonster, secondMonster],
                DateTimeOffset.UtcNow);
            registry.JoinMap(
                partialSession,
                partialCharacter.AccountId,
                partialCharacter,
                WorldObjectIds.ForPlayer(partialCharacter.Id));
            registry.JoinMap(
                farSession,
                farCharacter.AccountId,
                farCharacter,
                WorldObjectIds.ForPlayer(farCharacter.Id));

            var partialTransition =
                await registry.BeginMonsterVisibilityTransitionAsync(
                    partialSession,
                    partialCharacter.CurrentMap,
                    partialCharacter.PositionX,
                    partialCharacter.PositionZ,
                    timeout.Token)
                ?? throw new InvalidOperationException("partial AoE transition was unavailable");
            try
            {
                Check.True(
                    partialTransition.Delta.Entering.Select(monster => monster.ObjectId)
                        .SequenceEqual([firstMonster.ObjectId]),
                    "partial AoE viewer is entering only one of two hit monsters");
                await using (var farTransition =
                             await registry.BeginMonsterVisibilityTransitionAsync(
                                 farSession,
                                 farCharacter.CurrentMap,
                                 farCharacter.PositionX,
                                 farCharacter.PositionZ,
                                 timeout.Token)
                             ?? throw new InvalidOperationException("far AoE transition was unavailable"))
                {
                    Check.Equal(0, farTransition.Delta.Entering.Count, "far AoE viewer sees no hit monsters");
                    farTransition.Commit();
                }

                var appliedFirstDamage = registry.TryApplyMonsterDamage(
                    partialCharacter.CurrentMap,
                    firstMonster.ObjectId,
                    damage: 11,
                    out var firstDamage);
                var appliedSecondDamage = registry.TryApplyMonsterDamage(
                    partialCharacter.CurrentMap,
                    secondMonster.ObjectId,
                    damage: 13,
                    out var secondDamage);
                Check.True(
                    appliedFirstDamage && appliedSecondDamage,
                    "AoE fixture applies both authoritative monster hits");
                var visual = PacketBuilder.MonsterLifecycleMarker(0xABC001);
                var impact = PacketBuilder.MonsterLifecycleMarker(0xABC002);
                var areaBroadcast = registry.BroadcastMonsterAreaDamageToViewersAsync(
                    partialCharacter.CurrentMap,
                    visual,
                    impact,
                    WorldObjectIds.ForPlayer(partialCharacter.Id),
                    skillId: 2000,
                    [
                        new MonsterAreaDamageBroadcastHit(
                            firstDamage.HealthMutation!.Value,
                            11),
                        new MonsterAreaDamageBroadcastHit(
                            secondDamage.HealthMutation!.Value,
                            13)
                    ],
                    timeout.Token,
                    labelPrefix: "AreaDamageLeaseCheck");
                Check.True(
                    !areaBroadcast.IsCompleted,
                    "AoE delivery waits behind the partial viewer's entering appearance");

                var streamRead = ReadExactlyAsync(
                    partialInbound.GetStream(),
                    appearanceLength + markerLength + markerLength + oneHitClusterLength,
                    timeout.Token);
                var enteringMonster = partialTransition.Delta.Entering.Single();
                await partialSession.SendAsync(
                    PacketBuilder.CapturedMonsterSpawns([enteringMonster.Appearance]),
                    timeout.Token,
                    "AreaDamageEnteringAppearance",
                    framed: false);
                partialTransition.Commit();
                await partialTransition.DisposeAsync();
                Check.Equal(1, await areaBroadcast, "AoE reaches only the viewer of an eligible hit");

                var stream = await streamRead;
                var partialCipher = new PacketCipher();
                partialCipher.Transform(stream);
                var visualOffset = appearanceLength;
                var impactOffset = visualOffset + markerLength;
                var clusterOffset = impactOffset + markerLength;
                Check.Equal((ushort)10020, ReadUInt16(stream, 2), "AoE appearance precedes event packets");
                Check.Equal((ushort)10023, ReadUInt16(stream, visualOffset + 2), "AoE visual follows appearance");
                Check.Equal((ushort)10023, ReadUInt16(stream, impactOffset + 2), "AoE impact follows visual");
                Check.Equal((ushort)10047, ReadUInt16(stream, clusterOffset + 2), "filtered AoE cluster opcode");
                Check.Equal(1, ReadInt32(stream, clusterOffset + 8), "AoE cluster contains one visible hit");
                Check.Equal(
                    firstMonster.ObjectId,
                    ReadUInt32(stream, clusterOffset + 17),
                    "AoE cluster includes the visible monster");
                Check.Equal(
                    11u,
                    ReadUInt32(stream, clusterOffset + 25),
                    "AoE cluster preserves visible hit damage");
                Check.Equal(0, farInbound.Available, "far viewer receives no monster-linked AoE bytes");
                Check.Equal(
                    0,
                    await registry.BroadcastMonsterAreaDamageToViewersAsync(
                        partialCharacter.CurrentMap,
                        visual,
                        impact,
                        WorldObjectIds.ForPlayer(partialCharacter.Id),
                        skillId: 2000,
                        [
                            new MonsterAreaDamageBroadcastHit(
                                firstDamage.HealthMutation!.Value,
                                11),
                            new MonsterAreaDamageBroadcastHit(
                                secondDamage.HealthMutation!.Value,
                                13)
                        ],
                        timeout.Token,
                        labelPrefix: "AreaDamageReplayCheck"),
                    "AoE replay suppresses already-applied and invisible hits");

                Check.True(
                    registry.TryApplyMonsterDamage(
                        partialCharacter.CurrentMap,
                        firstMonster.ObjectId,
                        damage: 7,
                        attackerCharacterId: partialCharacter.Id,
                        expectedSpawnGeneration: 1,
                        out var selfAreaDamage),
                    "self AoE fixture applies an exact-next visible hit");
                var selfAreaRead = ReadExactlyAsync(
                    partialInbound.GetStream(),
                    oneHitClusterLength,
                    timeout.Token);
                Check.Equal(
                    true,
                    await registry.DeliverMonsterAreaDamageToViewerAsync(
                        partialSession,
                        partialCharacter.CurrentMap,
                        0x1448u,
                        skillId: 2000,
                        [new MonsterAreaDamageBroadcastHit(
                            selfAreaDamage.HealthMutation!.Value,
                            7)],
                        timeout.Token,
                        "AreaDamageSelfCheck"),
                    "self AoE cluster uses the same revision-aware viewer lease");
                var selfAreaFrame = await selfAreaRead;
                partialCipher.Transform(selfAreaFrame);
                Check.Equal((ushort)10047, ReadUInt16(selfAreaFrame, 2), "self AoE cluster opcode");
                Check.Equal(1, ReadInt32(selfAreaFrame, 8), "self AoE cluster hit count");
                Check.Equal(
                    false,
                    await registry.DeliverMonsterAreaDamageToViewerAsync(
                        partialSession,
                        partialCharacter.CurrentMap,
                        0x1448u,
                        skillId: 2000,
                        [new MonsterAreaDamageBroadcastHit(
                            selfAreaDamage.HealthMutation!.Value,
                            7)],
                        timeout.Token,
                        "AreaDamageSelfReplay"),
                    "self AoE replay is suppressed after stamp advancement");

                var partialZeroRead = ReadExactlyAsync(
                    partialInbound.GetStream(),
                    markerLength * 2,
                    timeout.Token);
                var farZeroRead = ReadExactlyAsync(
                    farInbound.GetStream(),
                    markerLength * 2,
                    timeout.Token);
                Check.Equal(
                    2,
                    await registry.BroadcastMonsterAreaDamageToViewersAsync(
                        partialCharacter.CurrentMap,
                        visual,
                        impact,
                        WorldObjectIds.ForPlayer(partialCharacter.Id),
                        skillId: 2000,
                        [],
                        timeout.Token,
                        labelPrefix: "AreaDamageZeroHitCheck"),
                    "zero-hit AoE preserves map-wide cast visibility");
                var partialZero = await partialZeroRead;
                partialCipher.Transform(partialZero);
                var farZero = await farZeroRead;
                var farCipher = new PacketCipher();
                farCipher.Transform(farZero);
                Check.Equal((ushort)10023, ReadUInt16(partialZero, 2), "partial viewer zero-hit visual");
                Check.Equal((ushort)10023, ReadUInt16(partialZero, markerLength + 2), "partial viewer zero-hit impact");
                Check.Equal((ushort)10023, ReadUInt16(farZero, 2), "far viewer zero-hit visual");
                Check.Equal((ushort)10023, ReadUInt16(farZero, markerLength + 2), "far viewer zero-hit impact");
            }
            finally
            {
                await partialTransition.DisposeAsync();
            }

            registry.Remove(partialSession);
            registry.Remove(farSession);
        }
        finally
        {
            listener.Stop();
        }
    }
}
