using Godswar.Server.Application.Zodiac;
using Godswar.Server.Infrastructure.Zodiac;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class
    PostgresZodiacSkillGridSelectionCommandIntegrationChecks
{
    private static async Task CheckDefenseSelectionEligibilityAsync(
        string connectionString,
        Fixture fixture,
        PostgresZodiacSkillGridSelectionCommandExecutor executor)
    {
        const int firstDefenseGrid = 8;
        const int firstEnemyKind = 10_025;
        var durable = await executor.ExecuteAsync(
            Envelope(
                fixture,
                Guid.NewGuid(),
                firstDefenseGrid,
                firstEnemyKind));
        Check.True(
            durable.Disposition ==
                ZodiacSkillGridSelectionExecutionDisposition.Committed &&
            durable.Receipt?.Status ==
                ZodiacSkillGridSelectionReceiptStatus.Succeeded,
            "durable defense selection accepts an unlearned enemy skill");

        const int secondDefenseGrid = 12;
        const int secondEnemyKind = 20_028;
        await using (var compatibilityStore =
                     new PostgresGameStore(connectionString))
        {
            var compatibility =
                await compatibilityStore.SelectZodiacSkillGridAsync(
                    fixture.AccountId,
                    fixture.CharacterId,
                    secondDefenseGrid,
                    secondEnemyKind);
            Check.True(
                compatibility?.Committed == true,
                "raw compatibility defense selection accepts an " +
                "unlearned enemy skill");
        }

        await using var connection =
            new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT grid_index, selected_skill_id
            FROM public.character_zodiac_skill_grids
            WHERE user_id = @characterId
              AND grid_index IN (8, 12)
            ORDER BY grid_index;
            """,
            connection);
        command.Parameters.AddWithValue(
            "characterId",
            fixture.CharacterId);
        await using var reader = await command.ExecuteReaderAsync();
        Check.True(
            await reader.ReadAsync() &&
            reader.GetInt16(0) == firstDefenseGrid &&
            reader.GetInt32(1) == firstEnemyKind &&
            await reader.ReadAsync() &&
            reader.GetInt16(0) == secondDefenseGrid &&
            reader.GetInt32(1) == secondEnemyKind &&
            !await reader.ReadAsync(),
            "both PostgreSQL selection paths persist defense choices");
    }
}
