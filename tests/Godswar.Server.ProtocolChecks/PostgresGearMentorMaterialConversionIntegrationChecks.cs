using System.Text.RegularExpressions;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Inventory;
using Godswar.Server.Infrastructure.Inventory;
using Godswar.Server.Infrastructure.Messaging;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class
    PostgresGearMentorMaterialConversionIntegrationChecks
{
    public const string CheckName =
        "PostgreSQL durable Gear Mentor material conversions";

    private const string ConnectionStringVariable =
        "GODSWAR_TEST_POSTGRES_CONNECTION_STRING";
    private const short DefaultSelectedSlot = 0;

    private static readonly Regex DisposableDatabasePattern = new(
        @"^godswar_(?:b03_[a-f0-9]{10}_smoke_[0-9]{2}|b(?:08|09)_[a-z0-9_]{1,40})$",
        RegexOptions.CultureInvariant);

    public static async Task RunAsync()
    {
        var connectionString =
            Environment.GetEnvironmentVariable(ConnectionStringVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Console.WriteLine(
                "SKIP PostgreSQL material-conversion integration " +
                $"({ConnectionStringVariable} is not set)");
            return;
        }

        await using (var safetySource =
                     NpgsqlDataSource.Create(connectionString))
        {
            var databaseName =
                await ReadDatabaseNameAsync(safetySource);
            if (!DisposableDatabasePattern.IsMatch(databaseName))
            {
                Console.WriteLine(
                    "SKIP PostgreSQL material-conversion integration " +
                    "requires a disposable B03/B08/B09 database; " +
                    $"received '{databaseName}'");
                return;
            }
        }

        await using (var store =
                     new Godswar.Server.State.PostgresGameStore(
                         connectionString))
        {
            await store.EnsureSeedDataAsync();
        }

        await AssertTransformRecipesAsync(connectionString);
        await AssertCombineRecipesAsync(connectionString);
        await AssertMutationBranchesAsync(connectionString);
        await AssertReplayConflictAndConcurrencyAsync(connectionString);
        await AssertTerminalRejectionsAsync(connectionString);
        await AssertCrossFamilyStreamAsync(connectionString);
        await AssertFaultRecoveryAsync(connectionString);
    }

    private static
        PostgresGearMentorMaterialConversionCommandExecutor
        CreateExecutor(
            NpgsqlDataSource dataSource,
            IPostgresGearMentorMaterialConversionCommandProbe?
                probe = null) =>
        new(
            dataSource,
            new PostgresOutboxDispatcherOptions(),
            TestItemContent.Content,
            probe);

    private static async Task<
            GearMentorMaterialConversionExecutionResult>
        ExecuteAsync(
            PostgresGearMentorMaterialConversionCommandExecutor executor,
            ConversionFixture fixture,
            Guid operationId,
            int? npcId = null,
            int? selectedSlot = null,
            string? expectedState = null,
            Guid? connectionId = null)
    {
        var subject = new CommandSubject(
            fixture.AccountId,
            fixture.CharacterId);
        var correlation = new CommandConnectionCorrelation(
            connectionId ?? Guid.NewGuid(),
            CommandTransportKind.SecureTlsLegacy);
        var slot = selectedSlot ?? fixture.SelectedSlot;
        var state = expectedState ?? fixture.ExpectedSelectedState;
        if (fixture.Family ==
            CommandFamily.GearMentorTransformCrystal)
        {
            if (!GearMentorTransformCrystalCommandEnvelope
                .TryCreateCommand(
                    operationId,
                    npcId ??
                        GearMentorTransformCrystalCommandEnvelope
                            .SpartaGearMentorNpcId,
                    slot,
                    state,
                    out var command))
            {
                throw new InvalidOperationException(
                    "The fixture requested an invalid Transform command.");
            }

            return await executor.ExecuteAsync(
                PlayerOwnershipTestFences.Bind(
                    GearMentorTransformCrystalCommandEnvelope.Create(
                    subject,
                    correlation,
                    DateTimeOffset.UtcNow,
                    command)));
        }

        if (!GearMentorCombineGemPiecesCommandEnvelope.TryCreateCommand(
                operationId,
                npcId ??
                    GearMentorCombineGemPiecesCommandEnvelope
                        .SpartaGearMentorNpcId,
                slot,
                state,
                out var combineCommand))
        {
            throw new InvalidOperationException(
                "The fixture requested an invalid Combine command.");
        }

        return await executor.ExecuteAsync(
            PlayerOwnershipTestFences.Bind(
                GearMentorCombineGemPiecesCommandEnvelope.Create(
                subject,
                correlation,
                DateTimeOffset.UtcNow,
                combineCommand)));
    }

    private static Task<GearMentorMaterialConversionExecutionResult>
        ReplayAsync(
            PostgresGearMentorMaterialConversionCommandExecutor executor,
            ConversionFixture fixture,
            Guid operationId)
    {
        var subject = new CommandSubject(
            fixture.AccountId,
            fixture.CharacterId);
        return fixture.Family ==
               CommandFamily.GearMentorTransformCrystal
            ? executor.TryReplayTransformAsync(
                subject,
                PlayerOwnershipTestFences.ForCharacter(
                    fixture.CharacterId),
                operationId)
            : executor.TryReplayCombineAsync(
                subject,
                PlayerOwnershipTestFences.ForCharacter(
                    fixture.CharacterId),
                operationId);
    }

    private static GearMentorMaterialConversionExecutionReceipt
        RequireReceipt(
            GearMentorMaterialConversionExecutionResult result,
            GearMentorMaterialConversionExecutionDisposition
                expectedDisposition,
            string description)
    {
        Check.Equal(
            (int)expectedDisposition,
            (int)result.Disposition,
            $"{description} disposition");
        return result.Receipt ??
               throw new InvalidOperationException(
                   $"{description} returned no durable receipt.");
    }

    private static void AssertReceiptsEqual(
        GearMentorMaterialConversionExecutionReceipt expected,
        GearMentorMaterialConversionExecutionReceipt actual,
        string description)
    {
        Check.True(
            expected.Family == actual.Family &&
            expected.CharacterId == actual.CharacterId &&
            expected.Status == actual.Status &&
            expected.NativeResultSubId == actual.NativeResultSubId &&
            expected.SelectedKitBagSlot ==
                actual.SelectedKitBagSlot &&
            expected.SourceItemId == actual.SourceItemId &&
            expected.OutputItemId == actual.OutputItemId &&
            expected.OutputQuantity == actual.OutputQuantity &&
            expected.IsBound == actual.IsBound &&
            expected.InventoryRevision == actual.InventoryRevision &&
            string.Equals(
                expected.AuditReference,
                actual.AuditReference,
                StringComparison.Ordinal) &&
            expected.OutboxEventId == actual.OutboxEventId,
            description);
    }

    private static async Task<string> ReadDatabaseNameAsync(
        NpgsqlDataSource dataSource)
    {
        await using var command =
            dataSource.CreateCommand("SELECT current_database();");
        return await command.ExecuteScalarAsync() as string ??
               throw new InvalidDataException(
                   "PostgreSQL returned no current database name.");
    }
}
