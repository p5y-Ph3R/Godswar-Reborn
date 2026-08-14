using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class PostgresPetMergeSavvyLookupContentMigrationChecks
{
    public const string CheckName =
        "PostgreSQL immutable pet Merge-savvy lookup migration";

    public static Task RunAsync()
    {
        var migrations = PostgresSchemaMigrationCatalog.All;
        var index = migrations.Select((migration, order) => (migration, order))
            .Single(value => value.migration.Id ==
                "20260812_084_pet_merge_savvy_lookup_content").order;
        var sql = migrations[index].Sql;
        Check.True(
            migrations[index - 1].Id ==
                "20260812_083_pet_learned_skill_content" &&
            sql.Contains("merge_savvy_lookup_count",
                StringComparison.Ordinal) &&
            sql.Contains("pet_content_merge_savvy_lookup",
                StringComparison.Ordinal) &&
            sql.Contains("minimum_savvy_difference",
                StringComparison.Ordinal) &&
            sql.Contains("base_increase", StringComparison.Ordinal) &&
            sql.Contains("spirit_count BETWEEN 0 AND 5",
                StringComparison.Ordinal) &&
            sql.Contains("expected.merge_savvy_lookup_count",
                StringComparison.Ordinal) &&
            sql.Contains("BEFORE UPDATE OR DELETE",
                StringComparison.Ordinal) &&
            sql.Contains("BEFORE INSERT",
                StringComparison.Ordinal),
            "migration 084 owns immutable, counted lookup rows and zero-spirit content");
        return Task.CompletedTask;
    }
}
