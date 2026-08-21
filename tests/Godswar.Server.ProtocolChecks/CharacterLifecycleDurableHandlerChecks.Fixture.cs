using System.Buffers.Binary;
using System.Reflection;
using Godswar.Server.Application.Accounts;
using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Commands;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Game;
using Godswar.Server.Networking;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class CharacterLifecycleDurableHandlerChecks
{
    private const int AccountId = 7;
    private const string AccountName = "test2";
    private const string CharacterName = "SnapshotHero";
    private static readonly Guid CreateOperationId =
        Guid.Parse("4888f5a4-cdf6-4d7c-b13e-7102cd42ebea");
    private static readonly Guid DeleteOperationId =
        Guid.Parse("20caeae1-83f2-4cf0-936b-485b5d95a5a5");
    private static readonly Guid OutboxEventId =
        Guid.Parse("fcb0acf1-abd8-4cd0-8bbc-b5ce050e341c");
    private static readonly MethodInfo HandlePacketMethod =
        typeof(GameClientHandler).GetMethod(
            "HandlePacketAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException(
            "GameClientHandler.HandlePacketAsync was not found.");

    private static LifecycleFixture CreateSecureFixture(
        CharacterAccountSnapshot initialSnapshot,
        CharacterAccountSnapshot projectionSnapshot,
        CharacterLifecycleExecutionResult executionResult)
    {
        var transport = new LifecycleSecureTransport();
        var executor = new LifecycleExecutor(executionResult);
        return CreateFixture(
            transport,
            initialSnapshot,
            [projectionSnapshot],
            executor,
            injectLifecycleExecutor: true);
    }

    private static LifecycleFixture CreateRawFixture(
        CharacterAccountSnapshot initialSnapshot,
        params CharacterAccountSnapshot[] projections) =>
        CreateFixture(
            new LifecycleRawTransport(),
            initialSnapshot,
            projections,
            new LifecycleExecutor(
                CharacterLifecycleExecutionResult.InvalidIntent()),
            injectLifecycleExecutor: false);

    private static LifecycleFixture CreateMixedRawFixture(
        CharacterAccountSnapshot initialSnapshot,
        params CharacterAccountSnapshot[] projections) =>
        CreateFixture(
            new LifecycleRawTransport(),
            initialSnapshot,
            projections,
            new LifecycleExecutor(
                CharacterLifecycleExecutionResult.InvalidIntent()),
            injectLifecycleExecutor: true);

    private static LifecycleFixture CreateFixture(
        ILifecycleCaptureTransport transport,
        CharacterAccountSnapshot initialSnapshot,
        IReadOnlyList<CharacterAccountSnapshot> projections,
        LifecycleExecutor executor,
        bool injectLifecycleExecutor)
    {
        var initial =
            CharacterLoadSnapshotHydrator.Hydrate(initialSnapshot);
        var projectedCharacter =
            projections.Select(
                    CharacterLoadSnapshotHydrator.Hydrate)
                .FirstOrDefault(value => value is not null)?
                .Character;
        var store = new LifecycleStore(projectedCharacter);
        var session = new ClientSession(
            transport,
            endpointRole: NetworkEndpointRole.Game);
        var registry = new GameSessionRegistry();
        var snapshotReader =
            new LifecycleSnapshotReader(projections);
        var handler = new GameClientHandler(
            session,
            store,
            registry,
            snapshotReader,
            WorldContentReaderTestFixtures.Empty,
            characterLifecycleCommands:
                injectLifecycleExecutor ? executor : null);
        SetField(
            handler,
            "_account",
            new AccountIdentity(AccountId, AccountName));
        SetField(
            handler,
            "_character",
            initial?.Character);
        SetField(
            handler,
            "_characterLoadSnapshot",
            initial);
        SetField(handler, "_characterSnapshotLoaded", true);
        SetField(
            handler,
            "_characterSnapshotBootstrapPending",
            initial is not null);
        return new LifecycleFixture(
            session,
            transport,
            handler,
            executor,
            store,
            snapshotReader,
            registry);
    }

    private static CharacterAccountSnapshot ActiveSnapshot() =>
        CharacterSnapshotContractChecks.CreateValidSnapshot() with
        {
            ProviderSnapshotToken =
                "character-lifecycle-handler-active"
        };

    private static CharacterAccountSnapshot EmptySnapshot() =>
        ActiveSnapshot() with
        {
            ProviderSnapshotToken =
                "character-lifecycle-handler-empty",
            Character = null
        };

    private static CharacterLifecycleReceipt SuccessReceipt(
        CommandFamily family) =>
        SuccessReceipt(family, RealmId.Tempest);

    private static CharacterLifecycleReceipt SuccessReceipt(
        CommandFamily family,
        RealmId realmId)
    {
        var snapshot = ActiveSnapshot();
        var identity = snapshot.Character?.Identity ??
            throw new InvalidOperationException(
                "Lifecycle receipt fixture requires a character.");
        return new CharacterLifecycleReceipt(
            family,
            family == CommandFamily.CharacterCreate
                ? CharacterLifecycleReceiptStatus.Created
                : CharacterLifecycleReceiptStatus.Deleted,
            AccountId,
            realmId,
            CharacterLifecycleCommandContract.SingleCharacterSlot,
            identity.CharacterId,
            identity.LifecycleVersion,
            identity.Name,
            family == CommandFamily.CharacterDelete
                ? DateTimeOffset.UtcNow.AddDays(7)
                : null,
            family == CommandFamily.CharacterDelete
                ? DateTimeOffset.UtcNow.AddDays(30)
                : null,
            $"audit:character-lifecycle:{family}",
            OutboxEventId);
    }

    private static CharacterLifecycleReceipt RejectionReceipt(
        CommandFamily family) =>
        new(
            family,
            family == CommandFamily.CharacterCreate
                ? CharacterLifecycleReceiptStatus.NameUnavailable
                : CharacterLifecycleReceiptStatus.NameMismatch,
            AccountId,
            CharacterLifecycleCommandContract.SingleCharacterSlot,
            0,
            0,
            CharacterName,
            null,
            null,
            $"audit:character-lifecycle:{family}:rejected",
            null);

    private static async Task InvokeAsync(
        GameClientHandler handler,
        GamePacket packet)
    {
        try
        {
            var task = HandlePacketMethod.Invoke(
                handler,
                [packet, CancellationToken.None]) as Task
                ?? throw new InvalidOperationException(
                    "Character lifecycle handler returned no task.");
            await task;
        }
        catch (TargetInvocationException ex)
            when (ex.InnerException is not null)
        {
            throw ex.InnerException;
        }
    }

    private static GamePacket CreateRolePacket(
        Guid? operationId,
        string characterName = CharacterName)
    {
        var packet = new byte[80];
        WriteHeader(packet, Opcodes.CreateRole);
        PacketText.WriteFixedAscii(
            packet.AsSpan(4, 32),
            characterName);
        packet[36] = 1;
        packet[37] = GameDefaults.SpartaCamp;
        packet[38] = 1;
        packet[39] = 7;
        packet[40] = 53;
        packet[41] = 0;
        packet[74] = 0;
        return new GamePacket(packet, operationId);
    }

    private static GamePacket DeleteRolePacket(
        Guid? operationId,
        string untrustedUsername,
        string characterName = CharacterName)
    {
        var packet = new byte[68];
        WriteHeader(packet, Opcodes.DeleteRole);
        PacketText.WriteFixedAscii(
            packet.AsSpan(4, 32),
            untrustedUsername);
        PacketText.WriteFixedAscii(
            packet.AsSpan(36, 32),
            characterName);
        return new GamePacket(packet, operationId);
    }

    private static void WriteHeader(
        Span<byte> packet,
        ushort opcode)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet,
            checked((ushort)packet.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet[2..],
            opcode);
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

    private sealed record LifecycleFixture(
        ClientSession Session,
        ILifecycleCaptureTransport Transport,
        GameClientHandler Handler,
        LifecycleExecutor Executor,
        LifecycleStore Store,
        LifecycleSnapshotReader SnapshotReader,
        GameSessionRegistry Registry) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Registry.Remove(Session);
            await Session.DisposeAsync();
        }
    }

    private sealed class LifecycleExecutor(
        CharacterLifecycleExecutionResult executionResult) :
        ICharacterLifecycleCommandExecutor
    {
        public int CreateCount { get; private set; }
        public int DeleteCount { get; private set; }
        public CommandEnvelope<CharacterCreateCommand>?
            CreateEnvelope { get; private set; }
        public CommandEnvelope<CharacterDeleteCommand>?
            DeleteEnvelope { get; private set; }

        public Task<CharacterLifecycleExecutionResult> ExecuteAsync(
            CommandEnvelope<CharacterCreateCommand> envelope,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CreateCount++;
            CreateEnvelope = envelope;
            return Task.FromResult(executionResult);
        }

        public Task<CharacterLifecycleExecutionResult> ExecuteAsync(
            CommandEnvelope<CharacterDeleteCommand> envelope,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DeleteCount++;
            DeleteEnvelope = envelope;
            return Task.FromResult(executionResult);
        }

        public Task<CharacterLifecycleExecutionResult> ExecuteAsync(
            CommandEnvelope<CharacterRestoreCommand> envelope,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException(
                "Handler checks do not restore characters.");

        public Task<CharacterLifecycleExecutionResult> ExecuteAsync(
            CommandEnvelope<CharacterPurgeCommand> envelope,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException(
                "Handler checks do not purge characters.");
    }

    private sealed class LifecycleSnapshotReader(
        IReadOnlyList<CharacterAccountSnapshot> snapshots) :
        ICharacterSnapshotReader
    {
        private readonly Queue<CharacterAccountSnapshot> _snapshots =
            new(snapshots);
        private CharacterAccountSnapshot? _last =
            snapshots.LastOrDefault();

        public int ReadCount { get; private set; }

        public Task<CharacterAccountSnapshot> ReadAsync(
            int accountId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Check.Equal(
                AccountId,
                accountId,
                "lifecycle projection uses authenticated account");
            ReadCount++;
            if (_snapshots.Count > 0)
            {
                _last = _snapshots.Dequeue();
            }
            return Task.FromResult(
                _last ?? throw new InvalidOperationException(
                    "Lifecycle projection fixture is empty."));
        }
    }

    private sealed class LifecycleStore(GameCharacter? createdCharacter) :
        GameStoreTestStub
    {
        public int CreateCount { get; private set; }
        public int DeleteCount { get; private set; }

        public override Task<GameCharacter> CreateCharacterAsync(
            int accountId,
            GameCharacter character,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CreateCount++;
            return Task.FromResult(
                createdCharacter ?? throw new InvalidOperationException(
                    "Legacy create fixture has no projected character."));
        }

        public override Task<bool> DeleteCharacterAsync(
            int accountId,
            string characterName,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DeleteCount++;
            return Task.FromResult(true);
        }
    }
}
