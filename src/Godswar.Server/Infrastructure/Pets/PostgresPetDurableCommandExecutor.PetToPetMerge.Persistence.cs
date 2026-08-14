using System.Text.Json;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Pets;
using Godswar.Server.State;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.Infrastructure.Pets;

internal sealed partial class PostgresPetDurableCommandExecutor
{
    private async Task<IReadOnlyList<LockedPetMergeMaterialStack>>
        LockPetMergeMaterialStacksAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            int characterId,
            uint materialItemId,
            CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            SELECT
                id, slot_index, prop_id, item_quality,
                bound, stack, to_jsonb(character_items)::text
            FROM public.character_items
            WHERE user_id = @characterId
              AND item_location = 1
              AND prop_id = @propId
              AND stack > 0
            ORDER BY slot_index, id
            LIMIT 5
            FOR UPDATE;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("propId", checked((int)materialItemId));
        var result = new List<LockedPetMergeMaterialStack>(5);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new LockedPetMergeMaterialStack(
                reader.GetInt16(1),
                new LockedBagItem(
                    reader.GetInt64(0), reader.GetInt32(2),
                    reader.GetInt16(3), reader.GetInt16(4) != 0,
                    reader.GetInt16(5), reader.GetString(6))));
        }
        return result;
    }

    private async Task<IReadOnlyList<ConsumedPetMergeMaterial>>
        ConsumePetMergeMaterialsAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            int characterId,
            int quantity,
            IReadOnlyList<LockedPetMergeMaterialStack> stacks,
            CancellationToken cancellationToken)
    {
        var remaining = quantity;
        var result = new List<ConsumedPetMergeMaterial>(stacks.Count);
        foreach (var stack in stacks)
        {
            if (remaining == 0)
            {
                break;
            }

            var consumed = Math.Min(remaining, stack.Item.Stack);
            var afterState = await ConsumePetMergeMaterialStackAsync(
                connection,
                transaction,
                characterId,
                stack,
                consumed,
                cancellationToken);
            result.Add(new ConsumedPetMergeMaterial(
                stack,
                consumed,
                consumed == stack.Item.Stack ? "delete" : "update",
                afterState));
            remaining -= consumed;
        }

        if (remaining != 0)
        {
            throw new InvalidDataException(
                "Pet Merge material consumption was not exact.");
        }
        return result;
    }

    private async Task<string?> ConsumePetMergeMaterialStackAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        LockedPetMergeMaterialStack stack,
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
            AddPetMergeStackParameters(
                delete,
                characterId,
                stack);
            return await delete.ExecuteNonQueryAsync(cancellationToken) == 1
                ? null
                : throw new InvalidDataException(
                    "Pet Merge material was not deleted exactly once.");
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
            RETURNING to_jsonb(character_items)::text;
            """,
            connection,
            transaction);
        AddPetMergeStackParameters(update, characterId, stack);
        update.Parameters.AddWithValue("quantity", checked((short)quantity));
        return await update.ExecuteScalarAsync(cancellationToken) as string ??
            throw new InvalidDataException(
                "Pet Merge material stack was not reduced exactly once.");
    }

    private static void AddPetMergeStackParameters(
        NpgsqlCommand command,
        int characterId,
        LockedPetMergeMaterialStack stack)
    {
        command.Parameters.AddWithValue("itemId", stack.Item.ItemId);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("bagSlot", (short)stack.BagSlot);
        command.Parameters.AddWithValue("propId", stack.Item.PropId);
        command.Parameters.AddWithValue("expectedStack", stack.Item.Stack);
    }

    private async Task<long> PersistPetMergePlanAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        LockedOwnerMergePet primary,
        LockedOwnerMergePet deputy,
        PetMergeStats primaryStats,
        PetMergePlan plan,
        CancellationToken cancellationToken)
    {
        var after = plan.PrimaryPetAfter.InitialSavvy;
        var values = new[]
        {
            after.Agility, after.Strength, after.Accuracy,
            after.Technique, after.Wisdom, after.Luck
        };
        for (var index = 0; index < primaryStats.Rows.Count; index++)
        {
            var row = primaryStats.Rows[index];
            await using var updateStat = CreateCommand(
                """
                UPDATE public.character_pet_stat_values
                SET initial_savvy = @after,
                    revision = revision + 1
                WHERE pet_id = @petId
                  AND stat_code = @statCode
                  AND initial_savvy = @before
                  AND revision = @revision;
                """,
                connection,
                transaction);
            updateStat.Parameters.AddWithValue("after", values[index]);
            updateStat.Parameters.AddWithValue("petId", primary.PetId);
            updateStat.Parameters.AddWithValue("statCode", row.StatCode);
            updateStat.Parameters.AddWithValue("before", row.InitialSavvy);
            updateStat.Parameters.AddWithValue("revision", row.Revision);
            if (await updateStat.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new InvalidDataException(
                    $"Pet Merge stat {row.StatCode} did not advance exactly once.");
            }
        }

        await using (var deleteDeputy = CreateCommand(
            """
            DELETE FROM public.character_pets
            WHERE id = @petId
              AND user_id = @characterId
              AND revision = @revision
              AND activity_state = @activityState
              AND is_summoned = @isSummoned
              AND contributes_to_character = @contributes;
            """,
            connection,
            transaction))
        {
            deleteDeputy.Parameters.AddWithValue("petId", deputy.PetId);
            deleteDeputy.Parameters.AddWithValue("characterId", characterId);
            deleteDeputy.Parameters.AddWithValue("revision", deputy.Revision);
            deleteDeputy.Parameters.AddWithValue(
                "activityState",
                deputy.ActivityState);
            deleteDeputy.Parameters.AddWithValue(
                "isSummoned",
                deputy.IsSummoned);
            deleteDeputy.Parameters.AddWithValue(
                "contributes",
                deputy.ContributesToCharacter);
            if (await deleteDeputy.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new InvalidDataException(
                    "Pet Merge deputy was not consumed exactly once.");
            }
        }

        await using var updatePrimary = CreateCommand(
            """
            UPDATE public.character_pets
            SET completed_pet_merges = @completedMerges,
                rank = @rankAfter,
                revision = revision + 1,
                updated_at = transaction_timestamp()
            WHERE id = @petId
              AND user_id = @characterId
              AND revision = @revision
              AND rank = @rank
            RETURNING revision;
            """,
            connection,
            transaction);
        updatePrimary.Parameters.AddWithValue(
            "completedMerges",
            plan.PrimaryPetAfter.CompletedPetMerges);
        updatePrimary.Parameters.AddWithValue(
            "rankAfter",
            plan.PrimaryPetAfter.Rank);
        updatePrimary.Parameters.AddWithValue("petId", primary.PetId);
        updatePrimary.Parameters.AddWithValue("characterId", characterId);
        updatePrimary.Parameters.AddWithValue("revision", primary.Revision);
        updatePrimary.Parameters.AddWithValue("rank", primary.Rank);
        return await updatePrimary.ExecuteScalarAsync(cancellationToken)
            is long revision && revision == checked(primary.Revision + 1)
            ? revision
            : throw new InvalidDataException(
                "Pet Merge primary revision did not advance exactly once.");
    }

    private async Task<PetTransition> RejectPetMergeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CommandEnvelope<PetToPetMergeCommand> envelope,
        PetDurableReceiptStatus status,
        LockedOwnerMergePet? primary,
        LockedOwnerMergePet? deputy,
        CancellationToken cancellationToken)
    {
        await InsertPetMergeAuditAsync(
            connection,
            transaction,
            envelope,
            status,
            primary,
            primary,
            deputy,
            before: null,
            deputySavvy: null,
            after: null,
            savvyEvidence: null,
            rankEvidence: null,
            consumed: [],
            committed: false,
            cancellationToken);
        return new PetTransition(
            status,
            PetId: primary?.PetId ?? 0,
            PetLevel: primary?.Level ?? 0,
            PetExperience: primary?.Experience ?? 0,
            PetRevision: primary?.Revision ?? 0,
            IsCarried: primary?.IsCarried ?? false,
            IsSummoned: primary?.IsSummoned ?? false,
            DeputyPetId: deputy?.PetId ?? envelope.Command.DeputyPetId);
    }

    private async Task InsertPetMergeAuditAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CommandEnvelope<PetToPetMergeCommand> envelope,
        PetDurableReceiptStatus status,
        LockedOwnerMergePet? primaryBefore,
        LockedOwnerMergePet? primaryAfter,
        LockedOwnerMergePet? deputy,
        PetSavvy? before,
        PetSavvy? deputySavvy,
        PetSavvy? after,
        PetMergeSavvyRollEvidence? savvyEvidence,
        PetMergeRankRollEvidence? rankEvidence,
        IReadOnlyList<ConsumedPetMergeMaterial> consumed,
        bool committed,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            INSERT INTO public.pet_operation_audit (
                request_id, user_id, user_id_snapshot,
                pet_id, pet_id_snapshot, operation, outcome,
                before_state, after_state, consumed_items, reason_code
            )
            VALUES (
                @requestId, @characterId, @characterId,
                @petId, @petIdSnapshot, 'pet_merge', @outcome,
                @beforeState::jsonb, @afterState::jsonb,
                @consumedItems::jsonb, @reasonCode
            );
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue(
            "requestId",
            envelope.Command.Identity.OperationId);
        command.Parameters.AddWithValue(
            "characterId",
            envelope.Subject.CharacterId);
        command.Parameters.Add(
            "petId",
            NpgsqlDbType.Bigint).Value =
            primaryBefore is null ? DBNull.Value : primaryBefore.PetId;
        command.Parameters.AddWithValue(
            "petIdSnapshot",
            envelope.Command.PrimaryPetId);
        command.Parameters.AddWithValue(
            "outcome",
            committed ? "committed" : "rejected");
        command.Parameters.AddWithValue(
            "beforeState",
            SerializePetMergeState(
                primaryBefore,
                deputy,
                before,
                deputySavvy,
                savvyEvidence,
                rankEvidence,
                envelope.Command,
                _petContent.Revision.Sha256));
        command.Parameters.AddWithValue(
            "afterState",
            SerializePetMergeState(
                primaryAfter,
                committed ? null : deputy,
                after,
                committed ? deputySavvy : null,
                savvyEvidence,
                rankEvidence,
                envelope.Command,
                _petContent.Revision.Sha256));
        command.Parameters.AddWithValue(
            "consumedItems",
            JsonSerializer.Serialize(consumed.Select(value => new
            {
                item_id = value.Stack.Item.PropId,
                item_instance_id = value.Stack.Item.ItemId,
                quantity = value.Quantity,
                kit_bag_slot = value.Stack.BagSlot
            })));
        command.Parameters.AddWithValue("reasonCode", status.ToString());
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidDataException(
                "Pet Merge audit was not inserted exactly once.");
        }
    }

    private sealed record LockedPetMergeMaterialStack(
        int BagSlot,
        LockedBagItem Item);

    private sealed record ConsumedPetMergeMaterial(
        LockedPetMergeMaterialStack Stack,
        int Quantity,
        string MutationKind,
        string? AfterState);
}
