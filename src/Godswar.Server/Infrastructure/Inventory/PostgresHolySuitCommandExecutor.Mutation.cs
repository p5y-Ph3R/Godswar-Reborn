using Godswar.Server.Application.Inventory;
using Npgsql;

namespace Godswar.Server.Infrastructure.Inventory;

internal sealed partial class PostgresHolySuitCommandExecutor
{
    private async Task<HolySuitExecutionResult> PersistCommittedResultAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        HolySuitCommandContext context,
        LockedCharacter character,
        DailyUsage daily,
        bool battlePass,
        HolySuitPlan plan,
        string principalKey,
        string aggregateKey,
        byte[] operationId,
        byte[] requestHash,
        CancellationToken cancellationToken)
    {
        ValidateCommittedPlan(context.Command, character, daily, plan);
        var applied = await ApplyMutationsAsync(
            connection,
            transaction,
            context.Subject.CharacterId,
            plan.Mutations,
            cancellationToken);
        var inventoryRevision = checked(character.InventoryRevision + 1);
        var spendsCharacterExperience =
            plan.CharacterExperienceAfter != character.Experience;
        var progressionRevision = spendsCharacterExperience
            ? checked(character.ProgressionRevision + 1)
            : character.ProgressionRevision;
        await AdvanceCharacterStateAsync(
            connection,
            transaction,
            context,
            character,
            plan.CharacterExperienceAfter,
            progressionRevision,
            inventoryRevision,
            cancellationToken);
        if (context.Command.Operation ==
            HolySuitCommandOperation.StoreExperience)
        {
            await AdvanceDailyUsageAsync(
                connection,
                transaction,
                context.Subject.AccountId,
                character.RealmId,
                daily,
                plan.DailyStoredExperienceAfter,
                cancellationToken);
        }
        if (context.Command.Operation ==
            HolySuitCommandOperation.ConsumeWare)
        {
            await RecomputeHolySuitPointsAsync(
                connection,
                transaction,
                context.Subject.CharacterId,
                cancellationToken);
        }

        var auditId = await InsertAuditAsync(
            connection,
            transaction,
            context,
            plan.Status,
            principalKey,
            aggregateKey,
            operationId,
            requestHash,
            character,
            plan,
            battlePass,
            cancellationToken);
        var eventId = Guid.NewGuid();
        var receipt = CreateReceipt(
            context,
            character,
            daily,
            battlePass,
            plan,
            applied,
            progressionRevision,
            inventoryRevision,
            auditId,
            eventId);
        var payload = HolySuitPersistenceCodec.Encode(receipt);
        var inboxId = await InsertInboxAsync(
            connection,
            transaction,
            context.Family,
            plan.Status,
            principalKey,
            aggregateKey,
            operationId,
            requestHash,
            HolySuitPersistenceCodec.Hash(payload),
            auditId,
            payload,
            cancellationToken);
        await InsertInventoryLedgerAsync(
            connection,
            transaction,
            inboxId,
            context,
            inventoryRevision,
            applied,
            cancellationToken);
        await InsertOutboxAsync(
            connection,
            transaction,
            inboxId,
            context.Family,
            aggregateKey,
            inventoryRevision,
            eventId,
            payload,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return HolySuitExecutionResult.Committed(receipt);
    }

    private static void ValidateCommittedPlan(
        HolySuitCommand command,
        LockedCharacter character,
        DailyUsage daily,
        HolySuitPlan plan)
    {
        if (!plan.Committed || plan.Mutations.Count is < 1 or > 96 ||
            plan.Mutations.Select(static value => value.Slot)
                .Distinct().Count() != plan.Mutations.Count)
        {
            throw new InvalidDataException(
                "The committed Holy Suit mutation plan is invalid.");
        }
        if (plan.Mutations.Any(static value =>
            value.Before == value.After ||
            value.Existing is null != value.Before.IsEmpty ||
            value.After.IsEmpty && value.Existing is null))
        {
            throw new InvalidDataException(
                "The Holy Suit plan contradicts its item identity.");
        }
        if (command.Operation == HolySuitCommandOperation.StoreExperience &&
            plan.Mutations.Count != 1 ||
            command.Operation == HolySuitCommandOperation.TransferExperience &&
            plan.Mutations.Count != 2 ||
            command.Operation == HolySuitCommandOperation.ConsumeWare &&
            plan.Mutations.Count < 2)
        {
            throw new InvalidDataException(
                "The Holy Suit plan has an invalid role count.");
        }

        ValidateStoredExperiencePlan(command, character, daily, plan);
    }

    private async Task<IReadOnlyList<AppliedMutation>> ApplyMutationsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        IReadOnlyList<PlannedMutation> mutations,
        CancellationToken cancellationToken)
    {
        var applied = new List<AppliedMutation>(mutations.Count);
        foreach (var mutation in mutations)
        {
            applied.Add(mutation.Existing is null
                ? await InsertItemAsync(
                    connection,
                    transaction,
                    characterId,
                    mutation,
                    cancellationToken)
                : mutation.After.IsEmpty
                    ? await DeleteItemAsync(
                        connection,
                        transaction,
                        characterId,
                        mutation,
                        cancellationToken)
                    : await UpdateItemAsync(
                        connection,
                        transaction,
                        characterId,
                        mutation,
                        cancellationToken));
        }
        return applied;
    }

    private async Task<AppliedMutation> UpdateItemAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        PlannedMutation mutation,
        CancellationToken cancellationToken)
    {
        var existing = mutation.Existing ??
            throw new InvalidDataException("A Holy Suit update has no item.");
        await using var command = CreateCommand(
            """
            UPDATE public.character_items
            SET bound = @bound,
                stack = @stack,
                item_exp = @itemExp,
                holy_suit_code = @holySuitCode,
                updated_at = now()
            WHERE id = @itemInstanceId
              AND user_id = @characterId
              AND item_location = 1
              AND slot_index = @slotIndex
              AND prop_id = @itemId
            RETURNING to_jsonb(character_items)::text;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("bound", mutation.After.Bound);
        command.Parameters.AddWithValue("stack", mutation.After.Stack);
        command.Parameters.AddWithValue("itemExp", mutation.After.Exp);
        command.Parameters.AddWithValue(
            "holySuitCode",
            mutation.After.HolySuitCode);
        command.Parameters.AddWithValue(
            "itemInstanceId",
            existing.ItemInstanceId);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("slotIndex", mutation.Slot);
        command.Parameters.AddWithValue(
            "itemId",
            checked((int)mutation.Before.Id));
        var afterState =
            await command.ExecuteScalarAsync(cancellationToken) as string ??
            throw new InvalidDataException(
                "The locked Holy Suit item did not update exactly once.");
        return new AppliedMutation(
            mutation.Role,
            mutation.Slot,
            mutation.Before.Id,
            existing.ItemInstanceId,
            mutation.Before.ToCompactString(),
            mutation.After.ToCompactString(),
            "update",
            existing.BeforeState,
            afterState);
    }

    private async Task<AppliedMutation> DeleteItemAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        PlannedMutation mutation,
        CancellationToken cancellationToken)
    {
        var existing = mutation.Existing ??
            throw new InvalidDataException("A Holy Suit delete has no item.");
        await using var command = CreateCommand(
            """
            WITH deleted AS (
                DELETE FROM public.character_items
                WHERE id = @itemInstanceId
                  AND user_id = @characterId
                  AND item_location = 1
                  AND slot_index = @slotIndex
                  AND prop_id = @itemId
                RETURNING *
            )
            INSERT INTO public.character_item_audit (
                source,
                action,
                user_id,
                item_location,
                slot_index,
                prop_id,
                item_quality,
                item_grade,
                item_exp,
                old_item
            )
            SELECT
                'holy-suit-forger',
                'delete',
                user_id,
                item_location,
                slot_index,
                prop_id,
                item_quality,
                item_grade,
                item_exp,
                to_jsonb(deleted)
            FROM deleted;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue(
            "itemInstanceId",
            existing.ItemInstanceId);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("slotIndex", mutation.Slot);
        command.Parameters.AddWithValue(
            "itemId",
            checked((int)mutation.Before.Id));
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidDataException(
                "The locked Holy Suit item did not delete exactly once.");
        }
        return new AppliedMutation(
            mutation.Role,
            mutation.Slot,
            mutation.Before.Id,
            existing.ItemInstanceId,
            mutation.Before.ToCompactString(),
            "[]",
            "delete",
            existing.BeforeState,
            null);
    }

    private async Task<AppliedMutation> InsertItemAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        PlannedMutation mutation,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            INSERT INTO public.character_items (
                user_id,
                item_location,
                slot_index,
                prop_id,
                item_quality,
                item_grade,
                bound,
                stack,
                item_exp,
                holy_suit_code
            )
            VALUES (
                @characterId,
                1,
                @slotIndex,
                @itemId,
                @itemQuality,
                @itemGrade,
                @bound,
                @stack,
                @itemExp,
                @holySuitCode
            )
            RETURNING id, to_jsonb(character_items)::text;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("slotIndex", mutation.Slot);
        command.Parameters.AddWithValue(
            "itemId",
            checked((int)mutation.After.Id));
        command.Parameters.AddWithValue(
            "itemQuality",
            mutation.After.Quality);
        command.Parameters.AddWithValue("itemGrade", mutation.After.Grade);
        command.Parameters.AddWithValue("bound", mutation.After.Bound);
        command.Parameters.AddWithValue("stack", mutation.After.Stack);
        command.Parameters.AddWithValue("itemExp", mutation.After.Exp);
        command.Parameters.AddWithValue(
            "holySuitCode",
            mutation.After.HolySuitCode);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidDataException(
                "The Holy Suit prism insert returned no identity.");
        }
        return new AppliedMutation(
            mutation.Role,
            mutation.Slot,
            mutation.After.Id,
            reader.GetInt64(0),
            "[]",
            mutation.After.ToCompactString(),
            "add",
            null,
            reader.GetString(1));
    }

    private async Task AdvanceCharacterStateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        HolySuitCommandContext context,
        LockedCharacter before,
        long experienceAfter,
        long progressionRevision,
        long inventoryRevision,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            UPDATE public.character_base
            SET fighter_job_exp = @experienceAfter,
                progression_reward_revision = @progressionRevision,
                inventory_revision = @inventoryRevision
            WHERE account_id = @accountId
              AND id = @characterId
              AND server_id = @realmId
              AND fighter_job_exp = @experienceBefore
              AND progression_reward_revision = @expectedProgressionRevision
              AND inventory_revision = @expectedInventoryRevision;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("experienceAfter", experienceAfter);
        command.Parameters.AddWithValue(
            "progressionRevision",
            progressionRevision);
        command.Parameters.AddWithValue(
            "inventoryRevision",
            inventoryRevision);
        command.Parameters.AddWithValue("accountId", context.Subject.AccountId);
        command.Parameters.AddWithValue(
            "characterId",
            context.Subject.CharacterId);
        command.Parameters.AddWithValue(
            "realmId",
            _realmCalendar.RealmId.Value);
        command.Parameters.AddWithValue("experienceBefore", before.Experience);
        command.Parameters.AddWithValue(
            "expectedProgressionRevision",
            before.ProgressionRevision);
        command.Parameters.AddWithValue(
            "expectedInventoryRevision",
            before.InventoryRevision);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidDataException(
                "The Holy Suit character revisions did not advance once.");
        }
    }

    private async Task AdvanceDailyUsageAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int accountId,
        Godswar.Server.Domain.World.Instances.RealmId realmId,
        DailyUsage daily,
        long after,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            UPDATE public.holy_suit_daily_exp_storage
            SET stored_exp = @after,
                operation_count = operation_count + 1,
                updated_at = now()
            WHERE account_id = @accountId
              AND realm_id = @realmId
              AND usage_day = @usageDay
              AND stored_exp = @before;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("after", after);
        command.Parameters.AddWithValue("accountId", accountId);
        command.Parameters.AddWithValue("realmId", realmId.Value);
        command.Parameters.AddWithValue("usageDay", daily.UsageDay);
        command.Parameters.AddWithValue("before", daily.StoredExperience);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidDataException(
                "The Holy Suit daily usage did not advance exactly once.");
        }
    }

    private async Task RecomputeHolySuitPointsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            "SELECT public.recompute_character_holy_suit_points(@characterId);",
            connection,
            transaction);
        command.Parameters.AddWithValue("characterId", characterId);
        if (await command.ExecuteScalarAsync(cancellationToken) is not int)
        {
            throw new InvalidDataException(
                "Holy Suit points could not be recomputed.");
        }
    }

}
