using Npgsql;

namespace Godswar.Server.Infrastructure.Inventory;

internal sealed partial class PostgresHolyStoneCommandExecutor
{
    private async Task UpdateGoldWalletAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        HolyStoneCommandContext context,
        LockedCharacter character,
        int balanceAfter,
        long nextRevision,
        CancellationToken cancellationToken)
    {
        if (balanceAfter < 0 ||
            balanceAfter >= character.Gold ||
            nextRevision != character.WalletRevision + 1)
        {
            throw new InvalidDataException(
                "The Holy Stone Gold transition is invalid.");
        }

        await using var command = CreateCommand(
            """
            UPDATE public.character_base
            SET "Stone" = @balanceAfter,
                wallet_revision = @nextRevision
            WHERE account_id = @accountId
              AND id = @characterId
              AND "Stone" = @balanceBefore
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
            character.Gold);
        command.Parameters.AddWithValue(
            "expectedRevision",
            character.WalletRevision);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidDataException(
                "The Holy Stone Gold wallet did not advance exactly once.");
        }
    }

    private async Task InsertGoldLedgerAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long inboxId,
        HolyStoneCommandContext context,
        LockedCharacter character,
        int balanceAfter,
        long walletRevision,
        CancellationToken cancellationToken)
    {
        var delta = checked(balanceAfter - character.Gold);
        if (delta >= 0 ||
            walletRevision != character.WalletRevision + 1)
        {
            throw new InvalidDataException(
                "A Holy Stone Gold ledger requires one negative revision.");
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
                'gold',
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
            character.Gold);
        command.Parameters.AddWithValue(
            "balanceAfter",
            balanceAfter);
        command.Parameters.AddWithValue(
            "reasonCode",
            HolyStonePersistenceCodec.LedgerReasonCode(
                context.Command.Operation));
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidDataException(
                "The Holy Stone Gold ledger append was not exact.");
        }
    }
}
