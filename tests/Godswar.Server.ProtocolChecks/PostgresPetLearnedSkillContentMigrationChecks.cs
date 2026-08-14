using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class PostgresPetLearnedSkillContentMigrationChecks
{
    public const string CheckName =
        "PostgreSQL immutable learned pet-skill content migration";

    public static Task RunAsync()
    {
        var migrations = PostgresSchemaMigrationCatalog.All;
        var index = migrations.Select((migration, order) => (migration, order))
            .Single(value => value.migration.Id ==
                "20260812_083_pet_learned_skill_content").order;
        var sql = migrations[index].Sql;
        Check.True(
            migrations[index - 1].Id ==
                "20260812_082_pet_hatch_evidence_hardening" &&
            sql.Contains("pet_skill_curve_definitions",
                StringComparison.Ordinal) &&
            sql.Contains("pet_skill_curve_steps", StringComparison.Ordinal) &&
            sql.Contains("opaque_add", StringComparison.Ordinal) &&
            sql.Contains("opaque_flag", StringComparison.Ordinal) &&
            sql.Contains("FOR UPDATE", StringComparison.Ordinal) &&
            sql.Contains("pet-skill content rows are immutable",
                StringComparison.Ordinal) &&
            sql.Contains("BEFORE INSERT OR UPDATE OR DELETE",
                StringComparison.Ordinal),
            "migration 083 owns normalized, immutable, concurrency-guarded skill content");
        return Task.CompletedTask;
    }
}
