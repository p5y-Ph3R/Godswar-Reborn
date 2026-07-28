using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresPetLevelUpgradeIntegrationChecks
{
    private static async Task AssertCommittedAuditAsync(
        string connectionString,
        PetLevelFixture fixture)
    {
        await using var connection =
            new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT
                user_id,
                user_id_snapshot,
                pet_id,
                pet_id_snapshot,
                operation,
                outcome,
                reason_code,
                (before_state ->> 'Level')::smallint,
                (before_state ->> 'Experience')::bigint,
                (before_state ->> 'ActivityState'),
                (before_state ->> 'Revision')::bigint,
                (after_state ->> 'Level')::smallint,
                (after_state ->> 'Experience')::bigint,
                (after_state ->> 'ActivityState'),
                (after_state ->> 'Revision')::bigint
            FROM public.pet_operation_audit
            WHERE user_id_snapshot = @characterId
              AND pet_id_snapshot = @petId
              AND operation = 'level_up'
              AND outcome = 'committed';
            """,
            connection);
        command.Parameters.AddWithValue(
            "characterId",
            fixture.OwnerCharacterId);
        command.Parameters.AddWithValue(
            "petId",
            fixture.SuccessPetId);
        await using var reader = await command.ExecuteReaderAsync();
        Check.True(
            await reader.ReadAsync(),
            "committed pet level-up has an audit row");
        Check.Equal(
            fixture.OwnerCharacterId,
            reader.GetInt32(0),
            "pet level audit retains owner FK");
        Check.Equal(
            fixture.OwnerCharacterId,
            reader.GetInt32(1),
            "pet level audit snapshots owner");
        Check.Equal(
            fixture.SuccessPetId,
            reader.GetInt64(2),
            "pet level audit retains pet FK");
        Check.Equal(
            fixture.SuccessPetId,
            reader.GetInt64(3),
            "pet level audit snapshots pet");
        Check.Equal("level_up", reader.GetString(4), "audit operation");
        Check.Equal("committed", reader.GetString(5), "audit outcome");
        Check.True(reader.IsDBNull(6), "committed audit has no reason");
        Check.Equal((short)1, reader.GetInt16(7), "audit before level");
        Check.Equal(2_000L, reader.GetInt64(8), "audit before EXP");
        Check.Equal("owned", reader.GetString(9), "audit before state");
        Check.Equal(7L, reader.GetInt64(10), "audit before revision");
        Check.Equal((short)2, reader.GetInt16(11), "audit after level");
        Check.Equal(500L, reader.GetInt64(12), "audit after EXP");
        Check.Equal("owned", reader.GetString(13), "audit after state");
        Check.Equal(8L, reader.GetInt64(14), "audit after revision");
        Check.True(
            !await reader.ReadAsync(),
            "one committed level-up writes exactly one committed audit");
    }

    private static async Task AssertRaceAuditsAsync(
        string connectionString,
        PetLevelFixture fixture)
    {
        await using var connection =
            new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT
                outcome,
                reason_code,
                (before_state ->> 'Level')::smallint,
                (before_state ->> 'Experience')::bigint,
                (after_state ->> 'Level')::smallint,
                (after_state ->> 'Experience')::bigint
            FROM public.pet_operation_audit
            WHERE user_id_snapshot = @characterId
              AND pet_id_snapshot = @petId
              AND operation = 'level_up'
            ORDER BY outcome;
            """,
            connection);
        command.Parameters.AddWithValue(
            "characterId",
            fixture.OwnerCharacterId);
        command.Parameters.AddWithValue("petId", fixture.RacePetId);

        var rows = new List<RaceAuditRow>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new RaceAuditRow(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.GetInt16(2),
                reader.GetInt64(3),
                reader.GetInt16(4),
                reader.GetInt64(5)));
        }

        Check.Equal(2, rows.Count, "duplicate attempts are both audited");
        var committed = rows.Single(static row =>
            row.Outcome == "committed");
        Check.True(
            committed.ReasonCode is null &&
            committed.BeforeLevel == 1 &&
            committed.BeforeExperience == 1_500 &&
            committed.AfterLevel == 2 &&
            committed.AfterExperience == 0,
            "concurrent committed audit records the exact transition");
        var rejected = rows.Single(static row =>
            row.Outcome == "rejected");
        Check.True(
            rejected.ReasonCode ==
                PetLevelUpgradeStatus.InsufficientExperience.ToString() &&
            rejected.BeforeLevel == 2 &&
            rejected.BeforeExperience == 0 &&
            rejected.AfterLevel == 2 &&
            rejected.AfterExperience == 0,
            "concurrent rejected audit observes committed state");
    }

    private sealed record RaceAuditRow(
        string Outcome,
        string? ReasonCode,
        short BeforeLevel,
        long BeforeExperience,
        short AfterLevel,
        long AfterExperience);
}
