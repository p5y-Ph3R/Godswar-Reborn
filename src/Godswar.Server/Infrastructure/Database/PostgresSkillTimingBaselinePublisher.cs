using Godswar.Server.State;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.Infrastructure.Database;

/// <summary>
/// Explicit one-time promotion boundary for timing fields that the reviewed
/// skill SQL predates. Generated declarations are never consulted by gameplay.
/// </summary>
internal static class PostgresSkillTimingBaselinePublisher
{
    public static async Task ApplyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        var skills = SkillTalentSeeds.Skills;
        if (skills.Count == 0 ||
            skills.Select(static value => value.SkillId).Distinct().Count() !=
                skills.Count)
        {
            throw new InvalidDataException(
                "The reviewed skill-timing baseline is empty or duplicated.");
        }

        await using var command = new NpgsqlCommand(
            """
            UPDATE skill_templates
            SET intonate_time = @intonate_time,
                cooling_time = @cooling_time
            WHERE skill_id = @skill_id;
            """,
            connection,
            transaction);
        var updated = 0;
        foreach (var skill in skills)
        {
            command.Parameters.Clear();
            command.Parameters.AddWithValue(
                "skill_id",
                NpgsqlDbType.Integer,
                skill.SkillId);
            command.Parameters.AddWithValue(
                "intonate_time",
                NpgsqlDbType.Numeric,
                skill.IntonateTime);
            command.Parameters.AddWithValue(
                "cooling_time",
                NpgsqlDbType.Numeric,
                skill.CoolingTime);
            updated += await command.ExecuteNonQueryAsync(cancellationToken);
        }

        if (updated != skills.Count)
        {
            throw new InvalidDataException(
                $"The skill-timing baseline updated {updated} rows; " +
                $"expected {skills.Count}.");
        }
    }
}
