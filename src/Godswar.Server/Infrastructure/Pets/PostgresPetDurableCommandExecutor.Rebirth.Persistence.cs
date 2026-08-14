using System.Text.Json;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Pets;
using Godswar.Server.State;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.Infrastructure.Pets;

internal sealed partial class PostgresPetDurableCommandExecutor
{
    private async Task<LockedRebirthPet?> LockActivePetForRebirthAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            SELECT
                id, species_id, name, level, experience, rank, aptitude,
                completed_pet_merges, completed_rebirths,
                rebirths_remaining, has_soul_contract,
                soul_contract_stage,
                has_owner_merge_talent, bound, is_carried, is_summoned,
                activity_state, current_energy, maximum_energy, amity,
                contributes_to_character, revision,
                initial_savvy_source_version
            FROM public.character_pets
            WHERE user_id = @characterId
              AND activity_state = 'owned'
              AND is_summoned
            ORDER BY id
            FOR UPDATE;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("characterId", characterId);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var pet = new LockedRebirthPet(
            reader.GetInt64(0),
            reader.GetInt16(1),
            reader.GetString(2),
            reader.GetInt16(3),
            reader.GetInt64(4),
            reader.GetDecimal(5),
            reader.GetInt16(6),
            reader.GetInt32(7),
            reader.GetInt16(8),
            reader.GetInt16(9),
            reader.GetBoolean(10),
            checked((byte)reader.GetInt16(11)),
            reader.GetBoolean(12),
            reader.GetBoolean(13),
            reader.GetBoolean(14),
            reader.GetBoolean(15),
            reader.GetString(16),
            reader.GetInt32(17),
            reader.GetInt32(18),
            reader.GetInt32(19),
            reader.GetBoolean(20),
            reader.GetInt64(21),
            reader.IsDBNull(22) ? null : reader.GetString(22));
        if (await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidDataException(
                "More than one summoned rebirth pet is authoritative.");
        }
        return pet;
    }

    private async Task<PetRebirthStats?> LockPetRebirthStatsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long petId,
        short petLevel,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            SELECT
                stat_code, initial_savvy, added_savvy,
                base_growth_rate, growth_acceleration,
                rarity_added_savvy, revision
            FROM public.character_pet_stat_values
            WHERE pet_id = @petId
            ORDER BY stat_code
            FOR UPDATE;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("petId", petId);
        var rows = new List<LockedRebirthStat>(6);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (reader.IsDBNull(5))
            {
                return null;
            }
            rows.Add(new LockedRebirthStat(
                reader.GetInt16(0),
                reader.GetDecimal(1),
                reader.GetDecimal(2),
                reader.GetDecimal(3),
                reader.GetDecimal(4),
                reader.GetDecimal(5),
                reader.GetInt64(6)));
        }
        if (rows.Count != 6 ||
            rows.Where((row, index) => row.StatCode != index + 1).Any() ||
            rows.Any(row =>
                row.InitialSavvy <= 0m ||
                row.BaseGrowthRate <= 0m ||
                row.GrowthAcceleration < 0m ||
                row.AddedSavvy !=
                    PetSavvyRuntimeSemantics.ResolveLevelScaledAdded(
                        petLevel,
                        row.BaseGrowthRate,
                        row.GrowthAcceleration) ||
                row.RarityAddedSavvy <= 0m ||
                row.Revision < 0) ||
            rows.Sum(static row => row.InitialSavvy) <
                rows.Sum(static row => row.RarityAddedSavvy))
        {
            return null;
        }

        return new PetRebirthStats(
            ToRebirthSavvy(rows, static row => row.InitialSavvy),
            ToRebirthSavvy(rows, static row => row.AddedSavvy),
            ToRebirthSavvy(rows, static row => row.BaseGrowthRate),
            ToRebirthSavvy(rows, static row => row.GrowthAcceleration),
            ToRebirthSavvy(rows, static row => row.RarityAddedSavvy),
            rows);
    }

    private async Task<IReadOnlyList<LockedRebirthMaterial>>
        LockRebirthMaterialStacksAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            int characterId,
            int materialTemplateId,
            CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            SELECT
                id, slot_index, prop_id, item_quality, bound, stack,
                to_jsonb(character_items)::text
            FROM public.character_items
            WHERE user_id = @characterId
              AND item_location = 1
              AND prop_id = @materialTemplateId
              AND stack > 0
            ORDER BY
                CASE WHEN stack >= @requiredQuantity THEN 0 ELSE 1 END,
                stack DESC,
                slot_index
            FOR UPDATE;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue(
            "materialTemplateId",
            materialTemplateId);
        command.Parameters.AddWithValue(
            "requiredQuantity",
            _petContent.Settings.RequiredRebirthSpiritCount);
        var rows = new List<LockedRebirthMaterial>();
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new LockedRebirthMaterial(
                reader.GetInt16(1),
                new LockedBagItem(
                    reader.GetInt64(0),
                    reader.GetInt32(2),
                    reader.GetInt16(3),
                    reader.GetInt16(4) != 0,
                    reader.GetInt16(5),
                    reader.GetString(6))));
        }
        return rows;
    }

    private async Task<IReadOnlyList<ConsumedRebirthMaterial>>
        ConsumeRebirthMaterialsAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            int characterId,
            IReadOnlyList<LockedRebirthMaterial> stacks,
            int quantity,
            CancellationToken cancellationToken)
    {
        var remaining = quantity;
        var consumed = new List<ConsumedRebirthMaterial>(stacks.Count);
        foreach (var stack in stacks)
        {
            if (remaining == 0)
            {
                break;
            }
            var take = Math.Min(remaining, stack.Item.Stack);
            consumed.Add(await ConsumeRebirthMaterialStackAsync(
                connection,
                transaction,
                characterId,
                stack,
                take,
                cancellationToken));
            remaining -= take;
        }
        if (remaining != 0 || consumed.Count is < 1 or > 5)
        {
            throw new InvalidDataException(
                "The rebirth material consumption was not exact.");
        }
        return consumed;
    }

    private async Task<ConsumedRebirthMaterial>
        ConsumeRebirthMaterialStackAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            int characterId,
            LockedRebirthMaterial stack,
            int quantity,
            CancellationToken cancellationToken)
    {
        if (quantity == stack.Item.Stack)
        {
            await using var delete = CreateCommand(
                """
                DELETE FROM public.character_items
                WHERE id = @itemId
                  AND user_id = @characterId
                  AND item_location = 1
                  AND slot_index = @bagSlot
                  AND prop_id = @propId
                  AND stack = @expectedStack;
                """,
                connection,
                transaction);
            AddRebirthMaterialParameters(
                delete,
                characterId,
                stack);
            if (await delete.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new InvalidDataException(
                    "A rebirth material stack was not deleted exactly once.");
            }
            return new(stack.BagSlot, stack.Item, quantity, "delete", null);
        }

        await using var update = CreateCommand(
            """
            UPDATE public.character_items
            SET stack = stack - @quantity,
                updated_at = transaction_timestamp()
            WHERE id = @itemId
              AND user_id = @characterId
              AND item_location = 1
              AND slot_index = @bagSlot
              AND prop_id = @propId
              AND stack = @expectedStack
              AND stack > @quantity
            RETURNING to_jsonb(character_items)::text;
            """,
            connection,
            transaction);
        AddRebirthMaterialParameters(update, characterId, stack);
        update.Parameters.AddWithValue("quantity", checked((short)quantity));
        var after = await update.ExecuteScalarAsync(cancellationToken)
            as string ?? throw new InvalidDataException(
                "A rebirth material stack was not reduced exactly once.");
        return new(stack.BagSlot, stack.Item, quantity, "update", after);
    }

    private static void AddRebirthMaterialParameters(
        NpgsqlCommand command,
        int characterId,
        LockedRebirthMaterial stack)
    {
        command.Parameters.AddWithValue("itemId", stack.Item.ItemId);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("bagSlot", stack.BagSlot);
        command.Parameters.AddWithValue("propId", stack.Item.PropId);
        command.Parameters.AddWithValue(
            "expectedStack",
            stack.Item.Stack);
    }

    private static PetSavvy ToRebirthSavvy(
        IReadOnlyList<LockedRebirthStat> rows,
        Func<LockedRebirthStat, decimal> select) =>
        new(
            select(rows[0]), select(rows[1]), select(rows[2]),
            select(rows[3]), select(rows[4]), select(rows[5]));

    private sealed record LockedRebirthPet(
        long PetId,
        short SpeciesId,
        string Name,
        short Level,
        long Experience,
        decimal Rank,
        short Aptitude,
        int CompletedPetMerges,
        short CompletedRebirths,
        short RebirthsRemaining,
        bool HasSoulContract,
        byte SoulContractStage,
        bool HasOwnerMergeTalent,
        bool IsBound,
        bool IsCarried,
        bool IsSummoned,
        string ActivityState,
        int CurrentEnergy,
        int MaximumEnergy,
        int Amity,
        bool ContributesToCharacter,
        long Revision,
        string? InitialSavvySourceVersion)
    {
        public OwnedPet ToOwnedPet(
            int ownerCharacterId,
            PetRebirthStats stats) =>
            new(
                PetId,
                ownerCharacterId,
                SpeciesId,
                Name,
                Level,
                Experience,
                Rank,
                (PetAptitude)Aptitude,
                stats.Initial,
                stats.Added,
                stats.BaseGrowth,
                stats.GrowthAcceleration,
                CompletedPetMerges,
                CompletedRebirths,
                RebirthsRemaining,
                HasSoulContract,
                HasOwnerMergeTalent,
                IsBound,
                IsSummoned,
                IsAway: !string.Equals(
                    ActivityState,
                    "owned",
                    StringComparison.Ordinal),
                CurrentEnergy,
                MaximumEnergy,
                Amity,
                ContributesToCharacter
                    ? new PetOwnerMergeState(
                        PetOwnerStatContribution.Zero,
                        [])
                    : null,
                stats.RarityAdded,
                SoulContractStage);
    }

    private readonly record struct PetRebirthStats(
        PetSavvy Initial,
        PetSavvy Added,
        PetSavvy BaseGrowth,
        PetSavvy GrowthAcceleration,
        PetSavvy RarityAdded,
        IReadOnlyList<LockedRebirthStat> Rows);

    private sealed record LockedRebirthStat(
        short StatCode,
        decimal InitialSavvy,
        decimal AddedSavvy,
        decimal BaseGrowthRate,
        decimal GrowthAcceleration,
        decimal RarityAddedSavvy,
        long Revision);

    private sealed record LockedRebirthMaterial(
        short BagSlot,
        LockedBagItem Item);

    private sealed record ConsumedRebirthMaterial(
        short BagSlot,
        LockedBagItem Item,
        int Quantity,
        string MutationKind,
        string? AfterState);
}
