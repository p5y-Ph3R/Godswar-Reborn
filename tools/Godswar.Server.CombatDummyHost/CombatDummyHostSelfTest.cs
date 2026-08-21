using System.Buffers.Binary;
using System.Text;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;

namespace Godswar.Server.CombatDummyHost;

internal static class CombatDummyHostSelfTest
{
    public static void Run()
    {
        var definition = CombatDummyDefinition.All[0];
        var gameLogin = DummyPackets.GameLogin(
            definition.AccountUsername);
        Check(
            LegacyGameLoginPacket.TryRead(
                new GamePacket(gameLogin),
                out var gameIdentity) &&
            gameIdentity is not null &&
            gameIdentity.Username == definition.AccountUsername &&
            gameIdentity.Identifier ==
                DummyPackets.TempestRealmIdentifier &&
            gameIdentity.RealmId == RealmId.Tempest,
            "game login carries the exact Tempest routing admission");

        var preview = CreatePreview(definition);
        CombatDummyHandshakeValidator.ValidateCharacterPreview(
            definition,
            preview);

        var enterMain = CreateEnterMain(definition);
        CombatDummyHandshakeValidator.ValidateEnterMain(
            definition,
            enterMain);

        var observedNpc = false;
        var localStatus = CreatePacket(10167, 340);
        BinaryPrimitives.WriteUInt32LittleEndian(
            localStatus.AsSpan(4, 4),
            DummyPackets.LocalPlayerObjectId);
        var remoteStatus = localStatus.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(
            remoteStatus.AsSpan(4, 4),
            0x0000_9001);
        Check(
            !CombatDummyHandshakeValidator.ObserveWorldReady(
                ref observedNpc,
                localStatus),
            "an early status packet cannot complete world readiness");
        Check(
            !CombatDummyHandshakeValidator.ObserveWorldReady(
                ref observedNpc,
                CreatePacket(
                    CombatDummyHandshakeValidator.NpcSpawnOpcode,
                    4)),
            "an NPC packet alone cannot complete world readiness");
        Check(
            !CombatDummyHandshakeValidator.ObserveWorldReady(
                ref observedNpc,
                remoteStatus),
            "a remote player's status cannot complete local readiness");
        Check(
            CombatDummyHandshakeValidator.ObserveWorldReady(
                ref observedNpc,
                localStatus),
            "NPC followed by status completes world readiness");

        var wrongId = enterMain.ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(wrongId.AsSpan(4, 4), 9999);
        Throws(
            () => CombatDummyHandshakeValidator.ValidateEnterMain(
                definition,
                wrongId),
            "wrong character ID is rejected");

        var injured = enterMain.ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(injured.AsSpan(76, 4), 9_999);
        Throws(
            () => CombatDummyHandshakeValidator.ValidateEnterMain(
                definition,
                injured),
            "non-full entry vitals are rejected");

        var wrongCase = preview.ToArray();
        WriteFixedAscii(wrongCase.AsSpan(5, 32), "aresBulwark");
        Throws(
            () => CombatDummyHandshakeValidator.ValidateCharacterPreview(
                definition,
                wrongCase),
            "case-variant character names are rejected");

        Throws(
            () => CombatDummyHostOptions.Parse(
                ["--game-port", "7001"]),
            "non-development game ports are rejected");
        Throws(
            () => CombatDummyHostOptions.Parse(
                ["--identity-manifest", "7001:7001:AresBulwark:0:0"]),
            "partial identity manifests are rejected");

        var defaults = CombatDummyHostOptions.Parse([]);
        Check(
            defaults.ReconnectDelay == TimeSpan.FromSeconds(
                CombatDummyHostOptions.DefaultReconnectSeconds),
            "the default reconnect delay preserves the full death lifecycle");
        Check(
            defaults.CorpseRetentionDelay == TimeSpan.FromSeconds(5) &&
            defaults.PostRemovalReconnectDelay == TimeSpan.FromSeconds(5),
            "the default lifecycle holds the corpse before a removal gap");
        var minimumReconnect = CombatDummyHostOptions.Parse(
            ["--reconnect-seconds", "6"]);
        Check(
            minimumReconnect.ReconnectDelay == TimeSpan.FromSeconds(
                CombatDummyHostOptions.MinimumReconnectSeconds) &&
            minimumReconnect.CorpseRetentionDelay == TimeSpan.FromSeconds(5) &&
            minimumReconnect.PostRemovalReconnectDelay == TimeSpan.FromSeconds(1),
            "the minimum lifecycle retains a post-removal gap");
        Throws(
            () => CombatDummyHostOptions.Parse(
                ["--reconnect-seconds", "5"]),
            "reconnect delays inside the corpse window are rejected");

        var zeroVitals = CreatePacket(0x2771, 16);
        BinaryPrimitives.WriteUInt32LittleEndian(
            zeroVitals.AsSpan(4, 4),
            DummyPackets.LocalPlayerObjectId);
        BinaryPrimitives.WriteInt32LittleEndian(
            zeroVitals.AsSpan(8, 4),
            0);
        Check(
            !CombatDummyConnection.IsTerminalLocalDeath(zeroVitals),
            "zero vitals do not reconnect before terminal death");

        var terminalDeath = CreatePacket(0x2722, 28);
        BinaryPrimitives.WriteUInt32LittleEndian(
            terminalDeath.AsSpan(4, 4),
            DummyPackets.LocalPlayerObjectId);
        Check(
            CombatDummyConnection.IsTerminalLocalDeath(terminalDeath),
            "terminal local death starts corpse-safe reconnect");
        BinaryPrimitives.WriteUInt32LittleEndian(
            terminalDeath.AsSpan(4, 4),
            0x0000_9001);
        Check(
            !CombatDummyConnection.IsTerminalLocalDeath(terminalDeath),
            "a remote death cannot restart the dummy session");

        var corpseDelayStarted = false;
        var releaseCorpseWindow = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var terminalDisconnect =
            CombatDummyConnection.DelayTerminalDisconnectAsync(
                defaults.CorpseRetentionDelay,
                (delay, _) =>
                {
                    Check(
                        delay == defaults.CorpseRetentionDelay,
                        "terminal disconnect uses the connected corpse delay");
                    corpseDelayStarted = true;
                    return releaseCorpseWindow.Task;
                },
                CancellationToken.None);
        Check(
            corpseDelayStarted && !terminalDisconnect.IsCompleted,
            "terminal disconnect cannot unwind the socket before the corpse window");
        releaseCorpseWindow.SetResult();
        ThrowsTerminalDeath(
            terminalDisconnect,
            "terminal disconnect unwinds only after the corpse window");
        Check(
            CombatDummyConnection.ResolveReconnectDelay(
                defaults,
                new CombatDummyTerminalDeathException()) ==
                    defaults.PostRemovalReconnectDelay &&
            CombatDummyConnection.ResolveReconnectDelay(
                defaults,
                new IOException("ordinary disconnect")) ==
                    defaults.ReconnectDelay,
            "only terminal death uses the bounded post-removal remainder");

        Check(
            CombatDummyDefinition.All.Select(static value => value.AccountId)
                .Distinct().Count() == 4 &&
            CombatDummyDefinition.All.Select(static value => value.CharacterId)
                .Distinct().Count() == 4,
            "all immutable account and character IDs are unique");
        Check(
            CombatDummyDefinition.All.Count == 4 &&
            CombatDummyDefinition.All.All(static value =>
                value.MapId is 0 or 1 &&
                value.Camp is 0 or 1 &&
                value.Camp != value.MapId) &&
            CombatDummyDefinition.All.Count(static value =>
                value.MapId == 0 && value.Camp == 1) == 2 &&
            CombatDummyDefinition.All.Count(static value =>
                value.MapId == 1 && value.Camp == 0) == 2,
            "each capital has exactly two opposing-camp dummies");
        Check(
            CombatDummyDefinition.IdentityManifest ==
                "7001:7001:AresBulwark:1:0:148:-154," +
                "7002:7002:AresMirage:1:0:148:-162," +
                "7003:7003:AthenaBulwark:0:1:148:-154," +
                "7004:7004:AthenaMirage:0:1:148:-162",
            "the immutable seven-field identity manifest is exact");
    }

