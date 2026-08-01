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
    private static Task CheckMountRideProtocolAsync()
    {
        var expected = new (uint ItemId, int Level, uint StatusId, float Speed)[]
        {
            (14220, 40, 1100, 0.20f),
            (14221, 50, 1101, 0.21f),
            (14222, 60, 1102, 0.22f),
            (14223, 70, 1103, 0.23f),
            (14224, 80, 1104, 0.24f),
            (14225, 90, 1105, 0.25f),
            (14226, 100, 1105, 0.26f),
            (14227, 110, 1105, 0.27f),
            (14228, 120, 1105, 0.28f),
            (14229, 120, 1105, 0.50f)
        };
        foreach (var entry in expected)
        {
            Check.True(
                TestItemContent.Content.Mounts.TryGetRideDefinition(entry.ItemId, out var definition),
                $"Greek mount {entry.ItemId} has a Ride definition");
            Check.Equal(entry.Level, definition.MountLevel, $"Greek mount {entry.ItemId} level");
            Check.Equal(entry.StatusId, definition.StatusId, $"Greek mount {entry.ItemId} status");
            Check.Equal(entry.Speed, definition.SpeedBonus, $"Greek mount {entry.ItemId} speed");
        }

        Check.True(
            TestItemContent.Content.Mounts.TryGetRideDefinition(6000, out var legacyDefinition) &&
            legacyDefinition.StatusId == 1100,
            "legacy Greek Steed 6000 uses its verified Ride.ini status");
        Check.Equal(50, MountCatalog.RideManaCost, "Ride MP cost");
        Check.Equal(TimeSpan.FromSeconds(6), MountCatalog.RideCastTime, "Ride intonation time");
        Check.Equal(TimeSpan.FromSeconds(6), MountCatalog.RideCooldown, "Ride cooldown");

        var now = new DateTimeOffset(2026, 7, 23, 12, 0, 0, TimeSpan.Zero);
        var ride = new ActiveRuntimeStatus(
            1100,
            MountCatalog.RuntimeStatusKind,
            1,
            false,
            DateTimeOffset.MaxValue,
            ClientStatusAggregate.Empty,
            1,
            MovementSpeedBonus: 0.20f);
        var snapshot = PlayerStatusComposer.Compose(
            ExperienceBoostState.Empty,
            [ride],
            now);
        Check.Equal(1, snapshot.Effects.Count, "Ride appears in the complete status snapshot");
        Check.Equal(1100u, snapshot.Effects[0].StatusId, "Ride status ID");
        Check.Equal(uint.MaxValue, snapshot.Effects[0].RemainingSeconds, "Ride uses permanent timer sentinel");
        Check.Equal(1.20f, snapshot.Aggregate.MovementSpeedMultiplier, "Ride composes movement speed");
        Check.True(snapshot.Aggregate.IsRiding, "Ride composes the native effect-33 riding flag");

        var character = CreateCharacter();
        character.Level = 40;
        character.Equipment = EquipmentSlots.SetSlot(
            character.Equipment,
            character.Profession,
            EquipmentSlots.Mount,
            "[14220,,,,,,1,1,0,1,0]");
        character.Equipment = EquipmentSlots.SetSlot(
            character.Equipment,
            character.Profession,
            EquipmentSlots.MountHead,
            "[14500,,,,,,1,1,0,1,0]");
        Check.True(
            TestItemContent.Content.Mounts.TryGetEquippedRideDefinition(character, out var equipped) &&
            equipped.ItemId == 14220,
            "equipped slot-20 mount resolves the Ride appearance");

        CheckQualityAwareRideDefinitions(character);

        var enter = PacketBuilder.EnterMain(character);
        var enterEquipmentMask = ReadUInt32(enter, 48);
        Check.True(
            (enterEquipmentMask & (1u << EquipmentSlots.MountHead)) != 0,
            "self-enter equipment includes mount-head slot 15");
        Check.True(
            (enterEquipmentMask & (1u << EquipmentSlots.Mount)) != 0,
            "self-enter equipment includes mount slot 20");

        var statusPacket = PacketBuilder.PlayerStatusEffects(
            character,
            snapshot.Effects,
            snapshot.Aggregate);
        Check.Equal(1.20f, ReadSingle(statusPacket, 324), "Ride speed uses native StatusData offset");
        Check.Equal(1u, ReadUInt32(statusPacket, 328), "Ride sets captured StatusData effect-33 flag");
        var movementPacket = PacketBuilder.PlayerStatusUpdate(character, 1.20f);
        Check.Equal((ushort)10166, ReadUInt16(movementPacket, 2), "Ride movement sync opcode");
        Check.Equal(1.20f, ReadSingle(movementPacket, 56), "Ride movement sync uses native GameData offset");

        var nonRide = new ActiveRuntimeStatus(
            160,
            6,
            2,
            true,
            now.AddMinutes(10),
            ClientStatusAggregate.Empty,
            2);
        var nonRideSnapshot = PlayerStatusComposer.Compose(
            ExperienceBoostState.Empty,
            [nonRide],
            now);
        Check.True(!nonRideSnapshot.Aggregate.IsRiding, "non-Ride status does not set effect 33");
        var nonRidePacket = PacketBuilder.PlayerStatusEffects(
            character,
            nonRideSnapshot.Effects,
            nonRideSnapshot.Aggregate);
        Check.Equal(0u, ReadUInt32(nonRidePacket, 328), "non-Ride packet keeps effect 33 cleared");

        var dismountedSnapshot = PlayerStatusComposer.Compose(
            ExperienceBoostState.Empty,
            [],
            now);
        Check.True(!dismountedSnapshot.Aggregate.IsRiding, "dismount clears composed riding state");
        var dismountedPacket = PacketBuilder.PlayerStatusEffects(
            character,
            dismountedSnapshot.Effects,
            dismountedSnapshot.Aggregate);
        Check.Equal(0u, ReadUInt32(dismountedPacket, 328), "dismount clears captured StatusData effect-33 flag");

        var spawn = PacketBuilder.PlayerWorldSpawn(
            character,
            0x6612u,
            snapshot.Effects);
        Check.Equal((ushort)1, ReadUInt16(spawn, 178), "player spawn embeds Ride status count");
        Check.Equal(1100u, ReadUInt32(spawn, 180), "player spawn embeds Ride status ID");
        Check.Equal(0x31585747u, ReadUInt32(spawn, 260), "Ride status embedding preserves appearance extension");
        return Task.CompletedTask;
    }

    private static async Task CheckImmediateMountRideDismountAsync()
    {
        var nativeDismountRequest = Convert.FromHexString(
            "1400502800000000060000000000000000000000");
        Check.True(
            GameClientHandler.IsRideDismountRequest(nativeDismountRequest),
            "native opcode 10320 action 6 routes to Ride dismount");
        var unrelatedPlayerStateAction = nativeDismountRequest.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(unrelatedPlayerStateAction.AsSpan(8, 4), 5);
        Check.True(
            !GameClientHandler.IsRideDismountRequest(unrelatedPlayerStateAction),
            "unrelated opcode 10320 actions do not dismount Ride");

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using var outbound = new TcpClient();
            var acceptTask = listener.AcceptTcpClientAsync();
            await outbound.ConnectAsync(IPAddress.Loopback, port);
            using var inbound = await acceptTask;
            await using var session = new ClientSession(new RawTcpLegacyTransport(outbound));

            var character = CreateCharacter();
            character.Level = 40;
            character.CurrentMp = 150;
            character.Equipment = EquipmentSlots.SetSlot(
                character.Equipment,
                character.Profession,
                EquipmentSlots.Mount,
                "[14220,,,,,,1,1,0,1,0]");
            Check.True(
                TestItemContent.Content.Mounts.TryGetEquippedRideDefinition(character, out var mount),
                "instant dismount fixture resolves its equipped mount");

            var registry = new GameSessionRegistry(
                itemContent: TestItemContent.Content);
            registry.JoinMap(
                session,
                character.AccountId,
                character,
                WorldObjectIds.ForPlayer(character.Id),
                worldReady: false);
            var lifeRevision = registry.GetPlayerLifeRevision(session);
            var statusPacketLength = PacketBuilder.PlayerStatusEffects(
                character,
                [],
                ClientStatusAggregate.Empty).Length;
            Check.Equal(340, statusPacketLength, "complete Ride status packet length");

            using var setupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            Check.True(
                await registry.SetPersistentRuntimeStatusAndPublishAsync(
                    session,
                    MountCatalog.RuntimeStatusKind,
                    mount.StatusId,
                    priority: 1,
                    beneficial: false,
                    mount.SpeedBonus,
                    active: true,
                    DateTimeOffset.UtcNow,
                    "mount-test-activate",
                    setupTimeout.Token),
                "instant dismount fixture starts mounted");

            var receiveCipher = new PacketCipher();
            var mountedPacket = await ReadExactlyAsync(
                inbound.GetStream(),
                statusPacketLength,
                setupTimeout.Token);
            receiveCipher.Transform(mountedPacket);
            Check.Equal((ushort)10167, ReadUInt16(mountedPacket, 2), "mounted setup status opcode");
            Check.Equal(1.20f, ReadSingle(mountedPacket, 324), "mounted setup movement speed");
            Check.Equal(1u, ReadUInt32(mountedPacket, 328), "mounted setup effect-33 flag");
            var mountedMovementPacket = await ReadExactlyAsync(
                inbound.GetStream(),
                236,
                setupTimeout.Token);
            receiveCipher.Transform(mountedMovementPacket);
            Check.Equal((ushort)10166, ReadUInt16(mountedMovementPacket, 2), "mounted setup movement-sync opcode");
            Check.Equal(1.20f, ReadSingle(mountedMovementPacket, 56), "mounted setup local movement multiplier");

            var manaBeforeDismount = character.CurrentMp;
            using var instantTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var statusChanged = await GameClientHandler.DismountMountRideAndPublishAsync(
                session,
                registry,
                character,
                lifeRevision,
                instantTimeout.Token);
            Check.True(statusChanged, "second Ride use removes the active status");

            var dismountedPacket = await ReadExactlyAsync(
                inbound.GetStream(),
                statusPacketLength,
                setupTimeout.Token);
            receiveCipher.Transform(dismountedPacket);
            Check.Equal((ushort)10167, ReadUInt16(dismountedPacket, 2), "dismount sends status opcode first");
            Check.Equal(0u, ReadUInt32(dismountedPacket, 8), "dismount removes the Ride status entry");
            Check.Equal(1f, ReadSingle(dismountedPacket, 324), "dismount restores movement speed immediately");
            Check.Equal(0u, ReadUInt32(dismountedPacket, 328), "dismount clears effect-33 immediately");
            var dismountedMovementPacket = await ReadExactlyAsync(
                inbound.GetStream(),
                236,
                setupTimeout.Token);
            receiveCipher.Transform(dismountedMovementPacket);
            Check.Equal((ushort)10166, ReadUInt16(dismountedMovementPacket, 2), "dismount follows with movement-sync opcode");
            Check.Equal(1f, ReadSingle(dismountedMovementPacket, 56), "dismount restores local locomotion multiplier");
            Check.Equal(0, inbound.Available, "dismount sends no cast visual, impact, or mana packet");
            Check.Equal(manaBeforeDismount, character.CurrentMp, "dismount consumes no MP");
            Check.True(
                !registry.IsRuntimeStatusActive(
                    session,
                    MountCatalog.RuntimeStatusKind,
                    DateTimeOffset.UtcNow),
                "dismount clears authoritative Ride state");
            var aggregate = registry.GetRuntimeStatusAggregate(session, DateTimeOffset.UtcNow);
            Check.Equal(1f, aggregate.MovementSpeedMultiplier, "dismount clears the mount speed bonus");
            Check.True(!aggregate.IsRiding, "dismount clears composed riding state");

            var lateSnapshot = await registry.GetStatusSnapshotAsync(
                session,
                DateTimeOffset.UtcNow,
                setupTimeout.Token);
            var lateSpawn = PacketBuilder.PlayerWorldSpawn(
                character,
                WorldObjectIds.ForPlayer(character.Id),
                lateSnapshot.Effects);
            Check.Equal((ushort)0, ReadUInt16(lateSpawn, 178), "late viewer sees no active Ride status");
            Check.True(
                (ReadUInt32(lateSpawn, 168) & (1u << EquipmentSlots.Mount)) != 0,
                "dismount keeps the mount equipped for the next Ride use");

            registry.Remove(session);
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task CheckAtomicMountRideActivationAsync()
    {
        var dataPath = Path.Combine(
            Path.GetTempPath(),
            $"godswar-atomic-ride-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataPath);
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using var outbound = new TcpClient();
            var acceptTask = listener.AcceptTcpClientAsync();
            await outbound.ConnectAsync(IPAddress.Loopback, port);
            using var inbound = await acceptTask;
            await using var session = new ClientSession(new RawTcpLegacyTransport(outbound));

            await using var store = new JsonGameStore(dataPath);
            await store.EnsureSeedDataAsync();
            var account = await store.LoginOrCreateAccountAsync("atomic-ride", "");
            var character = await store.CreateCharacterAsync(
                account.Id,
                new GameCharacter
                {
                    Name = "AtomicRideHero",
                    Camp = GameDefaults.SpartaCamp
                });
            character.Level = 40;
            character.CurrentHp = 500;
            character.CurrentMp = 150;
            character.MaxMp = Math.Max(character.MaxMp, 150);
            character.MarkVitalsChanged();
            await store.SaveCharacterVitalsAsync(
                account.Id,
                character.Id,
                character.CurrentHp,
                character.CurrentMp,
                character.VitalsRevision);
            character.Equipment = EquipmentSlots.SetSlot(
                character.Equipment,
                character.Profession,
                EquipmentSlots.Mount,
                "[14220,,,,,,1,1,0,1,0]");
            Check.True(
                TestItemContent.Content.Mounts.TryGetEquippedRideDefinition(character, out var mount),
                "atomic Ride fixture resolves its equipped mount");

            var registry = new GameSessionRegistry(
                store,
                itemContent: TestItemContent.Content);
            registry.JoinMap(
                session,
                character.AccountId,
                character,
                WorldObjectIds.ForPlayer(character.Id));
            var lifeRevision = registry.GetPlayerLifeRevision(session);
            var stale = await registry.TryActivateMountRideAndPublishAsync(
                session,
                character.Id,
                lifeRevision + 1,
                mount,
                DateTimeOffset.UtcNow,
                CancellationToken.None);
            Check.True(stale is null, "stale life generation interrupts Ride before activation");
            Check.Equal(150, character.CurrentMp, "interrupted Ride does not consume MP");

            await CheckRideQualityRecheckAsync(
                registry,
                session,
                character,
                lifeRevision,
                mount);

            var activated = await registry.TryActivateMountRideAndPublishAsync(
                session,
                character.Id,
                lifeRevision,
                mount,
                DateTimeOffset.UtcNow,
                CancellationToken.None);
            Check.True(activated is not null, "matching life generation commits Ride");
            Check.Equal(100, activated!.Value.CurrentMana, "atomic Ride commit consumes exactly 50 MP");
            Check.Equal(100, character.CurrentMp, "committed Ride updates authoritative character MP");
            var persisted = await store.GetFirstCharacterAsync(account.Id)
                ?? throw new InvalidOperationException("atomic Ride character was not persisted");
            Check.Equal(100, persisted.CurrentMp, "Ride MP cost is durable before status publication returns");
            Check.True(
                registry.IsRuntimeStatusActive(
                    session,
                    MountCatalog.RuntimeStatusKind,
                    DateTimeOffset.UtcNow),
                "atomic Ride commit publishes the persistent Ride status");
            Check.Equal(
                1.20f,
                registry.GetRuntimeStatusAggregate(session, DateTimeOffset.UtcNow)
                    .MovementSpeedMultiplier,
                "atomic Ride commit applies mount movement speed");

            var deathLifeRevision = registry.AdvancePlayerLifeRevision(session);
            var revivedLifeRevision = registry.AdvancePlayerLifeRevision(session);
            await registry.SetPersistentRuntimeStatusAndPublishAsync(
                session,
                MountCatalog.RuntimeStatusKind,
                statusId: 0,
                priority: 0,
                beneficial: false,
                movementSpeedBonus: 0f,
                active: false,
                DateTimeOffset.UtcNow,
                "mount-test-revive",
                CancellationToken.None);
            var postReviveRide = await registry.TryActivateMountRideAndPublishAsync(
                session,
                character.Id,
                revivedLifeRevision,
                mount,
                DateTimeOffset.UtcNow,
                CancellationToken.None);
            Check.True(postReviveRide is not null, "new life can activate a new Ride status");
            Check.True(
                !await registry.RemovePersistentRuntimeStatusForLifeRevisionAndPublishAsync(
                    session,
                    deathLifeRevision,
                    MountCatalog.RuntimeStatusKind,
                    DateTimeOffset.UtcNow,
                    "mount-test-delayed-death",
                    CancellationToken.None),
                "delayed death cleanup cannot clear a post-revive Ride status");
            Check.True(
                registry.IsRuntimeStatusActive(
                    session,
                    MountCatalog.RuntimeStatusKind,
                    DateTimeOffset.UtcNow),
                "post-revive Ride survives stale death cleanup");

            registry.Remove(session);
        }
        finally
        {
            listener.Stop();
            Directory.Delete(dataPath, recursive: true);
        }
    }
}
