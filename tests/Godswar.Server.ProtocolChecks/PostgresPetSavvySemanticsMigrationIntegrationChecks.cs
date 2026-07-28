using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class
    PostgresPetSavvySemanticsMigrationIntegrationChecks
{
    private const string ConnectionStringVariable =
        "GODSWAR_TEST_POSTGRES_CONNECTION_STRING";
    private const string RequiredMigrationId =
        "20260728_019_pet_initial_savvy_policy";
    private const string MigrationId =
        "20260729_020_pet_savvy_semantics";
    private const string ArchiveRelation =
        "public.pet_savvy_semantics_reconciliation_archive";

    public static async Task RunAsync()
    {
        var connectionString =
            Environment.GetEnvironmentVariable(ConnectionStringVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Console.WriteLine(
                $"SKIP PostgreSQL pet-savvy semantics migration integration ({ConnectionStringVariable} is not set)");
            return;
        }

        if (!await IsMigrationAppliedAsync(
                connectionString,
                RequiredMigrationId))
        {
            Console.WriteLine(
                $"SKIP PostgreSQL pet-savvy semantics migration integration ({RequiredMigrationId} is required)");
            return;
        }

        if (await IsMigrationAppliedAsync(connectionString, MigrationId))
        {
            Console.WriteLine(
                $"SKIP PostgreSQL pet-savvy semantics migration integration ({MigrationId} is already applied)");
            return;
        }

        Check.True(
            !await RelationExistsAsync(connectionString, ArchiveRelation),
            "integration database has no partial migration-020 archive");

        await using var dataSource =
            NpgsqlDataSource.Create(connectionString);
        await using var connection =
            await dataSource.OpenConnectionAsync();
        await using var transaction =
            await connection.BeginTransactionAsync();
        try
        {
            var before = await ReadManagedPetsBeforeAsync(
                connection,
                transaction);
            Check.True(
                before.Count > 0,
                "integration database contains at least one migration-019 managed pet");

            var migration = PostgresSchemaMigrationCatalog.All.Single(
                candidate => candidate.Id == MigrationId);
            await CheckProgressedPetGuardAsync(
                connection,
                transaction,
                migration,
                before[0]);

            await ExecuteAsync(
                connection,
                transaction,
                migration.Sql);

            await CheckSchemaAsync(connection, transaction);
            await CheckManagedPetsAsync(
                connection,
                transaction,
                before);
            await CheckArchiveAsync(
                connection,
                transaction,
                before);
        }
        finally
        {
            await transaction.RollbackAsync();
        }

        Check.True(
            !await RelationExistsAsync(connectionString, ArchiveRelation),
            "rollback removes the migration-020 archive relation");
        Check.True(
            !await ColumnExistsAsync(
                connectionString,
                "character_pets",
                "rarity_added_savvy_baseline_total"),
            "rollback removes migration-020 character-pet columns");
        Check.True(
            !await IsMigrationAppliedAsync(connectionString, MigrationId),
            "rollback leaves migration 020 unapplied");
    }

    private static async Task CheckProgressedPetGuardAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PostgresSchemaMigration migration,
        ManagedPetBefore target)
    {
        const string savepoint = "pet_savvy_020_guard";
        await ExecuteAsync(
            connection,
            transaction,
            $"SAVEPOINT {savepoint};");

        PostgresException? expected = null;
        try
        {
            await ExecuteAsync(
                connection,
                transaction,
                """
                UPDATE public.character_pet_stat_values
                SET added_savvy = added_savvy + 0.01
                WHERE pet_id = @petId
                  AND stat_code = (
                      SELECT min(stat_code)
                      FROM public.character_pet_stat_values
                      WHERE pet_id = @petId
                  );
                """,
                ("petId", target.PetId));
            await ExecuteAsync(
                connection,
                transaction,
                migration.Sql);
        }
        catch (PostgresException exception)
        {
            expected = exception;
        }

        await ExecuteAsync(
            connection,
            transaction,
            $"ROLLBACK TO SAVEPOINT {savepoint};");

        Check.True(
            expected is not null,
            "migration 020 rejects a progressed migration-019 pet");
        Check.Equal(
            "P0001",
            expected!.SqlState,
            "progressed-pet guard raises a PostgreSQL exception");
        Check.True(
            expected.MessageText.Contains(
                "manual reconciliation is required",
                StringComparison.Ordinal),
            "progressed-pet guard explains why automatic correction stopped");

        var restored = await ReadManagedPetsBeforeAsync(
            connection,
            transaction);
        Check.Equal(
            target.Stats[0].AddedSavvy,
            restored.Single(pet => pet.PetId == target.PetId)
                .Stats[0]
                .AddedSavvy,
            "guard savepoint restores the temporarily progressed stat");
    }

    private static async Task CheckSchemaAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction)
    {
        Check.True(
            await ScalarAsync<bool>(
                connection,
                transaction,
                "SELECT to_regclass(@relation) IS NOT NULL;",
                ("relation", ArchiveRelation)),
            "migration creates the reconciliation archive");

        foreach (var (table, column) in new[]
                 {
                     ("pet_aptitude_templates", "minimum_added_savvy"),
                     ("pet_aptitude_templates", "maximum_added_savvy"),
                     ("pet_aptitude_templates", "added_savvy_policy_version"),
                     ("character_pets", "rarity_added_savvy_baseline_total"),
                     ("character_pets", "rarity_added_savvy_policy_version"),
                     ("character_pets", "initial_savvy_source_version"),
                     ("character_pet_stat_values", "birth_initial_savvy"),
                     ("character_pet_stat_values", "rarity_added_savvy")
                 })
        {
            Check.True(
                await ColumnExistsAsync(
                    connection,
                    transaction,
                    table,
                    column),
                $"migration creates {table}.{column}");
        }
    }

    private static async Task CheckManagedPetsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IReadOnlyList<ManagedPetBefore> before)
    {
        foreach (var oldPet in before)
        {
            var pet = await ReadPetAfterAsync(
                connection,
                transaction,
                oldPet.PetId);
            var stats = await ReadStatsAfterAsync(
                connection,
                transaction,
                oldPet.PetId);

            Check.Equal(
                oldPet.StableJson,
                pet.StableJson,
                $"pet {oldPet.PetId} preserves every unrelated field");
            Check.Equal(
                oldPet.Revision + 1,
                pet.Revision,
                $"pet {oldPet.PetId} revision advances once");
            Check.Equal(
                oldPet.BaselineTotal,
                pet.RarityBaselineTotal,
                $"pet {oldPet.PetId} moves the rarity total to added savvy");
            Check.Equal(
                "project-v2",
                pet.RarityPolicyVersion,
                $"pet {oldPet.PetId} records added-savvy policy provenance");
            Check.Equal(
                "growth-x1-v1",
                pet.InitialSourceVersion,
                $"pet {oldPet.PetId} records growth-derived basic savvy");
            Check.True(
                pet.OldBaselineTotal is null &&
                pet.OldPolicyVersion is null,
                $"pet {oldPet.PetId} clears obsolete initial-savvy provenance");

            Check.Equal(
                6,
                stats.Count,
                $"pet {oldPet.PetId} retains six stat rows");
            Check.Equal(
                (decimal)oldPet.BaselineTotal,
                stats.Sum(stat => stat.RarityAddedSavvy),
                $"pet {oldPet.PetId} rarity baselines have the exact old total");
            Check.Equal(
                (decimal)oldPet.BaselineTotal,
                stats.Sum(stat => stat.AddedSavvy),
                $"pet {oldPet.PetId} added savvy has the exact old total");
            Check.True(
                stats.Select(stat => stat.RarityAddedSavvy)
                    .Distinct()
                    .Count() > 1,
                $"pet {oldPet.PetId} receives a non-equal rarity allocation");

            foreach (var stat in stats)
            {
                var oldStat = oldPet.Stats.Single(
                    candidate => candidate.StatCode == stat.StatCode);
                Check.Equal(
                    oldStat.StableJson,
                    stat.StableJson,
                    $"pet {oldPet.PetId} stat {stat.StatCode} preserves unrelated fields");
                Check.Equal(
                    oldStat.BaseGrowthRate,
                    stat.InitialSavvy,
                    $"pet {oldPet.PetId} stat {stat.StatCode} basic savvy equals growth");
                Check.Equal(
                    oldStat.BaseGrowthRate,
                    stat.BirthInitialSavvy,
                    $"pet {oldPet.PetId} stat {stat.StatCode} stores its birth basic-savvy baseline");
                Check.Equal(
                    stat.RarityAddedSavvy,
                    stat.AddedSavvy,
                    $"pet {oldPet.PetId} stat {stat.StatCode} stores rarity in added savvy");
                Check.True(
                    stat.RarityAddedSavvy > 0,
                    $"pet {oldPet.PetId} stat {stat.StatCode} rarity allocation is positive");
                Check.Equal(
                    oldStat.Revision + 1,
                    stat.Revision,
                    $"pet {oldPet.PetId} stat {stat.StatCode} revision advances once");
            }
        }
    }

    private static async Task CheckArchiveAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IReadOnlyList<ManagedPetBefore> before)
    {
        var rows = await ReadArchiveAsync(connection, transaction);
        Check.Equal(
            before.Count * 6,
            rows.Count,
            "archive contains exactly six rows per managed pet");

        foreach (var oldPet in before)
        {
            var petRows = rows
                .Where(row => row.PetId == oldPet.PetId)
                .OrderBy(row => row.StatCode)
                .ToArray();
            Check.Equal(
                6,
                petRows.Length,
                $"archive contains six rows for pet {oldPet.PetId}");

            foreach (var row in petRows)
            {
                var oldStat = oldPet.Stats.Single(
                    stat => stat.StatCode == row.StatCode);
                Check.Equal(MigrationId, row.MigrationId, "archive migration");
                Check.Equal(oldPet.OwnerId, row.OwnerId, "archive owner");
                Check.Equal(oldPet.Aptitude, row.Aptitude, "archive aptitude");
                Check.Equal(
                    oldStat.InitialSavvy,
                    row.InitialSavvy,
                    "archive old initial savvy");
                Check.Equal(
                    oldStat.AddedSavvy,
                    row.AddedSavvy,
                    "archive old added savvy");
                Check.Equal(
                    oldStat.BaseGrowthRate,
                    row.BaseGrowthRate,
                    "archive old base growth");
                Check.Equal(
                    oldStat.GrowthAcceleration,
                    row.GrowthAcceleration,
                    "archive old growth acceleration");
                Check.Equal(
                    oldStat.Revision,
                    row.StatRevision,
                    "archive old stat revision");
                Check.Equal(
                    oldPet.Revision,
                    row.PetRevision,
                    "archive old pet revision");
                Check.Equal(
                    oldPet.BaselineTotal,
                    row.BaselineTotal,
                    "archive old baseline total");
                Check.Equal(
                    oldPet.PolicyVersion,
                    row.PolicyVersion,
                    "archive old policy version");
            }
        }
    }
}
