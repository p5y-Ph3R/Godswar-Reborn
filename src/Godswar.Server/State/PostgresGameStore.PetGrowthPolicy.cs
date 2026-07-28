namespace Godswar.Server.State;

internal sealed partial class PostgresGameStore
{
    private async Task ValidatePetGrowthPolicyAsync(
        CancellationToken cancellationToken)
    {
        await using var command = _dataSource.CreateCommand(
            """
            SELECT
                aptitude,
                minimum_total_growth,
                maximum_total_growth,
                maximum_stat_deviation,
                growth_policy_version
            FROM pet_aptitude_templates
            ORDER BY aptitude;
            """);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);

        foreach (var expected in PetGrowthPolicy.All)
        {
            if (!await reader.ReadAsync(cancellationToken) ||
                reader.GetInt16(0) != expected.AptitudeValue ||
                reader.GetDecimal(1) != expected.MinimumTotalGrowth ||
                reader.GetDecimal(2) != expected.MaximumTotalGrowth ||
                reader.GetDecimal(3) !=
                    expected.MaximumStatDeviationFraction ||
                !string.Equals(
                    reader.GetString(4),
                    PetGrowthPolicy.Version,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Persisted pet growth policy does not match runtime aptitude {expected.AptitudeValue}.");
            }
        }

        if (await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidDataException(
                "Persisted pet growth policy contains unexpected aptitude rows.");
        }
    }

    private async Task ValidatePetGrowthStateAsync(
        CancellationToken cancellationToken)
    {
        await using var command = _dataSource.CreateCommand(
            """
            SELECT pet.id
            FROM character_pets pet
            INNER JOIN pet_aptitude_templates aptitude
                ON aptitude.aptitude = pet.aptitude
            LEFT JOIN character_pet_stat_values stat
                ON stat.pet_id = pet.id
            GROUP BY
                pet.id,
                aptitude.minimum_total_growth,
                aptitude.maximum_total_growth
            HAVING count(stat.stat_code) <> 6
                OR count(DISTINCT stat.stat_code) <> 6
                OR count(*) FILTER (
                    WHERE stat.base_growth_rate <= 0
                ) > 0
                OR COALESCE(sum(stat.base_growth_rate), 0)
                    < aptitude.minimum_total_growth
                OR COALESCE(sum(stat.base_growth_rate), 0)
                    > aptitude.maximum_total_growth
            ORDER BY pet.id
            LIMIT 1;
            """);
        var invalidPetId =
            await command.ExecuteScalarAsync(cancellationToken);
        if (invalidPetId is not null && invalidPetId is not DBNull)
        {
            throw new InvalidDataException(
                $"Pet {invalidPetId} does not have six positive authoritative growth values inside its aptitude bracket.");
        }
    }
}
