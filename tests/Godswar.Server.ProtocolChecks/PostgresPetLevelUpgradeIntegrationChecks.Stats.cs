using System.Text.Json;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresPetLevelUpgradeIntegrationChecks
{
    private static async Task InsertPetStatsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long petId,
        short level)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO public.character_pet_stat_values (
                pet_id,
                stat_code,
                initial_savvy,
                added_savvy,
                base_growth_rate,
                birth_initial_savvy,
                rarity_added_savvy,
                growth_acceleration,
                revision
            )
            SELECT
                @petId,
                stat_code,
                60 + stat_code,
                (CASE
                    WHEN stat_code = 2 THEN 9
                    ELSE stat_code + 0.125
                END + 0.5) * @level,
                CASE
                    WHEN stat_code = 2 THEN 9
                    ELSE stat_code + 0.125
                END,
                47 + stat_code,
                47 + stat_code,
                0.5,
                1000 + stat_code
            FROM generate_series(1, 6) AS stat(stat_code);
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("petId", petId);
        command.Parameters.AddWithValue("level", level);
        Check.Equal(
            6,
            await command.ExecuteNonQueryAsync(),
            "pet level fixture creates all six authoritative stat rows");
    }

    private static async Task MakePetStatsMalformedAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long petId)
    {
        await using var command = new NpgsqlCommand(
            """
            UPDATE public.character_pet_stat_values
            SET base_growth_rate = 0
            WHERE pet_id = @petId
              AND stat_code = 1;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("petId", petId);
        Check.Equal(
            1,
            await command.ExecuteNonQueryAsync(),
            "malformed pet fixture zeros one exact Growth baseline");
    }

    private static async Task<IReadOnlyList<PetStatState>>
        ReadPetStatsAsync(
            string connectionString,
            long petId)
    {
        await using var connection =
            new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
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
            FROM public.character_pet_stat_values
            WHERE pet_id = @petId
            ORDER BY stat_code;
            """,
            connection);
        command.Parameters.AddWithValue("petId", petId);

        var rows = new List<PetStatState>(6);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new PetStatState(
                reader.GetInt16(0),
                reader.GetDecimal(1),
                reader.GetDecimal(2),
                reader.GetDecimal(3),
                reader.GetDecimal(4),
                reader.GetDecimal(5),
                reader.GetDecimal(6),
                reader.GetInt64(7)));
        }

        Check.Equal(
            6,
            rows.Count,
            $"pet {petId} retains all six stat rows");
        return rows;
    }

    private static IReadOnlyList<PetStatState> AdvanceStatsOneLevel(
        IReadOnlyList<PetStatState> before) =>
        before
            .Select(static stat => stat with
            {
                AddedSavvy =
                    (stat.BaseGrowthRate + stat.GrowthAcceleration) * 2,
                Revision = stat.Revision + 1
            })
            .ToArray();

    private static PetSavvy ToPetSavvy(
        IReadOnlyList<PetStatState> stats)
    {
        Check.Equal(
            6,
            stats.Count,
            "pet savvy conversion requires six ordered stat values");
        return new PetSavvy(
            stats[0].InitialSavvy,
            stats[1].InitialSavvy,
            stats[2].InitialSavvy,
            stats[3].InitialSavvy,
            stats[4].InitialSavvy,
            stats[5].InitialSavvy);
    }

    private static async Task AssertCommittedStatAuditAsync(
        string connectionString,
        PetLevelFixture fixture,
        IReadOnlyList<PetStatState> expectedBefore,
        IReadOnlyList<PetStatState> expectedAfter)
    {
        var audit = await ReadStatAuditAsync(
            connectionString,
            fixture.OwnerCharacterId,
            fixture.SuccessPetId,
            "committed");
        Check.True(
            audit.Before.SequenceEqual(expectedBefore),
            "committed audit captures the exact before stat vector");
        Check.True(
            audit.After.SequenceEqual(expectedAfter),
            "committed audit captures the exact after stat vector");
    }

    private static async Task AssertRejectedStatAuditAsync(
        string connectionString,
        PetLevelFixture fixture,
        long petId,
        IReadOnlyList<PetStatState> expected)
    {
        var audit = await ReadStatAuditAsync(
            connectionString,
            fixture.OwnerCharacterId,
            petId,
            "rejected");
        Check.True(
            audit.Before.SequenceEqual(expected) &&
            audit.After.SequenceEqual(expected),
            "rejected level-up audit preserves the exact stat vector");
    }

    private static async Task AssertRaceStatAuditsAsync(
        string connectionString,
        PetLevelFixture fixture,
        IReadOnlyList<PetStatState> expectedBefore,
        IReadOnlyList<PetStatState> expectedAfter)
    {
        var committed = await ReadStatAuditAsync(
            connectionString,
            fixture.OwnerCharacterId,
            fixture.RacePetId,
            "committed");
        Check.True(
            committed.Before.SequenceEqual(expectedBefore) &&
            committed.After.SequenceEqual(expectedAfter),
            "race winner audits one exact stat progression");

        var rejected = await ReadStatAuditAsync(
            connectionString,
            fixture.OwnerCharacterId,
            fixture.RacePetId,
            "rejected");
        Check.True(
            rejected.Before.SequenceEqual(expectedAfter) &&
            rejected.After.SequenceEqual(expectedAfter),
            "race loser audits the already-committed stat vector unchanged");
    }

    private static async Task<PetStatAuditState> ReadStatAuditAsync(
        string connectionString,
        int characterId,
        long petId,
        string outcome)
    {
        await using var connection =
            new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT
                before_state -> 'Stats',
                after_state -> 'Stats'
            FROM public.pet_operation_audit
            WHERE user_id_snapshot = @characterId
              AND pet_id_snapshot = @petId
              AND operation = 'level_up'
              AND outcome = @outcome;
            """,
            connection);
        command.Parameters.AddWithValue(
            "characterId",
            characterId);
        command.Parameters.AddWithValue("petId", petId);
        command.Parameters.AddWithValue("outcome", outcome);

        await using var reader = await command.ExecuteReaderAsync();
        Check.True(
            await reader.ReadAsync(),
            $"{outcome} level-up audit carries stat vectors");
        var before = DeserializeAuditStats(reader.GetString(0));
        var after = DeserializeAuditStats(reader.GetString(1));
        Check.True(
            !await reader.ReadAsync(),
            $"{outcome} level-up has one exact audit row");
        return new PetStatAuditState(before, after);
    }

    private static async Task<long> ReadPetAuditCountAsync(
        string connectionString,
        long petId)
    {
        await using var connection =
            new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT count(*)
            FROM public.pet_operation_audit
            WHERE pet_id_snapshot = @petId
              AND operation = 'level_up';
            """,
            connection);
        command.Parameters.AddWithValue("petId", petId);
        return (long)(await command.ExecuteScalarAsync()
                      ?? throw new InvalidOperationException(
                          "Pet audit count returned null."));
    }

    private static IReadOnlyList<PetStatState> DeserializeAuditStats(
        string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement
            .EnumerateArray()
            .Select(static stat => new PetStatState(
                stat.GetProperty("StatCode").GetInt16(),
                stat.GetProperty("InitialSavvy").GetDecimal(),
                stat.GetProperty("AddedSavvy").GetDecimal(),
                stat.GetProperty("BaseGrowthRate").GetDecimal(),
                stat.GetProperty("BirthInitialSavvy").GetDecimal(),
                stat.GetProperty("RarityAddedSavvy").GetDecimal(),
                stat.GetProperty("GrowthAcceleration").GetDecimal(),
                stat.GetProperty("Revision").GetInt64()))
            .ToArray();
    }

    private sealed record PetStatState(
        short StatCode,
        decimal InitialSavvy,
        decimal AddedSavvy,
        decimal BaseGrowthRate,
        decimal BirthInitialSavvy,
        decimal RarityAddedSavvy,
        decimal GrowthAcceleration,
        long Revision);

    private sealed record PetStatAuditState(
        IReadOnlyList<PetStatState> Before,
        IReadOnlyList<PetStatState> After);
}
