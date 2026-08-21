using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Pets;
using Npgsql;

namespace Godswar.Server.Infrastructure.Pets;

internal sealed partial class PostgresPetDurableCommandExecutor
{
    public async Task<PetOwnerMergeLifecycleResult> RestoreEnergyAsync(
        CommandSubject subject,
        PlayerOwnershipFence ownership,
        int energyPoints,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(energyPoints);

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

        var carried = await LockCarriedPetEnergyRowAsync(
            connection,
            transaction,
            subject.CharacterId,
            cancellationToken);
        if (carried is null || carried.ContributesToCharacter)
        {
            await transaction.CommitAsync(cancellationToken);
            await RequireCurrentOwnershipAsync(
                subject,
                ownership,
                cancellationToken);
            return Validated(new PetOwnerMergeLifecycleResult(
                PetOwnerMergeLifecycleStatus.NoRechargeTarget,
                PetId: 0,
                CurrentEnergy: 0,
                MaximumEnergy: 0,
                PetRevision: 0,
                IsCarried: false,
                IsSummoned: false));
        }

        if (carried.CurrentEnergy == carried.MaximumEnergy)
        {
            await transaction.CommitAsync(cancellationToken);
            await RequireCurrentOwnershipAsync(
                subject,
                ownership,
                cancellationToken);
            return Validated(new PetOwnerMergeLifecycleResult(
                PetOwnerMergeLifecycleStatus.EnergyAtMaximum,
                carried.PetId,
                carried.CurrentEnergy,
                carried.MaximumEnergy,
                carried.Revision,
                IsCarried: true,
                carried.IsSummoned));
        }

        var energy = checked((int)Math.Min(
            carried.MaximumEnergy,
            (long)carried.CurrentEnergy + energyPoints));
        await using var update = CreateCommand(
            """
            UPDATE public.character_pets
            SET current_energy = @energy,
                revision = revision + 1,
                updated_at = transaction_timestamp()
            WHERE id = @petId
              AND user_id = @characterId
              AND revision = @expectedRevision
              AND is_carried
              AND NOT contributes_to_character
            RETURNING revision;
            """,
            connection,
            transaction);
        update.Parameters.AddWithValue("energy", energy);
        update.Parameters.AddWithValue("petId", carried.PetId);
        update.Parameters.AddWithValue(
            "characterId",
            subject.CharacterId);
        update.Parameters.AddWithValue(
            "expectedRevision",
            carried.Revision);
        var nextRevision =
            await update.ExecuteScalarAsync(cancellationToken) as long? ??
            throw new InvalidDataException(
                "The carried pet changed during energy recovery.");

        await transaction.CommitAsync(cancellationToken);
        await RequireCurrentOwnershipAsync(
            subject,
            ownership,
            cancellationToken);
        return Validated(new PetOwnerMergeLifecycleResult(
            PetOwnerMergeLifecycleStatus.EnergyChanged,
            carried.PetId,
            energy,
            carried.MaximumEnergy,
            nextRevision,
            IsCarried: true,
            carried.IsSummoned));
    }

    private async Task RequireCurrentOwnershipAsync(
        CommandSubject subject,
        PlayerOwnershipFence ownership,
        CancellationToken cancellationToken) =>
        (await _ownershipGuard.ValidateCurrentAsync(
            subject,
            ownership,
            cancellationToken)).RequireCurrent();

    private async Task<CarriedPetEnergyRow?> LockCarriedPetEnergyRowAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            SELECT
                id, current_energy, maximum_energy,
                revision, is_summoned, contributes_to_character
            FROM public.character_pets
            WHERE user_id = @characterId
              AND is_carried
            ORDER BY id
            FOR UPDATE;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("characterId", characterId);
        var rows = new List<CarriedPetEnergyRow>(2);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var row = new CarriedPetEnergyRow(
                reader.GetInt64(0),
                reader.GetInt32(1),
                reader.GetInt32(2),
                reader.GetInt64(3),
                reader.GetBoolean(4),
                reader.GetBoolean(5));
            if (row.CurrentEnergy < 0 ||
                row.MaximumEnergy <= 0 ||
                row.CurrentEnergy > row.MaximumEnergy)
            {
                throw new InvalidDataException(
                    "The carried pet has invalid energy state.");
            }
            rows.Add(row);
        }

        return rows.Count switch
        {
            0 => null,
            1 => rows[0],
            _ => throw new InvalidDataException(
                "A character has multiple carried pets.")
        };
    }

    private sealed record CarriedPetEnergyRow(
        long PetId,
        int CurrentEnergy,
        int MaximumEnergy,
        long Revision,
        bool IsSummoned,
        bool ContributesToCharacter);
}
