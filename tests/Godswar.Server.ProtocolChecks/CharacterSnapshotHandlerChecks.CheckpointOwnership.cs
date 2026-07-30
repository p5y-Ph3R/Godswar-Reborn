using System.Collections.Concurrent;
using Godswar.Server.Application.Characters;
using Godswar.Server.Game;
using Godswar.Server.Networking;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;

namespace Godswar.Server.ProtocolChecks;

internal static partial class CharacterSnapshotHandlerChecks
{
    private static async Task
        CheckReplacedSessionCannotStealCheckpointOwnershipAsync()
    {
        var source =
            CharacterSnapshotContractChecks.CreateValidSnapshot();
        var character = source.Character ??
            throw new InvalidOperationException(
                "Checkpoint race fixture has no character.");
        var checkpoints = new InterleavedCheckpointCoordinator(
            character.Location.PositionRevision,
            character.Vitals.Revision);
        var registry = new GameSessionRegistry(
            store: null,
            checkpointCoordinator: checkpoints);
        var store = new FanOutRejectingStore(source.AccountId);
        var staleTransport = new ScriptedLegacyByteTransport();
        var replacementTransport =
            new ScriptedLegacyByteTransport();
        await using var staleSession = new ClientSession(
            staleTransport,
            endpointRole: NetworkEndpointRole.Game);
        await using var replacementSession = new ClientSession(
            replacementTransport,
            endpointRole: NetworkEndpointRole.Game);
        var staleHandler = new GameClientHandler(
            staleSession,
            store,
            registry,
            new CountingSnapshotReader(source),
            WorldContentReaderTestFixtures.Empty,
            legacyAuthenticationAccess: CreateLocalAccess(),
            characterCheckpoints: checkpoints);
        var replacementHandler = new GameClientHandler(
            replacementSession,
            store,
            registry,
            new CountingSnapshotReader(source),
            WorldContentReaderTestFixtures.Empty,
            legacyAuthenticationAccess: CreateLocalAccess(),
            characterCheckpoints: checkpoints);

        await InvokePacketAsync(
            staleHandler,
            CreateLoginPacket("snapshot-user"));
        var staleBytesBeforeEnter =
            staleTransport.WrittenBytes.Length;
        var staleEnter = InvokePacketAsync(
            staleHandler,
            CreatePacket(Opcodes.EnterGame));
        await checkpoints.FirstAcquireStarted.WaitAsync(
            TimeSpan.FromSeconds(5));

        await InvokePacketAsync(
            replacementHandler,
            CreateLoginPacket("snapshot-user"));
        Check.True(
            registry.IsCurrentAccountSession(
                source.AccountId,
                replacementSession),
            "replacement session becomes the authoritative account owner");
        Check.Equal(
            1,
            staleTransport.DisconnectCount,
            "replacement disconnects the stale handler");

        var replacementEnter = InvokePacketAsync(
            replacementHandler,
            CreatePacket(Opcodes.EnterGame));
        Check.Equal(
            1,
            checkpoints.AcquireCalls,
            "replacement acquisition waits behind the stale in-flight call");
        Check.True(
            !replacementEnter.IsCompleted,
            "replacement enter remains behind the account acquisition gate");

        checkpoints.ReleaseFirstAcquire();
        await Task.WhenAll(staleEnter, replacementEnter).WaitAsync(
            TimeSpan.FromSeconds(5));

        Check.Equal(
            2,
            checkpoints.AcquireCalls,
            "both handlers made one serialized acquisition attempt");
        Check.Equal(
            1,
            checkpoints.ReleaseCalls,
            "stale post-acquire ownership is released exactly once");
        Check.True(
            checkpoints.FirstOwnerId != Guid.Empty &&
            checkpoints.SecondOwnerId != Guid.Empty &&
            checkpoints.FirstOwnerId != checkpoints.SecondOwnerId,
            "each handler uses an independent checkpoint owner UUID");
        Check.Equal(
            checkpoints.SecondOwnerId,
            checkpoints.CurrentOwnerId ?? Guid.Empty,
            "the current replacement owns the final checkpoint fence");
        Check.True(
            checkpoints.Events.SequenceEqual(
            [
                "acquire-1-start",
                "acquire-1-commit",
                "release-1",
                "acquire-2-start",
                "acquire-2-commit"
            ]),
            "stale acquisition commits and releases before replacement acquire");
        Check.Equal(
            staleBytesBeforeEnter,
            staleTransport.WrittenBytes.Length,
            "stale handler publishes no world-entry packets");
        Check.Equal(
            0,
            replacementTransport.DisconnectCount,
            "current replacement completes world entry");
        Check.True(
            registry.IsCurrentAccountSession(
                source.AccountId,
                replacementSession),
            "forced interleaving does not disturb current-session identity");

        registry.RemoveAccountSession(
            source.AccountId,
            replacementSession);
    }

