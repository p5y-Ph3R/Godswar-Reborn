using System.Data.Common;

namespace Godswar.Server.State;

internal sealed partial class PostgresGameStore
{
    private static async Task SeedCharacterCreationEconomyBaselineAsync(
        DbConnection connection,
        DbTransaction transaction,
        int characterId,
        int accountId,
        CancellationToken cancellationToken)
    {
        int itemCount;
        await using (var baseline = CreateEconomyBaselineCommand(
            connection,
            transaction,
            """
            INSERT INTO public.character_economy_baseline (
                character_id,
                account_id,
                wallet_revision,
                inventory_revision,
                silver,
                gold,
                item_count,
                baseline_source
            )
            SELECT
                character_row.id,
                character_row.account_id,
                character_row.wallet_revision,
                character_row.inventory_revision,
                character_row."Money"::bigint,
                character_row."Stone"::bigint,
                (
                    SELECT count(*)::integer
                    FROM public.character_items item_row
                    WHERE item_row.user_id = character_row.id
                ),
                'character_creation'
            FROM public.character_base character_row
            WHERE character_row.id = @characterId
              AND character_row.account_id = @accountId
              AND character_row.wallet_revision = 0
              AND character_row.inventory_revision = 0
            RETURNING item_count;
            """))
        {
            AddEconomyBaselineParameter(
                baseline,
                "characterId",
                characterId);
            AddEconomyBaselineParameter(
                baseline,
                "accountId",
                accountId);
            itemCount = Convert.ToInt32(
                await baseline.ExecuteScalarAsync(cancellationToken) ??
                throw new InvalidOperationException(
                    "Character creation could not establish its economy baseline."));
        }

        await using var inventory = CreateEconomyBaselineCommand(
            connection,
            transaction,
            """
            INSERT INTO public.character_inventory_baseline_items (
                character_id,
                account_id,
                item_instance_id,
                item_location,
                slot_index,
                prop_id,
                state_contract_version,
                item_state
            )
            SELECT
                item_row.user_id,
                @accountId,
                item_row.id,
                item_row.item_location,
                item_row.slot_index,
                item_row.prop_id,
                1,
                to_jsonb(item_row)
            FROM public.character_items item_row
            WHERE item_row.user_id = @characterId
            ORDER BY item_row.id;
            """);
        AddEconomyBaselineParameter(
            inventory,
            "characterId",
            characterId);
        AddEconomyBaselineParameter(
            inventory,
            "accountId",
            accountId);

        var capturedItemCount =
            await inventory.ExecuteNonQueryAsync(cancellationToken);
        if (capturedItemCount != itemCount)
        {
            throw new InvalidOperationException(
                "Character creation inventory baseline was not exact.");
        }
    }

    private static DbCommand CreateEconomyBaselineCommand(
        DbConnection connection,
        DbTransaction transaction,
        string sql)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command;
    }

    private static void AddEconomyBaselineParameter(
        DbCommand command,
        string name,
        object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
