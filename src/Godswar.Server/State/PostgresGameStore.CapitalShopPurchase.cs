using System.Data;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Infrastructure.Inventory;
using Npgsql;

namespace Godswar.Server.State;

internal sealed partial class PostgresGameStore
{
    public async Task<CapitalShopPurchaseResult>
        PurchaseCapitalShopItemAsync(
        int accountId,
        int characterId,
        Guid purchaseId,
        CapitalShopOffer offer,
        int quantity,
        CancellationToken cancellationToken = default)
    {
        if (accountId <= 0 || characterId <= 0 ||
            purchaseId == Guid.Empty || quantity is < 1 or > byte.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(quantity));
        }
        if (!offer.IsValid)
        {
            return new(
                CapitalShopPurchaseStatus.UnsupportedItem,
                Character: null,
                CurrencyBalance: 0);
        }

        var totalCost = checked((long)offer.UnitPrice * quantity);
        await using var connection =
            await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);

        var policy = await ReadLootItemPolicyAsync(
            connection,
            transaction,
            offer.Item.Id,
            cancellationToken);
        if (!policy.HasValue)
        {
            await transaction.CommitAsync(cancellationToken);
            return new(
                CapitalShopPurchaseStatus.UnsupportedItem,
                Character: null,
                CurrencyBalance: 0);
        }

        var character = await LockCapitalShopCharacterAsync(
            connection,
            transaction,
            accountId,
            characterId,
            cancellationToken);
        if (!character.HasValue)
        {
            await transaction.CommitAsync(cancellationToken);
            return new(
                CapitalShopPurchaseStatus.CharacterNotFound,
                Character: null,
                CurrencyBalance: 0);
        }
        if (!await PostgresCharacterEconomyBaseline.EnsureAsync(
                connection,
                transaction,
                accountId,
                characterId,
                commandTimeoutSeconds: 30,
                cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(
                CapitalShopPurchaseStatus.CharacterNotFound,
                Character: null,
                CurrencyBalance: 0);
        }
        var currencyBalance = character.Value.GetBalance(offer.Currency);
        if (totalCost > currencyBalance)
        {
            await transaction.CommitAsync(cancellationToken);
            return new(
                CapitalShopPurchaseStatus.InsufficientCurrency,
                Character: null,
                currencyBalance);
        }

        var occupied = await LockCapitalShopBagAsync(
            connection,
            transaction,
            characterId,
            cancellationToken);
        var inserts = PlanCapitalShopInserts(
            occupied,
            quantity,
            Math.Clamp(policy.Value.StackCap, (short)1, (short)byte.MaxValue));
        if (inserts is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return new(
                CapitalShopPurchaseStatus.InsufficientCapacity,
                Character: null,
                currencyBalance);
        }

        var walletRevision = checked(character.Value.WalletRevision + 1);
        var inventoryRevision = checked(
            character.Value.InventoryRevision + 1);
        var balanceAfter = checked(
            currencyBalance - checked((int)totalCost));
        var mutations = await InsertCapitalShopItemsAsync(
            connection,
            transaction,
            characterId,
            offer.Item,
            inserts,
            cancellationToken);
        var inboxId = await InsertCapitalShopEvidenceAsync(
            connection,
            transaction,
            accountId,
            characterId,
            purchaseId,
            offer,
            quantity,
            checked((int)totalCost),
            character.Value,
            balanceAfter,
            walletRevision,
            inventoryRevision,
            mutations,
            cancellationToken);
        await UpdateCapitalShopCharacterAsync(
            connection,
            transaction,
            accountId,
            characterId,
            character.Value,
            offer.Currency,
            balanceAfter,
            walletRevision,
            inventoryRevision,
            cancellationToken);
        await InsertCapitalShopLedgersAsync(
            connection,
            transaction,
            inboxId,
            accountId,
            characterId,
            offer.Currency,
            checked((int)totalCost),
            currencyBalance,
            balanceAfter,
            walletRevision,
            inventoryRevision,
            mutations,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var refreshed = await GetCharacterByIdAsync(
            characterId,
            cancellationToken) ?? throw new InvalidDataException(
                "Purchased shop state could not be reloaded.");
        var refreshedBalance = offer.Currency == CapitalNpcShopCurrency.Gold
            ? refreshed.Gold
            : refreshed.BindingGold;
        if (refreshedBalance != balanceAfter)
        {
            throw new InvalidDataException(
                "Purchased shop wallet projection is stale.");
        }
        return new(
            CapitalShopPurchaseStatus.Purchased,
            refreshed,
            balanceAfter);
    }

    private static async Task<CapitalShopLockedCharacter?>
        LockCapitalShopCharacterAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int accountId,
        int characterId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT "Stone", "BindingGold",
                   wallet_revision, inventory_revision
            FROM public.character_base
            WHERE id = @characterId AND account_id = @accountId
              AND lifecycle_state = 'active'
            FOR UPDATE;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("accountId", accountId);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new CapitalShopLockedCharacter(
                reader.GetInt32(0),
                reader.GetInt32(1),
                reader.GetInt64(2),
                reader.GetInt64(3))
            : null;
    }

    private static async Task<bool[]> LockCapitalShopBagAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        CancellationToken cancellationToken)
    {
        var occupied = new bool[KitBagProjectionSlots];
        await using var command = new NpgsqlCommand(
            """
            SELECT slot_index
            FROM public.character_items
            WHERE user_id = @characterId AND item_location = 1
              AND slot_index BETWEEN 0 AND 95
            ORDER BY slot_index, id
            FOR UPDATE;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("characterId", characterId);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var slot = reader.GetInt16(0);
            if (occupied[slot])
            {
                throw new InvalidDataException(
                    "The locked kit bag contains duplicate slots.");
            }
            occupied[slot] = true;
        }
        return occupied;
    }

    private static IReadOnlyList<CapitalShopInsert>?
        PlanCapitalShopInserts(
        bool[] occupied,
        int quantity,
        short stackCap)
    {
        var remaining = quantity;
        var inserts = new List<CapitalShopInsert>();
        for (short slot = 0;
             slot < occupied.Length && remaining > 0;
             slot++)
        {
            if (occupied[slot])
            {
                continue;
            }
            var stack = checked((short)Math.Min(remaining, stackCap));
            inserts.Add(new CapitalShopInsert(slot, stack));
            remaining -= stack;
        }
        return remaining == 0 ? inserts : null;
    }

    private static async Task<IReadOnlyList<CapitalShopMutation>>
        InsertCapitalShopItemsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        CompactItemEntry offeredItem,
        IReadOnlyList<CapitalShopInsert> inserts,
        CancellationToken cancellationToken)
    {
        var mutations = new List<CapitalShopMutation>(inserts.Count);
        foreach (var insert in inserts)
        {
            await InsertCharacterItemIntoEmptySlotAsync(
                connection,
                transaction,
                characterId,
                ItemLocationKitBag,
                insert.Slot,
                offeredItem with { Stack = insert.Stack },
                cancellationToken);
            await using var command = new NpgsqlCommand(
                """
                SELECT id, to_jsonb(character_items)::text
                FROM public.character_items
                WHERE user_id = @characterId AND item_location = 1
                  AND slot_index = @slot
                FOR UPDATE;
                """,
                connection,
                transaction);
            command.Parameters.AddWithValue("characterId", characterId);
            command.Parameters.AddWithValue("slot", insert.Slot);
            await using var reader =
                await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidDataException(
                    "A purchased item insert returned no durable row.");
            }
            mutations.Add(new CapitalShopMutation(
                reader.GetInt64(0),
                insert.Slot,
                reader.GetString(1)));
        }
        return mutations;
    }

    private readonly record struct CapitalShopLockedCharacter(
        int Gold,
        int BindingGold,
        long WalletRevision,
        long InventoryRevision)
    {
        public int GetBalance(CapitalNpcShopCurrency currency) =>
            currency == CapitalNpcShopCurrency.Gold
                ? Gold
                : BindingGold;
    }

    private readonly record struct CapitalShopInsert(
        short Slot,
        short Stack);

    private readonly record struct CapitalShopMutation(
        long ItemInstanceId,
        short Slot,
        string AfterState);
}
