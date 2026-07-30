using Godswar.Server.Application.Characters;
using Godswar.Server.Game;
using Godswar.Server.Networking;
using Godswar.Server.Operations;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class CharacterSnapshotHandlerChecks
{
    private static async Task
        CheckPriorSessionIsDisconnectedBeforeSnapshotAsync()
    {
        const int accountId = 7;
        var store = new FanOutRejectingStore(accountId);
        var registry = new GameSessionRegistry(store: null);
        var snapshotReader =
            new BlockingSnapshotReader(EmptySnapshot(accountId));
        var priorTransport = new ScriptedLegacyByteTransport();
        var currentTransport = new ScriptedLegacyByteTransport();
        await using var priorSession = new ClientSession(
            priorTransport,
            endpointRole: NetworkEndpointRole.Game);
        await using var currentSession = new ClientSession(
            currentTransport,
            endpointRole: NetworkEndpointRole.Game);
        Check.True(
            registry.ReplaceAccountSession(accountId, priorSession) is null,
            "duplicate-session fixture installs its prior owner");

        var handler = new GameClientHandler(
            currentSession,
            store,
            registry,
            snapshotReader,
            WorldContentReaderTestFixtures.Empty,
            legacyAuthenticationAccess: CreateLocalAccess());
        var login = InvokePacketAsync(
            handler,
            CreateLoginPacket("snapshot-user"));

        await snapshotReader.WaitUntilStartedAsync();
        Check.Equal(
            1,
            priorTransport.DisconnectCount,
            "prior account session disconnects before snapshot read blocks");
        Check.True(
            !login.IsCompleted,
            "login remains blocked inside the synthetic snapshot read");

        snapshotReader.Release();
        await login;
        Check.Equal(
            0,
            currentTransport.DisconnectCount,
            "replacement session completes after the snapshot is released");

        registry.RemoveAccountSession(accountId, currentSession);
    }

    private static async Task
        CheckCancelledSnapshotCleansAccountSessionAsync()
    {
        const int accountId = 7;
        var store = new OfflineTrackingStore(accountId);
        var registry = new GameSessionRegistry(store: null);
        var snapshotReader = new CancellationSnapshotReader();
        var loginBytes =
            CreateLoginPacket("snapshot-user").Buffer.ToArray();
        new PacketCipher().Transform(loginBytes);
        var transport = new ScriptedLegacyByteTransport(loginBytes);
        await using var session = new ClientSession(
            transport,
            endpointRole: NetworkEndpointRole.Game);
        var handler = new GameClientHandler(
            session,
            store,
            registry,
            snapshotReader,
            WorldContentReaderTestFixtures.Empty,
            legacyAuthenticationAccess: CreateLocalAccess());
        using var cancellation = new CancellationTokenSource();

        var run = handler.RunAsync(cancellation.Token);
        await snapshotReader.WaitUntilStartedAsync();
        cancellation.Cancel();
        try
        {
            await run;
            throw new InvalidOperationException(
                "Cancelled character snapshot unexpectedly completed.");
        }
        catch (OperationCanceledException)
            when (cancellation.IsCancellationRequested)
        {
        }

        Check.Equal(
            1,
            store.MarkOfflineCalls,
            "cancelled snapshot marks the registered account offline");

        var probeTransport = new ScriptedLegacyByteTransport();
        await using var probeSession = new ClientSession(
            probeTransport,
            endpointRole: NetworkEndpointRole.Game);
        Check.True(
            registry.ReplaceAccountSession(accountId, probeSession) is null,
            "cancelled snapshot removes the newly registered account session");
        registry.RemoveAccountSession(accountId, probeSession);
    }

    private static async Task CheckOccupiedSlotRejectsCreateAsync()
    {
        var source = CharacterSnapshotContractChecks.CreateValidSnapshot();
        var snapshotReader = new CountingSnapshotReader(source);
        var store = new FanOutRejectingStore(source.AccountId);
        var registry = new GameSessionRegistry(store: null);
        var transport = new ScriptedLegacyByteTransport();
        await using var session = new ClientSession(
            transport,
            endpointRole: NetworkEndpointRole.Game);
        var handler = new GameClientHandler(
            session,
            store,
            registry,
            snapshotReader,
            WorldContentReaderTestFixtures.Empty,
            legacyAuthenticationAccess: CreateLocalAccess());

        await InvokePacketAsync(
            handler,
            CreateLoginPacket("snapshot-user"));
        await InvokePacketAsync(
            handler,
            CreatePacket(Opcodes.CreateRole));

        Check.Equal(
            0,
            store.CreateCharacterCalls,
            "occupied SingleCharacterV1 slot rejects CreateRole before store");
        Check.Equal(
            1,
            transport.DisconnectCount,
            "forged occupied-slot CreateRole fails closed");
        registry.RemoveAccountSession(source.AccountId, session);
    }

    private static LegacyAuthenticationAccess CreateLocalAccess()
    {
        var options = new ServerOptions
        {
            RuntimeProfile = "LocalDevelopment",
            Storage = new StorageOptions { Provider = "Json" }
        };
        options.Authentication.AllowLegacyRawAuthentication = true;
        return LegacyAuthenticationAccess.Create(
                ServerRuntimeProfilePolicy.Validate(options)) ??
            throw new InvalidOperationException(
                "The local authentication capability was not created.");
    }

    private static CharacterAccountSnapshot EmptySnapshot(int accountId) =>
        new(
            CharacterSnapshotContractVersions.Current,
            accountId,
            $"protocol-check-empty-{accountId}",
            DateTimeOffset.UtcNow,
            CharacterSlotPolicy.SingleCharacterV1,
            Character: null);

    private sealed class BlockingSnapshotReader(
        CharacterAccountSnapshot snapshot) : ICharacterSnapshotReader
    {
        private readonly TaskCompletionSource<bool> _started =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<CharacterAccountSnapshot> ReadAsync(
            int accountId,
            CancellationToken cancellationToken = default)
        {
            Check.Equal(
                snapshot.AccountId,
                accountId,
                "blocking snapshot uses authenticated account identity");
            _started.TrySetResult(true);
            await _release.Task.WaitAsync(cancellationToken);
            return snapshot;
        }

        public Task WaitUntilStartedAsync() =>
            _started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        public void Release() => _release.TrySetResult(true);
    }

    private sealed class CancellationSnapshotReader :
        ICharacterSnapshotReader
    {
        private readonly TaskCompletionSource<bool> _started =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<CharacterAccountSnapshot> ReadAsync(
            int accountId,
            CancellationToken cancellationToken = default)
        {
            _started.TrySetResult(true);
            await Task.Delay(
                Timeout.InfiniteTimeSpan,
                cancellationToken);
            throw new InvalidOperationException(
                "An infinite cancelled delay unexpectedly completed.");
        }

        public Task WaitUntilStartedAsync() =>
            _started.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private sealed class OfflineTrackingStore(int accountId) :
        GameStoreTestStub
    {
        public int MarkOfflineCalls { get; private set; }

        public override Task<GameAccount?> FindAccountByUsernameAsync(
            string username,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<GameAccount?>(
                new GameAccount
                {
                    Id = accountId,
                    Username = username
                });

        public override Task MarkAccountOfflineAsync(
            int requestedAccountId,
            CancellationToken cancellationToken = default)
        {
            Check.Equal(
                accountId,
                requestedAccountId,
                "offline cleanup uses authenticated account identity");
            MarkOfflineCalls++;
            return Task.CompletedTask;
        }
    }
}
