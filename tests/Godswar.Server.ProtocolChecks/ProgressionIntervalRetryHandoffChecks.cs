using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Progression;
using Godswar.Server.Game;
using Godswar.Server.Networking;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class ProgressionIntervalRetryHandoffChecks
{
    private static readonly DateTimeOffset OnlineFrom =
        new(2026, 7, 31, 4, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset DisconnectedAt =
        OnlineFrom.AddSeconds(30);

    public static async Task RunAsync()
    {
        var snapshot =
            CharacterSnapshotContractChecks.CreateValidSnapshot();
        var hydrated = CharacterLoadSnapshotHydrator.Hydrate(snapshot) ??
            throw new InvalidOperationException(
                "The progression retry fixture did not hydrate.");
        var character = hydrated.Character;
        var executor = new FailThenCommitExecutor(failures: 2);
        var registry = new GameSessionRegistry(
            new StubStore(),
            zodiacEnergyOptions: null,
            MonsterRuntimeMode.Ecs,
            PlayerRuntimeMode.Ecs);
        registry.ConfigureProgressionIntervalSettlement(executor);
        await using var session = new ClientSession(
            new ScriptedLegacyByteTransport());
        GameHandlerOwnershipTestFences.Bind(
            registry,
            session,
            character.AccountId,
            character);
        registry.JoinMap(
            session,
            character.AccountId,
            character,
            WorldObjectIds.ForPlayer(character.Id),
            joinedAt: OnlineFrom);

        await ExpectProviderFailureAsync(
            () => registry.FinishProgressionBoostOnlineSessionAsync(
                session,
                DisconnectedAt,
                CancellationToken.None),
            "failed boost finalization remains retryable");
        await ExpectProviderFailureAsync(
            () => registry.FinishZodiacOnlineSessionAsync(
                session,
                DisconnectedAt,
                CancellationToken.None),
            "failed Zodiac finalization hands off the exact envelope");

        Check.Equal(
            1,
            registry.DurableProgressionRetryCount,
            "disconnect leaves one process-owned bounded retry");
        Check.Equal(
            2,
            executor.Envelopes.Count,
            "both finalization paths attempted persistence");
        Check.Equal(
            executor.Envelopes[0].OperationId,
            executor.Envelopes[1].OperationId,
            "unknown final outcome retries the same operation identity");

        var retried =
            await registry.RetryDurableProgressionIntervalsOnceAsync(
                DisconnectedAt.AddHours(3),
                CancellationToken.None);
        Check.Equal(
            1,
            retried,
            "the supervised retry commits the handed-off interval");
        Check.Equal(
            0,
            registry.DurableProgressionRetryCount,
            "a committed retry releases bounded handoff state");
        Check.Equal(
            3,
            executor.Envelopes.Count,
            "the process retry invokes the executor once");
        Check.Equal(
            executor.Envelopes[0].OperationId,
            executor.Envelopes[2].OperationId,
            "the process retry preserves the exact operation identity");
        Check.Equal(
            DisconnectedAt,
            executor.Envelopes[2].Command.OnlineUntilUtc,
            "retry time cannot extend the interval into offline time");

        registry.Remove(session);
    }

    private static async Task ExpectProviderFailureAsync(
        Func<Task> action,
        string message)
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
            $"Check failed: {message}. Expected IOException.");
    }

    private sealed class FailThenCommitExecutor(int failures) :
        IProgressionIntervalSettlementCommandExecutor
    {
        private readonly object _gate = new();
        private int _remainingFailures = failures;

        public List<CommandEnvelope<ProgressionIntervalSettlementCommand>>
            Envelopes { get; } = [];

        public Task<ProgressionIntervalSettlementExecutionResult>
            ExecuteAsync(
                CommandEnvelope<ProgressionIntervalSettlementCommand>
                    envelope,
                CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                Envelopes.Add(envelope);
                if (_remainingFailures-- > 0)
                {
                    throw new IOException(
                        "Simulated unknown PostgreSQL outcome.");
                }
            }

            var command = envelope.Command;
            var projection = new ProgressionIntervalProjection(
                command.OnlineSessionId,
                command.IntervalSequence,
                command.OnlineUntilUtc,
                command.IntervalSequence,
                0,
                0,
                DateOnly.FromDateTime(
                    command.OnlineUntilUtc.UtcDateTime),
                command.OnlineUntilUtc.UtcTicks -
                    command.OnlineFromUtc.UtcTicks,
                null);
            var receipt = new ProgressionIntervalSettlementReceipt(
                envelope.Subject.CharacterId,
                command.OnlineSessionId,
                command.IntervalSequence,
                command.OnlineFromUtc,
                command.OnlineUntilUtc,
                0,
                false,
                0,
                projection,
                "retry-fixture",
                Guid.NewGuid());
            return Task.FromResult(
                ProgressionIntervalSettlementExecutionResult.Committed(
                    receipt));
        }
    }

    private sealed class StubStore : GameStoreTestStub;
}
