using Godswar.Server.Application.Reconciliation;
using NpgsqlTypes;

namespace Godswar.Server.Infrastructure.Reconciliation;

internal sealed partial class PostgresReconciliationSnapshot
{
    internal static string LedgerChainSqlForChecks => LedgerChainSql;

    private const string LedgerChainSql =
        """
        WITH keys(character_id) AS MATERIALIZED (
            SELECT unnest(@character_keys::bigint[])
        ),
        wallet_rows AS (
            SELECT
                ledger.character_id::bigint AS character_id,
                ledger.currency_code,
                ledger.balance_before,
                row_number() OVER (
                    PARTITION BY
                        ledger.character_id,
                        ledger.currency_code
                    ORDER BY ledger.wallet_revision
                ) AS chain_ordinal,
                lag(ledger.balance_after) OVER (
                    PARTITION BY
                        ledger.character_id,
                        ledger.currency_code
                    ORDER BY ledger.wallet_revision
                ) AS previous_balance
            FROM public.character_currency_ledger ledger
            INNER JOIN keys
                ON keys.character_id = ledger.character_id
        ),
        wallet_findings AS (
            SELECT row.character_id
            FROM wallet_rows row
            INNER JOIN public.character_economy_baseline baseline
                ON baseline.character_id = row.character_id
            GROUP BY row.character_id
            HAVING bool_or(
                row.balance_before IS DISTINCT FROM
                CASE
                    WHEN row.chain_ordinal > 1
                        THEN row.previous_balance
                    WHEN row.currency_code = 'silver'
                        THEN baseline.silver
                    ELSE baseline.gold
                END
            )
        ),
        inventory_rows AS (
            SELECT
                ledger.character_id::bigint AS character_id,
                ledger.item_instance_id,
                ledger.before_state,
                row_number() OVER (
                    PARTITION BY
                        ledger.character_id,
                        ledger.item_instance_id
                    ORDER BY ledger.inventory_revision
                ) AS chain_ordinal,
                lag(ledger.after_state) OVER (
                    PARTITION BY
                        ledger.character_id,
                        ledger.item_instance_id
                    ORDER BY ledger.inventory_revision
                ) AS previous_state
            FROM public.character_inventory_ledger ledger
            INNER JOIN keys
                ON keys.character_id = ledger.character_id
        ),
        inventory_findings AS (
            SELECT row.character_id
            FROM inventory_rows row
            INNER JOIN public.character_economy_baseline baseline
                ON baseline.character_id = row.character_id
            LEFT JOIN public.character_inventory_baseline_items
                baseline_item
                ON baseline_item.character_id = row.character_id
               AND baseline_item.item_instance_id =
                    row.item_instance_id
            GROUP BY row.character_id
            HAVING bool_or(
                public.canonical_character_item_state_v2(
                    row.before_state
                ) IS DISTINCT FROM
                public.canonical_character_item_state_v2(
                    CASE
                        WHEN row.chain_ordinal > 1
                            THEN row.previous_state
                        ELSE baseline_item.item_state
                    END
                )
            )
        )
        SELECT
            (SELECT count(*)::bigint FROM wallet_findings),
            (SELECT count(*)::bigint FROM inventory_findings);
        """;

    private async Task<IReadOnlyList<ReconciliationCategoryCount>>
        ReadLedgerChainFindingsAsync(
            IReadOnlyCollection<long> characterKeys,
            CancellationToken cancellationToken)
    {
        if (characterKeys.Count == 0)
        {
            return Array.Empty<ReconciliationCategoryCount>();
        }

        if (characterKeys.Count > 500 ||
            characterKeys.Any(key => key <= 0))
        {
            throw new InvalidDataException(
                "Ledger reconciliation keys must be 1..500 positive IDs.");
        }

        var findings = new List<ReconciliationCategoryCount>(2);
        await using var command = CreateCommand(LedgerChainSql);
        command.Parameters.Add(
            "character_keys",
            NpgsqlDbType.Array | NpgsqlDbType.Bigint).Value =
            characterKeys.ToArray();
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            var walletCount = reader.GetInt64(0);
            if (walletCount > 0)
            {
                findings.Add(new ReconciliationCategoryCount(
                    ReconciliationCategory.WalletLedgerChainMismatch,
                    walletCount));
            }

            var inventoryCount = reader.GetInt64(1);
            if (inventoryCount > 0)
            {
                findings.Add(new ReconciliationCategoryCount(
                    ReconciliationCategory.InventoryLedgerChainMismatch,
                    inventoryCount));
            }
        }

        return findings;
    }
}
