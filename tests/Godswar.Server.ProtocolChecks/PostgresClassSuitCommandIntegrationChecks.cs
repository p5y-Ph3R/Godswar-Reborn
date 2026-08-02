using System.Text.RegularExpressions;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Inventory;
using Godswar.Server.Infrastructure.Inventory;
using Godswar.Server.Infrastructure.Messaging;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresClassSuitCommandIntegrationChecks
{
    public const string CheckName =
        "PostgreSQL authoritative Class Suit transaction";

    private const string ConnectionStringVariable =
        "GODSWAR_TEST_POSTGRES_CONNECTION_STRING";
    private const short GearSlot = 10;
    private const short InsigniaSlot = 11;

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
                "SKIP PostgreSQL Class Suit integration " +
                $"({ConnectionStringVariable} is not set)");
            return;
        }

        await using (var safetySource =
                     NpgsqlDataSource.Create(connectionString))
        {
            var databaseName = await ReadDatabaseNameAsync(safetySource);
            if (!DisposableDatabasePattern.IsMatch(databaseName))
            {
                Console.WriteLine(
                    "SKIP PostgreSQL Class Suit integration requires a " +
                    "disposable B03/B08/B09 database; " +
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

        await AssertCommitReplayAndConflictAsync(connectionString);
        await AssertStaleSelectionIsAtomicAsync(connectionString);
        await AssertInsufficientInsigniaIsAtomicAsync(connectionString);
    }

    private static PostgresGearMentorMaterialConversionCommandExecutor
        CreateExecutor(NpgsqlDataSource dataSource) =>
        new(
            dataSource,
            new PostgresOutboxDispatcherOptions(),
            TestItemContent.Content);

    private static async Task<ClassSuitExecutionResult> ExecuteAsync(
        PostgresGearMentorMaterialConversionCommandExecutor executor,
        ClassSuitFixture fixture,
        Guid operationId,
        int? npcId = null,
        string? expectedGearState = null,
        string? expectedInsigniaState = null)
    {
        var identity = ClassSuitOperationIdentity.SecureClient(operationId);
        if (!ClassSuitCommandEnvelope.TryCreateCommand(
                identity,
                ClassSuitCommandOperation.ExchangeTierI,
                npcId ?? ClassSuitCommandEnvelope.SpartaNpcId,
                ClassSuitCommandEnvelope.DialogIndex,
                new ClassSuitCommandSelection(
                    GearSlot,
                    expectedGearState ?? fixture.ExpectedGearState),
                new ClassSuitCommandSelection(
                    InsigniaSlot,
                    expectedInsigniaState ??
                        fixture.ExpectedInsigniaState),
                secondaryMaterial: null,
                out var command))
        {
            throw new InvalidOperationException(
                "The Class Suit fixture produced an invalid command.");
        }

        var envelope = ClassSuitCommandEnvelope.Create(
            new CommandSubject(fixture.AccountId, fixture.CharacterId),
            new CommandConnectionCorrelation(
                Guid.NewGuid(),
                CommandTransportKind.SecureTlsLegacy),
            DateTimeOffset.UtcNow,
            command);
        return await executor.ExecuteAsync(
            PlayerOwnershipTestFences.Bind(envelope));
    }

    private static async Task<ClassSuitExecutionResult> ReplayAsync(
        PostgresGearMentorMaterialConversionCommandExecutor executor,
        ClassSuitFixture fixture,
        Guid operationId,
        int npcId = ClassSuitCommandEnvelope.SpartaNpcId,
        int gearSlot = GearSlot,
        int materialSlot = InsigniaSlot)
    {
        if (!ClassSuitReplayIntent.TryCreate(
                ClassSuitCommandOperation.ExchangeTierI,
                npcId,
                ClassSuitCommandEnvelope.DialogIndex,
                gearSlot,
                materialSlot,
                ClassSuitReplayIntent.NoKitBagSlot,
                out var replayIntent))
        {
            throw new InvalidOperationException(
                "The Class Suit replay fixture produced an invalid intent.");
        }

        return await executor.TryReplayAsync(
            new CommandSubject(fixture.AccountId, fixture.CharacterId),
            PlayerOwnershipTestFences.ForCharacter(fixture.CharacterId),
            replayIntent,
            ClassSuitOperationIdentity.SecureClient(operationId));
    }

    private static ClassSuitExecutionReceipt RequireReceipt(
        ClassSuitExecutionResult result,
        ClassSuitExecutionDisposition expected,
        string description)
    {
        Check.Equal(
            (int)expected,
            (int)result.Disposition,
            $"{description} disposition");
        return result.Receipt ?? throw new InvalidOperationException(
            $"{description} returned no durable receipt.");
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
