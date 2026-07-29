using Godswar.Server.Infrastructure.Inventory;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class
    PostgresMakeAttributeStoneCommandIntegrationChecks
{
    private static async Task<StoneMutationShape>
        ReadMutationShapeAsync(
            string connectionString,
            StoneFixture fixture)
    {
        await using var connection =
            new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT
                count(*) FILTER (
                    WHERE ledger.mutation_kind = 'add')::bigint,
                count(*) FILTER (
                    WHERE ledger.mutation_kind = 'update')::bigint,
                count(*) FILTER (
                    WHERE ledger.mutation_kind = 'delete')::bigint,
                count(*) FILTER (
                    WHERE
                        (ledger.before_state ->> 'prop_id')::integer =
                            @dustItemId
                        AND
                        (ledger.after_state ->> 'prop_id')::integer =
                            @dustItemId)::bigint,
                count(*) FILTER (
                    WHERE
                        (ledger.before_state ->> 'prop_id')::integer =
                            @stoneItemId
                        AND
                        (ledger.after_state ->> 'prop_id')::integer =
                            @stoneItemId)::bigint
            FROM public.character_inventory_ledger ledger
            WHERE ledger.account_id = @accountId
              AND ledger.character_id = @characterId
              AND ledger.reason_code = @ledgerReason;
            """,
            connection);
        command.Parameters.AddWithValue("accountId", fixture.AccountId);
        command.Parameters.AddWithValue(
            "characterId",
            fixture.CharacterId);
        command.Parameters.AddWithValue(
            "dustItemId",
            checked((int)DustItemId));
        command.Parameters.AddWithValue(
            "stoneItemId",
            checked((int)AttributeStoneItemId));
        command.Parameters.AddWithValue(
            "ledgerReason",
            MakeAttributeStonePersistenceCodec.LedgerReasonCode);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new InvalidDataException(
                "The Make Attribute Stone mutation state disappeared.");
        }

        return new StoneMutationShape(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            reader.GetInt64(3),
            reader.GetInt64(4));
    }

    private sealed record StoneMutationShape(
        long AddCount,
        long UpdateCount,
        long DeleteCount,
        long DustRemainderUpdateCount,
        long StoneStackUpdateCount);
}
