using Npgsql;

namespace Godswar.Server.State;

internal sealed partial class PostgresGameStore
{
    private const int PetLevelStatCount = 6;

    private static async Task<IReadOnlyList<PetLevelStatRow>>
        LockPetLevelStatsAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            PetLevelRow pet,
            CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT
                stat_code,
                initial_savvy,
                added_savvy,
                base_growth_rate,
                birth_initial_savvy,
                rarity_added_savvy,
                growth_acceleration,
                revision
            FROM character_pet_stat_values
            WHERE pet_id = @petId
            ORDER BY stat_code
            FOR UPDATE;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("petId", pet.PetId);

        var rows = new List<PetLevelStatRow>(PetLevelStatCount);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(ReadPetLevelStat(reader));
        }

        ValidatePetLevelStats(pet, rows, pet.Level);
        return rows;
    }

    private static async Task<IReadOnlyList<PetLevelStatRow>>
        PersistPetLevelStatGrowthAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            PetLevelRow pet,
            short nextLevel,
            CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            UPDATE character_pet_stat_values
            SET added_savvy =
                    (base_growth_rate + growth_acceleration) * @level,
                revision = revision + 1
            WHERE pet_id = @petId
            RETURNING
                stat_code,
                initial_savvy,
                added_savvy,
                base_growth_rate,
                birth_initial_savvy,
                rarity_added_savvy,
                growth_acceleration,
                revision;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("petId", pet.PetId);
        command.Parameters.AddWithValue("level", nextLevel);

        var rows = new List<PetLevelStatRow>(PetLevelStatCount);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(ReadPetLevelStat(reader));
        }

        rows.Sort(static (left, right) =>
            left.StatCode.CompareTo(right.StatCode));
        ValidatePetLevelStats(pet, rows, nextLevel);
        return rows;
    }

    private static PetLevelStatRow ReadPetLevelStat(
        NpgsqlDataReader reader) =>
        new(
            reader.GetInt16(0),
            reader.GetDecimal(1),
            reader.GetDecimal(2),
            reader.GetDecimal(3),
            reader.IsDBNull(4) ? null : reader.GetDecimal(4),
            reader.IsDBNull(5) ? null : reader.GetDecimal(5),
            reader.GetDecimal(6),
            reader.GetInt64(7));

    private static void ValidatePetLevelStats(
        PetLevelRow pet,
        IReadOnlyList<PetLevelStatRow> rows,
        short expectedLevel)
    {
        if (!string.Equals(
                pet.InitialSavvySourceVersion,
                PetSavvyRuntimeSemantics.SourceVersion,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Pet {pet.PetId} does not have the required " +
                $"{PetSavvyRuntimeSemantics.SourceVersion} savvy provenance.");
        }

        if (rows.Count != PetLevelStatCount)
        {
            throw new InvalidOperationException(
                $"Pet {pet.PetId} must have exactly {PetLevelStatCount} " +
                $"stat rows before it can level; found {rows.Count}.");
        }

        for (var index = 0; index < PetLevelStatCount; index++)
        {
            var row = rows[index];
            var expectedStatCode = checked((short)(index + 1));
            if (row.StatCode != expectedStatCode ||
                row.InitialSavvy <= 0 ||
                row.AddedSavvy < 0 ||
                row.BaseGrowthRate <= 0 ||
                row.BirthInitialSavvy is null or <= 0 ||
                row.RarityAddedSavvy is null or < 0 ||
                row.BirthInitialSavvy != row.RarityAddedSavvy ||
                row.GrowthAcceleration < 0 ||
                row.AddedSavvy !=
                    PetSavvyRuntimeSemantics.ResolveLevelScaledAdded(
                        expectedLevel,
                        row.BaseGrowthRate,
                        row.GrowthAcceleration) ||
                row.Revision < 0)
            {
                throw new InvalidOperationException(
                    $"Pet {pet.PetId} has malformed level-growth stat " +
                    $"data at stat code {row.StatCode}.");
            }
        }
        if (rows.Sum(static row => row.InitialSavvy) <
            rows.Sum(static row => row.BirthInitialSavvy!.Value))
        {
            throw new InvalidOperationException(
                $"Pet {pet.PetId} has malformed aggregate Basic Savvy data.");
        }
    }

    private static PetLevelAuditSnapshot CreatePetLevelAuditSnapshot(
        PetLevelRow row,
        IReadOnlyList<PetLevelStatRow> stats) =>
        new(
            row.Level,
            row.Experience,
            row.ActivityState,
            row.Revision,
            row.InitialSavvySourceVersion,
            stats.Select(static stat =>
                new PetLevelStatAuditState(
                    stat.StatCode,
                    stat.InitialSavvy,
                    stat.AddedSavvy,
                    stat.BaseGrowthRate,
                    stat.BirthInitialSavvy,
                    stat.RarityAddedSavvy,
                    stat.GrowthAcceleration,
                    stat.Revision))
                .ToArray());

    private static PetSavvy CreatePetLevelBasicSavvy(
        IReadOnlyList<PetLevelStatRow> stats)
    {
        if (stats.Count != PetLevelStatCount)
        {
            throw new InvalidOperationException(
                "A pet level result requires exactly six stat values.");
        }

        return new PetSavvy(
            stats[0].InitialSavvy,
            stats[1].InitialSavvy,
            stats[2].InitialSavvy,
            stats[3].InitialSavvy,
            stats[4].InitialSavvy,
            stats[5].InitialSavvy);
    }

    private sealed record PetLevelStatRow(
        short StatCode,
        decimal InitialSavvy,
        decimal AddedSavvy,
        decimal BaseGrowthRate,
        decimal? BirthInitialSavvy,
        decimal? RarityAddedSavvy,
        decimal GrowthAcceleration,
        long Revision);

    private sealed record PetLevelAuditSnapshot(
        short Level,
        long Experience,
        string ActivityState,
        long Revision,
        string? InitialSavvySourceVersion,
        IReadOnlyList<PetLevelStatAuditState> Stats);

    private sealed record PetLevelStatAuditState(
        short StatCode,
        decimal InitialSavvy,
        decimal AddedSavvy,
        decimal BaseGrowthRate,
        decimal? BirthInitialSavvy,
        decimal? RarityAddedSavvy,
        decimal GrowthAcceleration,
        long Revision);
}
