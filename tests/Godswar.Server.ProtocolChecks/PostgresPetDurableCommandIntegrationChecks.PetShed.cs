using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Pets;
using Godswar.Server.Infrastructure.Pets;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class
    PostgresPetDurableCommandIntegrationChecks
{
    private const short PetShedItemSlot = 88;

    private static async Task AssertPetShedExpansionAsync(
        NpgsqlDataSource dataSource,
        PostgresPetDurableCommandExecutor executor,
        PostgresPetDurableCommandExecutor restarted,
        CommandSubject subject,
        CommandConnectionCorrelation correlation)
    {
        await SeedPetShedItemAsync(dataSource, subject.CharacterId);
        var operationId = Guid.NewGuid();
        var envelope = CreatePetShedEnvelope(
            subject,
            correlation,
            operationId);
        var concurrent = await Task.WhenAll(
            executor.ExecuteAsync(envelope),
            executor.ExecuteAsync(envelope));
        AssertCommitAndDuplicate(
            concurrent,
            PetDurableReceiptStatus.PetShedExpanded,
            "concurrent pet shed expansion");
        var committed = concurrent.Single(result =>
            result.Disposition ==
                PetDurableExecutionDisposition.Committed).Receipt!;
        var replayed = await restarted.ExecuteAsync(envelope);
        Check.True(
            replayed.Disposition ==
                PetDurableExecutionDisposition.Duplicate &&
            replayed.Receipt == committed,
            "pet shed expansion replays exactly after executor restart");

        var expanded = await ReadPetShedExpansionStateAsync(
            dataSource,
            subject.CharacterId);
        Check.True(
            expanded is
            {
                Capacity: 3,
                ShedRevision: 1,
                ShedItemCount: 0,
                InventoryRevision: 3,
                InventoryLedgerCount: 3,
                InventoryOutboxCount: 3,
                PetStreamRevision: 7
            },
            "pet shed item atomically advances capacity, shed/inventory streams, ledger, and outbox");

        await SetMaximumAndSeedPetShedItemAsync(
            dataSource,
            subject.CharacterId);
        // The first committed stock shed activation starts group 4720's
        // one-second cooldown. Expire only that fixture deadline so this
        // independent assertion reaches the native maximum-capacity rule.
        await ExpireConsumableCooldownAsync(
            dataSource,
            subject.CharacterId,
            4720);
        var maximumEnvelope = CreatePetShedEnvelope(
            subject,
            correlation,
            Guid.NewGuid());
        var rejected = await executor.ExecuteAsync(maximumEnvelope);
        var rejectedReplay = await restarted.ExecuteAsync(maximumEnvelope);
        Check.True(
            rejected.Disposition ==
                PetDurableExecutionDisposition.TerminalRejected &&
            rejected.Receipt?.Status ==
                PetDurableReceiptStatus.PetShedMaximumReached &&
            rejectedReplay.Disposition ==
                PetDurableExecutionDisposition.Duplicate &&
            rejectedReplay.Receipt == rejected.Receipt,
            "maximum-eight rejection is durable and retry-safe");

        var maximum = await ReadPetShedExpansionStateAsync(
            dataSource,
            subject.CharacterId);
        Check.True(
            maximum is
            {
                Capacity: 8,
                ShedRevision: 1,
                ShedItemCount: 1,
                InventoryRevision: 3,
                InventoryLedgerCount: 3,
                InventoryOutboxCount: 3,
                PetStreamRevision: 7
            },
            "maximum-eight rejection does not consume or advance value state");
    }

    private static CommandEnvelope<BagItemActivationCommand>
        CreatePetShedEnvelope(
            CommandSubject subject,
            CommandConnectionCorrelation correlation,
            Guid operationId) =>
        PlayerOwnershipTestFences.Bind(
            BagItemActivationCommandEnvelope.CreateRawLocal(
                subject,
                correlation,
                DateTimeOffset.UtcNow,
                new BagItemActivationCommand(
                    PetCommandOperationIdentity.RawLocalServer(
                        operationId,
                        correlation.ConnectionId),
                    PetShedItemSlot)));

    private static async Task SeedPetShedItemAsync(
        NpgsqlDataSource dataSource,
        int characterId)
    {
        await using var command = dataSource.CreateCommand(
            """
            INSERT INTO public.character_items (
                user_id, item_location, slot_index, prop_id,
                item_quality, item_grade, bound, stack,
                item_exp, holy_suit_code
            )
            VALUES (
                @characterId, 1, @slot, @propId,
                0, 1, 1, 1, 0, 0
            );
            """);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("slot", PetShedItemSlot);
        command.Parameters.AddWithValue(
            "propId",
            checked((int)PetItemCatalog.SpecialPetShed));
        Check.Equal(
            1,
            await command.ExecuteNonQueryAsync(),
            "pet shed fixture inserts one stock item");
    }

    private static async Task SetMaximumAndSeedPetShedItemAsync(
        NpgsqlDataSource dataSource,
        int characterId)
    {
        await using var connection =
            await dataSource.OpenConnectionAsync();
        await using var transaction =
            await connection.BeginTransactionAsync();
        await using (var maximum = new NpgsqlCommand(
            """
            UPDATE public.character_base
            SET pet_shed_capacity = @maximum
            WHERE id = @characterId;
            """,
            connection,
            transaction))
        {
            maximum.Parameters.AddWithValue("characterId", characterId);
            maximum.Parameters.AddWithValue(
                "maximum",
                PetShedCapacityPolicy.MaximumOpenedCellCount);
            Check.Equal(
                1,
                await maximum.ExecuteNonQueryAsync(),
                "pet shed fixture sets the native maximum");
        }
        await using (var insert = new NpgsqlCommand(
            """
            INSERT INTO public.character_items (
                user_id, item_location, slot_index, prop_id,
                item_quality, item_grade, bound, stack,
                item_exp, holy_suit_code
            )
            VALUES (
                @characterId, 1, @slot, @propId,
                0, 1, 1, 1, 0, 0
            );
            """,
            connection,
            transaction))
        {
            insert.Parameters.AddWithValue("characterId", characterId);
            insert.Parameters.AddWithValue("slot", PetShedItemSlot);
            insert.Parameters.AddWithValue(
                "propId",
                checked((int)PetItemCatalog.SpecialPetShed));
            Check.Equal(
                1,
                await insert.ExecuteNonQueryAsync(),
                "maximum pet shed fixture restores one stock item");
        }
        await transaction.CommitAsync();
    }

    private static async Task<PetShedExpansionState>
        ReadPetShedExpansionStateAsync(
            NpgsqlDataSource dataSource,
            int characterId)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT
                character_row.pet_shed_capacity,
                character_row.pet_shed_revision,
                (
                    SELECT count(*)
                    FROM public.character_items
                    WHERE user_id = character_row.id
                      AND item_location = 1
                      AND slot_index = @slot
                      AND prop_id = @propId
                ),
                character_row.inventory_revision,
                (
                    SELECT count(*)
                    FROM public.character_inventory_ledger
                    WHERE character_id = character_row.id
                ),
                (
                    SELECT count(*)
                    FROM public.outbox_events
                    WHERE consumer_key = 'inventory_projection_v1'
                      AND aggregate_type = 'character_inventory'
                      AND aggregate_key = concat(
                          'character:', character_row.id, ':inventory')
                      AND event_type =
                          'inventory.pet_bag_item_activated'
                ),
                stream.current_version
            FROM public.character_base character_row
            JOIN public.pet_durable_stream_versions stream
              ON stream.character_id = character_row.id
            WHERE character_row.id = @characterId;
            """);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("slot", PetShedItemSlot);
        command.Parameters.AddWithValue(
            "propId",
            checked((int)PetItemCatalog.SpecialPetShed));
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new InvalidDataException(
                "Pet shed expansion state disappeared.");
        }
        return new PetShedExpansionState(
            reader.GetInt16(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            reader.GetInt64(3),
            reader.GetInt64(4),
            reader.GetInt64(5),
            reader.GetInt64(6));
    }

    private sealed record PetShedExpansionState(
        short Capacity,
        long ShedRevision,
        long ShedItemCount,
        long InventoryRevision,
        long InventoryLedgerCount,
        long InventoryOutboxCount,
        long PetStreamRevision);
}
