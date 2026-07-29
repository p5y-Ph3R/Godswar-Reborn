using Godswar.Server.Application.Talents;
using Godswar.Server.Infrastructure.Talents;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresTalentInboxOutboxIntegrationChecks
{
    private static readonly PostgresTalentUpgradeCommandStage[]
        PreCommitStages =
        [
            PostgresTalentUpgradeCommandStage.AuditInserted,
            PostgresTalentUpgradeCommandStage.InboxInserted,
            PostgresTalentUpgradeCommandStage.MutationApplied,
            PostgresTalentUpgradeCommandStage.OutboxInserted,
            PostgresTalentUpgradeCommandStage.BeforeCommit
        ];

    private static async Task
        AssertRejectedCommandsAndCorrectionAsync(
            string connectionString)
    {
        await AssertWrongPrincipalAsync(connectionString);
        await AssertRankCorrectionAsync(connectionString);
        await AssertLevelCorrectionAsync(connectionString);
        await AssertPointsCorrectionAsync(connectionString);
    }

    private static async Task AssertWrongPrincipalAsync(
        string connectionString)
    {
        var fixture = await CreateFixtureAsync(
            connectionString,
            "owner",
            level: 80,
            talentPoints: 100);
        await using var source =
            NpgsqlDataSource.Create(connectionString);
        var result = await CreateExecutor(source).ExecuteAsync(
            CreateEnvelope(
                fixture,
                expectedRank: 0,
                accountId: int.MaxValue));
        Check.True(
            result.Disposition ==
                TalentUpgradeExecutionDisposition.PreconditionFailed &&
            result.Receipt is null,
            "wrong authenticated principal is rejected without a receipt");
        AssertNoCommandRows(
            await ReadStateAsync(connectionString, fixture),
            expectedPoints: 100,
            expectedRank: 0,
            expectedRevision: 0,
            "wrong principal creates no durable command evidence");
    }

    private static async Task AssertRankCorrectionAsync(
        string connectionString)
    {
        var fixture = await CreateFixtureAsync(
            connectionString,
            "rank",
            level: 180,
            talentPoints: 10_000);
        var envelope = CreateEnvelope(
            fixture,
            expectedRank: 1);
        await using var source =
            NpgsqlDataSource.Create(connectionString);
        var executor = CreateExecutor(source);
        var rejected = await executor.ExecuteAsync(envelope);
        Check.True(
            rejected.Disposition ==
                TalentUpgradeExecutionDisposition.PreconditionFailed,
            "rank mismatch is rejected");
        AssertNoCommandRows(
            await ReadStateAsync(connectionString, fixture),
            expectedPoints: 10_000,
            expectedRank: 0,
            expectedRevision: 0,
            "rank mismatch reserves no operation identity");

        await SetTalentRankAsync(
            connectionString,
            fixture,
            rank: 1);
        var committed = RequireReceipt(
            await executor.ExecuteAsync(envelope),
            TalentUpgradeExecutionDisposition.Committed,
            "rank-corrected retry");
        Check.True(
            committed.Rank == 2 &&
            committed.Cost == 2 &&
            committed.RemainingTalentPoints == 9_998,
            "same rank operation succeeds after authoritative correction");
        AssertCommittedState(
            await ReadStateAsync(connectionString, fixture),
            expectedPoints: 9_998,
            expectedRank: 2,
            expectedRevision: 1,
            expectedDuplicateCount: 0,
            "rank-corrected retry commits once");
    }

    private static async Task AssertLevelCorrectionAsync(
        string connectionString)
    {
        var fixture = await CreateFixtureAsync(
            connectionString,
            "level",
            level: 120,
            talentPoints: 10_000,
            rank: 40);
        var envelope = CreateEnvelope(
            fixture,
            expectedRank: 40);
        await using var source =
            NpgsqlDataSource.Create(connectionString);
        var executor = CreateExecutor(source);
        var rejected = await executor.ExecuteAsync(envelope);
        Check.True(
            rejected.Disposition ==
                TalentUpgradeExecutionDisposition.PreconditionFailed,
            "insufficient character level is rejected");
        AssertNoCommandRows(
            await ReadStateAsync(connectionString, fixture),
            expectedPoints: 10_000,
            expectedRank: 40,
            expectedRevision: 0,
            "level rejection writes no audit, inbox, or outbox");

        await SetLevelAsync(
            connectionString,
            fixture,
            level: 121);
        var committed = RequireReceipt(
            await executor.ExecuteAsync(envelope),
            TalentUpgradeExecutionDisposition.Committed,
            "level-corrected retry");
        Check.True(
            committed.Rank == 41 &&
            committed.Cost == 380 &&
            committed.RemainingTalentPoints == 9_620,
            "same level-gated operation succeeds after correction");
        AssertCommittedState(
            await ReadStateAsync(connectionString, fixture),
            expectedPoints: 9_620,
            expectedRank: 41,
            expectedRevision: 1,
            expectedDuplicateCount: 0,
            "level-corrected retry commits once");
    }

    private static async Task AssertPointsCorrectionAsync(
        string connectionString)
    {
        var fixture = await CreateFixtureAsync(
            connectionString,
            "points",
            level: 80,
            talentPoints: 0);
        var envelope = CreateEnvelope(
            fixture,
            expectedRank: 0);
        await using var source =
            NpgsqlDataSource.Create(connectionString);
        var executor = CreateExecutor(source);
        var rejected = await executor.ExecuteAsync(envelope);
        Check.True(
            rejected.Disposition ==
                TalentUpgradeExecutionDisposition.PreconditionFailed,
            "insufficient persisted talent points are rejected");
        AssertNoCommandRows(
            await ReadStateAsync(connectionString, fixture),
            expectedPoints: 0,
            expectedRank: 0,
            expectedRevision: 0,
            "points rejection reserves no inbox identity");

        await SetTalentPointsAsync(
            connectionString,
            fixture,
            talentPoints: 1);
        var committed = RequireReceipt(
            await executor.ExecuteAsync(envelope),
            TalentUpgradeExecutionDisposition.Committed,
            "points-corrected retry");
        Check.True(
            committed.Rank == 1 &&
            committed.Cost == 1 &&
            committed.RemainingTalentPoints == 0,
            "same points-gated operation succeeds after correction");
        AssertCommittedState(
            await ReadStateAsync(connectionString, fixture),
            expectedPoints: 0,
            expectedRank: 1,
            expectedRevision: 1,
            expectedDuplicateCount: 0,
            "points-corrected retry commits once");
    }

    private static async Task AssertPreCommitFaultRollbackAsync(
        string connectionString)
    {
        var fixture = await CreateFixtureAsync(
            connectionString,
            "fault",
            level: 80,
            talentPoints: 100);
        var envelope = CreateEnvelope(
            fixture,
            expectedRank: 0);

        foreach (var stage in PreCommitStages)
        {
            await using var source =
                NpgsqlDataSource.Create(connectionString);
            var executor = CreateExecutor(
                source,
                new ThrowingTalentProbe(stage));
            await AssertInjectedFaultAsync(
                () => executor.ExecuteAsync(envelope),
                stage);
            AssertNoCommandRows(
                await ReadStateAsync(connectionString, fixture),
                expectedPoints: 100,
                expectedRank: 0,
                expectedRevision: 0,
                $"{stage} rolls back mutation, audit, inbox, and outbox");
        }

        await using var recoverySource =
            NpgsqlDataSource.Create(connectionString);
        _ = RequireReceipt(
            await CreateExecutor(recoverySource)
                .ExecuteAsync(envelope),
            TalentUpgradeExecutionDisposition.Committed,
            "post-rollback recovery");
        AssertCommittedState(
            await ReadStateAsync(connectionString, fixture),
            expectedPoints: 99,
            expectedRank: 1,
            expectedRevision: 1,
            expectedDuplicateCount: 0,
            "rolled-back identity remains available for one real commit");
    }

    private static async Task AssertAfterCommitRecoveryAsync(
        string connectionString)
    {
        var fixture = await CreateFixtureAsync(
            connectionString,
            "ack",
            level: 80,
            talentPoints: 100);
        var envelope = CreateEnvelope(
            fixture,
            expectedRank: 0);

        await using (var faultSource =
                     NpgsqlDataSource.Create(connectionString))
        {
            await AssertInjectedFaultAsync(
                () => CreateExecutor(
                        faultSource,
                        new ThrowingTalentProbe(
                            PostgresTalentUpgradeCommandStage.AfterCommit))
                    .ExecuteAsync(envelope),
                PostgresTalentUpgradeCommandStage.AfterCommit);
        }

        AssertCommittedState(
            await ReadStateAsync(connectionString, fixture),
            expectedPoints: 99,
            expectedRank: 1,
            expectedRevision: 1,
            expectedDuplicateCount: 0,
            "AfterCommit fault preserves the completed transaction");
        var stored = await ReadPersistedCommandAsync(
            connectionString,
            fixture);
        var storedReceipt =
            TalentUpgradePersistenceCodec.DecodeAndVerify(
                stored.ResultPayload,
                stored.ResultHash);

        TalentUpgradeExecutionResult retry;
        await using (var retrySource =
                     NpgsqlDataSource.Create(connectionString))
        {
            retry = await CreateExecutor(retrySource).ExecuteAsync(
                CreateEnvelope(
                    fixture,
                    expectedRank: 0,
                    connectionId: Guid.NewGuid()));
        }

        var duplicate = RequireReceipt(
            retry,
            TalentUpgradeExecutionDisposition.Duplicate,
            "lost-ack retry");
        AssertReceiptsEqual(
            storedReceipt,
            duplicate,
            "lost-ack retry recovers the stored committed receipt");
        AssertCommittedState(
            await ReadStateAsync(connectionString, fixture),
            expectedPoints: 99,
            expectedRank: 1,
            expectedRevision: 1,
            expectedDuplicateCount: 1,
            "lost-ack retry does not repeat the mutation or outbox append");
        AssertPersistedCommand(
            await ReadPersistedCommandAsync(
                connectionString,
                fixture),
            fixture,
            envelope,
            duplicate,
            expectedDuplicateCount: 1);
    }

    private static async Task AssertInjectedFaultAsync(
        Func<Task<TalentUpgradeExecutionResult>> action,
        PostgresTalentUpgradeCommandStage expectedStage)
    {
        try
        {
            _ = await action();
        }
        catch (InjectedTalentCommandFault ex)
        {
            Check.True(
                ex.Stage == expectedStage,
                $"{expectedStage} fault reports its exact stage");
            return;
        }

        throw new InvalidOperationException(
            $"The {expectedStage} probe did not interrupt execution.");
    }

    private sealed class ThrowingTalentProbe(
        PostgresTalentUpgradeCommandStage stage) :
        IPostgresTalentUpgradeCommandProbe
    {
        public ValueTask ReachedAsync(
            PostgresTalentUpgradeCommandStage reachedStage,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reachedStage == stage)
            {
                throw new InjectedTalentCommandFault(stage);
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class InjectedTalentCommandFault(
        PostgresTalentUpgradeCommandStage stage) : Exception
    {
        public PostgresTalentUpgradeCommandStage Stage { get; } =
            stage;
    }
}
