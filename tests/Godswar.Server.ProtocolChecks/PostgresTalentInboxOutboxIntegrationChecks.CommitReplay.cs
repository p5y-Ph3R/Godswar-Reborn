using Godswar.Server.Application.Talents;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresTalentInboxOutboxIntegrationChecks
{
    private static async Task AssertFirstCommitAndReplayAsync(
        string connectionString)
    {
        var fixture = await CreateFixtureAsync(
            connectionString,
            "atomic",
            level: 80,
            talentPoints: 100);
        var firstEnvelope = CreateEnvelope(
            fixture,
            expectedRank: 0);

        TalentUpgradeExecutionResult first;
        await using (var firstSource =
                     NpgsqlDataSource.Create(connectionString))
        {
            first = await CreateExecutor(firstSource)
                .ExecuteAsync(firstEnvelope);
        }

        var committed = RequireReceipt(
            first,
            TalentUpgradeExecutionDisposition.Committed,
            "first talent command");
        Check.True(
            committed.CharacterId == fixture.CharacterId &&
            committed.TalentId == TalentId &&
            committed.Rank == 1 &&
            committed.Cost == 1 &&
            committed.RemainingTalentPoints == 99 &&
            committed.DisplayValue == 4 &&
            committed.AggregateRevision == 1 &&
            long.TryParse(
                committed.AuditReference,
                out var auditReference) &&
            auditReference > 0 &&
            committed.OutboxEventId != Guid.Empty,
            "first commit returns the complete canonical receipt");
        AssertCommittedState(
            await ReadStateAsync(connectionString, fixture),
            expectedPoints: 99,
            expectedRank: 1,
            expectedRevision: 1,
            expectedDuplicateCount: 0,
            "first command atomically commits mutation, audit, inbox, and outbox");
        var persisted = await ReadPersistedCommandAsync(
            connectionString,
            fixture);
        AssertPersistedCommand(
            persisted,
            fixture,
            firstEnvelope,
            committed,
            expectedDuplicateCount: 0);
        await AssertRetentionGuardsAsync(
            connectionString,
            persisted);
        AssertPersistedCommand(
            await ReadPersistedCommandAsync(
                connectionString,
                fixture),
            fixture,
            firstEnvelope,
            committed,
            expectedDuplicateCount: 0);

        var retryEnvelope = CreateEnvelope(
            fixture,
            expectedRank: 0,
            connectionId: Guid.NewGuid());
        Check.True(
            string.Equals(
                firstEnvelope.OperationId,
                retryEnvelope.OperationId,
                StringComparison.Ordinal) &&
            string.Equals(
                firstEnvelope.RequestHash,
                retryEnvelope.RequestHash,
                StringComparison.Ordinal),
            "reconnect preserves the durable operation identity and hash");

        TalentUpgradeExecutionResult retry;
        await using (var retrySource =
                     NpgsqlDataSource.Create(connectionString))
        {
            retry = await CreateExecutor(retrySource)
                .ExecuteAsync(retryEnvelope);
        }

        var duplicate = RequireReceipt(
            retry,
            TalentUpgradeExecutionDisposition.Duplicate,
            "cross-executor exact retry");
        AssertReceiptsEqual(
            committed,
            duplicate,
            "exact retry returns the byte-equivalent stored receipt");
        Check.True(
            retry.AuthoritativeRank == 1 &&
            retry.AuthoritativeTalentPoints == 99,
            "duplicate reports current authoritative talent state");
        AssertCommittedState(
            await ReadStateAsync(connectionString, fixture),
            expectedPoints: 99,
            expectedRank: 1,
            expectedRevision: 1,
            expectedDuplicateCount: 1,
            "exact retry changes only its bounded duplicate evidence");
        AssertPersistedCommand(
            await ReadPersistedCommandAsync(
                connectionString,
                fixture),
            fixture,
            firstEnvelope,
            committed,
            expectedDuplicateCount: 1);
    }

    private static async Task AssertConcurrentExecutorsAsync(
        string connectionString)
    {
        var fixture = await CreateFixtureAsync(
            connectionString,
            "race",
            level: 80,
            talentPoints: 100);
        var envelopeA = CreateEnvelope(
            fixture,
            expectedRank: 0,
            connectionId: Guid.NewGuid());
        var envelopeB = CreateEnvelope(
            fixture,
            expectedRank: 0,
            connectionId: Guid.NewGuid());

        await using var sourceA =
            NpgsqlDataSource.Create(connectionString);
        await using var sourceB =
            NpgsqlDataSource.Create(connectionString);
        var results = await Task.WhenAll(
            CreateExecutor(sourceA).ExecuteAsync(envelopeA),
            CreateExecutor(sourceB).ExecuteAsync(envelopeB));

        Check.Equal(
            1,
            results.Count(result =>
                result.Disposition ==
                TalentUpgradeExecutionDisposition.Committed),
            "one concurrent executor commits");
        Check.Equal(
            1,
            results.Count(result =>
                result.Disposition ==
                TalentUpgradeExecutionDisposition.Duplicate),
            "one concurrent executor replays the committed result");
        var committed = results.Single(result =>
            result.Disposition ==
            TalentUpgradeExecutionDisposition.Committed).Receipt ??
            throw new InvalidOperationException(
                "The race winner returned no receipt.");
        var duplicate = results.Single(result =>
            result.Disposition ==
            TalentUpgradeExecutionDisposition.Duplicate).Receipt ??
            throw new InvalidOperationException(
                "The race duplicate returned no receipt.");
        AssertReceiptsEqual(
            committed,
            duplicate,
            "concurrent executors observe one canonical receipt");
        AssertCommittedState(
            await ReadStateAsync(connectionString, fixture),
            expectedPoints: 99,
            expectedRank: 1,
            expectedRevision: 1,
            expectedDuplicateCount: 1,
            "concurrent execution produces one mutation and one event");
        AssertPersistedCommand(
            await ReadPersistedCommandAsync(
                connectionString,
                fixture),
            fixture,
            envelopeA,
            committed,
            expectedDuplicateCount: 1);
    }

    private static async Task AssertProfessionChangedReplayAsync(
        string connectionString)
    {
        var fixture = await CreateFixtureAsync(
            connectionString,
            "class",
            level: 80,
            talentPoints: 100);
        var committedEnvelope = CreateEnvelope(
            fixture,
            expectedRank: 0);

        TalentUpgradeExecutionResult first;
        await using (var firstSource =
                     NpgsqlDataSource.Create(connectionString))
        {
            first = await CreateExecutor(firstSource)
                .ExecuteAsync(committedEnvelope);
        }

        var committed = RequireReceipt(
            first,
            TalentUpgradeExecutionDisposition.Committed,
            "profession-change setup command");
        await SetProfessionAsync(
            connectionString,
            fixture,
            profession: 1);

        var retryEnvelope = CreateEnvelope(
            fixture,
            expectedRank: 0,
            connectionId: Guid.NewGuid());
        TalentUpgradeExecutionResult retry;
        await using (var retrySource =
                     NpgsqlDataSource.Create(connectionString))
        {
            retry = await CreateExecutor(retrySource)
                .ExecuteAsync(retryEnvelope);
        }

        var duplicate = RequireReceipt(
            retry,
            TalentUpgradeExecutionDisposition.Duplicate,
            "retry after profession change");
        AssertReceiptsEqual(
            committed,
            duplicate,
            "profession change does not hide a durable result");
        Check.True(
            retry.AuthoritativeRank == 1 &&
            retry.AuthoritativeTalentPoints == 99,
            "profession-change retry reports current durable state");
        var beforeNewCommand =
            await ReadStateAsync(connectionString, fixture);
        AssertCommittedState(
            beforeNewCommand,
            expectedPoints: 99,
            expectedRank: 1,
            expectedRevision: 1,
            expectedDuplicateCount: 1,
            "profession-change retry only records duplicate evidence");

        var ineligibleEnvelope = CreateEnvelope(
            fixture,
            expectedRank: 1,
            connectionId: Guid.NewGuid());
        Check.True(
            !string.Equals(
                committedEnvelope.OperationId,
                ineligibleEnvelope.OperationId,
                StringComparison.Ordinal),
            "new talent intent has a distinct operation identity");

        TalentUpgradeExecutionResult rejected;
        await using (var ineligibleSource =
                     NpgsqlDataSource.Create(connectionString))
        {
            rejected = await CreateExecutor(ineligibleSource)
                .ExecuteAsync(ineligibleEnvelope);
        }

        Check.True(
            rejected.Disposition ==
            TalentUpgradeExecutionDisposition.PreconditionFailed &&
            rejected.Receipt is null,
            "new command for another profession is rejected");
        Check.True(
            beforeNewCommand ==
            await ReadStateAsync(connectionString, fixture),
            "ineligible new command creates no mutation, inbox, audit, or outbox");
        AssertPersistedCommand(
            await ReadPersistedCommandAsync(
                connectionString,
                fixture),
            fixture,
            committedEnvelope,
            committed,
            expectedDuplicateCount: 1);
    }
}
