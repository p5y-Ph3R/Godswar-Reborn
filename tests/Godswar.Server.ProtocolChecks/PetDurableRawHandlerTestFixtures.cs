using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Reflection;
using Godswar.Server.Application.Accounts;
using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Pets;
using Godswar.Server.Game;
using Godswar.Server.Networking;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal sealed class PetDurableRawHandlerFixture : IAsyncDisposable
{
    private static readonly MethodInfo HandlePacketMethod =
        typeof(GameClientHandler).GetMethod(
            "HandlePacketAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException(
            "GameClientHandler.HandlePacketAsync was not found.");

    private PetDurableRawHandlerFixture(
        ClientSession session,
        ScriptedLegacyByteTransport transport,
        GameClientHandler handler,
        GameSessionRegistry registry)
    {
        Session = session;
        Transport = transport;
        Handler = handler;
        Registry = registry;
    }

    public ClientSession Session { get; }

    public ScriptedLegacyByteTransport Transport { get; }

    public GameClientHandler Handler { get; }

    private GameSessionRegistry Registry { get; }

    public static PetDurableRawHandlerFixture Create(
        GameCharacter liveCharacter,
        GameCharacter persistedCharacter,
        IReadOnlyList<PetBootstrapSnapshot> persistedPets,
        IPetDurableCommandExecutor executor,
        bool hasLocalDevelopmentCapability,
        short openedPetShedCells =
            PetShedCapacityPolicy.DefaultOpenedCellCount)
    {
        ArgumentNullException.ThrowIfNull(liveCharacter);
        ArgumentNullException.ThrowIfNull(persistedCharacter);
        ArgumentNullException.ThrowIfNull(persistedPets);
        ArgumentNullException.ThrowIfNull(executor);

        var transport = new ScriptedLegacyByteTransport(
            remoteEndPoint: "raw-local-pet-handler-check");
        var session = new ClientSession(transport);
        var registry = GameHandlerOwnershipTestFences.CreateRegistry(
            session,
            liveCharacter.AccountId,
            liveCharacter);
        var snapshot = PetDurableHandlerFixture.CreateSnapshot(
            persistedCharacter,
            persistedPets,
            openedPetShedCells);
        var snapshotReader = new RawPetSnapshotReader(snapshot);
        var localAccess = hasLocalDevelopmentCapability
            ? LegacyAuthenticationAccess.Create(
                new ValidatedServerRuntimeProfile(
                    ServerRuntimeProfileKind.LocalDevelopment,
                    GameStorageProviderKind.Postgres,
                    ServerListenerTransport.RawTcp,
                    AllowsLegacyAuthentication: true))
            : null;
        var handler = new GameClientHandler(
            session,
            new RawPetHandlerStore(),
            registry,
            snapshotReader,
            WorldContentReaderTestFixtures.Empty,
            legacyAuthenticationAccess: localAccess,
            petDurableCommands: executor,
            ownedPetSnapshots: snapshotReader,
            itemContent: TestItemContent.Content,
            petContent: PetContentTestCatalog.Instance);
        PetDurableHandlerFixture.SetField(
            handler,
            "_account",
            new AccountIdentity(
                liveCharacter.AccountId,
                "raw-local-pet-check"));
        PetDurableHandlerFixture.SetField(
            handler,
            "_character",
            liveCharacter);
        PetDurableHandlerFixture.SetField(
            handler,
            "_requiresDurablePlayerCommands",
            true);
        return new(session, transport, handler, registry);
    }

    public async Task InvokeAsync(GamePacket packet)
    {
        var task = HandlePacketMethod.Invoke(
            Handler,
            [packet, CancellationToken.None]) as Task
            ?? throw new InvalidOperationException(
                "GameClientHandler.HandlePacketAsync returned no task.");
        await task;
    }

    public IReadOnlyList<byte[]> ReadLegacyPackets()
    {
        var clear = Transport.WrittenBytes;
        new PacketCipher().Transform(clear);
        var packets = new List<byte[]>();
        var offset = 0;
        while (offset < clear.Length)
        {
            if (clear.Length - offset < sizeof(ushort))
            {
                throw new InvalidDataException(
                    "Captured raw pet stream ended inside a frame header.");
            }

            var length = BinaryPrimitives.ReadUInt16LittleEndian(
                clear.AsSpan(offset, sizeof(ushort)));
            if (length < 4 || length > clear.Length - offset)
            {
                throw new InvalidDataException(
                    "Captured raw pet stream has an invalid frame.");
            }

            packets.Add(clear.AsSpan(offset, length).ToArray());
            offset += length;
        }

        return packets;
    }

    public async ValueTask DisposeAsync()
    {
        Registry.Remove(Session);
        await Session.DisposeAsync();
    }

    private sealed class RawPetHandlerStore : GameStoreTestStub
    {
    }

    private sealed class RawPetSnapshotReader(
        CharacterAccountSnapshot snapshot) :
        ICharacterSnapshotReader,
        IOwnedPetSnapshotReader
    {
        public Task<CharacterAccountSnapshot> ReadAsync(
            int accountId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Check.Equal(
                snapshot.AccountId,
                accountId,
                "raw pet snapshot account");
            return Task.FromResult(snapshot);
        }

        public Task<ImmutableArray<CharacterPetSnapshot>>
            ReadOwnedPetsAsync(
                int accountId,
                int characterId,
                CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Check.Equal(
                snapshot.AccountId,
                accountId,
                "raw pet projection account");
            var character = snapshot.Character ??
                throw new InvalidOperationException(
                    "Raw pet fixture snapshot has no character.");
            Check.Equal(
                character.Identity.CharacterId,
                characterId,
                "raw pet projection character");
            return Task.FromResult(character.Pets);
        }
    }
}
