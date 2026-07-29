using Godswar.Server.Application.Inventory;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.Infrastructure.Inventory;

internal sealed partial class PostgresEquipmentForgeCommandExecutor
{
    private async Task UpdateWalletAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        EquipmentForgeCommandContext context,
        LockedCharacter character,
        int balanceAfter,
        long nextRevision,
        CancellationToken cancellationToken)
    {
        if (balanceAfter >= character.Silver ||
            nextRevision != character.WalletRevision + 1)
        {
            throw new InvalidDataException(
                "The equipment-forge wallet transition is invalid.");
        }

        await using var command = CreateCommand(
            """
            UPDATE public.character_base
            SET "Money" = @balanceAfter,
                wallet_revision = @nextRevision
            WHERE account_id = @accountId
              AND id = @characterId
              AND "Money" = @balanceBefore
              AND wallet_revision = @expectedRevision;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("balanceAfter", balanceAfter);
        command.Parameters.AddWithValue("nextRevision", nextRevision);
        command.Parameters.AddWithValue(
            "accountId",
            context.Subject.AccountId);
        command.Parameters.AddWithValue(
            "characterId",
            context.Subject.CharacterId);
        command.Parameters.AddWithValue(
            "balanceBefore",
            character.Silver);
        command.Parameters.AddWithValue(
            "expectedRevision",
            character.WalletRevision);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidDataException(
                "The equipment-forge wallet did not advance exactly once.");
        }
    }

    private async Task AdvanceInventoryRevisionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        EquipmentForgeCommandContext context,
        long expectedRevision,
        long nextRevision,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            UPDATE public.character_base
            SET inventory_revision = @nextRevision
            WHERE account_id = @accountId
              AND id = @characterId
              AND inventory_revision = @expectedRevision;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("nextRevision", nextRevision);
        command.Parameters.AddWithValue(
            "expectedRevision",
            expectedRevision);
        command.Parameters.AddWithValue(
            "accountId",
            context.Subject.AccountId);
        command.Parameters.AddWithValue(
            "characterId",
            context.Subject.CharacterId);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidDataException(
                "The equipment-forge inventory revision did not advance exactly once.");
        }
    }

    private async Task InsertCurrencyLedgerAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long inboxId,
        EquipmentForgeCommandContext context,
        LockedCharacter character,
        int balanceAfter,
        long walletRevision,
        CancellationToken cancellationToken)
    {
        var delta = checked(balanceAfter - character.Silver);
        if (delta >= 0)
        {
            throw new InvalidDataException(
                "A silver forge ledger requires a negative delta.");
        }

        await using var command = CreateCommand(
            """
            INSERT INTO public.character_currency_ledger (
                command_inbox_id,
                account_id,
                character_id,
                wallet_revision,
                currency_code,
                delta,
                balance_before,
                balance_after,
                reason_code
            )
            VALUES (
                @inboxId,
                @accountId,
                @characterId,
                @walletRevision,
                'silver',
                @delta,
                @balanceBefore,
                @balanceAfter,
                @reasonCode
            );
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("inboxId", inboxId);
        command.Parameters.AddWithValue(
            "accountId",
            context.Subject.AccountId);
        command.Parameters.AddWithValue(
            "characterId",
            context.Subject.CharacterId);
        command.Parameters.AddWithValue(
            "walletRevision",
            walletRevision);
        command.Parameters.AddWithValue("delta", delta);
        command.Parameters.AddWithValue(
            "balanceBefore",
            character.Silver);
        command.Parameters.AddWithValue(
            "balanceAfter",
            balanceAfter);
        command.Parameters.AddWithValue(
            "reasonCode",
            EquipmentForgePersistenceCodec.LedgerReasonCode);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidDataException(
                "The equipment-forge currency ledger append was not exact.");
        }
    }

    private async Task InsertInventoryLedgerAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long inboxId,
        EquipmentForgeCommandContext context,
        long inventoryRevision,
        IReadOnlyList<InventoryMutation> mutations,
        CancellationToken cancellationToken)
    {
        if (mutations.Count is < 1 or >
            EquipmentForgeCommandEnvelope.MaximumOddsQuantity + 2)
        {
            throw new InvalidDataException(
                "Equipment-forge ledger evidence has an invalid count.");
        }

        var materialOffset =
            mutations[0].Role ==
                EquipmentForgeCommandItemRole.Equipment
                ? 1
                : 0;
        if (materialOffset >= mutations.Count ||
            mutations[materialOffset].Role !=
                EquipmentForgeCommandItemRole.PrimaryMaterial ||
            mutations.Skip(materialOffset + 1).Any(
                static mutation =>
                    mutation.Role !=
                        EquipmentForgeCommandItemRole.OddsMaterial))
        {
            throw new InvalidDataException(
                "Equipment-forge ledger evidence is not in role order.");
        }

        await using var command = CreateCommand(
            """
            INSERT INTO public.character_inventory_ledger (
                command_inbox_id,
                account_id,
                character_id,
                inventory_revision,
                entry_ordinal,
                item_instance_id,
                mutation_kind,
                state_contract_version,
                before_state,
                after_state,
                reason_code
            )
            VALUES (
                @inboxId,
                @accountId,
                @characterId,
                @inventoryRevision,
                @entryOrdinal,
                @itemInstanceId,
                @mutationKind,
                1,
                @beforeState,
                @afterState,
                @reasonCode
            );
            """,
            connection,
            transaction);
        for (var index = 0; index < mutations.Count; index++)
        {
            var mutation = mutations[index];
            command.Parameters.Clear();
            command.Parameters.AddWithValue("inboxId", inboxId);
            command.Parameters.AddWithValue(
                "accountId",
                context.Subject.AccountId);
            command.Parameters.AddWithValue(
                "characterId",
                context.Subject.CharacterId);
            command.Parameters.AddWithValue(
                "inventoryRevision",
                inventoryRevision);
            command.Parameters.AddWithValue(
                "entryOrdinal",
                checked((short)index));
            command.Parameters.AddWithValue(
                "itemInstanceId",
                mutation.ItemInstanceId);
            command.Parameters.AddWithValue(
                "mutationKind",
                mutation.MutationKind);
            AddJsonParameter(
                command,
                "beforeState",
                mutation.BeforeState);
            AddJsonParameter(
                command,
                "afterState",
                mutation.AfterState);
            command.Parameters.AddWithValue(
                "reasonCode",
                EquipmentForgePersistenceCodec.LedgerReasonCode);
            if (await command.ExecuteNonQueryAsync(
                    cancellationToken) != 1)
            {
                throw new InvalidDataException(
                    "The equipment-forge inventory ledger append was not exact.");
            }
        }
    }

    private static void AddJsonParameter(
        NpgsqlCommand command,
        string name,
        string? value)
    {
        command.Parameters.Add(
            name,
            NpgsqlDbType.Jsonb).Value =
            value is null ? DBNull.Value : value;
    }
}
