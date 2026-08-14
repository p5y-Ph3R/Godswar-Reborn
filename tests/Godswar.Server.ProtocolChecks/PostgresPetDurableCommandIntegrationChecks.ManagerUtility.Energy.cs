using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class
    PostgresPetDurableCommandIntegrationChecks
{
    private static async Task AssertUnsealEnergyAuditAsync(
        NpgsqlDataSource dataSource,
        int characterId)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT
                (before_state #>> '{pet,CurrentEnergy}')::integer,
                (before_state #>> '{pet,MaximumEnergy}')::integer,
                (after_state #>> '{pet,CurrentEnergy}')::integer,
                (after_state #>> '{pet,MaximumEnergy}')::integer
            FROM public.pet_operation_audit
            WHERE user_id_snapshot = @characterId
              AND operation = 'unseal'
              AND outcome = 'committed';
            """);
        command.Parameters.AddWithValue("characterId", characterId);
        await using var reader = await command.ExecuteReaderAsync();
        Check.True(
            await reader.ReadAsync() &&
            reader.GetInt32(0) == 31 &&
            reader.GetInt32(1) == 100 &&
            reader.GetInt32(2) == 100 &&
            reader.GetInt32(3) == 100 &&
            !await reader.ReadAsync(),
            "committed Unseal audit pins the exact 31/100 to 100/100 " +
            "energy reset");
    }
}
