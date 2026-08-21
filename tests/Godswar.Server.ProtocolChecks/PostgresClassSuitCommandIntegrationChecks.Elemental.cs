using System.Globalization;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Inventory;
using Godswar.Server.Infrastructure.Inventory;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresClassSuitCommandIntegrationChecks
{
    private const short ElementalStoneSlot = 12;
    private const short SameElementStoneSlot = 13;

    private static async Task AssertElementalAttributeExactOnceAsync(
        string connectionString)
    {
        var fixture = await CreateElementalFixtureAsync(connectionString);
        var operationId = Guid.NewGuid();

        ClassSuitExecutionReceipt committed;
        await using (var source = NpgsqlDataSource.Create(connectionString))
        {
            committed = RequireReceipt(
                await ExecuteElementalAsync(
                    CreateExecutor(source),
                    fixture,
                    operationId,
                    fixture.ExpectedGearState,
                    fixture.ExpectedFlameSparkState,
                    fixture.ExpectedElementalStoneState,
                    ElementalStoneSlot),
                ClassSuitExecutionDisposition.Committed,
                "elemental Class Suit first execution");
        }

        Check.True(
            committed.InventoryRevision == 1 &&
            committed.Status == ClassSuitCommandResultStatus.Succeeded &&
            committed.Mutations.Count == 3,
            "elemental Class Suit commit records gear and both consumables");
        var committedState = await ReadElementalStateAsync(
            connectionString,
            fixture);
        AssertElementalCommittedState(
            committedState,
            expectedDuplicateCount: 0,
            "elemental Class Suit commit persists [Burn] Damage over time and consumes " +
            "one stone plus one Flame Spark");

        ClassSuitExecutionReceipt duplicate;
        await using (var source = NpgsqlDataSource.Create(connectionString))
        {
            duplicate = RequireReceipt(
                await ExecuteElementalAsync(
                    CreateExecutor(source),
                    fixture,
                    operationId,
                    fixture.ExpectedGearState,
                    fixture.ExpectedFlameSparkState,
                    fixture.ExpectedElementalStoneState,
                    ElementalStoneSlot),
                ClassSuitExecutionDisposition.Duplicate,
                "elemental Class Suit duplicate UUID");
        }

        AssertReceiptsEqual(
            committed,
            duplicate,
            "elemental Class Suit replay returns the durable receipt");
        var replayedState = await ReadElementalStateAsync(
            connectionString,
            fixture);
        AssertElementalCommittedState(
            replayedState,
            expectedDuplicateCount: 1,
            "elemental Class Suit replay cannot consume either material twice");

        await AssertSameElementRejectedAsync(
            connectionString,
            fixture,
            replayedState);
    }

    private static async Task AssertSameElementRejectedAsync(
        string connectionString,
        ElementalFixture fixture,
        ElementalDurableState current)
    {
        ClassSuitExecutionReceipt rejected;
        await using (var source = NpgsqlDataSource.Create(connectionString))
        {
            rejected = RequireReceipt(
                await ExecuteElementalAsync(
                    CreateExecutor(source),
                    fixture,
                    Guid.NewGuid(),
                    current.Gear.ToCompactString(),
                    current.FlameSpark.ToCompactString(),
                    current.SameElementStone.ToCompactString(),
                    SameElementStoneSlot),
                ClassSuitExecutionDisposition.TerminalRejected,
                "same-element Class Suit operation");
        }

        Check.True(
            rejected.Status ==
                ClassSuitCommandResultStatus.AttributeAlreadyPresent &&
            rejected.Mutations.Count == 0,
            "a second family from the same element is terminally rejected");
        var after = await ReadElementalStateAsync(
            connectionString,
            fixture);
        Check.True(
            after.InventoryRevision == 1 &&
            after.Gear == current.Gear &&
            after.FlameSpark == current.FlameSpark &&
            after.ElementalStone == current.ElementalStone &&
            after.SameElementStone == current.SameElementStone &&
            after.RejectedCount == 1,
            "same-element rejection consumes nothing and does not advance inventory");
    }

    private static async Task<ClassSuitExecutionResult>
        ExecuteElementalAsync(
            PostgresGearMentorMaterialConversionCommandExecutor executor,
            ElementalFixture fixture,
            Guid operationId,
            string expectedGear,
            string expectedFlameSpark,
            string expectedStone,
            short stoneSlot)
    {
        if (!ClassSuitCommandEnvelope.TryCreateCommand(
                ClassSuitOperationIdentity.SecureClient(operationId),
                ClassSuitCommandOperation.AddAttribute,
                ClassSuitCommandEnvelope.SpartaNpcId,
                ClassSuitCommandEnvelope.DialogIndex,
                new ClassSuitCommandSelection(GearSlot, expectedGear),
                new ClassSuitCommandSelection(
                    InsigniaSlot,
                    expectedFlameSpark),
                new ClassSuitCommandSelection(stoneSlot, expectedStone),
                out var command))
        {
            throw new InvalidOperationException(
                "The elemental Class Suit fixture produced an invalid command.");
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

    private static async Task<ElementalFixture> CreateElementalFixtureAsync(
        string connectionString)
    {
        var token = Guid.NewGuid().ToString("N")[..12];
        var gear = CreateElementalGear();
        var flameSpark = CreateStack(
            GearEnhancementMaterialCatalog.FlameSparkItemId,
            3);
        var elementalStone = CreateStack(16300, 3);
        var sameElementStone = CreateStack(16300, 3);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var accountId = await InsertElementalAccountAsync(
            connection,
            transaction,
            token);
        var characterId = await InsertElementalCharacterAsync(
            connection,
            transaction,
            accountId,
            token);
        await InsertItemAsync(
            connection, transaction, characterId, GearSlot, gear);
        await InsertItemAsync(
            connection, transaction, characterId, InsigniaSlot, flameSpark);
        await InsertItemAsync(
            connection,
            transaction,
            characterId,
            ElementalStoneSlot,
            elementalStone);
        await InsertItemAsync(
            connection,
            transaction,
            characterId,
            SameElementStoneSlot,
            sameElementStone);
        Check.True(
            await PostgresCharacterEconomyBaseline.EnsureAsync(
                connection,
                transaction,
                accountId,
                characterId,
                commandTimeoutSeconds: 30,
                CancellationToken.None),
            "elemental Class Suit fixture captures an economy baseline");
        await PlayerOwnershipTestFences.InstallAsync(
            connection,
            transaction,
            accountId,
            characterId);
        await transaction.CommitAsync();

        return new ElementalFixture(
            accountId,
            characterId,
            gear.ToCompactString(),
            flameSpark.ToCompactString(),
            elementalStone.ToCompactString());
    }

    private static async Task<int> InsertElementalAccountAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string token)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO public.accounts (username, password)
            VALUES (@username, '')
            RETURNING id;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("username", $"b09el_{token}");
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static async Task<int> InsertElementalCharacterAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int accountId,
        string token)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO public.character_base (
                account_id, server_id, name, camp, profession, fighter_job_lv,
                "Money", "Stone", inventory_revision)
            VALUES (@accountId, 1, @name, 1, 0, 160, 1000, 100, 0)
            RETURNING id;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("accountId", accountId);
        command.Parameters.AddWithValue("name", $"EL{token}");
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static CompactItemEntry CreateElementalGear() =>
        CompactItemEntry.Empty with
        {
            Id = 1034,
            Attribute1 = 40,
            AttributeLevel1 = 3,
            Quality = 20,
            Grade = 25,
            Bound = 0,
            Stack = 1
        };

    private static CompactItemEntry CreateStack(uint itemId, short stack) =>
        CompactItemEntry.Empty with
        {
            Id = itemId,
            Quality = 1,
            Grade = 1,
            Bound = 1,
            Stack = stack
        };

    private static async Task<ElementalDurableState>
        ReadElementalStateAsync(
            string connectionString,
            ElementalFixture fixture)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        var family = ClassSuitPersistenceCodec.FamilyCode(
            CommandFamily.ClassSuitAddAttribute);
        await using var command = new NpgsqlCommand(
            """
            SELECT character_row.inventory_revision,
                   COALESCE((SELECT max(duplicate_count)
                     FROM public.command_inbox
                     WHERE principal_type = @principalType
                       AND principal_key = @principalKey
                       AND aggregate_type = @aggregateType
                       AND aggregate_key = @aggregateKey
                       AND command_family = @commandFamily), 0),
                   (SELECT count(*)
                     FROM public.command_inbox
                     WHERE principal_type = @principalType
                       AND principal_key = @principalKey
                       AND aggregate_type = @aggregateType
                       AND aggregate_key = @aggregateKey
                       AND command_family = @commandFamily
                       AND result_code = 'terminal_rejected')
            FROM public.character_base character_row
            WHERE character_row.id = @characterId;
            """,
            connection);
        command.Parameters.AddWithValue("characterId", fixture.CharacterId);
        command.Parameters.AddWithValue(
            "principalType",
            ClassSuitPersistenceCodec.PrincipalType);
        command.Parameters.AddWithValue(
            "principalKey",
            fixture.AccountId.ToString(CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue(
            "aggregateType",
            ClassSuitPersistenceCodec.AggregateType);
        command.Parameters.AddWithValue(
            "aggregateKey",
            ClassSuitPersistenceCodec.AggregateKey(fixture.CharacterId));
        command.Parameters.AddWithValue("commandFamily", family);
        await using var reader = await command.ExecuteReaderAsync();
        Check.True(
            await reader.ReadAsync(),
            "elemental Class Suit fixture character remains durable");
        var revision = reader.GetInt64(0);
        var duplicates = reader.GetInt32(1);
        var rejected = reader.GetInt64(2);
        await reader.CloseAsync();
        return new ElementalDurableState(
            revision,
            await ReadItemAsync(connection, fixture.CharacterId, GearSlot),
            await ReadItemAsync(
                connection,
                fixture.CharacterId,
                InsigniaSlot),
            await ReadItemAsync(
                connection,
                fixture.CharacterId,
                ElementalStoneSlot),
            await ReadItemAsync(
                connection,
                fixture.CharacterId,
                SameElementStoneSlot),
            duplicates,
            rejected);
    }

    private static void AssertElementalCommittedState(
        ElementalDurableState state,
        int expectedDuplicateCount,
        string message)
    {
        Check.True(
            state.InventoryRevision == 1 &&
            state.Gear.ElementalAttribute1 == 480 &&
            state.Gear.ElementalAttribute2 is null &&
            state.Gear.ClassAttribute1 is null &&
            state.Gear.ClassAttribute2 is null &&
            state.FlameSpark.Stack == 2 &&
            state.ElementalStone.Stack == 2 &&
            state.SameElementStone.Stack == 3 &&
            state.DuplicateCount == expectedDuplicateCount,
            message);
    }

    private sealed record ElementalFixture(
        int AccountId,
        int CharacterId,
        string ExpectedGearState,
        string ExpectedFlameSparkState,
        string ExpectedElementalStoneState);

    private sealed record ElementalDurableState(
        long InventoryRevision,
        CompactItemEntry Gear,
        CompactItemEntry FlameSpark,
        CompactItemEntry ElementalStone,
        CompactItemEntry SameElementStone,
        int DuplicateCount,
        long RejectedCount);
}
