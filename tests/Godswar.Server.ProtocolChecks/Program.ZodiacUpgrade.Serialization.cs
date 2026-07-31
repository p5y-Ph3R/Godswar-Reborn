using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Progression;
using Godswar.Server.Application.Zodiac;
using Godswar.Server.Game;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class Program
{
    private static async Task
        CheckReverseZodiacLevelUpgradeSerializationAsync()
    {
        await using var socket =
            await RuntimePolicySessionSocket.CreateAsync();
        var startedAt = new DateTimeOffset(
            2026,
            7,
            23,
            11,
            0,
            0,
            TimeSpan.Zero);
        await using var store = new ReverseSerializedZodiacStore(
            startedAt);
        var registry = new GameSessionRegistry(
            progressionIntervalSettlementCommands: store,
            zodiacLevelStore: store);
        var character = new GameCharacter
        {
            Id = 14,
            AccountId = 8,
            Name = "ReverseSerializedZodiac",
            Level = 80,
            ZodiacLevel = 1,
            ZodiacEnergy = 1_000
        };
        var ownership = new PlayerOwnershipFence(Guid.NewGuid(), 1);
        character.CheckpointOwnerId = ownership.OwnerId;
        character.CheckpointOwnerGeneration = ownership.Generation;
        registry.ReplaceAccountSession(
            character.AccountId,
            socket.Session);
        Check.True(
            registry.TryBindAccountSessionOwnership(
                character.AccountId,
                socket.Session,
                ownership),
            "reverse Zodiac fixture binds player ownership");
        registry.JoinMap(
            socket.Session,
            character.AccountId,
            character,
            objectId: 0x1449,
            worldReady: true,
            joinedAt: startedAt);

        var firstIntervalEnd = startedAt.AddMinutes(1);
        await ExpectReverseSettlementFailureAsync(
            () => registry.FinishProgressionBoostOnlineSessionAsync(
                socket.Session,
                firstIntervalEnd,
                CancellationToken.None));

        var upgrade = registry.UpgradeZodiacLevelAsync(
            socket.Session,
            character.AccountId,
            character,
            ownership,
            CancellationToken.None);
        await store.PendingRetryEntered;

        var secondIntervalEnd = startedAt.AddMinutes(2);
        // The call executes synchronously until it owns the Zodiac gate and
        // waits for the durable gate held by the upgrade's pending retry.
        var accrual = registry.AdvanceZodiacEnergyAccrualOnceAsync(
            secondIntervalEnd,
            CancellationToken.None);
        store.ReleasePendingRetry();
        await store.FollowupAccrualEntered;
        store.ReleaseFollowupAccrual();

        await accrual;
        var result = await upgrade ??
            throw new InvalidOperationException(
                "Reverse serialized Zodiac upgrade returned no result.");
        Check.True(result.Committed, "reverse serialized upgrade commits");
        Check.Equal(
            3,
            store.SettlementCalls,
            "reverse serialized fixture executes failure, retry, and accrual");
        Check.Equal(
            2,
            (int)character.ZodiacLevel,
            "reverse serialized live Zodiac level");
        Check.Equal(
            700,
            character.ZodiacEnergy,
            "reverse serialized live Zodiac energy");
        Check.True(
            character.ZodiacLastOnlineAt == secondIntervalEnd,
            "reverse serialized upgrade preserves newest online timestamp");
        Check.Equal(
            secondIntervalEnd.UtcTicks - startedAt.UtcTicks,
            character.ZodiacOnlineDurationTicksToday,
            "reverse serialized upgrade preserves newest online duration");

        registry.Remove(socket.Session);
    }

    private static async Task ExpectReverseSettlementFailureAsync(
        Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (IOException)
        {
            return;
        }

        throw new InvalidOperationException(
            "Reverse serialized setup expected an unknown outcome.");
    }

    private sealed class ReverseSerializedZodiacStore(
        DateTimeOffset sessionStartedAt) :
        IProgressionIntervalSettlementCommandExecutor,
        IZodiacLevelStore,
        IAsyncDisposable
    {
        private readonly TaskCompletionSource<bool> _pendingRetryEntered =
            NewCompletionSource();
        private readonly TaskCompletionSource<bool> _releasePendingRetry =
            NewCompletionSource();
        private readonly TaskCompletionSource<bool> _followupAccrualEntered =
            NewCompletionSource();
        private readonly TaskCompletionSource<bool> _releaseFollowupAccrual =
            NewCompletionSource();
        private int _settlementCalls;

        public Task PendingRetryEntered => _pendingRetryEntered.Task;

        public Task FollowupAccrualEntered =>
            _followupAccrualEntered.Task;

        public int SettlementCalls => Volatile.Read(ref _settlementCalls);

        public void ReleasePendingRetry() =>
            _releasePendingRetry.TrySetResult(true);

        public void ReleaseFollowupAccrual() =>
            _releaseFollowupAccrual.TrySetResult(true);

        public async Task<ProgressionIntervalSettlementExecutionResult>
            ExecuteAsync(
                CommandEnvelope<ProgressionIntervalSettlementCommand>
                    envelope,
                CancellationToken cancellationToken = default)
        {
            var call = Interlocked.Increment(ref _settlementCalls);
            switch (call)
            {
                case 1:
                    throw new IOException(
                        "Injected unknown progression outcome.");
                case 2:
                    _pendingRetryEntered.TrySetResult(true);
                    await _releasePendingRetry.Task.WaitAsync(
                        cancellationToken);
                    break;
                case 3:
                    _followupAccrualEntered.TrySetResult(true);
                    await _releaseFollowupAccrual.Task.WaitAsync(
                        cancellationToken);
                    break;
                default:
                    throw new InvalidOperationException(
                        "Unexpected reverse serialized settlement call.");
            }

            var command = envelope.Command;
            var energy = call == 2 ? 1_100 : 1_200;
            var projection = new ProgressionIntervalProjection(
                command.OnlineSessionId,
                command.IntervalSequence,
                command.OnlineUntilUtc,
                command.IntervalSequence,
                energy,
                ZodiacEnergyRemainderX100: 0,
                DateOnly.FromDateTime(
                    command.OnlineUntilUtc.UtcDateTime),
                command.OnlineUntilUtc.UtcTicks -
                    sessionStartedAt.UtcTicks,
                ZodiacLastCompensationDay: null);
            var receipt = new ProgressionIntervalSettlementReceipt(
                envelope.Subject.CharacterId,
                command.OnlineSessionId,
                command.IntervalSequence,
                command.OnlineFromUtc,
                command.OnlineUntilUtc,
                GainedZodiacEnergyX100: 10_000,
                ZodiacCompensationApplied: false,
                UpdatedBoostCount: 0,
                projection,
                AuditReference: "reverse-serialized-zodiac",
                Guid.NewGuid());
            return ProgressionIntervalSettlementExecutionResult.Committed(
                receipt);
        }

        public Task<ZodiacLevelUpgradeStoreResult?> UpgradeAsync(
            int accountId,
            int characterId,
            PlayerOwnershipFence ownership,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<ZodiacLevelUpgradeStoreResult?>(
                new ZodiacLevelUpgradeStoreResult(
                    ZodiacLevelUpgradeStoreStatus.Succeeded,
                    PreviousLevel: 1,
                    CurrentLevel: 2,
                    RequiredCharacterLevel: 10,
                    EnergyCost: 500,
                    CurrentEnergy: 700,
                    CurrentEnergyRemainderX100: 0));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private static TaskCompletionSource<bool> NewCompletionSource() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
