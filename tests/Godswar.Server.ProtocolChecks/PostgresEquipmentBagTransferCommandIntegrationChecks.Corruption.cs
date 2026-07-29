using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class
    PostgresEquipmentBagTransferCommandIntegrationChecks
{
    private static async Task AssertPhysicalEmptyRowFailsClosedAsync(
        string connectionString)
    {
        var fixture = await CreateFixtureAsync(
            connectionString,
            "emptyrow");
        long corruptRowId;
        await using (var connection =
                     new NpgsqlConnection(connectionString))
        {
            await connection.OpenAsync();
            await using var transaction =
                await connection.BeginTransactionAsync();
            await using (var template = new NpgsqlCommand(
                             """
                             INSERT INTO public.item_templates (
                                 id,
                                 kind,
                                 name_key,
                                 display_name,
                                 equipment_slot,
                                 class_ids,
                                 stats
                             )
                             VALUES (
                                 0,
                                 'weapon',
                                 'CorruptEmptyItem',
                                 'Corrupt Empty Item',
                                 10,
                                 '{}'::smallint[],
                                 '{}'::jsonb
                             )
                             ON CONFLICT (id) DO NOTHING;
                             """,
                             connection,
                             transaction))
            {
                await template.ExecuteNonQueryAsync();
            }
            corruptRowId = await InsertItemAsync(
                connection,
                transaction,
                fixture.CharacterId,
                location: 1,
                fixture.KitBagSlot,
                Item(0, quality: 0, grade: 0));
            await transaction.CommitAsync();
        }

        await using var dataSource =
            NpgsqlDataSource.Create(connectionString);
        try
        {
            _ = await ExecuteAsync(
                CreateExecutor(dataSource),
                fixture,
                Guid.NewGuid());
        }
        catch (InvalidDataException exception) when (
            exception.Message.Contains(
                "decoded as an empty item",
                StringComparison.Ordinal))
        {
            var state = await ReadStateAsync(
                connectionString,
                fixture);
            Check.Equal(
                corruptRowId,
                state.KitBagItemId,
                "corrupt physical row remains available for repair");
            Check.Equal(
                0L,
                state.InventoryRevision,
                "corrupt row does not advance revision");
            Check.Equal(0L, state.AuditCount, "corrupt row no audit");
            Check.Equal(0L, state.InboxCount, "corrupt row no inbox");
            Check.Equal(0L, state.LedgerCount, "corrupt row no ledger");
            Check.Equal(0L, state.OutboxCount, "corrupt row no outbox");
            return;
        }

        throw new InvalidOperationException(
            "Assertion failed: a physical empty item row must fail " +
            "closed before durable evidence.");
    }
}
