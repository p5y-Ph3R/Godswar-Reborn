using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class PostgresPetManagerUtilityMigrationChecks
{
    public const string CheckName =
        "PostgreSQL durable Pet Manager utility migration";

    public static Task RunAsync()
    {
        var migrations = PostgresSchemaMigrationCatalog.All;
        var position = migrations
            .Select((migration, index) => (migration, index))
            .Single(static value => value.migration.Id ==
                "20260813_089_pet_manager_utility");
        var sql = position.migration.Sql;
        Check.True(
            migrations[position.index - 1].Id ==
                "20260813_088_pet_soul_contract" &&
            migrations[position.index + 1].Id ==
                "20260813_090_bag_consumable_cooldown_state" &&
            sql.Contains(
                "CREATE TABLE public.sealed_pet_items",
                StringComparison.Ordinal) &&
            sql.Contains(
                "item_instance_id bigint NOT NULL UNIQUE",
                StringComparison.Ordinal) &&
            sql.Contains(
                "pet_id bigint NOT NULL UNIQUE",
                StringComparison.Ordinal) &&
            sql.Contains(
                "sync_sealed_pet_item_owner",
                StringComparison.Ordinal) &&
            sql.Contains(
                "validate_active_sealed_pet_link",
                StringComparison.Ordinal) &&
            sql.Contains(
                "activity_state = 'sealed'",
                StringComparison.Ordinal) &&
            sql.Contains(
                "'pet_manager_utility'",
                StringComparison.Ordinal) &&
            sql.Contains(
                "10283, 'C2S', 'PackedPetDetailRequest'",
                StringComparison.Ordinal) &&
            sql.Contains(
                "10284, 'S2C', 'PackedPetDetailResponse'",
                StringComparison.Ordinal),
            "migration 089 pins active packed-pet authority, owner transfer, " +
            "durable utility evidence, and the stock detail opcodes");
        Check.True(
            !sql.Contains("TRUNCATE", StringComparison.OrdinalIgnoreCase) &&
            !sql.Contains("DROP TABLE", StringComparison.OrdinalIgnoreCase),
            "migration 089 is additive and non-destructive");
        return Task.CompletedTask;
    }
}
