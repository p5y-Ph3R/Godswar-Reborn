using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.Infrastructure.Warehouse;

internal sealed partial class PostgresWarehouseTransferCommandExecutor
{
    private async Task<short> FindPrivateTemporarySlotAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            SELECT candidate.slot_index::smallint
            FROM generate_series(-32768, -1) candidate(slot_index)
            WHERE NOT EXISTS (
                SELECT 1
                FROM public.character_items item
                WHERE item.user_id = @characterId
                  AND item.item_location = 2
                  AND item.slot_index = candidate.slot_index
            )
            ORDER BY candidate.slot_index
            LIMIT 1;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("characterId", characterId);
        return await command.ExecuteScalarAsync(cancellationToken) is short slot
            ? slot
            : throw new InvalidDataException(
                "No private warehouse swap slot is available.");
    }

    private async Task InsertCompatibilityAuditAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        LockedItem item,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            INSERT INTO public.character_item_audit (
                source, action, user_id, item_location, slot_index,
                prop_id, item_quality, item_grade, item_exp, old_item)
            VALUES (
                'warehouse-transfer', 'move', @characterId, @location,
                @slot, @propId, @quality, @grade, @itemExp, @oldItem);
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("location", item.Location);
        command.Parameters.AddWithValue("slot", item.Slot);
        command.Parameters.AddWithValue("propId", checked((int)item.Item.Id));
        command.Parameters.AddWithValue("quality", item.Item.Quality);
        command.Parameters.AddWithValue("grade", item.Item.Grade);
        command.Parameters.AddWithValue("itemExp", item.Item.Exp);
        command.Parameters.Add("oldItem", NpgsqlDbType.Jsonb).Value =
            item.BeforeState;
        await RequireOneAsync(
            command,
            "The warehouse compatibility audit was not exact.",
            cancellationToken);
    }
}
