using Godswar.Server.Application.Pets;
using Npgsql;

namespace Godswar.Server.Infrastructure.Pets;

internal sealed partial class PostgresPetDurableCommandExecutor
{
    private async Task<PetManagerGrowthEvidence> ReadEffectiveGrowthAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long petId,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            SELECT stat_code, base_growth_rate + growth_acceleration
            FROM public.character_pet_stat_values
            WHERE pet_id = @petId
            ORDER BY stat_code
            FOR UPDATE;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("petId", petId);
        var values = new decimal[6];
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        for (var index = 0; index < values.Length; index++)
        {
            if (!await reader.ReadAsync(cancellationToken) ||
                reader.GetInt16(0) != index + 1)
            {
                throw new InvalidDataException(
                    "Pet Growth check requires six ordered stat rows.");
            }
            values[index] = reader.GetDecimal(1);
        }
        if (await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidDataException(
                "Pet Growth check found extra stat rows.");
        }
        return new(
            values[0], values[1], values[2],
            values[3], values[4], values[5]);
    }

    private async Task<long> MarkGrowthRevealedAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        LockedUtilityPet pet,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            UPDATE public.character_pets
            SET growth_revealed = true,
                revision = revision + 1,
                updated_at = transaction_timestamp()
            WHERE id = @petId
              AND user_id = @characterId
              AND revision = @revision
              AND activity_state = 'owned'
              AND is_carried
              AND is_summoned
            RETURNING revision;
            """,
            connection,
            transaction);
        AddUtilityPetRevisionParameters(command, characterId, pet);
        return RequireNextRevision(
            await command.ExecuteScalarAsync(cancellationToken),
            pet.Revision,
            "Growth-check pet");
    }

    private async Task<long> UpdateUtilityPetSexAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        LockedUtilityPet pet,
        byte sex,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            UPDATE public.character_pets
            SET sex = @sex,
                revision = revision + 1,
                updated_at = transaction_timestamp()
            WHERE id = @petId
              AND user_id = @characterId
              AND revision = @revision
              AND activity_state = 'owned'
              AND is_carried
              AND is_summoned
              AND bound
              AND NOT contributes_to_character
            RETURNING revision;
            """,
            connection,
            transaction);
        AddUtilityPetRevisionParameters(command, characterId, pet);
        command.Parameters.AddWithValue("sex", checked((short)sex));
        return RequireNextRevision(
            await command.ExecuteScalarAsync(cancellationToken),
            pet.Revision,
            "gender-change pet");
    }

    private static void AddUtilityPetRevisionParameters(
        NpgsqlCommand command,
        int characterId,
        LockedUtilityPet pet)
    {
        command.Parameters.AddWithValue("petId", pet.PetId);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("revision", pet.Revision);
    }

    private static long RequireNextRevision(
        object? value,
        long expectedRevision,
        string operation) =>
        value is long revision &&
            revision == checked(expectedRevision + 1)
                ? revision
                : throw new InvalidDataException(
                    $"The {operation} revision was not advanced once.");
}
