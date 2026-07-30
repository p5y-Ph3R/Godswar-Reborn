using System.Reflection;
using Godswar.Server.Application.Characters;
using Godswar.Server.Game;
using Godswar.Server.Networking;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class GameHandlerCheckpointLifecycleChecks
{
    private static readonly BindingFlags PrivateInstance =
        BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly FieldInfo AccountField =
        RequiredField("_account");
    private static readonly FieldInfo CharacterField =
        RequiredField("_character");
    private static readonly FieldInfo RegistryField =
        RequiredField("_registry");
    private static readonly FieldInfo SessionField =
        RequiredField("_session");
    private static readonly FieldInfo OwnershipAcquiredField =
        RequiredField("_checkpointOwnershipAcquired");
    private static readonly MethodInfo EnsureOwnershipMethod =
        RequiredMethod("EnsureCheckpointOwnershipAsync");
    private static readonly MethodInfo FinalizeOwnershipMethod =
        RequiredMethod("FinalizeCheckpointOwnershipAsync");
    private static readonly MethodInfo InstallCharacterMethod =
        RequiredMethod("InstallUpdatedCharacter");

    public static async Task RunAsync()
    {
        await CheckFailedRefreshReleasesCapturedIdentityAsync();
        await CheckFinalFlushesAreIndependentAsync();
        await CheckVitalsClampAdvancesRevisionAsync();
    }

    private static async Task
        CheckFailedRefreshReleasesCapturedIdentityAsync()
    {
        var snapshot =
            CharacterSnapshotContractChecks.CreateValidSnapshot();
        var character =
            CharacterLoadSnapshotHydrator.Hydrate(snapshot)?.Character ??
            throw new InvalidOperationException(
                "Checkpoint lifecycle fixture did not hydrate.");
        var coordinator = new RecordingCoordinator(
            character.PositionRevision,
            character.VitalsRevision);
        await using var session = new ClientSession(
            new ScriptedLegacyByteTransport(),
            endpointRole: NetworkEndpointRole.Game);
        var handler = CreateHandler(
            session,
            new RejectingSnapshotReader(),
            coordinator);
        InstallIdentity(handler, snapshot.AccountId, character);

        var acquired = await InvokeAsync<bool>(
            EnsureOwnershipMethod,
            handler,
            CancellationToken.None);

        Check.True(
            !acquired,
            "failed post-acquire snapshot refresh rejects ownership");
        Check.Equal(
            1,
            coordinator.Releases.Count,
            "failed refresh releases the acquired checkpoint owner");
        var release = coordinator.Releases.Single();
        Check.Equal(
            snapshot.AccountId,
            release.AccountId,
            "release retains the captured account identity");
        Check.Equal(
            character.Id,
            release.CharacterId,
            "release retains the captured character identity");
        Check.True(
            CharacterField.GetValue(handler) is null,
            "failed refresh can clear mutable handler character state");
    }

    private static async Task CheckFinalFlushesAreIndependentAsync()
    {
        var snapshot =
            CharacterSnapshotContractChecks.CreateValidSnapshot();
        var character =
            CharacterLoadSnapshotHydrator.Hydrate(snapshot)?.Character ??
            throw new InvalidOperationException(
                "Checkpoint lifecycle fixture did not hydrate.");
        var coordinator = new RecordingCoordinator(
            character.PositionRevision,
            character.VitalsRevision)
        {
            PositionFailure = new InvalidOperationException(
                "Synthetic final position failure.")
        };
        character.CheckpointOwnerId = coordinator.Owner.OwnerId;
        character.CheckpointOwnerGeneration =
            coordinator.Owner.Generation;

        await using var session = new ClientSession(
            new ScriptedLegacyByteTransport(),
            endpointRole: NetworkEndpointRole.Game);
        var handler = CreateHandler(
            session,
            new RejectingSnapshotReader(),
            coordinator);
        InstallIdentity(handler, snapshot.AccountId, character);
        OwnershipAcquiredField.SetValue(handler, true);

        await InvokeAsync(FinalizeOwnershipMethod, handler);

        Check.True(
            coordinator.Operations.SequenceEqual(
                ["position", "vitals", "release"]),
            "vitals finalization and release continue after position failure");
        Check.Equal(
            1,
            coordinator.VitalsWrites,
            "final vitals barrier is attempted independently");
        Check.Equal(
            1,
            coordinator.Releases.Count,
            "owner is released after both final barriers are attempted");
    }

    private static async Task CheckVitalsClampAdvancesRevisionAsync()
    {
        await using var session = new ClientSession(
            new ScriptedLegacyByteTransport(),
            endpointRole: NetworkEndpointRole.Game);
        var handler = CreateHandler(
            session,
            new RejectingSnapshotReader(),
            characterCheckpoints: null);
        var ownerId = Guid.NewGuid();
        var current = new GameCharacter
        {
            Id = 42,
            AccountId = 7,
            MaxHp = 100,
            MaxMp = 50,
            CurrentHp = 100,
            CurrentMp = 50,
            VitalsRevision = 7,
            PositionRevision = 3,
            CheckpointOwnerId = ownerId,
            CheckpointOwnerGeneration = 9
        };
        CharacterField.SetValue(handler, current);
        var reduced = new GameCharacter
        {
            Id = current.Id,
            AccountId = current.AccountId,
            MaxHp = 60,
            MaxMp = 30,
            VitalsRevision = 7
        };

        Invoke(InstallCharacterMethod, handler, reduced);

        var installed =
            (GameCharacter?)CharacterField.GetValue(handler) ??
            throw new InvalidOperationException(
                "Updated character was not installed.");
        Check.Equal(
            60,
            installed.CurrentHp,
            "updated maximum clamps current HP");
        Check.Equal(
            30,
            installed.CurrentMp,
            "updated maximum clamps current MP");
        Check.Equal(
            8L,
            installed.VitalsRevision,
            "a vitals clamp advances the durable revision exactly once");

        var expanded = new GameCharacter
        {
            Id = installed.Id,
            AccountId = installed.AccountId,
            MaxHp = 120,
            MaxMp = 80,
            VitalsRevision = installed.VitalsRevision
        };
        Invoke(InstallCharacterMethod, handler, expanded);
        installed =
            (GameCharacter?)CharacterField.GetValue(handler) ??
            throw new InvalidOperationException(
                "Expanded character was not installed.");
        Check.Equal(
            8L,
            installed.VitalsRevision,
            "an unchanged current-vitals payload keeps its revision");
    }

    private static GameClientHandler CreateHandler(
        ClientSession session,
        ICharacterSnapshotReader snapshotReader,
        ICharacterCheckpointCoordinator? characterCheckpoints) =>
        new(
            session,
            new EmptyStore(),
            new GameSessionRegistry(store: null),
            snapshotReader,
            WorldContentReaderTestFixtures.Empty,
            characterCheckpoints: characterCheckpoints);

    private static void InstallIdentity(
        GameClientHandler handler,
        int accountId,
        GameCharacter character)
    {
        AccountField.SetValue(
            handler,
            new GameAccount
            {
                Id = accountId,
                Username = "checkpoint-fixture"
            });
        CharacterField.SetValue(handler, character);
        var registry =
            (GameSessionRegistry?)RegistryField.GetValue(handler) ??
            throw new InvalidOperationException(
                "Game handler registry was not installed.");
        var session =
            (ClientSession?)SessionField.GetValue(handler) ??
            throw new InvalidOperationException(
                "Game handler session was not installed.");
        registry.ReplaceAccountSession(accountId, session);
    }

    private static async Task<T> InvokeAsync<T>(
        MethodInfo method,
        object target,
        params object?[] arguments)
    {
        try
        {
            return await ((Task<T>?)method.Invoke(target, arguments) ??
                throw new InvalidOperationException(
                    $"{method.Name} returned no task."));
        }
        catch (TargetInvocationException error)
            when (error.InnerException is not null)
        {
            throw error.InnerException;
        }
    }

    private static async Task InvokeAsync(
        MethodInfo method,
        object target)
    {
        try
        {
            await ((Task?)method.Invoke(target, null) ??
                throw new InvalidOperationException(
                    $"{method.Name} returned no task."));
        }
        catch (TargetInvocationException error)
            when (error.InnerException is not null)
        {
            throw error.InnerException;
        }
    }

    private static void Invoke(
        MethodInfo method,
        object target,
        params object?[] arguments)
    {
        try
        {
            method.Invoke(target, arguments);
        }
        catch (TargetInvocationException error)
            when (error.InnerException is not null)
        {
            throw error.InnerException;
        }
    }

    private static FieldInfo RequiredField(string name) =>
        typeof(GameClientHandler).GetField(name, PrivateInstance) ??
        throw new InvalidOperationException(
            $"GameClientHandler.{name} was not found.");

    private static MethodInfo RequiredMethod(string name) =>
        typeof(GameClientHandler).GetMethod(name, PrivateInstance) ??
        throw new InvalidOperationException(
            $"GameClientHandler.{name} was not found.");

    private sealed class EmptyStore : GameStoreTestStub
    {
    }

    private sealed class RejectingSnapshotReader :
        ICharacterSnapshotReader
    {
        public Task<CharacterAccountSnapshot> ReadAsync(
            int accountId,
            CancellationToken cancellationToken = default) =>
            Task.FromException<CharacterAccountSnapshot>(
                new CharacterSnapshotUnavailableException(
                    CharacterSnapshotFailureReason.ProviderUnavailable,
                    "Synthetic post-acquire snapshot failure."));
    }

    private sealed class RecordingCoordinator(
        long positionRevision,
        long vitalsRevision) : ICharacterCheckpointCoordinator
    {
        public PlayerOwnershipFence Owner { get; } =
            new(Guid.NewGuid(), 1);

        public List<string> Operations { get; } = [];

        public List<ReleaseCall> Releases { get; } = [];

        public Exception? PositionFailure { get; init; }

        public int VitalsWrites { get; private set; }

        public Task<CharacterCheckpointOwnership?> AcquireAsync(
            int accountId,
            int characterId,
            Guid ownerId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<CharacterCheckpointOwnership?>(
                new(
                    new PlayerOwnershipFence(
                        ownerId,
                        Owner.Generation),
                    positionRevision,
                    vitalsRevision));

        public Task<CharacterCheckpointWriteResult>
            FlushThroughAsync(
                CharacterPositionCheckpoint checkpoint,
                CancellationToken cancellationToken = default)
        {
            Operations.Add("position");
            return PositionFailure is null
                ? Task.FromResult(Applied(checkpoint.Revision))
                : Task.FromException<CharacterCheckpointWriteResult>(
                    PositionFailure);
        }

        public Task<CharacterCheckpointWriteResult>
            FlushThroughAsync(
                CharacterVitalsCheckpoint checkpoint,
                CancellationToken cancellationToken = default)
        {
            Operations.Add("vitals");
            VitalsWrites++;
            return Task.FromResult(Applied(checkpoint.Revision));
        }

        public Task<CharacterCheckpointReleaseStatus> ReleaseAsync(
            int accountId,
            int characterId,
            PlayerOwnershipFence owner,
            CancellationToken cancellationToken = default)
        {
            Operations.Add("release");
            Releases.Add(new ReleaseCall(
                accountId,
                characterId,
                owner));
            return Task.FromResult(
                CharacterCheckpointReleaseStatus.Released);
        }

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

        public Task RunAsync(
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task WaitUntilReadyAsync(
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

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

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private static CharacterCheckpointWriteResult Applied(
            long revision) =>
            new(
                CharacterCheckpointWriteStatus.Applied,
                revision);
    }

    private readonly record struct ReleaseCall(
        int AccountId,
        int CharacterId,
        PlayerOwnershipFence Owner);
}
