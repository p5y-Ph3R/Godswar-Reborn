using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Pets;
using Npgsql;

namespace Godswar.Server.Infrastructure.Pets;

internal sealed partial class PostgresPetDurableCommandExecutor
{
    public Task<PetOwnerMergeLifecycleResult> DrainEnergyAsync(
        CommandSubject subject,
        PlayerOwnershipFence ownership,
        int energyPoints,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(energyPoints);
        return MutateOwnerMergeLifecycleAsync(
            subject,
            ownership,
            energyPoints,
            explicitEndReason: null,
            cancellationToken);
    }

    public Task<PetOwnerMergeLifecycleResult> EndAsync(
        CommandSubject subject,
        PlayerOwnershipFence ownership,
        PetOwnerMergeEndReason reason,
        CancellationToken cancellationToken = default)
    {
        if (reason is not (
                PetOwnerMergeEndReason.SessionEnded or
                PetOwnerMergeEndReason.StaleLoginRecovery))
        {
            throw new ArgumentOutOfRangeException(nameof(reason));
        }
        return MutateOwnerMergeLifecycleAsync(
            subject,
            ownership,
            energyPoints: 0,
            explicitEndReason: reason,
            cancellationToken);
    }

    private async Task<PetOwnerMergeLifecycleResult>
        MutateOwnerMergeLifecycleAsync(
            CommandSubject subject,
            PlayerOwnershipFence ownership,
            int energyPoints,
            PetOwnerMergeEndReason? explicitEndReason,
            CancellationToken cancellationToken)
    {
        await using var connection =
            await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction =
            await connection.BeginTransactionAsync(cancellationToken);
        (await _ownershipGuard.LockCurrentAsync(
            connection,
            transaction,
            subject,
            ownership,
            cancellationToken)).RequireCurrent();

        var active = await LockOwnerMergeLifecycleRowAsync(
            connection,
            transaction,
            subject.CharacterId,
            cancellationToken);
        if (active is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return Validated(new PetOwnerMergeLifecycleResult(
                PetOwnerMergeLifecycleStatus.NoActiveMerge,
                PetId: 0,
                CurrentEnergy: 0,
                MaximumEnergy: 0,
                PetRevision: 0,
                IsCarried: false,
                IsSummoned: false));
        }

        var energy = explicitEndReason.HasValue
            ? active.CurrentEnergy
            : Math.Max(0, active.CurrentEnergy - energyPoints);
        var endReason = explicitEndReason ??
            (energy == 0
                ? PetOwnerMergeEndReason.EnergyDepleted
                : (PetOwnerMergeEndReason?)null);
        var ended = endReason.HasValue;

        if (ended)
        {
            await using var clear = CreateCommand(
                """
                DELETE FROM public.character_pet_character_bonuses
                WHERE pet_id = @petId;
                """,
                connection,
                transaction);
            clear.Parameters.AddWithValue("petId", active.PetId);
            await clear.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var update = CreateCommand(
            """
            UPDATE public.character_pets
            SET current_energy = @energy,
                contributes_to_character = @contributes,
                revision = revision + 1,
                updated_at = transaction_timestamp()
            WHERE id = @petId
              AND user_id = @characterId
              AND revision = @expectedRevision
              AND contributes_to_character
            RETURNING revision;
            """,
            connection,
            transaction);
        update.Parameters.AddWithValue("energy", energy);
        update.Parameters.AddWithValue("contributes", !ended);
        update.Parameters.AddWithValue("petId", active.PetId);
        update.Parameters.AddWithValue(
            "characterId",
            subject.CharacterId);
        update.Parameters.AddWithValue(
            "expectedRevision",
            active.Revision);
        var nextRevision =
            await update.ExecuteScalarAsync(cancellationToken) as long? ??
            throw new InvalidDataException(
                "The active pet owner-Merge changed during settlement.");

        if (ended)
        {
            await InsertOwnerMergeLifecycleAuditAsync(
                connection,
                transaction,
                subject,
                active,
                energy,
                nextRevision,
                endReason!.Value,
                cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        (await _ownershipGuard.ValidateCurrentAsync(
            subject,
            ownership,
            cancellationToken)).RequireCurrent();
        return Validated(new PetOwnerMergeLifecycleResult(
            ended
                ? PetOwnerMergeLifecycleStatus.MergeEnded
                : PetOwnerMergeLifecycleStatus.EnergyChanged,
            active.PetId,
            energy,
            active.MaximumEnergy,
            nextRevision,
            active.IsCarried,
            active.IsSummoned));
    }

    private async Task<OwnerMergeLifecycleRow?>
        LockOwnerMergeLifecycleRowAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            int characterId,
            CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            SELECT
                id, current_energy, maximum_energy,
                revision, is_carried, is_summoned
            FROM public.character_pets
            WHERE user_id = @characterId
              AND contributes_to_character
            ORDER BY id
            FOR UPDATE;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("characterId", characterId);
        var rows = new List<OwnerMergeLifecycleRow>(2);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new OwnerMergeLifecycleRow(
                reader.GetInt64(0),
                reader.GetInt32(1),
                reader.GetInt32(2),
                reader.GetInt64(3),
                reader.GetBoolean(4),
                reader.GetBoolean(5)));
        }
        return rows.Count switch
        {
            0 => null,
            1 => rows[0],
            _ => throw new InvalidDataException(
                "A character has multiple active pet owner-Merge rows.")
        };
    }

    private async Task InsertOwnerMergeLifecycleAuditAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CommandSubject subject,
        OwnerMergeLifecycleRow active,
        int energy,
        long revision,
        PetOwnerMergeEndReason reason,
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
                @petId, @petId, 'owner_merge', 'committed',
                jsonb_build_object(
                    'currentEnergy', @beforeEnergy,
                    'revision', @beforeRevision,
                    'contributesToCharacter', true
                ),
                jsonb_build_object(
                    'currentEnergy', @afterEnergy,
                    'revision', @afterRevision,
                    'contributesToCharacter', false
                ),
                '[]'::jsonb, @reasonCode
            );
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("requestId", Guid.NewGuid());
        command.Parameters.AddWithValue(
            "characterId",
            subject.CharacterId);
        command.Parameters.AddWithValue("petId", active.PetId);
        command.Parameters.AddWithValue(
            "beforeEnergy",
            active.CurrentEnergy);
        command.Parameters.AddWithValue(
            "beforeRevision",
            active.Revision);
        command.Parameters.AddWithValue("afterEnergy", energy);
        command.Parameters.AddWithValue("afterRevision", revision);
        command.Parameters.AddWithValue("reasonCode", reason.ToString());
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidDataException(
                "Pet owner-Merge lifecycle audit was not inserted exactly once.");
        }
    }

    private static PetOwnerMergeLifecycleResult Validated(
        PetOwnerMergeLifecycleResult result)
    {
        result.Validate();
        return result;
    }

    private sealed record OwnerMergeLifecycleRow(
        long PetId,
        int CurrentEnergy,
        int MaximumEnergy,
        long Revision,
        bool IsCarried,
        bool IsSummoned);
}
