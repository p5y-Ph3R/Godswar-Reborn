using System.Buffers.Binary;
using System.Reflection;
using Godswar.Server.Application.Accounts;
using Godswar.Server.Game;
using Godswar.Server.Networking;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class CharacterUiStatsProtocolChecks
{
    public const string CheckName =
        "Character UI stats SID200 capability protocol";

    private const int AccountId = 7_200;
    private const int CharacterId = 7_201;
    private static readonly byte[] CanonicalProbe = Convert.FromHexString(
        "1800392800000000C800C800010000000000000000000000");
    private static readonly byte[] CanonicalResponse = Convert.FromHexString(
        "1800392848140000C800C800102700004D010000401F0000");
    private static readonly MethodInfo HandlePacketMethod =
        typeof(GameClientHandler).GetMethod(
            "HandlePacketAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException(
            "GameClientHandler.HandlePacketAsync was not found.");
    private static readonly MethodInfo LocalStatusMethod =
        typeof(GameClientHandler).GetMethod(
            "BuildLocalPlayerStatusUpdate",
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            types: Type.EmptyTypes,
            modifiers: null)
        ?? throw new InvalidOperationException(
            "GameClientHandler.BuildLocalPlayerStatusUpdate was not found.");

    public static async Task RunAsync()
    {
        CheckPacketContracts();
        await CheckCapabilityHandlerAsync();
        await CheckRejectedProbesAsync();
        await CheckReconnectResetAsync();
    }

    private static void CheckPacketContracts()
    {
        Check.True(
            ZodiacSyncRequest.TryParse(CanonicalProbe, out var request) &&
            request.IsCharacterUiStatsV1Envelope &&
            request.IsCanonicalCharacterUiStatsV1Probe,
            "exact module-200 SID200 probe is canonical");
        Check.Equal(0u, request.PlayerId,
            "native ConsEventRequest probe uses player placeholder zero");

        var response = PacketBuilder.CharacterUiStatsV1(
            new CharacterUiStatsV1Projection(
                SpeedBasisPoints: 10_000,
                PhysicalPenetrationBasisPoints: 333,
                MagicPenetrationBasisPoints: 8_000));
        Check.True(
            response.SequenceEqual(CanonicalResponse),
            "SID200 response matches the pinned symmetric-module vector");

        var clamped = PacketBuilder.CharacterUiStatsV1(
            new CharacterUiStatsV1Projection(
                SpeedBasisPoints: 0,
                PhysicalPenetrationBasisPoints: -1,
                MagicPenetrationBasisPoints: int.MaxValue));
        Check.Equal(1_000, ReadInt32(clamped, 12),
            "SID200 speed defensively clamps to the locomotion 0.1x floor");
        Check.Equal(0, ReadInt32(clamped, 16),
            "SID200 physical penetration defensively clamps at zero");
        Check.Equal(8_000, ReadInt32(clamped, 20),
            "SID200 magic penetration defensively clamps at eighty percent");

        var character = CreateCharacter();
        var rounded = GameSessionRegistry.ProjectCharacterUiStatsV1(
            character,
            ClientStatusAggregate.Empty with
            {
                MovementSpeedMultiplier = 1.23456f
            });
        Check.True(
            rounded == new CharacterUiStatsV1Projection(12_346, 333, 8_000),
            "game projection converts speed and penetration to basis points");

        var wrongOpcode = CanonicalProbe.ToArray();
        BinaryPrimitives.WriteUInt16LittleEndian(
            wrongOpcode.AsSpan(2, 2),
            Opcodes.Ping);
        Check.True(
            !ZodiacSyncRequest.TryParse(wrongOpcode, out _),
            "Zodiac parser rejects a canonical-shaped request on another opcode");
        Check.True(
            !ZodiacSyncRequest.TryParse(CanonicalProbe.AsSpan(0, 23), out _),
            "Zodiac parser rejects a truncated capability probe");
    }

    private static async Task CheckCapabilityHandlerAsync()
    {
        await using var fixture = CreateFixture();
        var stockBefore = BuildLocalStatus(fixture.Handler);
        CheckStockStatus(
            stockBefore,
            fixture.Character.Camp,
            "before capability negotiation");

        await InvokeAsync(fixture.Handler, CanonicalProbe);
        Check.True(
            fixture.Registry.IsCharacterUiStatsV1Enabled(fixture.Session),
            "canonical probe enables SID200 for only its registered session");
        var packets = ReadPackets(fixture.Transport);
        Check.True(
            packets.Count == 1 &&
            packets[0].SequenceEqual(CanonicalResponse),
            "canonical probe receives one standalone authoritative SID200 reply");

        var writtenAfterFirstProbe = fixture.Transport.WrittenBytes;
        await InvokeAsync(fixture.Handler, CanonicalProbe);
        Check.True(
            fixture.Transport.WrittenBytes.SequenceEqual(writtenAfterFirstProbe),
            "rapid repeated canonical probe is throttled without another reply");

        var stockAfter = BuildLocalStatus(fixture.Handler);
        CheckStockStatus(
            stockAfter,
            fixture.Character.Camp,
            "after capability negotiation");
        Check.True(
            stockAfter.SequenceEqual(stockBefore),
            "capability never appends SID200 to stock local 10166 framing");

        var farFuture = DateTimeOffset.UtcNow.AddYears(1);
        Check.True(
            fixture.Registry.TryAcceptCharacterUiStatsV1CapabilityProbe(
                fixture.Session,
                farFuture),
            "probe throttle admits the next one-second polling window");
        Check.True(
            !fixture.Registry.TryAcceptCharacterUiStatsV1CapabilityProbe(
                fixture.Session,
                farFuture.AddMilliseconds(999)),
            "probe throttle rejects a request just inside one second");
        Check.True(
            fixture.Registry.TryAcceptCharacterUiStatsV1CapabilityProbe(
                fixture.Session,
                farFuture.AddSeconds(1)),
            "probe throttle admits a request at the exact one-second boundary");
    }

    private static async Task CheckRejectedProbesAsync()
    {
        await using var fixture = CreateFixture();
        var malformed = CanonicalProbe.AsSpan(0, 23).ToArray();
        var spoofedPlayer = CanonicalProbe.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(
            spoofedPlayer.AsSpan(4, 4),
            0x1448);
        var wrongValue = CanonicalProbe.ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(
            wrongValue.AsSpan(12, 4),
            0);
        var wrongModule = CanonicalProbe.ToArray();
        BinaryPrimitives.WriteUInt16LittleEndian(
            wrongModule.AsSpan(8, 2),
            201);
        var wrongSid = CanonicalProbe.ToArray();
        BinaryPrimitives.WriteUInt16LittleEndian(
            wrongSid.AsSpan(10, 2),
            201);

        foreach (var rejected in new[]
                 {
                     malformed,
                     spoofedPlayer,
                     wrongValue,
                     wrongModule,
                     wrongSid
                 })
        {
            await InvokeAsync(fixture.Handler, rejected);
        }

        Check.True(
            fixture.Transport.WrittenBytes.Length == 0 &&
            !fixture.Registry.IsCharacterUiStatsV1Enabled(fixture.Session),
            "malformed, spoofed, and noncanonical probes neither reply nor enable");

        await InvokeAsync(fixture.Handler, CanonicalProbe);
        Check.True(
            ReadPackets(fixture.Transport).Count == 1 &&
            fixture.Registry.IsCharacterUiStatsV1Enabled(fixture.Session),
            "rejected probes do not consume the first canonical polling window");
    }

    private static async Task CheckReconnectResetAsync()
    {
        await using var fixture = CreateFixture();
        await InvokeAsync(fixture.Handler, CanonicalProbe);
        Check.True(
            fixture.Registry.IsCharacterUiStatsV1Enabled(fixture.Session),
            "pre-reconnect session negotiated SID200");

        fixture.Registry.Remove(fixture.Session);
        fixture.Registry.RemoveAccountSession(AccountId, fixture.Session);
        Check.True(
            !fixture.Registry.IsCharacterUiStatsV1Enabled(fixture.Session),
            "removed session no longer owns capability state");

        var reconnectTransport = new ScriptedLegacyByteTransport();
        var reconnectSession = new ClientSession(reconnectTransport);
        try
        {
            GameHandlerOwnershipTestFences.Bind(
                fixture.Registry,
                reconnectSession,
                AccountId,
                fixture.Character);
            fixture.Registry.JoinMap(
                reconnectSession,
                AccountId,
                fixture.Character,
                WorldObjectIds.ForPlayer(fixture.Character.Id));
            Check.True(
                !fixture.Registry.IsCharacterUiStatsV1Enabled(reconnectSession),
                "replacement session starts with capability disabled");
        }
        finally
        {
            fixture.Registry.Remove(reconnectSession);
            fixture.Registry.RemoveAccountSession(AccountId, reconnectSession);
            await reconnectSession.DisposeAsync();
        }
    }

    private static HandlerFixture CreateFixture()
    {
        var store = new UiStatsGameStore();
        var transport = new ScriptedLegacyByteTransport();
        var session = new ClientSession(transport);
        var registry = new GameSessionRegistry(store);
        var character = CreateCharacter();
        GameHandlerOwnershipTestFences.Bind(
            registry,
            session,
            AccountId,
            character);
        registry.JoinMap(
            session,
            AccountId,
            character,
            WorldObjectIds.ForPlayer(character.Id));
        var handler = new GameClientHandler(
            session,
            store,
            registry,
            CharacterSnapshotReaderTestFixtures.Unused,
            WorldContentReaderTestFixtures.Empty);
        SetField(
            handler,
            "_account",
            new AccountIdentity(AccountId, "ui-stats-check"));
        SetField(handler, "_character", character);
        return new HandlerFixture(
            session,
            transport,
            registry,
            handler,
            character);
    }

    private static GameCharacter CreateCharacter() =>
        new()
        {
            Id = CharacterId,
            AccountId = AccountId,
            Name = "UiStatsHero",
            Camp = GameDefaults.AthensCamp,
            Profession = 0,
            Level = 80,
            CurrentMap = 3,
            CurrentHp = 10_000,
            MaxHp = 10_000,
            CurrentMp = 1_000,
            MaxMp = 1_000,
            Equipment = GameDefaults.DefaultEquipment(0),
            KitBag = GameDefaults.StarterKitBag,
            CalculatedStats = new CharacterStats
            {
                IgnorePhysicalDefense = 333,
                IgnoreMagicDefense = 9_000
            }
        };

    private static byte[] BuildLocalStatus(GameClientHandler handler) =>
        (byte[]?)LocalStatusMethod.Invoke(handler, null)
        ?? throw new InvalidOperationException(
            "Local player status projection returned no packet.");

    private static void CheckStockStatus(
        byte[] packet,
        byte expectedCamp,
        string context)
    {
        Check.Equal(236, packet.Length, $"stock 10166 length {context}");
        Check.Equal((ushort)236,
            BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(0, 2)),
            $"stock 10166 declared length {context}");
        Check.Equal((ushort)10166,
            BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(2, 2)),
            $"stock 10166 opcode {context}");
        Check.True(
            packet[60] == 0 &&
            packet[61] == 0 &&
            packet[62] == expectedCamp &&
            packet[63] == 0,
            $"stock NPC interaction identity carries only camp {context}");
    }

    private static async Task InvokeAsync(
        GameClientHandler handler,
        byte[] packet)
    {
        var invocation = HandlePacketMethod.Invoke(
            handler,
            [new GamePacket(packet), CancellationToken.None]) as Task
            ?? throw new InvalidOperationException(
                "GameClientHandler did not return a packet task.");
        await invocation;
    }

    private static IReadOnlyList<byte[]> ReadPackets(
        ScriptedLegacyByteTransport transport)
    {
        var clear = transport.WrittenBytes;
        new PacketCipher().Transform(clear);
        var packets = new List<byte[]>();
        var offset = 0;
        while (offset < clear.Length)
        {
            var length = BinaryPrimitives.ReadUInt16LittleEndian(
                clear.AsSpan(offset, 2));
            if (length < 4 || length > clear.Length - offset)
            {
                throw new InvalidDataException(
                    "Character UI stats response has invalid framing.");
            }

            packets.Add(clear.AsSpan(offset, length).ToArray());
            offset += length;
        }

        return packets;
    }

    private static int ReadInt32(byte[] packet, int offset) =>
        BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(offset, 4));

    private static void SetField<T>(
        GameClientHandler handler,
        string name,
        T value)
    {
        var field = typeof(GameClientHandler).GetField(
            name,
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                $"GameClientHandler.{name} was not found.");
        field.SetValue(handler, value);
    }

    private sealed record HandlerFixture(
        ClientSession Session,
        ScriptedLegacyByteTransport Transport,
        GameSessionRegistry Registry,
        GameClientHandler Handler,
        GameCharacter Character) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Registry.Remove(Session);
            Registry.RemoveAccountSession(AccountId, Session);
            await Session.DisposeAsync();
        }
    }

    private sealed class UiStatsGameStore : GameStoreTestStub;
}