    private static byte[] CreatePreview(CombatDummyDefinition definition)
    {
        var packet = new byte[44];
        packet[4] = 1;
        WriteFixedAscii(packet.AsSpan(5, 32), definition.CharacterName);
        packet[37] = definition.Camp;
        packet[38] = definition.Profession;
        packet[39] = 160;
        return packet;
    }

    private static byte[] CreatePacket(ushort opcode, int length)
    {
        var packet = new byte[length];
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet,
            checked((ushort)length));
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(2, 2),
            opcode);
        return packet;
    }

    private static byte[] CreateEnterMain(CombatDummyDefinition definition)
    {
        var packet = new byte[104];
        BinaryPrimitives.WriteInt32LittleEndian(
            packet.AsSpan(4, 4),
            definition.CharacterId);
        WriteFixedAscii(packet.AsSpan(8, 32), definition.CharacterName);
        packet[41] = definition.Camp;
        packet[43] = definition.Profession;
        packet[46] = definition.MapId;
        BinaryPrimitives.WriteSingleLittleEndian(
            packet.AsSpan(56, 4),
            definition.PositionX);
        BinaryPrimitives.WriteSingleLittleEndian(
            packet.AsSpan(64, 4),
            definition.PositionZ);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(68, 4), 10_000);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(72, 4), 5_000);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(76, 4), 10_000);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(80, 4), 5_000);
        return packet;
    }

    private static void WriteFixedAscii(Span<byte> destination, string value)
    {
        destination.Clear();
        Encoding.ASCII.GetBytes(value, destination);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(
                $"Combat dummy self-test failed: {message}.");
        }
    }

    private static void Throws(Action action, string message)
    {
        try
        {
            action();
        }
        catch (Exception error)
            when (error is ArgumentException or InvalidDataException)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Combat dummy self-test failed: {message}.");
    }

    private static void ThrowsTerminalDeath(Task action, string message)
    {
        try
        {
            action.GetAwaiter().GetResult();
        }
        catch (CombatDummyTerminalDeathException)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Combat dummy self-test failed: {message}.");
    }
}