    private sealed class InterleavedCheckpointCoordinator(
        long positionRevision,
        long vitalsRevision) : ICharacterCheckpointCoordinator
    {
        private readonly ConcurrentQueue<string> _events = new();
        private readonly TaskCompletionSource _firstAcquireRelease =
            NewSignal();
        private readonly TaskCompletionSource _firstAcquireStarted =
            NewSignal();
        private readonly object _sync = new();
        private CharacterCheckpointOwner? _currentOwner;
        private long _generation;
        private int _acquireCalls;
        private int _releaseCalls;

        public int AcquireCalls =>
            Volatile.Read(ref _acquireCalls);

        public int ReleaseCalls =>
            Volatile.Read(ref _releaseCalls);

        public Task FirstAcquireStarted =>
            _firstAcquireStarted.Task;

        public Guid FirstOwnerId { get; private set; }

        public Guid SecondOwnerId { get; private set; }

        public Guid? CurrentOwnerId
        {
            get
            {
                lock (_sync)
                {
                    return _currentOwner?.OwnerId;
                }
            }
        }

        public string[] Events => _events.ToArray();

        public Task RunAsync(
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task WaitUntilReadyAsync(
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public CharacterCheckpointEnqueueResult TryEnqueue(
            CharacterPositionCheckpoint checkpoint) =>
            new(
                CharacterCheckpointEnqueueStatus.Accepted,
                checkpoint.Revision);

        public CharacterCheckpointEnqueueResult TryEnqueue(
            CharacterVitalsCheckpoint checkpoint) =>
            new(
                CharacterCheckpointEnqueueStatus.Accepted,
                checkpoint.Revision);

        public async Task<CharacterCheckpointOwnership?> AcquireAsync(
            int accountId,
            int characterId,
            Guid ownerId,
            CancellationToken cancellationToken = default)
        {
            var call = Interlocked.Increment(ref _acquireCalls);
            _events.Enqueue($"acquire-{call}-start");
            if (call == 1)
            {
                FirstOwnerId = ownerId;
                _firstAcquireStarted.TrySetResult();
                await _firstAcquireRelease.Task.WaitAsync(
                    cancellationToken);
            }
            else if (call == 2)
            {
                SecondOwnerId = ownerId;
            }
            else
            {
                throw new InvalidOperationException(
                    "Unexpected checkpoint acquisition attempt.");
            }

            CharacterCheckpointOwner owner;
            lock (_sync)
            {
                owner = new CharacterCheckpointOwner(
                    ownerId,
                    ++_generation);
                _currentOwner = owner;
            }
            _events.Enqueue($"acquire-{call}-commit");
            return new CharacterCheckpointOwnership(
                owner,
                positionRevision,
                vitalsRevision);
        }

        public Task<CharacterCheckpointReleaseStatus> ReleaseAsync(
            int accountId,
            int characterId,
            CharacterCheckpointOwner owner,
            CancellationToken cancellationToken = default)
        {
            lock (_sync)
            {
                if (_currentOwner != owner)
                {
                    return Task.FromResult(
                        CharacterCheckpointReleaseStatus
                            .OwnershipLost);
                }

                _currentOwner = null;
                Interlocked.Increment(ref _releaseCalls);
                _events.Enqueue("release-1");
                return Task.FromResult(
                    CharacterCheckpointReleaseStatus.Released);
            }
        }

        public Task<CharacterCheckpointWriteResult> FlushThroughAsync(
            CharacterPositionCheckpoint checkpoint,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(
                new CharacterCheckpointWriteResult(
                    CharacterCheckpointWriteStatus.Applied,
                    checkpoint.Revision));

        public Task<CharacterCheckpointWriteResult> FlushThroughAsync(
            CharacterVitalsCheckpoint checkpoint,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(
                new CharacterCheckpointWriteResult(
                    CharacterCheckpointWriteStatus.Applied,
                    checkpoint.Revision));

        public CharacterCheckpointRuntimeSnapshot GetSnapshot() =>
            new(
                CharacterCheckpointRuntimeState.Ready,
                Capacity: 1,
                PendingKeys: 0,
                ActiveWrites: 0,
                ScheduledRetries: 0,
                OldestPendingAge: TimeSpan.Zero,
                HeartbeatAge: TimeSpan.Zero,
                FailureType: null);

        public void Complete()
        {
        }

        public ValueTask DisposeAsync() =>
            ValueTask.CompletedTask;

        public void ReleaseFirstAcquire() =>
            _firstAcquireRelease.TrySetResult();

        private static TaskCompletionSource NewSignal() =>
            new(
                TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
