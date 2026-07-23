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
    private static async Task CheckMonsterHealthRevisionOrderingAsync()
    {
        const int appearanceLength = 108;
        const int removeLength = 12;
        const uint maximumHealth = 237;
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
                10042,
                viewerCharacter.PositionX + 1,
                viewerCharacter.PositionZ + 1,
                "A_normal_stub_001",
                maximumHealth: maximumHealth);
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

            Check.True(
                registry.TryApplyMonsterDamage(
                    viewerCharacter.CurrentMap,
                    monster.ObjectId,
                    damage: 37,
                    out var firstDamage) &&
                firstDamage.HealthMutation is
                {
                    BeforeHealthRevision: 0,
                    AfterHealthRevision: 1
                },
                "inverse race mutates health before the entering snapshot");
            var enteringTransition =
                await registry.BeginMonsterVisibilityTransitionAsync(
                    viewerSession,
                    viewerCharacter.CurrentMap,
                    viewerCharacter.PositionX,
                    viewerCharacter.PositionZ,
                    timeout.Token)
                ?? throw new InvalidOperationException("inverse-race transition was unavailable");
            var receiveCipher = new PacketCipher();
            try
            {
                var enteringMonster = enteringTransition.Delta.Entering.Single();
                Check.True(
                    enteringMonster.CurrentHealth == maximumHealth - 37 &&
                    enteringMonster.HealthRevision == 1,
                    "entering appearance captures the already-applied health revision");
                var firstPacket = PacketBuilder.PhysicalDamage(
                    WorldObjectIds.ForPlayer(viewerCharacter.Id),
                    monster.X,
                    0f,
                    monster.Z,
                    monster.ObjectId,
                    37,
                    result: 1);
                var firstBroadcast = registry.BroadcastToMonsterViewersAsync(
                    viewerCharacter.CurrentMap,
                    monster.ObjectId,
                    firstPacket,
                    timeout.Token,
                    label: "MonsterInverseDamageRace",
                    healthMutation: firstDamage.HealthMutation);
                Check.True(
                    !firstBroadcast.IsCompleted,
                    "inverse-race delta waits for the entering transition decision");

                var appearanceRead = ReadExactlyAsync(
                    viewerInbound.GetStream(),
                    appearanceLength,
                    timeout.Token);
                await viewerSession.SendAsync(
                    PacketBuilder.CapturedMonsterSpawns([enteringMonster.Appearance]),
                    timeout.Token,
                    "MonsterInverseDamageAppearance",
                    framed: false);
                enteringTransition.Commit();
                await enteringTransition.DisposeAsync();
                Check.Equal(
                    0,
                    await firstBroadcast,
                    "delta already represented by the committed appearance is suppressed");
                var appearance = await appearanceRead;
                receiveCipher.Transform(appearance);
                Check.Equal(
                    maximumHealth - 37,
                    ReadUInt32(appearance, 20),
                    "inverse-race viewer receives the reduced authoritative health once");
                Check.Equal(0, viewerInbound.Available, "suppressed inverse delta emits no trailing bytes");
            }
            finally
            {
                await enteringTransition.DisposeAsync();
            }

            Check.True(
                registry.TryApplyMonsterDamage(
                    viewerCharacter.CurrentMap,
                    monster.ObjectId,
                    damage: 10,
                    out var secondDamage) &&
                secondDamage.HealthMutation is { BeforeHealthRevision: 1, AfterHealthRevision: 2 },
                "next damage advances exactly one health revision");
            var secondPacket = PacketBuilder.PhysicalDamage(
                WorldObjectIds.ForPlayer(viewerCharacter.Id),
                monster.X,
                0f,
                monster.Z,
                monster.ObjectId,
                10,
                result: 1);
            var secondRead = ReadExactlyAsync(
                viewerInbound.GetStream(),
                secondPacket.Length,
                timeout.Token);
            Check.Equal(
                1,
                await registry.BroadcastToMonsterViewersAsync(
                    viewerCharacter.CurrentMap,
                    monster.ObjectId,
                    secondPacket,
                    timeout.Token,
                    label: "MonsterExactNextDamage",
                    healthMutation: secondDamage.HealthMutation),
                "exact-next health delta is delivered");
            var secondFrame = await secondRead;
            receiveCipher.Transform(secondFrame);
            Check.Equal((ushort)10026, ReadUInt16(secondFrame, 2), "exact-next damage opcode");
            Check.Equal(
                0,
                await registry.BroadcastToMonsterViewersAsync(
                    viewerCharacter.CurrentMap,
                    monster.ObjectId,
                    secondPacket,
                    timeout.Token,
                    label: "MonsterDuplicateDamage",
                    healthMutation: secondDamage.HealthMutation),
                "replayed damage revision is suppressed after successful delivery");

            var appliedSkippedDamage = registry.TryApplyMonsterDamage(
                viewerCharacter.CurrentMap,
                monster.ObjectId,
                damage: 5,
                out var skippedDamage);
            var appliedGapDamage = registry.TryApplyMonsterDamage(
                viewerCharacter.CurrentMap,
                monster.ObjectId,
                damage: 7,
                out var gapDamage);
            Check.True(
                appliedSkippedDamage &&
                appliedGapDamage &&
                skippedDamage.HealthMutation is { BeforeHealthRevision: 2, AfterHealthRevision: 3 } &&
                gapDamage.HealthMutation is { BeforeHealthRevision: 3, AfterHealthRevision: 4 },
                "gap fixture creates two ordered runtime revisions before delivery");
            var gapPacket = PacketBuilder.PhysicalDamage(
                WorldObjectIds.ForPlayer(viewerCharacter.Id),
                monster.X,
                0f,
                monster.Z,
                monster.ObjectId,
                7,
                result: 1);
            var reconciliationRead = ReadExactlyAsync(
                viewerInbound.GetStream(),
                removeLength + appearanceLength,
                timeout.Token);
            Check.Equal(
                1,
                await registry.BroadcastToMonsterViewersAsync(
                    viewerCharacter.CurrentMap,
                    monster.ObjectId,
                    gapPacket,
                    timeout.Token,
                    label: "MonsterHealthRevisionGap",
                    healthMutation: gapDamage.HealthMutation),
                "revision gap triggers authoritative viewer reconciliation");
            var reconciliation = await reconciliationRead;
            receiveCipher.Transform(reconciliation);
            Check.Equal((ushort)10024, ReadUInt16(reconciliation, 2), "gap reconciliation removes first");
            Check.Equal(
                (ushort)10020,
                ReadUInt16(reconciliation, removeLength + 2),
                "gap reconciliation respawns current appearance second");
            Check.Equal(
                maximumHealth - 37 - 10 - 5 - 7,
                ReadUInt32(reconciliation, removeLength + 20),
                "gap reconciliation carries current health through the latest revision");
            Check.Equal(
                0,
                await registry.BroadcastToMonsterViewersAsync(
                    viewerCharacter.CurrentMap,
                    monster.ObjectId,
                    PacketBuilder.PhysicalDamage(
                        WorldObjectIds.ForPlayer(viewerCharacter.Id),
                        monster.X,
                        0f,
                        monster.Z,
                        monster.ObjectId,
                        5,
                        result: 1),
                    timeout.Token,
                    label: "MonsterDelayedGapDamage",
                    healthMutation: skippedDamage.HealthMutation),
                "older delta is suppressed after authoritative gap reconciliation");

            registry.Remove(viewerSession);
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task CheckMonsterSelfViewerDamageOrderingAsync()
    {
        const int appearanceLength = 108;
        const int removeLength = 12;
        const uint maximumHealth = 237;
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

            var character = CreateCharacter();
            character.CurrentMap = 0;
            character.PositionX = 100;
            character.PositionZ = 100;
            var monster = CreateCapturedMonster(
                10045,
                character.PositionX + 1,
                character.PositionZ + 1,
                "A_normal_stub_001",
                maximumHealth: maximumHealth);
            var registry = new GameSessionRegistry();
            registry.InitializeMapMonsters(character.CurrentMap, [monster], DateTimeOffset.UtcNow);
            registry.JoinMap(
                viewerSession,
                character.AccountId,
                character,
                WorldObjectIds.ForPlayer(character.Id));
            var receiveCipher = new PacketCipher();

            await using (var initialTransition =
                         await registry.BeginMonsterVisibilityTransitionAsync(
                             viewerSession,
                             character.CurrentMap,
                             character.PositionX,
                             character.PositionZ,
                             timeout.Token)
                         ?? throw new InvalidOperationException("self-viewer initial transition was unavailable"))
            {
                var initialMonster = initialTransition.Delta.Entering.Single();
                var initialRead = ReadExactlyAsync(
                    viewerInbound.GetStream(),
                    appearanceLength,
                    timeout.Token);
                await viewerSession.SendAsync(
                    PacketBuilder.CapturedMonsterSpawns([initialMonster.Appearance]),
                    timeout.Token,
                    "SelfViewerInitialAppearance",
                    framed: false);
                initialTransition.Commit();
                var initialFrame = await initialRead;
                receiveCipher.Transform(initialFrame);
            }

            Check.True(
                registry.TryApplyMonsterDamage(
                    character.CurrentMap,
                    monster.ObjectId,
                    damage: 17,
                    attackerCharacterId: character.Id,
                    expectedSpawnGeneration: 1,
                    out var firstDamage),
                "self-viewer inverse fixture applies its first authoritative hit");
            var refreshTransition =
                await registry.BeginMonsterVisibilityTransitionAsync(
                    viewerSession,
                    character.CurrentMap,
                    character.PositionX,
                    character.PositionZ,
                    timeout.Token,
                    forceRefreshVisible: true)
                ?? throw new InvalidOperationException("self-viewer refresh transition was unavailable");
            try
            {
                var refreshedMonster = refreshTransition.Delta.Entering.Single();
                Check.True(
                    refreshedMonster.HealthRevision == 1 &&
                    refreshedMonster.CurrentHealth == maximumHealth - 17,
                    "self-viewer forced appearance already includes its first hit");
                var selfPacket = PacketBuilder.PhysicalDamage(
                    0x1448u,
                    0f,
                    0f,
                    0f,
                    monster.ObjectId,
                    17,
                    result: 3);
                var selfDelivery = registry.DeliverMonsterHealthPacketToViewerAsync(
                    viewerSession,
                    character.CurrentMap,
                    monster.ObjectId,
                    selfPacket,
                    firstDamage.HealthMutation!.Value,
                    timeout.Token,
                    "SelfViewerInverseDamage");
                Check.True(
                    !selfDelivery.IsCompleted,
                    "self damage waits behind its own forced appearance transition");

                var refreshRead = ReadExactlyAsync(
                    viewerInbound.GetStream(),
                    removeLength + appearanceLength,
                    timeout.Token);
                await viewerSession.SendAsync(
                    PacketBuilder.RemoveWorldObjects(monster.ObjectId),
                    timeout.Token,
                    "SelfViewerRefreshRemove");
                await viewerSession.SendAsync(
                    PacketBuilder.CapturedMonsterSpawns([refreshedMonster.Appearance]),
                    timeout.Token,
                    "SelfViewerRefreshAppearance",
                    framed: false);
                refreshTransition.Commit();
                await refreshTransition.DisposeAsync();
                Check.Equal(
                    false,
                    await selfDelivery,
                    "self delta already included by its appearance is suppressed");
                var refreshFrames = await refreshRead;
                receiveCipher.Transform(refreshFrames);
                Check.Equal((ushort)10024, ReadUInt16(refreshFrames, 2), "self refresh removes first");
                Check.Equal(
                    maximumHealth - 17,
                    ReadUInt32(refreshFrames, removeLength + 20),
                    "self refresh publishes reduced health exactly once");
                Check.Equal(0, viewerInbound.Available, "suppressed self delta emits no trailing bytes");
            }
            finally
            {
                await refreshTransition.DisposeAsync();
            }

            Check.True(
                registry.TryApplyMonsterDamage(
                    character.CurrentMap,
                    monster.ObjectId,
                    damage: 9,
                    attackerCharacterId: character.Id,
                    expectedSpawnGeneration: 1,
                    out var secondDamage),
                "self-viewer exact-next fixture applies a second hit");
            var secondPacket = PacketBuilder.PhysicalDamage(
                0x1448u,
                0f,
                0f,
                0f,
                monster.ObjectId,
                9,
                result: 3);
            var secondRead = ReadExactlyAsync(
                viewerInbound.GetStream(),
                secondPacket.Length,
                timeout.Token);
            Check.Equal(
                true,
                await registry.DeliverMonsterHealthPacketToViewerAsync(
                    viewerSession,
                    character.CurrentMap,
                    monster.ObjectId,
                    secondPacket,
                    secondDamage.HealthMutation!.Value,
                    timeout.Token,
                    "SelfViewerExactNextDamage"),
                "exact-next self damage is sent and advances its viewer stamp");
            var secondFrame = await secondRead;
            receiveCipher.Transform(secondFrame);
            Check.Equal((ushort)10026, ReadUInt16(secondFrame, 2), "self exact-next damage opcode");
            Check.Equal(
                false,
                await registry.DeliverMonsterHealthPacketToViewerAsync(
                    viewerSession,
                    character.CurrentMap,
                    monster.ObjectId,
                    secondPacket,
                    secondDamage.HealthMutation!.Value,
                    timeout.Token,
                    "SelfViewerDuplicateDamage"),
                "duplicate self damage revision is suppressed");

            registry.Remove(viewerSession);
        }
        finally
        {
            listener.Stop();
        }
    }
}
