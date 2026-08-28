using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Godswar.Server.Domain.World.Content;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.State;

internal sealed partial class PostgresGameStore
{
    private static async Task<long> InsertCapitalShopEvidenceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int accountId,
        int characterId,
        Guid purchaseId,
        CapitalShopOffer offer,
        int quantity,
        int totalCost,
        CapitalShopLockedCharacter before,
        int balanceAfter,
        long walletRevision,
        long inventoryRevision,
        IReadOnlyList<CapitalShopMutation> mutations,
        CancellationToken cancellationToken)
    {
        var requestPayload = JsonSerializer.Serialize(new
        {
            purchaseId,
            itemId = offer.Item.Id,
            quantity,
            offer.UnitPrice,
            currency = offer.Currency.ToString(),
            totalCost
        });
        var goldAfter = offer.Currency == CapitalNpcShopCurrency.Gold
            ? balanceAfter
            : before.Gold;
        var bindingGoldAfter =
            offer.Currency == CapitalNpcShopCurrency.BindingGold
                ? balanceAfter
                : before.BindingGold;
        var resultPayload = JsonSerializer.Serialize(new
        {
            purchaseId,
            characterId,
            itemId = offer.Item.Id,
            quantity,
            totalCost,
            currency = offer.Currency.ToString(),
            goldBefore = before.Gold,
            goldAfter,
            bindingGoldBefore = before.BindingGold,
            bindingGoldAfter,
            walletRevision,
            inventoryRevision,
            items = mutations.Select(static mutation => new
            {
                mutation.ItemInstanceId,
                mutation.Slot
            })
        });
        var requestHash = SHA256.HashData(
            Encoding.UTF8.GetBytes(requestPayload));
        var resultHash = SHA256.HashData(
            Encoding.UTF8.GetBytes(resultPayload));
        var operationId = purchaseId.ToByteArray();
        var principalKey = accountId.ToString(CultureInfo.InvariantCulture);
        var aggregateKey = $"character:{characterId}";

        long auditId;
        await using (var command = new NpgsqlCommand(
            """
            INSERT INTO public.command_audit (
                principal_type, principal_key, aggregate_type,
                aggregate_key, command_family, operation_id,
                request_hash, outcome_code, detail_payload)
            VALUES ('account', @principalKey, 'character', @aggregateKey,
                    'capital_shop_purchase', @operationId,
                    @requestHash, 'committed', @requestPayload)
            RETURNING id;
            """,
            connection,
            transaction))
        {
            command.Parameters.AddWithValue("principalKey", principalKey);
            command.Parameters.AddWithValue("aggregateKey", aggregateKey);
            command.Parameters.Add("operationId", NpgsqlDbType.Bytea).Value =
                operationId;
            command.Parameters.Add("requestHash", NpgsqlDbType.Bytea).Value =
                requestHash;
            command.Parameters.Add("requestPayload", NpgsqlDbType.Jsonb).Value =
                requestPayload;
            auditId = await command.ExecuteScalarAsync(cancellationToken)
                is long value && value > 0
                ? value
                : throw new InvalidDataException(
                    "Shop purchase audit returned no identity.");
        }

        await using var inbox = new NpgsqlCommand(
            """
            INSERT INTO public.command_inbox (
                principal_type, principal_key, aggregate_type,
                aggregate_key, command_family, operation_id,
                request_hash, result_contract_version, result_code,
                result_payload, result_hash, audit_id)
            VALUES ('account', @principalKey, 'character', @aggregateKey,
                    'capital_shop_purchase', @operationId,
                    @requestHash, 1, 'committed', @resultPayload,
                    @resultHash, @auditId)
            RETURNING id;
            """,
            connection,
            transaction);
        inbox.Parameters.AddWithValue("principalKey", principalKey);
        inbox.Parameters.AddWithValue("aggregateKey", aggregateKey);
        inbox.Parameters.Add("operationId", NpgsqlDbType.Bytea).Value =
            operationId;
        inbox.Parameters.Add("requestHash", NpgsqlDbType.Bytea).Value =
            requestHash;
        inbox.Parameters.Add("resultPayload", NpgsqlDbType.Jsonb).Value =
            resultPayload;
        inbox.Parameters.Add("resultHash", NpgsqlDbType.Bytea).Value =
            resultHash;
        inbox.Parameters.AddWithValue("auditId", auditId);
        return await inbox.ExecuteScalarAsync(cancellationToken)
            is long inboxId && inboxId > 0
            ? inboxId
            : throw new InvalidDataException(
                "Shop purchase inbox returned no identity.");
    }

    private static async Task UpdateCapitalShopCharacterAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int accountId,
        int characterId,
        CapitalShopLockedCharacter before,
        CapitalNpcShopCurrency currency,
        int balanceAfter,
        long walletRevision,
        long inventoryRevision,
        CancellationToken cancellationToken)
    {
        var goldAfter = currency == CapitalNpcShopCurrency.Gold
            ? balanceAfter
            : before.Gold;
        var bindingGoldAfter =
            currency == CapitalNpcShopCurrency.BindingGold
                ? balanceAfter
                : before.BindingGold;
        await using var command = new NpgsqlCommand(
            """
            UPDATE public.character_base
            SET "Stone" = @goldAfter,
                "BindingGold" = @bindingGoldAfter,
                wallet_revision = @walletRevision,
                inventory_revision = @inventoryRevision
            WHERE id = @characterId AND account_id = @accountId
              AND lifecycle_state = 'active'
              AND "Stone" = @goldBefore
              AND "BindingGold" = @bindingGoldBefore
              AND wallet_revision = @walletRevisionBefore
              AND inventory_revision = @inventoryRevisionBefore;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("goldAfter", goldAfter);
        command.Parameters.AddWithValue(
            "bindingGoldAfter",
            bindingGoldAfter);
        command.Parameters.AddWithValue("walletRevision", walletRevision);
        command.Parameters.AddWithValue(
            "inventoryRevision",
            inventoryRevision);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("accountId", accountId);
        command.Parameters.AddWithValue(
            "goldBefore",
            before.Gold);
        command.Parameters.AddWithValue(
            "bindingGoldBefore",
            before.BindingGold);
        command.Parameters.AddWithValue(
            "walletRevisionBefore",
            before.WalletRevision);
        command.Parameters.AddWithValue(
            "inventoryRevisionBefore",
            before.InventoryRevision);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidDataException(
                "Shop purchase character state was not advanced once.");
        }
    }

    private static async Task InsertCapitalShopLedgersAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long inboxId,
        int accountId,
        int characterId,
        CapitalNpcShopCurrency currencyCode,
        int totalCost,
        int balanceBefore,
        int balanceAfter,
        long walletRevision,
        long inventoryRevision,
        IReadOnlyList<CapitalShopMutation> mutations,
        CancellationToken cancellationToken)
    {
        await using (var currency = new NpgsqlCommand(
            """
            INSERT INTO public.character_currency_ledger (
                command_inbox_id, account_id, character_id,
                wallet_revision, currency_code, delta,
                balance_before, balance_after, reason_code)
            VALUES (@inboxId, @accountId, @characterId,
                    @walletRevision, @currencyCode, @delta,
                    @balanceBefore, @balanceAfter,
                    'capital_shop_purchase');
            """,
            connection,
            transaction))
        {
            currency.Parameters.AddWithValue("inboxId", inboxId);
            currency.Parameters.AddWithValue("accountId", accountId);
            currency.Parameters.AddWithValue("characterId", characterId);
            currency.Parameters.AddWithValue(
                "walletRevision",
                walletRevision);
            currency.Parameters.AddWithValue(
                "currencyCode",
                currencyCode == CapitalNpcShopCurrency.Gold
                    ? "gold"
                    : "binding_gold");
            currency.Parameters.AddWithValue("delta", -(long)totalCost);
            currency.Parameters.AddWithValue(
                "balanceBefore",
                (long)balanceBefore);
            currency.Parameters.AddWithValue(
                "balanceAfter",
                (long)balanceAfter);
            if (await currency.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new InvalidDataException(
                    "Shop purchase currency ledger was not inserted once.");
            }
        }

        for (var index = 0; index < mutations.Count; index++)
        {
            var mutation = mutations[index];
            await using var inventory = new NpgsqlCommand(
                """
                INSERT INTO public.character_inventory_ledger (
                    command_inbox_id, account_id, character_id,
                    inventory_revision, entry_ordinal,
                    item_instance_id, mutation_kind,
                    before_state, after_state, reason_code)
                VALUES (@inboxId, @accountId, @characterId,
                        @inventoryRevision, @ordinal,
                        @itemInstanceId, 'add', NULL,
                        @afterState, 'capital_shop_purchase');
                """,
                connection,
                transaction);
            inventory.Parameters.AddWithValue("inboxId", inboxId);
            inventory.Parameters.AddWithValue("accountId", accountId);
            inventory.Parameters.AddWithValue("characterId", characterId);
            inventory.Parameters.AddWithValue(
                "inventoryRevision",
                inventoryRevision);
            inventory.Parameters.AddWithValue(
                "ordinal",
                checked((short)index));
            inventory.Parameters.AddWithValue(
                "itemInstanceId",
                mutation.ItemInstanceId);
            inventory.Parameters.Add("afterState", NpgsqlDbType.Jsonb).Value =
                mutation.AfterState;
            if (await inventory.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new InvalidDataException(
                    "Shop purchase inventory ledger was not inserted once.");
            }
        }
    }
}
