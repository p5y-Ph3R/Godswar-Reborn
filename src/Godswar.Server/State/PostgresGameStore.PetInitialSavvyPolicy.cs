namespace Godswar.Server.State;

internal sealed partial class PostgresGameStore
{
    internal const string PetInitialSavvyStateValidationSql =
        """
        SELECT pet.id
        FROM character_pets pet
        INNER JOIN pet_content_aptitude_definitions aptitude
            ON aptitude.revision = @petContentRevision
           AND aptitude.aptitude = pet.aptitude
        LEFT JOIN character_pet_stat_values stat
            ON stat.pet_id = pet.id
        GROUP BY
            pet.id,
            pet.initial_savvy_baseline_total,
            pet.initial_savvy_policy_version,
            aptitude.minimum_initial_savvy,
            aptitude.maximum_initial_savvy,
            aptitude.minimum_added_savvy,
            aptitude.maximum_added_savvy
        HAVING count(stat.stat_code) <> 6
            OR count(DISTINCT stat.stat_code) <> 6
            OR (
                pet.initial_savvy_baseline_total IS NULL
                AND COALESCE(sum(stat.initial_savvy), 0) = 0
            )
            OR (
                pet.initial_savvy_baseline_total IS NOT NULL
                AND (
                    (
                        pet.initial_savvy_policy_version
                            IS DISTINCT FROM @initialPolicyVersion
                        AND pet.initial_savvy_policy_version
                            IS DISTINCT FROM @migratedLegacySavvyPolicyVersion
                    )
                    OR (
                        pet.initial_savvy_policy_version =
                            @initialPolicyVersion
                        AND pet.initial_savvy_baseline_total NOT BETWEEN
                            aptitude.minimum_initial_savvy AND
                            aptitude.maximum_initial_savvy
                    )
                    OR (
                        pet.initial_savvy_policy_version =
                            @migratedLegacySavvyPolicyVersion
                        AND pet.initial_savvy_baseline_total NOT BETWEEN
                            aptitude.minimum_added_savvy AND
                            aptitude.maximum_added_savvy
                    )
                    OR count(*) FILTER (
                        WHERE stat.initial_savvy <= 0
                    ) > 0
                    OR COALESCE(sum(stat.initial_savvy), 0)
                        < pet.initial_savvy_baseline_total
                )
            )
        ORDER BY pet.id
        LIMIT 1;
        """;

    private async Task ValidatePetInitialSavvyPolicyAsync(
        CancellationToken cancellationToken)
    {
        await using var command = _dataSource.CreateCommand(
            """
            SELECT
                aptitude,
                minimum_initial_savvy,
                maximum_initial_savvy,
                maximum_initial_savvy_stat_deviation
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
                reader.GetInt32(1) != expected.MinimumInitialSavvy ||
                reader.GetInt32(2) != expected.MaximumInitialSavvy ||
                reader.GetDecimal(3) !=
                    expected.MaximumInitialSavvyStatDeviation)
            {
                throw new InvalidDataException(
                    $"Published pet initial-savvy policy does not match aptitude {expected.Aptitude}.");
            }
        }

        if (await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidDataException(
                "Persisted pet initial-savvy policy contains unexpected aptitude rows.");
        }
    }

    private async Task ValidatePetInitialSavvyStateAsync(
        CancellationToken cancellationToken)
    {
        await using var command = _dataSource.CreateCommand(
            PetInitialSavvyStateValidationSql);
        command.Parameters.AddWithValue(
            "initialPolicyVersion",
            PetContent.Settings.InitialSavvyPolicyVersion);
        command.Parameters.AddWithValue(
            "migratedLegacySavvyPolicyVersion",
            PetSavvyRuntimeSemantics.LegacyHighSavvyPolicyVersion);
        command.Parameters.AddWithValue(
            "petContentRevision",
            PetContent.Revision.Sha256);
        var invalidPetId =
            await command.ExecuteScalarAsync(cancellationToken);
        if (invalidPetId is not null && invalidPetId is not DBNull)
        {
            throw new InvalidDataException(
                $"Pet {invalidPetId} has invalid initial-savvy baseline or progression state.");
        }
    }
}
