using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PetEggHatchPersistenceChecks
{
    private static async Task AssertAuditAsync(
        string connectionString,
        int characterId,
        params PetEggHatchResult[] expectedResults)
    {
        await using var connection =
            new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT
                pet_id_snapshot,
                (consumed_items -> 0 ->> 'item_id')::integer,
                (before_state ->> 'egg_quality')::smallint,
                (after_state ->> 'aptitude')::smallint,
                (after_state ->> 'sex')::smallint,
                (after_state ->> 'remaining_lifetime')::integer,
                (after_state ->> 'bound')::boolean,
                after_state ->> 'initial_savvy_source',
                (after_state ->> 'total_initial_savvy')::numeric,
                (after_state #>> '{initial_savvy,agility}')::numeric,
                (after_state #>> '{initial_savvy,strength}')::numeric,
                (after_state #>> '{initial_savvy,accuracy}')::numeric,
                (after_state #>> '{initial_savvy,technique}')::numeric,
                (after_state #>> '{initial_savvy,wisdom}')::numeric,
                (after_state #>> '{initial_savvy,luck}')::numeric,
                after_state ->> 'added_savvy_policy',
                (after_state ->> 'total_added_savvy')::integer,
                (after_state #>> '{added_savvy,agility}')::numeric,
                (after_state #>> '{added_savvy,strength}')::numeric,
                (after_state #>> '{added_savvy,accuracy}')::numeric,
                (after_state #>> '{added_savvy,technique}')::numeric,
                (after_state #>> '{added_savvy,wisdom}')::numeric,
                (after_state #>> '{added_savvy,luck}')::numeric,
                after_state ->> 'growth_policy',
                (after_state ->> 'total_growth')::numeric,
                (after_state #>> '{base_growth,agility}')::numeric,
                (after_state #>> '{base_growth,strength}')::numeric,
                (after_state #>> '{base_growth,accuracy}')::numeric,
                (after_state #>> '{base_growth,technique}')::numeric,
                (after_state #>> '{base_growth,wisdom}')::numeric,
                (after_state #>> '{base_growth,luck}')::numeric
            FROM pet_operation_audit
            WHERE user_id_snapshot = @characterId
              AND operation = 'hatch'
              AND outcome = 'committed'
            ORDER BY id;
            """,
            connection);
        command.Parameters.AddWithValue("characterId", characterId);

        var rows = new Dictionary<long, HatchAuditRow>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var row = new HatchAuditRow(
                reader.GetInt64(0),
                reader.GetInt32(1),
                reader.GetInt16(2),
                reader.GetInt16(3),
                reader.GetInt16(4),
                reader.GetInt32(5),
                reader.GetBoolean(6),
                reader.GetString(7),
                reader.GetDecimal(8),
                new PetSavvy(
                    reader.GetDecimal(9),
                    reader.GetDecimal(10),
                    reader.GetDecimal(11),
                    reader.GetDecimal(12),
                    reader.GetDecimal(13),
                    reader.GetDecimal(14)),
                reader.GetString(15),
                reader.GetInt32(16),
                new PetSavvy(
                    reader.GetDecimal(17),
                    reader.GetDecimal(18),
                    reader.GetDecimal(19),
                    reader.GetDecimal(20),
                    reader.GetDecimal(21),
                    reader.GetDecimal(22)),
                reader.GetString(23),
                reader.GetDecimal(24),
                new PetSavvy(
                    reader.GetDecimal(25),
                    reader.GetDecimal(26),
                    reader.GetDecimal(27),
                    reader.GetDecimal(28),
                    reader.GetDecimal(29),
                    reader.GetDecimal(30)));
            rows.Add(row.PetId, row);
        }

        Check.Equal(
            expectedResults.Length,
            rows.Count,
            "every committed hatch is audited");
        foreach (var expected in expectedResults)
        {
            Check.True(
                rows.TryGetValue(expected.PetId, out var row),
                $"hatch audit references pet {expected.PetId}");
            Check.True(
                row!.ItemId == EggItemId &&
                row.EggQuality == (short)EggAptitude &&
                row.Aptitude == (short)expected.Aptitude,
                $"hatch audit {expected.PetId} records its egg rarity");
            Check.True(
                row.Sex is 0 or 1 &&
                row.RemainingLifetime > 0 &&
                row.IsBound,
                $"hatch audit {expected.PetId} records generated native state");

            Check.Equal(
                "growth-x1-v1",
                row.InitialSavvySource,
                $"hatch audit {expected.PetId} records its initial-savvy source");
            Check.Equal(
                expected.Growth!.TotalGrowth,
                row.TotalInitialSavvy,
                $"hatch audit {expected.PetId} records total initial savvy");
            Check.Equal(
                expected.InitialSavvy,
                row.InitialSavvy,
                $"hatch audit {expected.PetId} records all six initial-savvy values");

            var expectedAddedSavvy = expected.AddedSavvy
                ?? throw new InvalidOperationException(
                    $"Hatch result {expected.PetId} has no added savvy.");
            Check.Equal(
                PetAddedSavvyPolicy.Version,
                row.AddedSavvyPolicy,
                $"hatch audit {expected.PetId} records its added-savvy policy");
            Check.Equal(
                expectedAddedSavvy.TotalSavvy,
                row.TotalAddedSavvy,
                $"hatch audit {expected.PetId} records total added savvy");
            Check.Equal(
                expectedAddedSavvy.AddedSavvy,
                row.AddedSavvy,
                $"hatch audit {expected.PetId} records all six added-savvy values");

            var expectedGrowth = expected.Growth
                ?? throw new InvalidOperationException(
                    $"Hatch result {expected.PetId} has no growth.");
            Check.Equal(
                PetGrowthPolicy.Version,
                row.GrowthPolicy,
                $"hatch audit {expected.PetId} records its growth policy");
            Check.Equal(
                expectedGrowth.TotalGrowth,
                row.TotalGrowth,
                $"hatch audit {expected.PetId} records total growth");
            Check.Equal(
                expectedGrowth.BaseGrowthRates,
                row.BaseGrowth,
                $"hatch audit {expected.PetId} records all six growth values");
        }
    }

    private static decimal[] SavvyValues(PetSavvy values) =>
    [
        values.Agility,
        values.Strength,
        values.Accuracy,
        values.Technique,
        values.Wisdom,
        values.Luck
    ];

    private sealed record HatchAuditRow(
        long PetId,
        int ItemId,
        short EggQuality,
        short Aptitude,
        short Sex,
        int RemainingLifetime,
        bool IsBound,
        string InitialSavvySource,
        decimal TotalInitialSavvy,
        PetSavvy InitialSavvy,
        string AddedSavvyPolicy,
        int TotalAddedSavvy,
        PetSavvy AddedSavvy,
        string GrowthPolicy,
        decimal TotalGrowth,
        PetSavvy BaseGrowth);
}
