using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Pets;
using Godswar.Server.State;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.Infrastructure.Pets;

internal sealed partial class PostgresPetDurableCommandExecutor
{
    private async Task<OwnerMergeSavvy?> ReadOwnerMergeSavvyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        LockedOwnerMergePet pet,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            SELECT
                stat_code, initial_savvy, added_savvy,
                base_growth_rate, growth_acceleration,
                rarity_added_savvy
            FROM public.character_pet_stat_values
            WHERE pet_id = @petId
            ORDER BY stat_code
            FOR UPDATE;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("petId", pet.PetId);
        var initial = new decimal[6];
        var added = new decimal[6];
        var growth = new decimal[6];
        var acceleration = new decimal[6];
        var rarity = new decimal[6];
        var count = 0;
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (count >= 6 || reader.GetInt16(0) != count + 1)
            {
                return null;
            }

            initial[count] = reader.GetDecimal(1);
            added[count] = reader.GetDecimal(2);
            growth[count] = reader.GetDecimal(3);
            acceleration[count] = reader.GetDecimal(4);
            rarity[count] = reader.GetDecimal(5);
            count++;
        }

        if (count != 6 ||
            !string.Equals(
                pet.InitialSavvySourceVersion,
                PetSavvyRuntimeSemantics.SourceVersion,
                StringComparison.Ordinal))
        {
            return null;
        }

        var result = new OwnerMergeSavvy(
            ToPetSavvy(initial),
            ToPetSavvy(added),
            ToPetSavvy(growth),
            ToPetSavvy(acceleration),
            ToPetSavvy(rarity));
        return result.Initial.Total >= result.RarityAdded.Total &&
            PetSavvyRuntimeSemantics.HasStrictlyPositiveValues(
                result.BaseGrowth) &&
            result.GrowthAcceleration.IsNonNegative &&
            PetSavvyRuntimeSemantics.HasStrictlyPositiveValues(
                result.RarityAdded) &&
            result.Added ==
                PetSavvyRuntimeSemantics.ResolveLevelScaledAdded(
                    pet.Level,
                    result.BaseGrowth,
                    result.GrowthAcceleration)
            ? result
            : null;
    }

    private async Task<OwnerMergeStoredContribution>
        ReadOwnerMergeContributionAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            long petId,
            CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            SELECT effect_code, effect_value, balance_revision
            FROM public.character_pet_character_bonuses
            WHERE pet_id = @petId
            ORDER BY effect_code
            FOR UPDATE;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("petId", petId);
        var values = new List<PetOwnerMergeEffectValue>(16);
        var isCurrentRevision = true;
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var code = (PetOwnerMergeEffectCode)reader.GetInt16(0);
            var value = reader.GetDecimal(1);
            if (!Enum.IsDefined(code) || value < 0m ||
                values.Any(existing => existing.Effect == code))
            {
                isCurrentRevision = false;
                continue;
            }
            if (reader.IsDBNull(2) || !reader.GetString(2).Equals(
                    _ownerMergeContent.Revision.Sha256,
                    StringComparison.Ordinal))
            {
                isCurrentRevision = false;
            }
            values.Add(new(code, value));
        }

        isCurrentRevision &= values.Count ==
            Enum.GetValues<PetOwnerMergeEffectCode>().Length;
        // A stale or malformed derived projection is never trusted as an
        // authoritative contribution. Zero exists only so unmerge remains
        // available and can delete the invalid rows; startup reconciliation
        // rematerializes active merges before listeners start.
        return isCurrentRevision
            ? new OwnerMergeStoredContribution(
                PetOwnerMergeContributionCalculator.FromEffectValues(values),
                IsCurrentRevision: true)
            : new OwnerMergeStoredContribution(
                PetOwnerStatContribution.Zero,
                IsCurrentRevision: false);
    }

    private async Task<long> PersistOwnerMergePlanAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        LockedOwnerMergePet pet,
        PetOwnerMergePlan plan,
        CancellationToken cancellationToken)
    {
        var nextRevision = checked(pet.Revision + 1);
        await using (var clear = CreateCommand(
            """
            DELETE FROM public.character_pet_character_bonuses
            WHERE pet_id = @petId;
            """,
            connection,
            transaction))
        {
            clear.Parameters.AddWithValue("petId", pet.PetId);
            await clear.ExecuteNonQueryAsync(cancellationToken);
        }

        if (plan.IsMerging)
        {
            var effects = PetOwnerMergeContributionCalculator
                .ToEffectValues(plan.StatContribution);
            await using var insert = CreateCommand(
                """
                INSERT INTO public.character_pet_character_bonuses (
                    pet_id, effect_code, effect_value, revision,
                    balance_revision
                )
                SELECT
                    @petId, value.effect_code, value.effect_value,
                    @revision, @balanceRevision
                FROM unnest(
                    @effectCodes::smallint[],
                    @effectValues::numeric[]
                ) AS value(effect_code, effect_value);
                """,
                connection,
                transaction);
            insert.Parameters.AddWithValue("petId", pet.PetId);
            insert.Parameters.AddWithValue("revision", nextRevision);
            insert.Parameters.AddWithValue(
                "balanceRevision",
                _ownerMergeContent.Revision.Sha256);
            insert.Parameters.Add(
                "effectCodes",
                NpgsqlDbType.Array | NpgsqlDbType.Smallint).Value =
                effects.Select(static value => (short)value.Effect).ToArray();
            insert.Parameters.Add(
                "effectValues",
                NpgsqlDbType.Array | NpgsqlDbType.Numeric).Value =
                effects.Select(static value => value.Value).ToArray();
            if (await insert.ExecuteNonQueryAsync(cancellationToken) !=
                effects.Count)
            {
                throw new InvalidDataException(
                    "Pet owner-merge bonuses were not inserted exactly.");
            }
        }

        await using var update = CreateCommand(
            """
            UPDATE public.character_pets
            SET contributes_to_character = @contributes,
                current_energy = @currentEnergy,
                revision = revision + 1,
                updated_at = transaction_timestamp()
            WHERE id = @petId
              AND user_id = @characterId
              AND revision = @expectedRevision
            RETURNING revision;
            """,
            connection,
            transaction);
        update.Parameters.AddWithValue("petId", pet.PetId);
        update.Parameters.AddWithValue("characterId", characterId);
        update.Parameters.AddWithValue("contributes", plan.IsMerging);
        update.Parameters.AddWithValue(
            "currentEnergy",
            plan.PetAfter.CurrentEnergy);
        update.Parameters.AddWithValue("expectedRevision", pet.Revision);
        var revision = await update.ExecuteScalarAsync(cancellationToken);
        return revision is long persisted && persisted == nextRevision
            ? persisted
            : throw new InvalidDataException(
                "Pet owner-merge revision did not advance exactly once.");
    }

    private async Task<PetTransition> RejectOwnerMergeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CommandEnvelope<PetOwnerMergeToggleCommand> envelope,
        PetDurableReceiptStatus status,
        LockedOwnerMergePet? pet,
        CancellationToken cancellationToken)
    {
        await InsertOwnerMergeAuditAsync(
            connection,
            transaction,
            envelope,
            status,
            pet,
            pet?.ContributesToCharacter ?? false,
            storedBalanceCurrent: null,
            committed: false,
            cancellationToken);
        return new PetTransition(
            status,
            PetId: pet?.PetId ?? 0,
            PetLevel: pet?.Level ?? 0,
            PetExperience: pet?.Experience ?? 0,
            PetRevision: pet?.Revision ?? 0,
            IsCarried: pet?.IsCarried ?? false,
            IsSummoned: pet?.IsSummoned ?? false);
    }

    private async Task InsertOwnerMergeAuditAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CommandEnvelope<PetOwnerMergeToggleCommand> envelope,
        PetDurableReceiptStatus status,
        LockedOwnerMergePet? pet,
        bool afterContributes,
        bool? storedBalanceCurrent,
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
                @petId, @petId, 'owner_merge', @outcome,
                jsonb_build_object(
                    'contributesToCharacter', @beforeContributes,
                    'revision', @beforeRevision,
                    'storedBalanceCurrent', @storedBalanceCurrent
                ),
                jsonb_build_object(
                    'contributesToCharacter', @afterContributes,
                    'policyVersion', @policyVersion,
                    'balanceRevision', @balanceRevision
                ),
                '[]'::jsonb, @reasonCode
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
            pet is null ? DBNull.Value : pet.PetId;
        command.Parameters.AddWithValue(
            "outcome",
            committed ? "committed" : "rejected");
        command.Parameters.AddWithValue(
            "beforeContributes",
            pet?.ContributesToCharacter ?? false);
        command.Parameters.AddWithValue(
            "beforeRevision",
            pet?.Revision ?? 0);
        command.Parameters.AddWithValue(
            "afterContributes",
            afterContributes);
        command.Parameters.Add(
            "storedBalanceCurrent",
            NpgsqlDbType.Boolean).Value =
            storedBalanceCurrent is null
                ? DBNull.Value
                : storedBalanceCurrent.Value;
        command.Parameters.AddWithValue(
            "policyVersion",
            _ownerMergeContent.Revision.PolicyVersion);
        command.Parameters.AddWithValue(
            "balanceRevision",
            _ownerMergeContent.Revision.Sha256);
        command.Parameters.AddWithValue("reasonCode", status.ToString());
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidDataException(
                "Pet owner-merge audit was not inserted exactly once.");
        }
    }

    private readonly record struct OwnerMergeStoredContribution(
        PetOwnerStatContribution Contribution,
        bool IsCurrentRevision);
}
