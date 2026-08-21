using System.Globalization;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Inventory;
using Godswar.Server.Infrastructure.Inventory;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresClassSuitCommandIntegrationChecks
{
    private const short ReverseRefundFirstSlot = 20;
    private const short ReverseRefundStackBefore = 98;
    private const short ReverseRefundStackAfter = 99;

    private static readonly uint[] TierIVReverseInsignias =
    [
        ClassSuitConversionCatalog.PromotionalInsigniaI,
        ClassSuitConversionCatalog.PromotionalInsigniaII,
        ClassSuitConversionCatalog.PromotionalInsigniaIII,
        ClassSuitConversionCatalog.PromotionalInsigniaIV
    ];

    private static async Task AssertTierIVReverseCommitAndReplayAsync(
        string connectionString)
    {
        var fixture = await CreateTierIVReverseFixtureAsync(
            connectionString);
        var operationId = Guid.NewGuid();

        ClassSuitExecutionReceipt committed;
        await using (var source = NpgsqlDataSource.Create(connectionString))
        {
            committed = RequireReceipt(
                await ExecuteTierIVReverseAsync(
                    CreateExecutor(source),
                    fixture,
                    operationId),
                ClassSuitExecutionDisposition.Committed,
                "Class Suit IV reverse first execution");
        }

        Check.True(
            committed.Family == CommandFamily.ClassSuitConvertToCommon &&
            committed.Operation ==
                ClassSuitCommandOperation.ConvertToCommon &&
            committed.Status == ClassSuitCommandResultStatus.Succeeded &&
            committed.NativeResultSubId == 152 &&
            committed.InventoryRevision == 1 &&
            committed.OutboxEventId.HasValue &&
            committed.Mutations.Count ==
                ClassSuitPersistenceCodec.MaximumMutationCount &&
            committed.Mutations.Count(static mutation =>
                mutation.KitBagSlot == GearSlot &&
                mutation.BeforeItemId == 1035 &&
                mutation.AfterItemId == 1013) == 1,
            "Tier-IV reverse commits the exact bounded receipt");
        foreach (var insigniaId in TierIVReverseInsignias)
        {
            Check.Equal(
                3,
                committed.Mutations.Count(mutation =>
                    mutation.BeforeItemId == insigniaId &&
                    mutation.AfterItemId == insigniaId),
                $"Tier-IV reverse records split refund {insigniaId}");
        }

        AssertTierIVReverseState(
            await ReadTierIVReverseStateAsync(
                connectionString,
                fixture),
            expectedDuplicateCount: 0,
            "Tier-IV reverse atomically replaces gear and refunds I-IV");

        ClassSuitExecutionReceipt duplicate;
        await using (var source = NpgsqlDataSource.Create(connectionString))
        {
            duplicate = RequireReceipt(
                await ExecuteTierIVReverseAsync(
                    CreateExecutor(source),
                    fixture,
                    operationId),
                ClassSuitExecutionDisposition.Duplicate,
                "Class Suit IV reverse duplicate UUID");
        }
        AssertReceiptsEqual(
            committed,
            duplicate,
            "Tier-IV reverse duplicate replays its durable receipt");
        AssertTierIVReverseState(
            await ReadTierIVReverseStateAsync(
                connectionString,
                fixture),
            expectedDuplicateCount: 1,
            "Tier-IV reverse duplicate cannot grant insignias twice");
    }

    private static async Task<ClassSuitExecutionResult>
        ExecuteTierIVReverseAsync(
            PostgresGearMentorMaterialConversionCommandExecutor executor,
            TierIVReverseFixture fixture,
            Guid operationId)
    {
        if (!ClassSuitCommandEnvelope.TryCreateCommand(
                ClassSuitOperationIdentity.SecureClient(operationId),
                ClassSuitCommandOperation.ConvertToCommon,
                ClassSuitCommandEnvelope.SpartaNpcId,
                ClassSuitCommandEnvelope.DialogIndex,
                new ClassSuitCommandSelection(
                    GearSlot,
                    fixture.ExpectedGearState),
                primaryMaterial: null,
                secondaryMaterial: null,
                out var command))
        {
            throw new InvalidOperationException(
                "The Tier-IV reverse fixture produced an invalid command.");
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

    private static async Task<TierIVReverseFixture>
        CreateTierIVReverseFixtureAsync(string connectionString)
    {
        var token = Guid.NewGuid().ToString("N")[..12];
        var username = $"b09cs_reverse4_{token}";
        var tierIVGear = CreateCommonGear() with
        {
            Id = 1035,
            Bound = 1,
            ClassAttribute1 = 200
        };

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction =
            await connection.BeginTransactionAsync();

        int accountId;
        await using (var command = new NpgsqlCommand(
            """
            INSERT INTO public.accounts (username, password)
            VALUES (@username, '')
            RETURNING id;
            """,
            connection,
            transaction))
        {
            command.Parameters.AddWithValue("username", username);
            accountId = Convert.ToInt32(
                await command.ExecuteScalarAsync() ??
                throw new InvalidDataException(
                    "The Tier-IV reverse account has no identity."));
        }

        int characterId;
        await using (var command = new NpgsqlCommand(
            """
            INSERT INTO public.character_base (
                account_id, server_id, name, camp, profession, fighter_job_lv,
                "Money", "Stone", inventory_revision
            )
            VALUES (@accountId, 1, @name, 1, 0, 160, 1000, 100, 0)
            RETURNING id;
            """,
            connection,
            transaction))
        {
            command.Parameters.AddWithValue("accountId", accountId);
            command.Parameters.AddWithValue(
                "name",
                $"CSR4{token}");
            characterId = Convert.ToInt32(
                await command.ExecuteScalarAsync() ??
                throw new InvalidDataException(
                    "The Tier-IV reverse character has no identity."));
        }

        await InsertItemAsync(
            connection,
            transaction,
            characterId,
            GearSlot,
            tierIVGear);
        var slot = ReverseRefundFirstSlot;
        foreach (var insigniaId in TierIVReverseInsignias)
        {
            for (var stack = 0; stack < 3; stack++)
            {
                await InsertItemAsync(
                    connection,
                    transaction,
                    characterId,
                    slot++,
                    CompactItemEntry.Empty with
                    {
                        Id = insigniaId,
                        Quality = 1,
                        Grade = 1,
                        Bound = 1,
                        Stack = ReverseRefundStackBefore
                    });
            }
        }

        Check.True(
            await PostgresCharacterEconomyBaseline.EnsureAsync(
                connection,
                transaction,
                accountId,
                characterId,
                commandTimeoutSeconds: 30,
                CancellationToken.None),
            "Tier-IV reverse fixture captures an economy baseline");
        await PlayerOwnershipTestFences.InstallAsync(
            connection,
            transaction,
            accountId,
            characterId);
        await transaction.CommitAsync();

        return new TierIVReverseFixture(
            accountId,
            characterId,
            tierIVGear.ToCompactString());
    }

    private static async Task<TierIVReverseDurableState>
        ReadTierIVReverseStateAsync(
            string connectionString,
            TierIVReverseFixture fixture)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        var family = ClassSuitPersistenceCodec.FamilyCode(
            CommandFamily.ClassSuitConvertToCommon);
        var aggregateKey = ClassSuitPersistenceCodec.AggregateKey(
            fixture.CharacterId);
        await using var command = new NpgsqlCommand(
            """
            SELECT
                character_row.inventory_revision,
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
                   AND command_family = @commandFamily), 0),
                (SELECT count(*) FROM public.character_items
                 WHERE user_id = @characterId
                   AND item_location = 1
                   AND slot_index BETWEEN @firstRefundSlot
                       AND @lastRefundSlot
                   AND stack = @refundStackAfter),
                COALESCE((SELECT sum(stack) FROM public.character_items
                 WHERE user_id = @characterId
                   AND item_location = 1
                   AND prop_id = @insigniaI), 0),
                COALESCE((SELECT sum(stack) FROM public.character_items
                 WHERE user_id = @characterId
                   AND item_location = 1
                   AND prop_id = @insigniaII), 0),
                COALESCE((SELECT sum(stack) FROM public.character_items
                 WHERE user_id = @characterId
                   AND item_location = 1
                   AND prop_id = @insigniaIII), 0),
                COALESCE((SELECT sum(stack) FROM public.character_items
                 WHERE user_id = @characterId
                   AND item_location = 1
                   AND prop_id = @insigniaIV), 0)
            FROM public.character_base character_row
            WHERE character_row.id = @characterId;
            """,
            connection);
        command.Parameters.AddWithValue("accountId", fixture.AccountId);
        command.Parameters.AddWithValue(
            "characterId",
            fixture.CharacterId);
        command.Parameters.AddWithValue(
            "principalType",
            ClassSuitPersistenceCodec.PrincipalType);
        command.Parameters.AddWithValue(
            "principalKey",
            fixture.AccountId.ToString(CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue(
            "aggregateType",
            ClassSuitPersistenceCodec.AggregateType);
        command.Parameters.AddWithValue("aggregateKey", aggregateKey);
        command.Parameters.AddWithValue("commandFamily", family);
        command.Parameters.AddWithValue(
            "eventType",
            ClassSuitPersistenceCodec.EventType);
        command.Parameters.AddWithValue(
            "firstRefundSlot",
            ReverseRefundFirstSlot);
        command.Parameters.AddWithValue(
            "lastRefundSlot",
            checked((short)(ReverseRefundFirstSlot + 11)));
        command.Parameters.AddWithValue(
            "refundStackAfter",
            ReverseRefundStackAfter);
        command.Parameters.AddWithValue(
            "insigniaI",
            checked((int)TierIVReverseInsignias[0]));
        command.Parameters.AddWithValue(
            "insigniaII",
            checked((int)TierIVReverseInsignias[1]));
        command.Parameters.AddWithValue(
            "insigniaIII",
            checked((int)TierIVReverseInsignias[2]));
        command.Parameters.AddWithValue(
            "insigniaIV",
            checked((int)TierIVReverseInsignias[3]));
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new InvalidDataException(
                "The Tier-IV reverse fixture character disappeared.");
        }

        var state = new TierIVReverseDurableState(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            reader.GetInt64(3),
            reader.GetInt64(4),
            reader.GetInt32(5),
            reader.GetInt64(6),
            reader.GetInt64(7),
            reader.GetInt64(8),
            reader.GetInt64(9),
            reader.GetInt64(10),
            CompactItemEntry.Empty);
        await reader.CloseAsync();
        return state with
        {
            Gear = await ReadItemAsync(
                connection,
                fixture.CharacterId,
                GearSlot)
        };
    }

    private static void AssertTierIVReverseState(
        TierIVReverseDurableState state,
        int expectedDuplicateCount,
        string description)
    {
        var expectedGear = CreateCommonGear() with
        {
            Bound = 1,
            Attribute2 = null,
            AttributeLevel2 = null
        };
        Check.True(
            state.InventoryRevision == 1 &&
            state.AuditCount == 1 &&
            state.InboxCount == 1 &&
            state.LedgerCount ==
                ClassSuitPersistenceCodec.MaximumMutationCount &&
            state.OutboxCount == 1 &&
            state.DuplicateCount == expectedDuplicateCount &&
            state.FullRefundStackCount == 12 &&
            state.InsigniaITotal == 297 &&
            state.InsigniaIITotal == 297 &&
            state.InsigniaIIITotal == 297 &&
            state.InsigniaIVTotal == 297 &&
            state.Gear == expectedGear,
            description);
    }

    private sealed record TierIVReverseFixture(
        int AccountId,
        int CharacterId,
        string ExpectedGearState);

    private sealed record TierIVReverseDurableState(
        long InventoryRevision,
        long AuditCount,
        long InboxCount,
        long LedgerCount,
        long OutboxCount,
        int DuplicateCount,
        long FullRefundStackCount,
        long InsigniaITotal,
        long InsigniaIITotal,
        long InsigniaIIITotal,
        long InsigniaIVTotal,
        CompactItemEntry Gear);
}
