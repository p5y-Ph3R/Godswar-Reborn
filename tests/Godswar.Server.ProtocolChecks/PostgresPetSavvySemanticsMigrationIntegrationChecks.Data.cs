using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class
    PostgresPetSavvySemanticsMigrationIntegrationChecks
{
    private static async Task<IReadOnlyList<ManagedPetBefore>>
        ReadManagedPetsBeforeAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction)
    {
        var pets = new List<ManagedPetBefore>();
        await using var command = new NpgsqlCommand(
            """
            SELECT
                pet.id,
                pet.user_id,
                pet.aptitude,
                pet.initial_savvy_baseline_total,
                pet.initial_savvy_policy_version,
                pet.revision,
                (
                    to_jsonb(pet)
                    - 'initial_savvy_baseline_total'
                    - 'initial_savvy_policy_version'
                    - 'revision'
                    - 'updated_at'
                )::text
            FROM public.character_pets pet
            WHERE pet.initial_savvy_policy_version = 'project-v1'
            ORDER BY pet.id;
            """,
            connection,
            transaction);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            Check.True(
                !reader.IsDBNull(3),
                $"migration-019 pet {reader.GetInt64(0)} has baseline provenance");
            pets.Add(new ManagedPetBefore(
                reader.GetInt64(0),
                reader.GetInt32(1),
                reader.GetInt16(2),
                reader.GetInt32(3),
                reader.GetString(4),
                reader.GetInt64(5),
                reader.GetString(6),
                []));
        }

        await reader.CloseAsync();
        for (var index = 0; index < pets.Count; index++)
        {
            var pet = pets[index];
            var stats = await ReadStatsBeforeAsync(
                connection,
                transaction,
                pet.PetId);
            Check.Equal(
                6,
                stats.Count,
                $"migration-019 pet {pet.PetId} starts with six stat rows");
            Check.Equal(
                6,
                stats.Select(stat => stat.StatCode).Distinct().Count(),
                $"migration-019 pet {pet.PetId} starts with six distinct stat codes");
            Check.True(
                stats.All(stat => stat.AddedSavvy == 0),
                $"migration-019 pet {pet.PetId} has not progressed added savvy");
            Check.Equal(
                (decimal)pet.BaselineTotal,
                stats.Sum(stat => stat.InitialSavvy),
                $"migration-019 pet {pet.PetId} has its recorded initial-savvy total");
            pets[index] = pet with { Stats = stats };
        }

        return pets;
    }

    private static async Task<IReadOnlyList<PetStatBefore>>
        ReadStatsBeforeAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            long petId)
    {
        var stats = new List<PetStatBefore>();
        await using var command = new NpgsqlCommand(
            """
            SELECT
                stat.stat_code,
                stat.initial_savvy,
                stat.added_savvy,
                stat.base_growth_rate,
                stat.growth_acceleration,
                stat.revision,
                (
                    to_jsonb(stat)
                    - 'initial_savvy'
                    - 'added_savvy'
                    - 'revision'
                )::text
            FROM public.character_pet_stat_values stat
            WHERE stat.pet_id = @petId
            ORDER BY stat.stat_code;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("petId", petId);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            stats.Add(new PetStatBefore(
                reader.GetInt16(0),
                reader.GetDecimal(1),
                reader.GetDecimal(2),
                reader.GetDecimal(3),
                reader.GetDecimal(4),
                reader.GetInt64(5),
                reader.GetString(6)));
        }

        return stats;
    }

    private static async Task<PetAfter> ReadPetAfterAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long petId)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT
                pet.revision,
                pet.rarity_added_savvy_baseline_total,
                pet.rarity_added_savvy_policy_version,
                pet.initial_savvy_source_version,
                pet.initial_savvy_baseline_total,
                pet.initial_savvy_policy_version,
                (
                    to_jsonb(pet)
                    - 'initial_savvy_baseline_total'
                    - 'initial_savvy_policy_version'
                    - 'rarity_added_savvy_baseline_total'
                    - 'rarity_added_savvy_policy_version'
                    - 'initial_savvy_source_version'
                    - 'revision'
                    - 'updated_at'
                )::text
            FROM public.character_pets pet
            WHERE pet.id = @petId;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("petId", petId);
        await using var reader = await command.ExecuteReaderAsync();
        Check.True(await reader.ReadAsync(), $"pet {petId} remains after migration");
        return new PetAfter(
            reader.GetInt64(0),
            reader.GetInt32(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetInt32(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.GetString(6));
    }

    private static async Task<IReadOnlyList<PetStatAfter>>
        ReadStatsAfterAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            long petId)
    {
        var stats = new List<PetStatAfter>();
        await using var command = new NpgsqlCommand(
            """
            SELECT
                stat.stat_code,
                stat.initial_savvy,
                stat.added_savvy,
                stat.birth_initial_savvy,
                stat.rarity_added_savvy,
                stat.revision,
                (
                    to_jsonb(stat)
                    - 'initial_savvy'
                    - 'added_savvy'
                    - 'birth_initial_savvy'
                    - 'rarity_added_savvy'
                    - 'revision'
                )::text
            FROM public.character_pet_stat_values stat
            WHERE stat.pet_id = @petId
            ORDER BY stat.stat_code;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("petId", petId);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            Check.True(
                !reader.IsDBNull(3) && !reader.IsDBNull(4),
                $"pet {petId} stat {reader.GetInt16(0)} has both birth baselines");
            stats.Add(new PetStatAfter(
                reader.GetInt16(0),
                reader.GetDecimal(1),
                reader.GetDecimal(2),
                reader.GetDecimal(3),
                reader.GetDecimal(4),
                reader.GetInt64(5),
                reader.GetString(6)));
        }

        return stats;
    }

    private static async Task<IReadOnlyList<ArchiveRow>> ReadArchiveAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction)
    {
        var rows = new List<ArchiveRow>();
        await using var command = new NpgsqlCommand(
            """
            SELECT
                migration_id,
                pet_id_snapshot,
                owner_user_id_snapshot,
                aptitude_snapshot,
                stat_code,
                old_initial_savvy,
                old_added_savvy,
                old_base_growth_rate,
                old_growth_acceleration,
                old_stat_revision,
                old_pet_revision,
                old_initial_savvy_baseline_total,
                old_initial_savvy_policy_version
            FROM public.pet_savvy_semantics_reconciliation_archive
            WHERE migration_id = @migrationId
            ORDER BY pet_id_snapshot, stat_code;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("migrationId", MigrationId);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new ArchiveRow(
                reader.GetString(0),
                reader.GetInt64(1),
                reader.GetInt32(2),
                reader.GetInt16(3),
                reader.GetInt16(4),
                reader.GetDecimal(5),
                reader.GetDecimal(6),
                reader.GetDecimal(7),
                reader.GetDecimal(8),
                reader.GetInt64(9),
                reader.GetInt64(10),
                reader.GetInt32(11),
                reader.GetString(12)));
        }

        return rows;
    }

    private static async Task<bool> IsMigrationAppliedAsync(
        string connectionString,
        string migrationId)
    {
        if (!await RelationExistsAsync(
                connectionString,
                "public.schema_migrations"))
        {
            return false;
        }

        await using var connection =
            new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT EXISTS (
                SELECT 1
                FROM public.schema_migrations
                WHERE migration_id = @migrationId
            );
            """,
            connection);
        command.Parameters.AddWithValue("migrationId", migrationId);
        return (bool)(await command.ExecuteScalarAsync()
                      ?? throw new InvalidOperationException(
                          "Migration-presence check returned null."));
    }

    private static async Task<bool> RelationExistsAsync(
        string connectionString,
        string qualifiedName)
    {
        await using var connection =
            new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT to_regclass(@qualifiedName) IS NOT NULL;",
            connection);
        command.Parameters.AddWithValue("qualifiedName", qualifiedName);
        return (bool)(await command.ExecuteScalarAsync()
                      ?? throw new InvalidOperationException(
                          "Relation-presence check returned null."));
    }

    private static async Task<bool> ColumnExistsAsync(
        string connectionString,
        string table,
        string column)
    {
        await using var connection =
            new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT EXISTS (
                SELECT 1
                FROM information_schema.columns
                WHERE table_schema = 'public'
                  AND table_name = @table
                  AND column_name = @column
            );
            """,
            connection);
        command.Parameters.AddWithValue("table", table);
        command.Parameters.AddWithValue("column", column);
        return (bool)(await command.ExecuteScalarAsync()
                      ?? throw new InvalidOperationException(
                          "Column-presence check returned null."));
    }

    private static Task<bool> ColumnExistsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string table,
        string column) =>
        ScalarAsync<bool>(
            connection,
            transaction,
            """
            SELECT EXISTS (
                SELECT 1
                FROM information_schema.columns
                WHERE table_schema = 'public'
                  AND table_name = @table
                  AND column_name = @column
            );
            """,
            ("table", table),
            ("column", column));

    private static async Task<T> ScalarAsync<T>(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var command =
            new NpgsqlCommand(sql, connection, transaction);
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(
                parameter.Name,
                parameter.Value);
        }

        return (T)(await command.ExecuteScalarAsync()
                   ?? throw new InvalidOperationException(
                       "PostgreSQL fixture command returned null."));
    }

    private static async Task ExecuteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var command =
            new NpgsqlCommand(sql, connection, transaction);
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(
                parameter.Name,
                parameter.Value);
        }

        await command.ExecuteNonQueryAsync();
    }

    private sealed record ManagedPetBefore(
        long PetId,
        int OwnerId,
        short Aptitude,
        int BaselineTotal,
        string PolicyVersion,
        long Revision,
        string StableJson,
        IReadOnlyList<PetStatBefore> Stats);

    private sealed record PetStatBefore(
        short StatCode,
        decimal InitialSavvy,
        decimal AddedSavvy,
        decimal BaseGrowthRate,
        decimal GrowthAcceleration,
        long Revision,
        string StableJson);

    private sealed record PetAfter(
        long Revision,
        int RarityBaselineTotal,
        string RarityPolicyVersion,
        string InitialSourceVersion,
        int? OldBaselineTotal,
        string? OldPolicyVersion,
        string StableJson);

    private sealed record PetStatAfter(
        short StatCode,
        decimal InitialSavvy,
        decimal AddedSavvy,
        decimal BirthInitialSavvy,
        decimal RarityAddedSavvy,
        long Revision,
        string StableJson);

    private sealed record ArchiveRow(
        string MigrationId,
        long PetId,
        int OwnerId,
        short Aptitude,
        short StatCode,
        decimal InitialSavvy,
        decimal AddedSavvy,
        decimal BaseGrowthRate,
        decimal GrowthAcceleration,
        long StatRevision,
        long PetRevision,
        int BaselineTotal,
        string PolicyVersion);
}
