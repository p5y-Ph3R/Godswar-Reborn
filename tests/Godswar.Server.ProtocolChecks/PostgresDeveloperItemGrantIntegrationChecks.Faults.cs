using Godswar.Server.Application.Inventory;
using Godswar.Server.Infrastructure.Inventory;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresDeveloperItemGrantIntegrationChecks
{
    private static readonly PostgresDeveloperItemGrantCommandStage[]
        PreCommitStages =
        [
            PostgresDeveloperItemGrantCommandStage.AuditInserted,
            PostgresDeveloperItemGrantCommandStage.InboxInserted,
            PostgresDeveloperItemGrantCommandStage.InventoryMutated,
            PostgresDeveloperItemGrantCommandStage.LedgerInserted,
            PostgresDeveloperItemGrantCommandStage.OutboxInserted,
            PostgresDeveloperItemGrantCommandStage.BeforeCommit
        ];

    private static async Task AssertPreCommitFaultRollbackAsync(
        string connectionString)
    {
        var fixture = await CreateFixtureAsync(
            connectionString,
            "fault");
        var envelope = CreateEnvelope(fixture, Guid.NewGuid());

        foreach (var stage in PreCommitStages)
        {
            await using var source =
                NpgsqlDataSource.Create(connectionString);
            var executor = CreateExecutor(
                source,
                new ThrowingGrantProbe(stage));
            await AssertInjectedFaultAsync(
                () => executor.ExecuteAsync(envelope),
                stage);
            AssertEmptyGrantState(
                await ReadStateAsync(connectionString, fixture),
                $"{stage} rolls back inventory, revision, inbox, " +
                "ledger, audit, and outbox");
        }

        await using var recoverySource =
            NpgsqlDataSource.Create(connectionString);
        _ = RequireReceipt(
            await CreateExecutor(recoverySource)
                .ExecuteAsync(envelope),
            DeveloperItemGrantExecutionDisposition.Committed,
            "post-rollback developer-item recovery");
        AssertCommittedState(
            await ReadStateAsync(connectionString, fixture),
            expectedQuantity: GrantQuantity,
            expectedDuplicateCount: 0,
            expectedConflictCount: 0,
            "rolled-back operation remains available for one commit");
    }

    private static async Task AssertAfterCommitRecoveryAsync(
        string connectionString)
    {
        var fixture = await CreateFixtureAsync(
            connectionString,
            "ack");
        var operationId = Guid.NewGuid();
        var envelope = CreateEnvelope(fixture, operationId);

        await using (var faultSource =
                     NpgsqlDataSource.Create(connectionString))
        {
            await AssertInjectedFaultAsync(
                () => CreateExecutor(
                        faultSource,
                        new ThrowingGrantProbe(
                            PostgresDeveloperItemGrantCommandStage
                                .AfterCommit))
                    .ExecuteAsync(envelope),
                PostgresDeveloperItemGrantCommandStage.AfterCommit);
        }

        AssertCommittedState(
            await ReadStateAsync(connectionString, fixture),
            expectedQuantity: GrantQuantity,
            expectedDuplicateCount: 0,
            expectedConflictCount: 0,
            "lost acknowledgement leaves the transaction committed");

        DeveloperItemGrantExecutionResult retry;
        await using (var retrySource =
                     NpgsqlDataSource.Create(connectionString))
        {
            retry = await CreateExecutor(retrySource).ExecuteAsync(
                CreateEnvelope(
                    fixture,
                    operationId,
                    connectionId: Guid.NewGuid()));
        }

        var duplicate = RequireReceipt(
            retry,
            DeveloperItemGrantExecutionDisposition.Duplicate,
            "lost-ack developer-item retry");
        Check.True(
            duplicate.CharacterId == fixture.CharacterId &&
            duplicate.ItemId == MaterialItemId &&
            duplicate.GrantedQuantity == GrantQuantity &&
            duplicate.InventoryRevision == 1 &&
            duplicate.OutboxEventId != Guid.Empty,
            "new executor recovers the committed durable receipt");
        AssertCommittedState(
            await ReadStateAsync(connectionString, fixture),
            expectedQuantity: GrantQuantity,
            expectedDuplicateCount: 1,
            expectedConflictCount: 0,
            "lost-ack replay does not duplicate inventory or evidence");
    }

    private static async Task AssertInjectedFaultAsync(
        Func<Task<DeveloperItemGrantExecutionResult>> action,
        PostgresDeveloperItemGrantCommandStage expectedStage)
    {
        try
        {
            _ = await action();
        }
        catch (InjectedGrantCommandFault exception)
        {
            Check.True(
                exception.Stage == expectedStage,
                $"{expectedStage} fault reports its exact stage");
            return;
        }

        throw new InvalidOperationException(
            $"The {expectedStage} probe did not interrupt execution.");
    }

    private sealed class ThrowingGrantProbe(
        PostgresDeveloperItemGrantCommandStage stage) :
        IPostgresDeveloperItemGrantCommandProbe
    {
        public ValueTask ReachedAsync(
            PostgresDeveloperItemGrantCommandStage reachedStage,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reachedStage == stage)
            {
                throw new InjectedGrantCommandFault(stage);
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class InjectedGrantCommandFault(
        PostgresDeveloperItemGrantCommandStage stage) : Exception
    {
        public PostgresDeveloperItemGrantCommandStage Stage { get; } =
            stage;
    }
}
