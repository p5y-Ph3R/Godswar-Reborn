using System.Text.RegularExpressions;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Inventory;
using Godswar.Server.Infrastructure.Inventory;
using Godswar.Server.Infrastructure.Messaging;
using Godswar.Server.State;
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
            await using var migrationSource =
                NpgsqlDataSource.Create(connectionString);
            await new PostgresSchemaMigrationRunner(migrationSource)
                .InitializeGodswarSchemaAsync();
            await store.EnsureSeedDataAsync();
        }

        await AssertCommitReplayAndConflictAsync(connectionString);
        await AssertEquippedWeaponCommitIsAtomicAsync(connectionString);
        await AssertTierIVReverseCommitAndReplayAsync(connectionString);
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
        string? expectedInsigniaState = null,
        ClassSuitItemLocation gearLocation =
            ClassSuitItemLocation.KitBag)
    {
        var identity = ClassSuitOperationIdentity.SecureClient(operationId);
        if (!ClassSuitCommandEnvelope.TryCreateCommand(
                identity,
                ClassSuitCommandOperation.ExchangeTierI,
                npcId ?? ClassSuitCommandEnvelope.SpartaNpcId,
                ClassSuitCommandEnvelope.DialogIndex,
                new ClassSuitCommandSelection(
                    GearSlot,
                    expectedGearState ?? fixture.ExpectedGearState,
                    gearLocation),
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

    private static async Task AssertEquippedWeaponCommitIsAtomicAsync(
        string connectionString)
    {
        var fixture = await CreateFixtureAsync(
            connectionString,
            "equipped");
        await MoveFixtureGearToEquipmentAsync(
            connectionString,
            fixture);
        var operationId = Guid.NewGuid();

        ClassSuitExecutionReceipt committed;
        await using (var source = NpgsqlDataSource.Create(connectionString))
        {
            committed = RequireReceipt(
                await ExecuteAsync(
                    CreateExecutor(source),
                    fixture,
                    operationId,
                    gearLocation: ClassSuitItemLocation.Equipment),
                ClassSuitExecutionDisposition.Committed,
                "equipped Class Suit first execution");
        }

        Check.True(
            committed.InventoryRevision == 1 &&
            committed.Mutations.Count == 2 &&
            committed.Mutations.Any(static mutation =>
                mutation.Location == ClassSuitItemLocation.Equipment &&
                mutation.KitBagSlot == EquipmentSlots.Weapon &&
                mutation.BeforeItemId == 1013 &&
                mutation.AfterItemId == 1032) &&
            committed.Mutations.Any(static mutation =>
                mutation.Location == ClassSuitItemLocation.KitBag &&
                mutation.KitBagSlot == InsigniaSlot &&
                mutation.BeforeItemId ==
                    ClassSuitConversionCatalog.PromotionalInsigniaI &&
                mutation.AfterItemId ==
                    ClassSuitConversionCatalog.PromotionalInsigniaI),
            "equipped Class Suit receipt identifies equipment and bag " +
            "mutations separately");

        var committedState = await ReadEquippedStateAsync(
            connectionString,
            fixture);
        AssertEquippedCommittedState(
            committedState,
            expectedDuplicateCount: 0,
            "equipped Class Suit atomically converts the weapon and " +
            "consumes its bag insignias");

        ClassSuitExecutionReceipt duplicate;
        await using (var source = NpgsqlDataSource.Create(connectionString))
        {
            duplicate = RequireReceipt(
                await ExecuteAsync(
                    CreateExecutor(source),
                    fixture,
                    operationId,
                    gearLocation: ClassSuitItemLocation.Equipment),
                ClassSuitExecutionDisposition.Duplicate,
                "equipped Class Suit duplicate UUID");
        }
        AssertReceiptsEqual(
            committed,
            duplicate,
            "equipped Class Suit duplicate replays the durable receipt");
        AssertEquippedCommittedState(
            await ReadEquippedStateAsync(connectionString, fixture),
            expectedDuplicateCount: 1,
            "equipped Class Suit duplicate cannot consume the insignia " +
            "or convert the weapon twice");
    }

    private static async Task MoveFixtureGearToEquipmentAsync(
        string connectionString,
        ClassSuitFixture fixture)
    {
        await using var connection =
            new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            UPDATE public.character_items
            SET item_location = 0
            WHERE user_id = @characterId
              AND item_location = 1
              AND slot_index = @slot;
            """,
            connection);
        command.Parameters.AddWithValue(
            "characterId",
            fixture.CharacterId);
        command.Parameters.AddWithValue("slot", GearSlot);
        Check.Equal(
            1,
            await command.ExecuteNonQueryAsync(),
            "equipped Class Suit fixture moves its weapon atomically");
    }

    private static async Task<EquippedClassSuitDurableState>
        ReadEquippedStateAsync(
            string connectionString,
            ClassSuitFixture fixture)
    {
        await using var connection =
            new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        var family = ClassSuitPersistenceCodec.FamilyCode(
            CommandFamily.ClassSuitExchangeTierI);
        var aggregateKey = ClassSuitPersistenceCodec.AggregateKey(
            fixture.CharacterId);
        await using var command = new NpgsqlCommand(
            """
            SELECT
                character_row.inventory_revision,
                (SELECT count(*) FROM public.character_items
                 WHERE user_id = @characterId
                   AND item_location = 0
                   AND slot_index = @gearSlot
                   AND prop_id = 1032
                   AND attribute1 = 40
                   AND attribute2 IS NULL
                   AND class_attribute1 IS NULL
                   AND class_attribute2 IS NULL
                   AND attribute_level1 = 3
                   AND attribute_level2 IS NULL
                   AND item_quality = 20
                   AND item_grade = 25
                   AND bound = 1
                   AND stack = 1
                   AND item_exp = 777
                   AND holy_suit_code = 705
                   AND holy_socket_count = 2
                   AND holy_socket1_effect_id = 501
                   AND holy_socket1_level = 2
                   AND holy_socket2_effect_id = 502
                   AND holy_socket2_level = 3),
                (SELECT count(*) FROM public.character_items
                 WHERE user_id = @characterId
                   AND item_location = 1
                   AND slot_index = @gearSlot),
                COALESCE((SELECT stack FROM public.character_items
                 WHERE user_id = @characterId
                   AND item_location = 1
                   AND slot_index = @insigniaSlot
                   AND prop_id = @insigniaId), -1),
                (SELECT count(*) FROM public.command_audit
                 WHERE principal_type = @principalType
                   AND principal_key = @principalKey
                   AND aggregate_type = @aggregateType
                   AND aggregate_key = @aggregateKey
                   AND command_family = @commandFamily),
                (SELECT count(*) FROM public.command_inbox
                 WHERE principal_type = @principalType
                   AND principal_key = @principalKey
                   AND aggregate_type = @aggregateType
                   AND aggregate_key = @aggregateKey
                   AND command_family = @commandFamily),
                (SELECT count(*) FROM public.character_inventory_ledger
                 WHERE account_id = @accountId
                   AND character_id = @characterId
                   AND reason_code = @commandFamily),
                (SELECT count(*) FROM public.outbox_events
                 WHERE aggregate_type = @aggregateType
                   AND aggregate_key = @aggregateKey
                   AND event_type = @eventType),
                COALESCE((SELECT max(duplicate_count)
                 FROM public.command_inbox
                 WHERE principal_type = @principalType
                   AND principal_key = @principalKey
                   AND aggregate_type = @aggregateType
                   AND aggregate_key = @aggregateKey
                   AND command_family = @commandFamily), 0)
            FROM public.character_base character_row
            WHERE character_row.id = @characterId;
            """,
            connection);
        command.Parameters.AddWithValue("accountId", fixture.AccountId);
        command.Parameters.AddWithValue(
            "characterId",
            fixture.CharacterId);
        command.Parameters.AddWithValue("gearSlot", GearSlot);
        command.Parameters.AddWithValue("insigniaSlot", InsigniaSlot);
        command.Parameters.AddWithValue(
            "insigniaId",
            checked((int)ClassSuitConversionCatalog.PromotionalInsigniaI));
        command.Parameters.AddWithValue(
            "principalType",
            ClassSuitPersistenceCodec.PrincipalType);
        command.Parameters.AddWithValue(
            "principalKey",
            fixture.AccountId.ToString(
                System.Globalization.CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue(
            "aggregateType",
            ClassSuitPersistenceCodec.AggregateType);
        command.Parameters.AddWithValue("aggregateKey", aggregateKey);
        command.Parameters.AddWithValue("commandFamily", family);
        command.Parameters.AddWithValue(
            "eventType",
            ClassSuitPersistenceCodec.EventType);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new InvalidDataException(
                "The equipped Class Suit fixture character disappeared.");
        }

        return new EquippedClassSuitDurableState(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            reader.GetInt16(3),
            reader.GetInt64(4),
            reader.GetInt64(5),
            reader.GetInt64(6),
            reader.GetInt64(7),
            reader.GetInt32(8));
    }

    private static void AssertEquippedCommittedState(
        EquippedClassSuitDurableState state,
        int expectedDuplicateCount,
        string description)
    {
        Check.True(
            state.InventoryRevision == 1 &&
            state.MatchingEquipmentRows == 1 &&
            state.BagGearRows == 0 &&
            state.InsigniaStack == 2 &&
            state.AuditCount == 1 &&
            state.InboxCount == 1 &&
            state.LedgerCount == 2 &&
            state.OutboxCount == 1 &&
            state.DuplicateCount == expectedDuplicateCount,
            description);
    }

    private sealed record EquippedClassSuitDurableState(
        long InventoryRevision,
        long MatchingEquipmentRows,
        long BagGearRows,
        short InsigniaStack,
        long AuditCount,
        long InboxCount,
        long LedgerCount,
        long OutboxCount,
        int DuplicateCount);

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
