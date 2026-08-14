using Godswar.Server.Application.Pets;
using Godswar.Server.State;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.Infrastructure.Pets;

internal sealed partial class PostgresPetDurableCommandExecutor
{
    private async Task<PetTransition>
        ExecuteWithBagConsumableCooldownAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            int characterId,
            int kitBagSlot,
            LockedBagItem item,
            Func<CancellationToken, Task<PetTransition>> activation,
            CancellationToken cancellationToken)
    {
        if (!BagConsumableCooldownPolicy.TryResolve(
                _itemContent.Templates,
                checked((uint)item.PropId),
                out var rule))
        {
            return await activation(cancellationToken);
        }

        if (await LockAndCheckBagConsumableCooldownAsync(
                connection,
                transaction,
                characterId,
                rule.Group,
                cancellationToken))
        {
            return new(
                PetDurableReceiptStatus.ConsumableCooldownActive,
                KitBagSlot: kitBagSlot);
        }

        var transition = await activation(cancellationToken);
        if (transition.Succeeded)
        {
            await AdvanceBagConsumableCooldownAsync(
                connection,
                transaction,
                characterId,
                rule,
                cancellationToken);
        }

        return transition;
    }

    private async Task<bool> LockAndCheckBagConsumableCooldownAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        int cooldownGroup,
        CancellationToken cancellationToken)
    {
        await using (var seed = CreateCommand(
            """
            INSERT INTO public.character_bag_consumable_cooldowns (
                character_id,
                cooldown_group,
                ready_at,
                updated_at
            )
            VALUES (
                @characterId,
                @cooldownGroup,
                '-infinity'::timestamptz,
                '-infinity'::timestamptz
            )
            ON CONFLICT (character_id, cooldown_group) DO NOTHING;
            """,
            connection,
            transaction))
        {
            seed.Parameters.AddWithValue("characterId", characterId);
            seed.Parameters.AddWithValue("cooldownGroup", cooldownGroup);
            _ = await seed.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var command = CreateCommand(
            """
            SELECT ready_at > transaction_timestamp()
            FROM public.character_bag_consumable_cooldowns
            WHERE character_id = @characterId
              AND cooldown_group = @cooldownGroup
            FOR UPDATE;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("cooldownGroup", cooldownGroup);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is bool active
            ? active
            : throw new InvalidDataException(
                "The bag-consumable cooldown row was not lockable.");
    }

    private async Task AdvanceBagConsumableCooldownAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        BagConsumableCooldownRule rule,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            UPDATE public.character_bag_consumable_cooldowns
            SET updated_at = transaction_timestamp(),
                ready_at = GREATEST(
                    ready_at,
                    transaction_timestamp()
                ) + @duration
            WHERE character_id = @characterId
              AND cooldown_group = @cooldownGroup;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("cooldownGroup", rule.Group);
        command.Parameters.Add(
            new NpgsqlParameter<TimeSpan>(
                "duration",
                NpgsqlDbType.Interval)
            {
                TypedValue = rule.Duration
            });
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidDataException(
                "The bag-consumable cooldown deadline was not advanced.");
        }
    }
}
