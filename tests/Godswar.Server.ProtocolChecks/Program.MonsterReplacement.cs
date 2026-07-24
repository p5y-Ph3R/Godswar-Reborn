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
    private static async Task CheckMonsterReturnViewerPacketOrderAsync()
    {
        const int movementStartLength = 40;
        const int movementEndLength = 34;
        const int lifecycleMarkerLength = 8;
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
            await using var viewerSession =
                new ClientSession(new RawTcpLegacyTransport(viewerOutbound));

            using var targetOutbound = new TcpClient();
            var targetAcceptTask = listener.AcceptTcpClientAsync(timeout.Token).AsTask();
            await targetOutbound.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
            using var targetInbound = await targetAcceptTask;
            await using var targetSession =
                new ClientSession(new RawTcpLegacyTransport(targetOutbound));

            var viewerCharacter = CreateCharacter();
            viewerCharacter.CurrentMap = 0;
            viewerCharacter.PositionX = 100;
            viewerCharacter.PositionZ = 100;
            var targetCharacter = CreateCharacter();
            targetCharacter.Id += 1;
            targetCharacter.AccountId += 1;
            targetCharacter.Name = "ReturnTarget";
            targetCharacter.CurrentMap = 0;

            var monster = CreateCapturedMonster(
                10038,
                viewerCharacter.PositionX + 1,
                viewerCharacter.PositionZ + 1,
                "A_normal_stub_001",
                maximumHealth: monsterMaximumHealth);
            targetCharacter.PositionX = monster.X + 20f;
            targetCharacter.PositionZ = monster.Z;
            var initializedAt = DateTimeOffset.UtcNow;
            var registry = new GameSessionRegistry();
            registry.InitializeMapMonsters(viewerCharacter.CurrentMap, [monster], initializedAt);
            registry.JoinMap(
                viewerSession,
                viewerCharacter.AccountId,
                viewerCharacter,
                WorldObjectIds.ForPlayer(viewerCharacter.Id));
            registry.JoinMap(
                targetSession,
                targetCharacter.AccountId,
                targetCharacter,
                WorldObjectIds.ForPlayer(targetCharacter.Id));

            await using (var transition =
                         await registry.BeginMonsterVisibilityTransitionAsync(
                             viewerSession,
                             viewerCharacter.CurrentMap,
                             viewerCharacter.PositionX,
                             viewerCharacter.PositionZ,
                             timeout.Token)
                         ?? throw new InvalidOperationException("replacement viewer transition was unavailable"))
            {
                Check.True(
                    transition.Delta.Entering.Select(entry => entry.ObjectId).SequenceEqual([monster.ObjectId]),
                    "replacement viewer initially enters the monster AOI");
                transition.Commit();
            }

            Check.True(
                registry.IsMonsterVisibleTo(viewerSession, monster.ObjectId),
                "replacement viewer is committed visible before the leash tick");

            var receiveCipher = new PacketCipher();
            Check.True(
                registry.TryApplyMonsterDamage(
                    viewerCharacter.CurrentMap,
                    monster.ObjectId,
                    damage: 37,
                    attackerCharacterId: targetCharacter.Id,
                    out _),
                "replacement setup damages and aggros the monster");

            var chaseAt = DateTimeOffset.UtcNow;
            var chaseStartRead = ReadExactlyAsync(
                viewerInbound.GetStream(),
                movementStartLength,
                timeout.Token);
            await registry.AdvanceMonsterWorldOnceAsync(chaseAt, timeout.Token);
            var chaseStartFrame = await chaseStartRead;
            receiveCipher.Transform(chaseStartFrame);
            AssertMonsterMovementFrame(
                chaseStartFrame,
                movementStartLength,
                expectedOpcode: 10016,
                monster.ObjectId,
                "initial chase start");

            for (var chaseStep = 1; chaseStep <= 6; chaseStep++)
            {
                var continuationRead = ReadExactlyAsync(
                    viewerInbound.GetStream(),
                    movementStartLength,
                    timeout.Token);
                await registry.AdvanceMonsterWorldOnceAsync(
                    chaseAt + TimeSpan.FromTicks(
                        MonsterMapRuntime.TickInterval.Ticks * chaseStep),
                    timeout.Token);
                var continuationFrame = await continuationRead;
                receiveCipher.Transform(continuationFrame);
                AssertMonsterMovementFrame(
                    continuationFrame,
                    movementStartLength,
                    expectedOpcode: 10016,
                    monster.ObjectId,
                    $"chase continuation {chaseStep}");
            }

            Check.Equal(
                true,
                registry.TryGetMonsterSnapshot(
                    viewerCharacter.CurrentMap,
                    monster.ObjectId,
                    out var chasedMonster) &&
                chasedMonster.X > chasedMonster.HomeX &&
                chasedMonster.IsSpawned,
                "socket fixture first moves the damaged monster away from home");

            targetCharacter.PositionX = 500;
            targetCharacter.PositionZ = 500;
            var returnAt = chaseAt + TimeSpan.FromTicks(
                MonsterMapRuntime.TickInterval.Ticks * 7);
            var returnStartRead = ReadExactlyAsync(
                viewerInbound.GetStream(),
                movementStartLength,
                timeout.Token);
            await registry.AdvanceMonsterWorldOnceAsync(returnAt, timeout.Token);
            var returnStartFrame = await returnStartRead;
            receiveCipher.Transform(returnStartFrame);
            AssertMonsterMovementFrame(
                returnStartFrame,
                movementStartLength,
                expectedOpcode: 10016,
                monster.ObjectId,
                "leash return start");
            Check.True(
                registry.TryGetMonsterSnapshot(
                    viewerCharacter.CurrentMap,
                    monster.ObjectId,
                    out var returningMonster) &&
                returningMonster.IsAlive &&
                returningMonster.IsSpawned &&
                returningMonster.IsMoving &&
                returningMonster.VelocityX < 0 &&
                returningMonster.CombatPhase == MonsterCombatPhase.Returning &&
                returningMonster.CurrentHealth == monsterMaximumHealth - 37,
                "return start keeps the damaged old generation visible and moving inward");
            Check.True(
                registry.TryApplyMonsterDamage(
                    viewerCharacter.CurrentMap,
                    monster.ObjectId,
                    damage: 19,
                    attackerCharacterId: targetCharacter.Id,
                    out var blockedReturnHit) &&
                blockedReturnHit.BeforeHealth == blockedReturnHit.AfterHealth,
                "returning socket fixture is authoritatively invulnerable");

            var arrivalAndRetireRead = ReadExactlyAsync(
                viewerInbound.GetStream(),
                movementEndLength + lifecycleMarkerLength,
                timeout.Token);
            var arrivalAt = returnAt + TimeSpan.FromTicks(
                MonsterMapRuntime.TickInterval.Ticks *
                checked((long)returningMonster.RemainingMovementTicks));
            await registry.AdvanceMonsterWorldOnceAsync(arrivalAt, timeout.Token);
            var arrivalAndRetireFrames = await arrivalAndRetireRead;
            receiveCipher.Transform(arrivalAndRetireFrames);
            Check.Equal(
                (ushort)movementEndLength,
                ReadUInt16(arrivalAndRetireFrames, 0),
                "home-arrival movement-end length");
            Check.Equal(
                (ushort)10017,
                ReadUInt16(arrivalAndRetireFrames, 2),
                "home-arrival movement-end precedes retirement");
            Check.Equal(
                monster.ObjectId,
                ReadUInt32(arrivalAndRetireFrames, 4),
                "home-arrival movement-end object id");
            Check.Equal(
                (ushort)lifecycleMarkerLength,
                ReadUInt16(arrivalAndRetireFrames, movementEndLength),
                "retirement marker length");
            Check.Equal(
                (ushort)10023,
                ReadUInt16(arrivalAndRetireFrames, movementEndLength + 2),
                "retirement marker follows movement-end");
            Check.Equal(
                monster.ObjectId,
                ReadUInt32(arrivalAndRetireFrames, movementEndLength + 4),
                "retirement marker object id");
            Check.True(
                registry.TryGetMonsterSnapshot(
                    viewerCharacter.CurrentMap,
                    monster.ObjectId,
                    out var retiredMonster) &&
                !retiredMonster.IsAlive &&
                !retiredMonster.IsSpawned &&
                retiredMonster.X == retiredMonster.HomeX &&
                retiredMonster.Z == retiredMonster.HomeZ &&
                retiredMonster.CurrentHealth == monsterMaximumHealth - 37 &&
                retiredMonster.SpawnGeneration == 1 &&
                !registry.IsMonsterVisibleTo(viewerSession, monster.ObjectId),
                "retirement commits the damaged exact-home generation as absent");

            var respawnRead = ReadExactlyAsync(
                viewerInbound.GetStream(),
                lifecycleMarkerLength + appearanceLength,
                timeout.Token);
            await registry.AdvanceMonsterWorldOnceAsync(
                arrivalAt + MonsterMapRuntime.TickInterval,
                timeout.Token);
            var respawnFrames = await respawnRead;
            receiveCipher.Transform(respawnFrames);
            AssertMonsterReplacementFrames(
                respawnFrames,
                monster.ObjectId,
                monsterMaximumHealth,
                "leash replacement respawn");
            Check.True(
                registry.TryGetMonsterSnapshot(
                    viewerCharacter.CurrentMap,
                    monster.ObjectId,
                    out var replacementMonster) &&
                replacementMonster.IsAlive &&
                replacementMonster.IsSpawned &&
                replacementMonster.CurrentHealth == monsterMaximumHealth &&
                replacementMonster.SpawnGeneration == 2 &&
                registry.IsMonsterVisibleTo(viewerSession, monster.ObjectId),
                "respawn publishes a new full-health viewer-visible runtime generation");
            Check.True(
                registry.TryApplyMonsterDamage(
                    viewerCharacter.CurrentMap,
                    monster.ObjectId,
                    damage: 19,
                    attackerCharacterId: targetCharacter.Id,
                    out var replacementHit) &&
                replacementHit.BeforeHealth == monsterMaximumHealth &&
                replacementHit.AfterHealth == monsterMaximumHealth - 19,
                "freshly published replacement is immediately attackable");

            registry.Remove(targetSession);
            registry.Remove(viewerSession);
        }
        finally
        {
            listener.Stop();
        }
    }

    private static void AssertMonsterMovementFrame(
        byte[] frame,
        int expectedLength,
        ushort expectedOpcode,
        uint monsterObjectId,
        string label)
    {
        Check.Equal(expectedLength, frame.Length, $"{label} frame length");
        Check.Equal((ushort)expectedLength, ReadUInt16(frame, 0), $"{label} declared length");
        Check.Equal(expectedOpcode, ReadUInt16(frame, 2), $"{label} opcode");
        Check.Equal(monsterObjectId, ReadUInt32(frame, 4), $"{label} object id");
    }

    private static void AssertMonsterReplacementFrames(
        byte[] stream,
        uint monsterObjectId,
        uint maximumHealth,
        string label)
    {
        const int lifecycleMarkerLength = 8;
        const int appearanceLength = 108;
        Check.Equal(
            lifecycleMarkerLength + appearanceLength,
            stream.Length,
            $"{label} combined frame length");
        Check.Equal((ushort)lifecycleMarkerLength, ReadUInt16(stream, 0), $"{label} marker length");
        Check.Equal((ushort)10023, ReadUInt16(stream, 2), $"{label} marker comes first");
        Check.Equal(monsterObjectId, ReadUInt32(stream, 4), $"{label} marker object id");

        Check.Equal(
            (ushort)appearanceLength,
            ReadUInt16(stream, lifecycleMarkerLength),
            $"{label} appearance length");
        Check.Equal(
            (ushort)10020,
            ReadUInt16(stream, lifecycleMarkerLength + 2),
            $"{label} fresh appearance follows marker");
        Check.Equal(
            monsterObjectId,
            ReadUInt32(stream, lifecycleMarkerLength + 8),
            $"{label} appearance object id");
        Check.Equal(
            maximumHealth,
            ReadUInt32(stream, lifecycleMarkerLength + 20),
            $"{label} current health is full");
        Check.Equal(
            maximumHealth,
            ReadUInt32(stream, lifecycleMarkerLength + 24),
            $"{label} maximum health");
    }
}
