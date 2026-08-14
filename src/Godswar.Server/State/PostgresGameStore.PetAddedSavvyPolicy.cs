namespace Godswar.Server.State;

internal sealed partial class PostgresGameStore
{
    internal const string PetSavvyBaselineValidationSql =
        """
        SELECT pet.id
        FROM character_pets pet
        INNER JOIN pet_content_aptitude_definitions aptitude
            ON aptitude.revision = @petContentRevision
           AND aptitude.aptitude = pet.aptitude
        LEFT JOIN character_pet_stat_values stat
            ON stat.pet_id = pet.id
        WHERE pet.initial_savvy_baseline_total IS NOT NULL
           OR pet.initial_savvy_policy_version IS NOT NULL
           OR pet.rarity_added_savvy_baseline_total IS NOT NULL
           OR pet.rarity_added_savvy_policy_version IS NOT NULL
           OR pet.initial_savvy_source_version IS NOT NULL
        GROUP BY
            pet.id,
            pet.level,
            pet.initial_savvy_baseline_total,
            pet.initial_savvy_policy_version,
            pet.rarity_added_savvy_baseline_total,
            pet.rarity_added_savvy_policy_version,
            pet.initial_savvy_source_version,
            aptitude.minimum_initial_savvy,
            aptitude.maximum_initial_savvy,
            aptitude.minimum_added_savvy,
            aptitude.maximum_added_savvy
        HAVING count(stat.stat_code) <> 6
            OR count(DISTINCT stat.stat_code) <> 6
            OR pet.initial_savvy_baseline_total IS NULL
            OR (
                pet.initial_savvy_policy_version
                    IS DISTINCT FROM @initialPolicyVersion
                AND pet.initial_savvy_policy_version
                    IS DISTINCT FROM @migratedLegacySavvyPolicyVersion
            )
            OR pet.rarity_added_savvy_baseline_total IS NULL
            OR (
                pet.rarity_added_savvy_policy_version
                    IS DISTINCT FROM @initialPolicyVersion
                AND pet.rarity_added_savvy_policy_version
                    IS DISTINCT FROM @migratedLegacySavvyPolicyVersion
            )
            OR pet.initial_savvy_policy_version
                IS DISTINCT FROM pet.rarity_added_savvy_policy_version
            OR pet.initial_savvy_source_version
                IS DISTINCT FROM @initialSourceVersion
            OR pet.initial_savvy_baseline_total
                IS DISTINCT FROM pet.rarity_added_savvy_baseline_total
            OR (
                pet.initial_savvy_policy_version = @initialPolicyVersion
                AND pet.rarity_added_savvy_baseline_total NOT BETWEEN
                    aptitude.minimum_initial_savvy AND
                    aptitude.maximum_initial_savvy
            )
            OR (
                pet.initial_savvy_policy_version =
                    @migratedLegacySavvyPolicyVersion
                AND pet.rarity_added_savvy_baseline_total NOT BETWEEN
                    aptitude.minimum_added_savvy AND
                    aptitude.maximum_added_savvy
            )
            OR count(*) FILTER (
                WHERE stat.birth_initial_savvy IS NULL
                   OR stat.rarity_added_savvy IS NULL
                   OR stat.base_growth_rate <= 0
                   OR stat.growth_acceleration < 0
                   OR stat.birth_initial_savvy
                        IS DISTINCT FROM stat.rarity_added_savvy
                   OR stat.initial_savvy <= 0
                   OR stat.added_savvy IS DISTINCT FROM
                        (
                            stat.base_growth_rate +
                            stat.growth_acceleration
                        ) * pet.level
            ) > 0
            OR COALESCE(sum(stat.rarity_added_savvy), 0)
                <> pet.rarity_added_savvy_baseline_total
            OR COALESCE(sum(stat.birth_initial_savvy), 0)
                <> pet.initial_savvy_baseline_total
            OR COALESCE(sum(stat.initial_savvy), 0)
                < pet.initial_savvy_baseline_total
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
                aptitude
            FROM pet_content_aptitude_definitions
            WHERE revision = @petContentRevision
            ORDER BY aptitude;
            """);
        command.Parameters.AddWithValue(
            "petContentRevision",
            PetContent.Revision.Sha256);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);

        foreach (var expected in PetContent.Aptitudes)
        {
            if (!await reader.ReadAsync(cancellationToken) ||
                reader.GetInt16(0) != expected.Aptitude ||
                reader.GetInt32(1) != expected.MinimumAddedSavvy ||
                reader.GetInt32(2) != expected.MaximumAddedSavvy ||
                reader.GetInt16(3) != expected.Aptitude)
            {
                throw new InvalidDataException(
                    $"Published pet added-savvy policy does not match aptitude {expected.Aptitude}.");
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
            "initialPolicyVersion",
            PetContent.Settings.InitialSavvyPolicyVersion);
        command.Parameters.AddWithValue(
            "migratedLegacySavvyPolicyVersion",
            PetSavvyRuntimeSemantics.LegacyHighSavvyPolicyVersion);
        command.Parameters.AddWithValue(
            "initialSourceVersion",
            PetSavvyRuntimeSemantics.SourceVersion);
        command.Parameters.AddWithValue(
            "petContentRevision",
            PetContent.Revision.Sha256);
        var invalidPetId =
            await command.ExecuteScalarAsync(cancellationToken);
        if (invalidPetId is not null && invalidPetId is not DBNull)
        {
            throw new InvalidDataException(
                $"Pet {invalidPetId} has invalid basic- or rarity-savvy baseline state.");
        }
    }
}
