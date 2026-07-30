using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Inventory;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class
    PostgresGearMentorMaterialConversionIntegrationChecks
{
    private static async Task
        AssertReplayConflictAndConcurrencyAsync(
            string connectionString)
    {
        foreach (var family in new[]
                 {
                     CommandFamily.GearMentorTransformCrystal,
                     CommandFamily.GearMentorCombineGemPieces
                 })
        {
            await AssertReplayAndConflictAsync(
                connectionString,
                family);
            await AssertConcurrentDuplicateAsync(
                connectionString,
                family);
            await AssertConcurrentDistinctIdentityAsync(
                connectionString,
                family);
            await AssertForgedFamilyIsInvalidAsync(
                connectionString,
                family);
        }
    }

    private static async Task AssertReplayAndConflictAsync(
        string connectionString,
        CommandFamily family)
    {
        var fixture = await CreateDefaultFixtureAsync(
            connectionString,
            "replay",
            family);
        var operationId = Guid.NewGuid();
        GearMentorMaterialConversionExecutionResult first;
        await using (var source =
                     NpgsqlDataSource.Create(connectionString))
        {
            first = await ExecuteAsync(
                CreateExecutor(source),
                fixture,
                operationId);
        }

        var committed = RequireReceipt(
            first,
            GearMentorMaterialConversionExecutionDisposition.Committed,
            $"{family} first execution");

        GearMentorMaterialConversionExecutionResult retry;
        await using (var source =
                     NpgsqlDataSource.Create(connectionString))
        {
            retry = await ExecuteAsync(
                CreateExecutor(source),
                fixture,
                operationId,
                connectionId: Guid.NewGuid());
        }
        AssertReceiptsEqual(
            committed,
            RequireReceipt(
                retry,
                GearMentorMaterialConversionExecutionDisposition
                    .Duplicate,
                $"{family} reconnect retry"),
            $"{family} reconnect returns the canonical receipt");

        GearMentorMaterialConversionExecutionResult replay;
        await using (var source =
                     NpgsqlDataSource.Create(connectionString))
        {
            replay = await ReplayAsync(
                CreateExecutor(source),
                fixture,
                operationId);
        }
        AssertReceiptsEqual(
            committed,
            RequireReceipt(
                replay,
                GearMentorMaterialConversionExecutionDisposition
                    .Duplicate,
                $"{family} selection-free replay"),
            $"{family} selection-free replay returns canonical receipt");

        GearMentorMaterialConversionExecutionResult conflict;
        await using (var source =
                     NpgsqlDataSource.Create(connectionString))
        {
            conflict = await ExecuteAsync(
                CreateExecutor(source),
                fixture,
                operationId,
                npcId:
                    GearMentorSingleMaterialCommandContract
                        .AthensGearMentorNpcId);
        }
        Check.True(
            conflict.Disposition ==
                GearMentorMaterialConversionExecutionDisposition
                    .RequestHashConflict &&
            conflict.Receipt is null,
            $"{family} rejects same UUID with changed request");

        var state = await ReadStateAsync(connectionString, fixture);
        Check.True(
            state.InventoryRevision == 1 &&
            state.AuditCount == 1 &&
            state.InboxCount == 1 &&
            state.LedgerCount == 1 &&
            state.OutboxCount == 1 &&
            state.DuplicateCount == 2 &&
            state.ConflictCount == 1 &&
            state.IsReconciled,
            $"{family} retries and conflict never duplicate value");
    }

    private static async Task AssertConcurrentDuplicateAsync(
        string connectionString,
        CommandFamily family)
    {
        var fixture = await CreateDefaultFixtureAsync(
            connectionString,
            "raceid",
            family);
        var operationId = Guid.NewGuid();
        await using var sourceA =
            NpgsqlDataSource.Create(connectionString);
        await using var sourceB =
            NpgsqlDataSource.Create(connectionString);
        var results = await Task.WhenAll(
            ExecuteAsync(
                CreateExecutor(sourceA),
                fixture,
                operationId,
                connectionId: Guid.NewGuid()),
            ExecuteAsync(
                CreateExecutor(sourceB),
                fixture,
                operationId,
                connectionId: Guid.NewGuid()));
        Check.Equal(
            1,
            results.Count(result =>
                result.Disposition ==
                GearMentorMaterialConversionExecutionDisposition
                    .Committed),
            $"{family} concurrent identical operation commits once");
        Check.Equal(
            1,
            results.Count(result =>
                result.Disposition ==
                GearMentorMaterialConversionExecutionDisposition
                    .Duplicate),
            $"{family} concurrent identical operation replays once");
        AssertReceiptsEqual(
            results.Single(result =>
                result.Disposition ==
                GearMentorMaterialConversionExecutionDisposition
                    .Committed).Receipt!,
            results.Single(result =>
                result.Disposition ==
                GearMentorMaterialConversionExecutionDisposition
                    .Duplicate).Receipt!,
            $"{family} concurrent executors share one receipt");
    }

    private static async Task AssertConcurrentDistinctIdentityAsync(
        string connectionString,
        CommandFamily family)
    {
        var fixture = await CreateDefaultFixtureAsync(
            connectionString,
            "race2",
            family);
        await using var sourceA =
            NpgsqlDataSource.Create(connectionString);
        await using var sourceB =
            NpgsqlDataSource.Create(connectionString);
        var results = await Task.WhenAll(
            ExecuteAsync(
                CreateExecutor(sourceA),
                fixture,
                Guid.NewGuid()),
            ExecuteAsync(
                CreateExecutor(sourceB),
                fixture,
                Guid.NewGuid()));
        Check.Equal(
            1,
            results.Count(result =>
                result.Disposition ==
                GearMentorMaterialConversionExecutionDisposition
                    .Committed),
            $"{family} distinct-UUID race commits value once");
        Check.Equal(
            1,
            results.Count(result =>
                result.Disposition ==
                GearMentorMaterialConversionExecutionDisposition
                    .TerminalRejected &&
                result.Receipt?.Status ==
                GearMentorMaterialConversionResultStatus
                    .StaleSelection),
            $"{family} distinct-UUID loser becomes durable stale result");

        var state = await ReadStateAsync(connectionString, fixture);
        Check.True(
            state.InventoryRevision == 1 &&
            state.AuditCount == 2 &&
            state.InboxCount == 2 &&
            state.LedgerCount == 1 &&
            state.OutboxCount == 1 &&
            state.CommittedInboxCount == 1 &&
            state.RejectedInboxCount == 1 &&
            state.IsReconciled,
            $"{family} distinct-UUID race preserves one mutation");
    }

    private static async Task AssertForgedFamilyIsInvalidAsync(
        string connectionString,
        CommandFamily family)
    {
        var fixture = await CreateDefaultFixtureAsync(
            connectionString,
            "forged",
            family);
        await using var source =
            NpgsqlDataSource.Create(connectionString);
        var executor = CreateExecutor(source);
        var subject = new CommandSubject(
            fixture.AccountId,
            fixture.CharacterId);
        var correlation = new CommandConnectionCorrelation(
            Guid.NewGuid(),
            CommandTransportKind.SecureTlsLegacy);
        var operationId = Guid.NewGuid();
        GearMentorMaterialConversionExecutionResult result;
        if (family == CommandFamily.GearMentorTransformCrystal)
        {
            _ = GearMentorTransformCrystalCommandEnvelope
                .TryCreateCommand(
                    operationId,
                    GearMentorSingleMaterialCommandContract
                        .SpartaGearMentorNpcId,
                    fixture.SelectedSlot,
                    fixture.ExpectedSelectedState,
                    out var command);
            var envelope =
                PlayerOwnershipTestFences.Bind(
                    GearMentorTransformCrystalCommandEnvelope.Create(
                    subject,
                    correlation,
                    DateTimeOffset.UtcNow,
                    command) with
                {
                    Family =
                        CommandFamily.GearMentorCombineGemPieces
                });
            result = await executor.ExecuteAsync(envelope);
        }
        else
        {
            _ = GearMentorCombineGemPiecesCommandEnvelope
                .TryCreateCommand(
                    operationId,
                    GearMentorSingleMaterialCommandContract
                        .SpartaGearMentorNpcId,
                    fixture.SelectedSlot,
                    fixture.ExpectedSelectedState,
                    out var command);
            var envelope =
                PlayerOwnershipTestFences.Bind(
                    GearMentorCombineGemPiecesCommandEnvelope.Create(
                    subject,
                    correlation,
                    DateTimeOffset.UtcNow,
                    command) with
                {
                    Family =
                        CommandFamily.GearMentorTransformCrystal
                });
            result = await executor.ExecuteAsync(envelope);
        }

        Check.True(
            result.Disposition ==
                GearMentorMaterialConversionExecutionDisposition
                    .InvalidIntent &&
            result.Receipt is null,
            $"{family} forged-family envelope fails as InvalidIntent");
        var state = await ReadStateAsync(connectionString, fixture);
        Check.True(
            state.InventoryRevision == 0 &&
            state.AuditCount == 0 &&
            state.InboxCount == 0 &&
            state.LedgerCount == 0 &&
            state.OutboxCount == 0,
            $"{family} forged-family envelope writes nothing");
    }

    private static Task<ConversionFixture> CreateDefaultFixtureAsync(
        string connectionString,
        string scenario,
        CommandFamily family) =>
        family == CommandFamily.GearMentorTransformCrystal
            ? CreateFixtureAsync(
                connectionString,
                scenario,
                family,
                sourceItemId: 4234,
                sourceStack: 1,
                outputItemId: 4233,
                outputQuantity: 2)
            : CreateFixtureAsync(
                connectionString,
                scenario,
                family,
                sourceItemId: 4216,
                sourceStack: 99,
                outputItemId: 4215,
                outputQuantity: 1);
}
