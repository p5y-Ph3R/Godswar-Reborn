using System.Buffers.Binary;
using Godswar.Server.Application.Accounts;
using Godswar.Server.Game;
using Godswar.Server.Networking;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class
    ZodiacSkillGridSelectionDurableHandlerChecks
{
    private static async Task
        CheckLocalRawAttackAndDefenseSelectionAsync()
    {
        await using (var attack = CreateRawFixture())
        {
            await InvokeAsync(
                attack.Handler,
                CreateSelectionPacket(operationId: null));
            AssertRawSelection(
                attack,
                GridIndex,
                SelectedKind,
                "learned own-class attack");
        }

        await using (var defense = CreateRawFixture())
        {
            await InvokeAsync(
                defense.Handler,
                CreateSelectionPacket(
                    operationId: null,
                    gridIndex: DefenseGridIndex,
                    selectedKind: DefenseSelectedKind));
            AssertRawSelection(
                defense,
                DefenseGridIndex,
                DefenseSelectedKind,
                "unlearned foreign-class defense");
        }
    }

    private static void AssertRawSelection(
        RawHandlerFixture fixture,
        int gridIndex,
        int selectedKind,
        string description)
    {
        Check.Equal(
            1,
            fixture.Store.SelectionCount,
            $"local raw SID102 persists {description} selection");
        Check.Equal(
            selectedKind,
            fixture.Character.ZodiacSkillGridSkillIds[gridIndex],
            $"local raw SID102 updates {description} live grid");
        Check.Equal(
            selectedKind,
            fixture.RegistryMirror.ZodiacSkillGridSkillIds[gridIndex],
            $"local raw SID102 updates {description} registry grid");

        var packets = fixture.Transport.ReadLegacyPackets();
        Check.True(
            packets.Count == 2 &&
            ZodiacSid(packets[0]) == 102 &&
            BinaryPrimitives.ReadInt32LittleEndian(
                packets[0].AsSpan(12, 4)) == gridIndex &&
            BinaryPrimitives.ReadInt32LittleEndian(
                packets[0].AsSpan(16, 4)) == selectedKind &&
            ZodiacSid(packets[1]) == 1,
            $"local raw SID102 acknowledges and syncs {description}");
    }

    private static RawHandlerFixture CreateRawFixture()
    {
        var transport = new RawSelectionCaptureTransport();
        var session = new ClientSession(transport);
        var authoritative = CreateCharacter();
        var store = new SelectionCompatibilityStore
        {
            AuthoritativeCharacter = authoritative
        };
        var registry = new GameSessionRegistry(store);
        var registryMirror = CreateCharacter();
        var ownership = GameHandlerOwnershipTestFences.Bind(
            registry,
            session,
            AccountId,
            registryMirror);
        registry.JoinMap(
            session,
            AccountId,
            registryMirror,
            objectId: 0x0000_1448);
        var character = CreateCharacter();
        character.CheckpointOwnerId = ownership.OwnerId;
        character.CheckpointOwnerGeneration = ownership.Generation;
        var localAccess = LegacyAuthenticationAccess.Create(
            new ValidatedServerRuntimeProfile(
                ServerRuntimeProfileKind.LocalDevelopment,
                GameStorageProviderKind.Postgres,
                ServerListenerTransport.RawTcp,
                AllowsLegacyAuthentication: true));
        var handler = new GameClientHandler(
            session,
            store,
            registry,
            CharacterSnapshotReaderTestFixtures.Unused,
            WorldContentReaderTestFixtures.Empty,
            legacyAuthenticationAccess: localAccess);
        SetField(
            handler,
            "_account",
            new AccountIdentity(AccountId, "raw-zodiac-selection-check"));
        SetField(handler, "_character", character);
        SetField(handler, "_requiresDurablePlayerCommands", true);
        return new RawHandlerFixture(
            session,
            transport,
            registry,
            handler,
            character,
            registryMirror,
            store);
    }

    private sealed record RawHandlerFixture(
        ClientSession Session,
        RawSelectionCaptureTransport Transport,
        GameSessionRegistry Registry,
        GameClientHandler Handler,
        GameCharacter Character,
        GameCharacter RegistryMirror,
        SelectionCompatibilityStore Store) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Registry.Remove(Session);
            await Session.DisposeAsync();
        }
    }

    private sealed class RawSelectionCaptureTransport :
        ILegacyByteTransport
    {
        private readonly List<byte[]> _writes = [];

        public string RemoteEndPoint => "raw-zodiac-selection-check";

        public ValueTask<int> ReadAsync(
            Memory<byte> destination,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(0);
        }

        public ValueTask WriteAsync(
            ReadOnlyMemory<byte> source,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _writes.Add(source.ToArray());
            return ValueTask.CompletedTask;
        }

        public IReadOnlyList<byte[]> ReadLegacyPackets()
        {
            var encrypted = _writes
                .SelectMany(static write => write)
                .ToArray();
            new PacketCipher().Transform(encrypted);

            var packets = new List<byte[]>();
            var offset = 0;
            while (offset < encrypted.Length)
            {
                var length = BinaryPrimitives.ReadUInt16LittleEndian(
                    encrypted.AsSpan(offset, sizeof(ushort)));
                if (length < sizeof(uint) ||
                    length > encrypted.Length - offset)
                {
                    throw new InvalidDataException(
                        "Raw SID102 capture has an invalid frame.");
                }

                packets.Add(encrypted.AsSpan(offset, length).ToArray());
                offset += length;
            }

            return packets;
        }

        public void Disconnect()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
