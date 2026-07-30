using Godswar.Server.Application.Zodiac;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class
    PostgresZodiacSkillGridActivationCommandIntegrationChecks
{
    private static async Task AssertReplayAndRecoveryAsync(
        string connectionString)
    {
        await AssertExactReplayAsync(connectionString);
        await AssertConcurrentExactCommandAsync(connectionString);
        await AssertInsufficientGoldRetryAsync(connectionString);
        await AssertEnvelopeConflictsAsync(connectionString);
    }

    private static async Task AssertExactReplayAsync(
        string connectionString)
    {
        const int gridIndex = 1;
        var fixture = await CreateFixtureAsync(
            connectionString,
            "replay",
            gold: 5_000);
        var firstEnvelope = CreateEnvelope(fixture, gridIndex);
        var retryEnvelope = CreateEnvelope(
            fixture,
            gridIndex,
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
            "reconnect preserves Zodiac activation identity");

        await using var firstSource =
            NpgsqlDataSource.Create(connectionString);
        var committed = RequireReceipt(
            await CreateExecutor(firstSource).ExecuteAsync(firstEnvelope),
            ZodiacSkillGridActivationExecutionDisposition.Committed,
            "first replay activation");
        await using var retrySource =
            NpgsqlDataSource.Create(connectionString);
        var duplicate = RequireReceipt(
            await CreateExecutor(retrySource).ExecuteAsync(retryEnvelope),
            ZodiacSkillGridActivationExecutionDisposition.Duplicate,
            "exact activation retry");
        Check.Equal(
            committed,
            duplicate,
            "exact retry returns the original canonical receipt");

        var state = await ReadStateAsync(
            connectionString,
            fixture,
            gridIndex);
        Check.True(
            state.Gold == 2_700 &&
            state.WalletRevision == 1 &&
            state.Level == 1 &&
            state.CurrencyLedgerCount == 1 &&
            state.GoldLedgerDelta == -2_300 &&
            state.AuditCount == 1 &&
            state.InboxCount == 1 &&
            state.OutboxCount == 1 &&
            state.DuplicateCount == 1 &&
            state.WalletReconciled,
            "exact retry records bounded duplicate evidence without redebit");
    }

    private static async Task AssertConcurrentExactCommandAsync(
        string connectionString)
    {
        const int gridIndex = 1;
        var fixture = await CreateFixtureAsync(
            connectionString,
            "race",
            gold: 5_000);
        var envelopeA = CreateEnvelope(fixture, gridIndex);
        var envelopeB = CreateEnvelope(
            fixture,
            gridIndex,
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
                ZodiacSkillGridActivationExecutionDisposition.Committed),
            "one concurrent Zodiac activation commits");
        Check.Equal(
            1,
            results.Count(result =>
                result.Disposition ==
                ZodiacSkillGridActivationExecutionDisposition.Duplicate),
            "one concurrent Zodiac activation replays");
        var committed = results.Single(result =>
            result.Disposition ==
            ZodiacSkillGridActivationExecutionDisposition.Committed)
            .Receipt ??
            throw new InvalidOperationException(
                "The concurrent winner returned no receipt.");
        var duplicate = results.Single(result =>
            result.Disposition ==
            ZodiacSkillGridActivationExecutionDisposition.Duplicate)
            .Receipt ??
            throw new InvalidOperationException(
                "The concurrent replay returned no receipt.");
        Check.Equal(
            committed,
            duplicate,
            "concurrent executors return one canonical receipt");

        var state = await ReadStateAsync(
            connectionString,
            fixture,
            gridIndex);
        Check.True(
            state.Gold == 2_700 &&
            state.WalletRevision == 1 &&
            state.CurrencyLedgerCount == 1 &&
            state.OutboxCount == 1 &&
            state.DuplicateCount == 1 &&
            state.WalletReconciled,
            "concurrent exact command debits and publishes once");
    }

    private static async Task AssertInsufficientGoldRetryAsync(
        string connectionString)
    {
        const int gridIndex = 1;
        var fixture = await CreateFixtureAsync(
            connectionString,
            "funds",
            gold: 1_000);
        var envelope = CreateEnvelope(fixture, gridIndex);
        await using var dataSource =
            NpgsqlDataSource.Create(connectionString);
        var rejected =
            await CreateExecutor(dataSource).ExecuteAsync(envelope);
        Check.True(
            rejected.Disposition ==
                ZodiacSkillGridActivationExecutionDisposition
                    .PreconditionFailed &&
            rejected.Receipt is null &&
            rejected.HasAuthoritativeProjection &&
            rejected.CurrentGold == 1_000 &&
            rejected.CurrentLevel == 0,
            "insufficient Gold returns the authoritative current projection");
        AssertUntouched(
            await ReadStateAsync(
                connectionString,
                fixture,
                gridIndex),
            expectedGold: 1_000,
            "insufficient-Gold activation");

        await AddDurableGoldAsync(
            connectionString,
            fixture,
            goldAfter: 5_000);
        var receipt = RequireReceipt(
            await CreateExecutor(dataSource).ExecuteAsync(
                CreateEnvelope(
                    fixture,
                    gridIndex,
                    connectionId: Guid.NewGuid())),
            ZodiacSkillGridActivationExecutionDisposition.Committed,
            "activation after top-up");
        Check.True(
            receipt.GoldBefore == 5_000 &&
            receipt.GoldAfter == 2_700 &&
            receipt.WalletRevision == 2,
            "retry consumes the top-up wallet revision exactly once");
        var state = await ReadStateAsync(
            connectionString,
            fixture,
            gridIndex);
        Check.True(
            state.Gold == 2_700 &&
            state.WalletRevision == 2 &&
            state.Level == 1 &&
            state.CurrencyLedgerCount == 1 &&
            state.GoldLedgerDelta == -2_300 &&
            state.AuditCount == 1 &&
            state.InboxCount == 1 &&
            state.OutboxCount == 1 &&
            state.WalletReconciled,
            "rejected command can succeed later without stale rejection state");
    }

    private static async Task AssertEnvelopeConflictsAsync(
        string connectionString)
    {
        const int gridIndex = 1;
        var fixture = await CreateFixtureAsync(
            connectionString,
            "guard",
            gold: 5_000);
        var valid = CreateEnvelope(fixture, gridIndex);
        await using var dataSource =
            NpgsqlDataSource.Create(connectionString);
        var executor = CreateExecutor(dataSource);

        var badHash = await executor.ExecuteAsync(
            valid with
            {
                RequestHash = MutateDigest(valid.RequestHash)
            });
        Check.True(
            badHash.Disposition ==
                ZodiacSkillGridActivationExecutionDisposition
                    .RequestHashConflict &&
            badHash.Receipt is null,
            "tampered Zodiac request hash fails closed");

        var badOperation = await executor.ExecuteAsync(
            valid with
            {
                OperationId = MutateDigest(valid.OperationId)
            });
        Check.True(
            badOperation.Disposition ==
                ZodiacSkillGridActivationExecutionDisposition
                    .InvalidIntent &&
            badOperation.Receipt is null,
            "tampered Zodiac operation identity fails closed");
        AssertUntouched(
            await ReadStateAsync(
                connectionString,
                fixture,
                gridIndex),
            expectedGold: 5_000,
            "tampered activation envelopes");
    }

    private static string MutateDigest(string value)
    {
        var replacement = value[0] == '0' ? '1' : '0';
        return replacement + value[1..];
    }
}
