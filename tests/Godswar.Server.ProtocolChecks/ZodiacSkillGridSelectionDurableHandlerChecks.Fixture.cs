using System.Buffers.Binary;
using System.Reflection;
using System.Security.Cryptography;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Zodiac;
using Godswar.Server.Game;
using Godswar.Server.Networking;
using Godswar.Server.Networking.Secure;
using Godswar.Server.Networking.Secure.Realtime;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class
    ZodiacSkillGridSelectionDurableHandlerChecks
{
    private const int AccountId = 7;
    private const int CharacterId = 19;
    private const int GridIndex = 1;
    private const int SelectedKind = 10_057;
    private static readonly Guid OperationId =
        Guid.Parse("0be13d22-8b3d-43e9-a74f-1e9595cf6d9c");
    private static readonly MethodInfo HandlePacketMethod =
        typeof(GameClientHandler).GetMethod(
            "HandlePacketAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException(
            "GameClientHandler.HandlePacketAsync was not found.");

    private static HandlerFixture CreateFixture(
        ZodiacSkillGridSelectionExecutionResult execution)
    {
        var store = new SelectionCompatibilityStore();
        var transport = new SelectionCaptureTransport();
        var session = new ClientSession(transport);
        var registry = new GameSessionRegistry(store);
        var registryMirror = CreateCharacter();
        registry.JoinMap(
            session,
            AccountId,
            registryMirror,
            objectId: 0x0000_1448);
        var character = CreateCharacter();
        var executor = new CapturingExecutor(
            (_, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(execution);
            });
        var handler = new GameClientHandler(
            session,
            store,
            registry,
            CharacterSnapshotReaderTestFixtures.Unused,
            WorldContentReaderTestFixtures.Empty,
            zodiacSkillGridSelectionCommands: executor);
        SetField(
            handler,
            "_account",
            new GameAccount
            {
                Id = AccountId,
                Username = "zodiac-selection-handler-check"
            });
        SetField(handler, "_character", character);
        return new HandlerFixture(
            session,
            transport,
            registry,
            handler,
            character,
            registryMirror,
            executor,
            store);
    }

    private static GameCharacter CreateCharacter()
    {
        var levels = ZodiacSkillGridCatalog.CreateEmptyLevels();
        var selected = ZodiacSkillGridCatalog.CreateEmptySkillIds();
        levels[GridIndex] = 1;
        return new GameCharacter
        {
            Id = CharacterId,
            AccountId = AccountId,
            Name = "DurableZodiacSelectionHero",
            Profession = 3,
            Level = 80,
            CurrentMap = 3,
            CurrentHp = 7_777,
            CurrentMp = 888,
            TalentPoints = 890,
            Silver = 654_321,
            Gold = 5_000,
            Equipment = GameDefaults.DefaultEquipment(3),
            KitBag = GameDefaults.StarterKitBag,
            ZodiacType = 2,
            ZodiacLevel = 9,
            ZodiacEnergy = 1_000,
            ZodiacSkillGridLevels = levels,
            ZodiacSkillGridSkillIds = selected
        };
    }

    private static GamePacket CreateSelectionPacket(
        Guid? operationId,
        int tail = 0)
    {
        var packet = Convert.FromHexString(
            "1800392800000000FF006600010000004927000000000000");
        BinaryPrimitives.WriteInt32LittleEndian(
            packet.AsSpan(20, 4),
            tail);
        return new GamePacket(packet, operationId);
    }

    private static async Task InvokeAsync(
        GameClientHandler handler,
        GamePacket packet)
    {
        var invocation = HandlePacketMethod.Invoke(
            handler,
            [packet, CancellationToken.None]) as Task
            ?? throw new InvalidOperationException(
                "SID102 handler did not return a task.");
        await invocation;
    }

    private static ZodiacSkillGridSelectionExecutionReceipt
        SuccessfulReceipt() =>
        new(
            CharacterId,
            ZodiacSkillGridSelectionReceiptStatus.Succeeded,
            GridIndex,
            currentLevel: 1,
            previousSkillKind: -1,
            selectedSkillKind: SelectedKind,
            aggregateRevision: 1,
            auditReference: "101",
            outboxEventId:
                Guid.Parse("9fa63503-f98b-4624-8ee6-9498acbf2923"));

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
        SelectionCaptureTransport Transport,
        GameSessionRegistry Registry,
        GameClientHandler Handler,
        GameCharacter Character,
        GameCharacter RegistryMirror,
        CapturingExecutor Executor,
        SelectionCompatibilityStore Store) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Registry.Remove(Session);
            await Session.DisposeAsync();
        }
    }

    private sealed class CapturingExecutor(
        Func<
            CommandEnvelope<ZodiacSkillGridSelectionCommand>,
            CancellationToken,
            Task<ZodiacSkillGridSelectionExecutionResult>> execute) :
        IZodiacSkillGridSelectionCommandExecutor
    {
        public int Count { get; private set; }
        public CommandEnvelope<ZodiacSkillGridSelectionCommand>?
            LastEnvelope { get; private set; }

        public Task<ZodiacSkillGridSelectionExecutionResult> ExecuteAsync(
            CommandEnvelope<ZodiacSkillGridSelectionCommand> envelope,
            CancellationToken cancellationToken = default)
        {
            Count++;
            LastEnvelope = envelope;
            return execute(envelope, cancellationToken);
        }
    }

    private sealed class SelectionCompatibilityStore :
        GameStoreTestStub
    {
        public int SelectionCount { get; private set; }

        public override Task<ZodiacSkillGridSelectionResult?>
            SelectZodiacSkillGridAsync(
                int accountId,
                int characterId,
                int gridIndex,
                int selectedSkillKind,
                CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SelectionCount++;
            return Task.FromResult<ZodiacSkillGridSelectionResult?>(
                null);
        }
    }

    private sealed class SelectionCaptureTransport :
        ILegacyByteTransport,
        ISecureControlChannel,
        ISecureCommandResultTransport
    {
        private readonly object _gate = new();
        private readonly List<byte[]> _legacyWrites = [];
        private readonly List<SecureLegacyCommandResult> _results = [];
        private readonly List<string> _events = [];

        public SelectionCaptureTransport()
        {
            var connectionId = Enumerable.Repeat(
                (byte)0x91,
                SecureProtocolConstants.ConnectionIdBytes).ToArray();
            var clientInstanceId = Enumerable.Repeat(
                (byte)0xA2,
                SecureProtocolConstants.ClientInstanceIdBytes).ToArray();
            var originHash = Enumerable.Repeat(
                (byte)0xB3,
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

        public string RemoteEndPoint =>
            "secure-zodiac-selection-handler-check";
        public SecureConnectionContext ConnectionContext { get; }
        public SecureBoundGamePrincipal? BoundGamePrincipal => null;
        public bool SupportsRealtimeMovement => false;
        public bool IsRealtimeMovementActive => false;

        public IReadOnlyList<SecureLegacyCommandResult> CommandResults
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

        public IReadOnlyList<byte[]> ReadLegacyPackets()
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
                var length =
                    BinaryPrimitives.ReadUInt16LittleEndian(
                        encrypted.AsSpan(offset, 2));
                if (length < 4 ||
                    length > encrypted.Length - offset)
                {
                    throw new InvalidDataException(
                        "Captured SID102 stream has an invalid frame.");
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
                    "SID102 checks cannot issue login grants."));

        public void MarkAuthenticated()
        {
        }

        public void Disconnect()
        {
        }

        public ValueTask DisposeAsync() =>
            ValueTask.CompletedTask;
    }
}
