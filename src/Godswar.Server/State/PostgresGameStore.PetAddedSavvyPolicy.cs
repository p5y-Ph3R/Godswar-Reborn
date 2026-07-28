namespace Godswar.Server.State;

internal sealed partial class PostgresGameStore
{
    internal const string PetSavvyBaselineValidationSql =
        """
        SELECT pet.id
        FROM character_pets pet
        INNER JOIN pet_aptitude_templates aptitude
            ON aptitude.aptitude = pet.aptitude
        LEFT JOIN character_pet_stat_values stat
            ON stat.pet_id = pet.id
        WHERE pet.rarity_added_savvy_baseline_total IS NOT NULL
           OR pet.rarity_added_savvy_policy_version IS NOT NULL
           OR pet.initial_savvy_source_version IS NOT NULL
        GROUP BY
            pet.id,
            pet.rarity_added_savvy_baseline_total,
            pet.rarity_added_savvy_policy_version,
            pet.initial_savvy_source_version,
            aptitude.minimum_added_savvy,
            aptitude.maximum_added_savvy
        HAVING count(stat.stat_code) <> 6
            OR count(DISTINCT stat.stat_code) <> 6
            OR pet.rarity_added_savvy_baseline_total IS NULL
            OR pet.rarity_added_savvy_policy_version
                IS DISTINCT FROM @addedPolicyVersion
            OR pet.initial_savvy_source_version
                IS DISTINCT FROM @initialSourceVersion
            OR pet.rarity_added_savvy_baseline_total
                < aptitude.minimum_added_savvy
            OR pet.rarity_added_savvy_baseline_total
                > aptitude.maximum_added_savvy
            OR count(*) FILTER (
                WHERE stat.birth_initial_savvy IS NULL
                   OR stat.rarity_added_savvy IS NULL
                   OR stat.birth_initial_savvy
                        IS DISTINCT FROM stat.base_growth_rate
                   OR stat.initial_savvy
                        < stat.birth_initial_savvy
                   OR stat.added_savvy
                        < stat.rarity_added_savvy
            ) > 0
            OR count(DISTINCT stat.rarity_added_savvy) < 2
            OR COALESCE(sum(stat.rarity_added_savvy), 0)
                <> pet.rarity_added_savvy_baseline_total
        ORDER BY pet.id
        LIMIT 1;
        """;

    private async Task ValidatePetAddedSavvyPolicyAsync(
        CancellationToken cancellationToken)
    {
        await using var command = _dataSource.CreateCommand(
            """
            SELECT
                aptitude,
                minimum_added_savvy,
                maximum_added_savvy,
                added_savvy_policy_version
            FROM pet_aptitude_templates
            ORDER BY aptitude;
            """);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);

        foreach (var expected in PetAddedSavvyPolicy.All)
        {
            if (!await reader.ReadAsync(cancellationToken) ||
                reader.GetInt16(0) != expected.AptitudeValue ||
                reader.GetInt32(1) != expected.MinimumTotalSavvy ||
                reader.GetInt32(2) != expected.MaximumTotalSavvy ||
                !string.Equals(
                    reader.GetString(3),
                    PetAddedSavvyPolicy.Version,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Persisted pet added-savvy policy does not match runtime aptitude {expected.AptitudeValue}.");
            }
        }

        if (await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidDataException(
                "Persisted pet added-savvy policy contains unexpected aptitude rows.");
        }
    }

    private async Task ValidatePetSavvyBaselineStateAsync(
        CancellationToken cancellationToken)
    {
        await using var command = _dataSource.CreateCommand(
            PetSavvyBaselineValidationSql);
        command.Parameters.AddWithValue(
            "addedPolicyVersion",
            PetAddedSavvyPolicy.Version);
        command.Parameters.AddWithValue(
            "initialSourceVersion",
            "growth-x1-v1");
        var invalidPetId =
            await command.ExecuteScalarAsync(cancellationToken);
        if (invalidPetId is not null && invalidPetId is not DBNull)
        {
            throw new InvalidDataException(
                $"Pet {invalidPetId} has invalid basic- or rarity-savvy baseline state.");
        }
    }
}
