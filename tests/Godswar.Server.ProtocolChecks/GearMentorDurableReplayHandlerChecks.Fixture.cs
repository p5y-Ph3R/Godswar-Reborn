using System.Buffers.Binary;
using System.Reflection;
using System.Security.Cryptography;
using Godswar.Server.Application.Accounts;
using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Inventory;
using Godswar.Server.Game;
using Godswar.Server.Networking;
using Godswar.Server.Networking.Secure;
using Godswar.Server.Networking.Secure.Realtime;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class GearMentorDurableReplayHandlerChecks
{
    private static ReplayFixture CreateFixture(
        GearMentorMaterialConversionExecutionResult replay)
    {
        var snapshot =
            CharacterSnapshotContractChecks.CreateValidSnapshot();
        var hydrated =
            CharacterLoadSnapshotHydrator.Hydrate(snapshot)
            ?? throw new InvalidOperationException(
                "Replay fixture character did not hydrate.");
        var liveCharacter = hydrated.Character;
        var persistedKitBag = liveCharacter.KitBag;
        liveCharacter.KitBag = GameDefaults.EmptyKitBag;

        var transport = new ReplayCaptureTransport();
        var session = new ClientSession(transport);
        var snapshotReader = new ReplaySnapshotReader(snapshot);
        var executor = new ReplayExecutor(
            snapshot.AccountId,
            liveCharacter.Id,
            ReplayOperationId,
            replay);
        var registry = GameHandlerOwnershipTestFences.CreateRegistry(
            session,
            snapshot.AccountId,
            liveCharacter);
        var handler = new GameClientHandler(
            session,
            new ReplayGameStore(),
            registry,
            snapshotReader,
            WorldContentReaderTestFixtures.Empty,
            gearMentorMaterialConversionCommands: executor);
        SetField(
            handler,
            "_account",
            new AccountIdentity(
                snapshot.AccountId,
                "durable-replay-check"));
        SetField(handler, "_character", liveCharacter);

        return new ReplayFixture(
            session,
            transport,
            handler,
            executor,
            snapshotReader,
            liveCharacter,
            persistedKitBag);
    }

    private static GearMentorMaterialConversionExecutionReceipt
        CreateSuccessfulTransformReceipt()
    {
        const long inventoryRevision = 41;
        return new GearMentorMaterialConversionExecutionReceipt(
            CommandFamily.GearMentorTransformCrystal,
            characterId: 19,
            GearMentorMaterialConversionResultStatus.Succeeded,
            GearMentorMaterialConversionNativeResults
                .TransformSucceededSubId,
            selectedKitBagSlot: 0,
            sourceItemId: 4231,
            outputItemId: 4230,
            outputQuantity: 8,
            isBound: false,
            inventoryRevision,
            auditReference: "audit:material-conversion:replay-check",
            outboxEventId:
                Guid.Parse("5e214555-c0a1-48a2-b123-dff49eaab3ac"));
    }

    private static GamePacket CreateFunctionActionPacket(
        uint npcId,
        int wireSubId,
        Guid operationId)
    {
        var packet = new byte[
            4 + GearEnhancerProtocol.FunctionActionPayloadLength];
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet,
            checked((ushort)packet.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(2, 2),
            Opcodes.NpcFunctionAction);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(4, 4),
            npcId);
        BinaryPrimitives.WriteInt32LittleEndian(
            packet.AsSpan(8, 4),
            GearEnhancerProtocol.DialogIndex);
        BinaryPrimitives.WriteInt32LittleEndian(
            packet.AsSpan(16, 4),
            wireSubId);
        return new GamePacket(packet, operationId);
    }

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

    private sealed record ReplayFixture(
        ClientSession Session,
        ReplayCaptureTransport Transport,
        GameClientHandler Handler,
        ReplayExecutor Executor,
        ReplaySnapshotReader SnapshotReader,
        GameCharacter LiveCharacter,
        string PersistedKitBag) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() =>
            Session.DisposeAsync();
    }

    private sealed class ReplayExecutor(
        int expectedAccountId,
        int expectedCharacterId,
        Guid expectedOperationId,
        GearMentorMaterialConversionExecutionResult replay) :
        IGearMentorMaterialConversionCommandExecutor
    {
        public int ExecuteCount { get; private set; }

        public int TransformReplayCount { get; private set; }

        public Task<GearMentorMaterialConversionExecutionResult>
            ExecuteAsync(
                CommandEnvelope<GearMentorTransformCrystalCommand>
                    envelope,
                CancellationToken cancellationToken = default)
        {
            ExecuteCount++;
            throw new InvalidOperationException(
                "Pre-route replay cannot execute a transform.");
        }

        public Task<GearMentorMaterialConversionExecutionResult>
            ExecuteAsync(
                CommandEnvelope<GearMentorCombineGemPiecesCommand>
                    envelope,
                CancellationToken cancellationToken = default)
        {
            ExecuteCount++;
            throw new InvalidOperationException(
                "Pre-route replay cannot execute a combination.");
        }

        public Task<GearMentorMaterialConversionExecutionResult>
            TryReplayTransformAsync(
                CommandSubject subject,
                PlayerOwnershipFence ownership,
                Guid clientOperationId,
                CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TransformReplayCount++;
            AssertIdentity(subject, clientOperationId);
            return Task.FromResult(replay);
        }

        public Task<GearMentorMaterialConversionExecutionResult>
            TryReplayCombineAsync(
                CommandSubject subject,
                PlayerOwnershipFence ownership,
                Guid clientOperationId,
                CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException(
                "Transform replay used the combination inbox.");

        private void AssertIdentity(
            CommandSubject subject,
            Guid clientOperationId)
        {
            Check.Equal(
                expectedAccountId,
                subject.AccountId,
                "replay subject account");
            Check.Equal(
                expectedCharacterId,
                subject.CharacterId,
                "replay subject character");
            Check.Equal(
                expectedOperationId,
                clientOperationId,
                "replay operation identity");
        }
    }

    private sealed class ReplaySnapshotReader(
        CharacterAccountSnapshot snapshot) :
        ICharacterSnapshotReader
    {
        public int ReadCount { get; private set; }

        public Task<CharacterAccountSnapshot> ReadAsync(
            int accountId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadCount++;
            Check.Equal(
                snapshot.AccountId,
                accountId,
                "replay projection account");
            return Task.FromResult(snapshot);
        }
    }

    private sealed class ReplayGameStore : GameStoreTestStub;

    private sealed class ReplayCaptureTransport :
        ILegacyByteTransport,
        ISecureControlChannel,
        ISecureCommandResultTransport
    {
        private readonly object _gate = new();
        private readonly List<byte[]> _legacyWrites = [];
        private readonly List<SecureLegacyCommandResult> _results = [];
        private readonly List<string> _events = [];
        private int _disconnected;
        private int _disposed;

        public ReplayCaptureTransport()
        {
            var connectionId = Enumerable.Repeat(
                (byte)0x31,
                SecureProtocolConstants.ConnectionIdBytes).ToArray();
            var clientInstanceId = Enumerable.Repeat(
                (byte)0x42,
                SecureProtocolConstants.ClientInstanceIdBytes).ToArray();
            var originHash = Enumerable.Repeat(
                (byte)0x53,
                SecureProtocolConstants.BuildHashBytes).ToArray();
            try
            {
                ConnectionContext = new SecureConnectionContext(
                    SecureEndpointRole.Game,
                    SecureProtocolConstants.ProtocolMajor,
                    SecureProtocolConstants.ProtocolMinor,
                    connectionId,
                    clientInstanceId,
                    originHash);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(connectionId);
                CryptographicOperations.ZeroMemory(clientInstanceId);
                CryptographicOperations.ZeroMemory(originHash);
            }
        }

        public string RemoteEndPoint => "secure-replay-check";

        public SecureConnectionContext ConnectionContext { get; }

        public SecureBoundGamePrincipal? BoundGamePrincipal => null;

        public bool SupportsRealtimeMovement => false;

        public bool IsRealtimeMovementActive => false;

        public IReadOnlyList<SecureLegacyCommandResult>
            CommandResults
        {
            get
            {
                lock (_gate)
                {
                    return _results.ToArray();
                }
            }
        }

        public IReadOnlyList<string> Events
        {
            get
            {
                lock (_gate)
                {
                    return _events.ToArray();
                }
            }
        }

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
            lock (_gate)
            {
                _legacyWrites.Add(source.ToArray());
                _events.Add("legacy");
            }
            return ValueTask.CompletedTask;
        }

        public ValueTask SendLegacyCommandResultAsync(
            SecureLegacyCommandResult result,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                _results.Add(result);
                _events.Add("command-result");
            }
            return ValueTask.CompletedTask;
        }

        public IReadOnlyList<byte[]> ReadClearLegacyPackets()
        {
            byte[] encrypted;
            lock (_gate)
            {
                encrypted = _legacyWrites
                    .SelectMany(static value => value)
                    .ToArray();
            }
            new PacketCipher().Transform(encrypted);

            var packets = new List<byte[]>();
            var offset = 0;
            while (offset < encrypted.Length)
            {
                if (encrypted.Length - offset < 4)
                {
                    throw new InvalidDataException(
                        "Captured legacy stream ends inside a header.");
                }
                var length = BinaryPrimitives.ReadUInt16LittleEndian(
                    encrypted.AsSpan(offset, 2));
                if (length < 4 ||
                    length > encrypted.Length - offset)
                {
                    throw new InvalidDataException(
                        "Captured legacy stream contains an invalid frame.");
                }
                packets.Add(
                    encrypted.AsSpan(offset, length).ToArray());
                offset += length;
            }
            return packets;
        }

        public bool TryTakeRealtimeMovement(
            out SecureRealtimeMovementIngress ingress)
        {
            ingress = default;
            return false;
        }

        public bool TryPublishRealtimeSnapshot(
            in SecureRealtimePositionSnapshot snapshot) => false;

        public ValueTask SendGameGrantAsync(
            SecureGameGrant grant,
            CancellationToken cancellationToken) =>
            ValueTask.FromException(
                new InvalidOperationException(
                    "A game replay check cannot issue login grants."));

        public void MarkAuthenticated()
        {
        }

        public void Disconnect() =>
            Interlocked.Exchange(ref _disconnected, 1);

        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref _disposed, 1);
            Disconnect();
            return ValueTask.CompletedTask;
        }
    }
}
