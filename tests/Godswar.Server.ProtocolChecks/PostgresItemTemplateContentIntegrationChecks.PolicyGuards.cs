using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresItemTemplateContentIntegrationChecks
{
    private static async Task AssertPolicyCountGuardsAsync(
        NpgsqlDataSource dataSource)
    {
        await AssertPolicyCountGuardAsync(
            dataSource,
            "item attributes",
            """
            INSERT INTO item_attribute_content_definitions (
                revision, id, name_key, stat_type, distribution,
                percent, max_level, level_values, stats)
            VALUES (
                @revision, 2147483100, 'guard-a', 1,
                ARRAY[1]::smallint[], false, 1,
                ARRAY[1]::numeric[], '{}'::jsonb);
            """,
            """
            INSERT INTO item_attribute_content_definitions (
                revision, id, name_key, stat_type, distribution,
                percent, max_level, level_values, stats)
            VALUES (
                @revision, 2147483101, 'guard-b', 1,
                ARRAY[1]::smallint[], false, 1,
                ARRAY[1]::numeric[], '{}'::jsonb);
            """);
        await AssertPolicyCountGuardAsync(
            dataSource,
            "equipment ranks",
            """
            INSERT INTO equipment_rank_content_definitions (
                revision, rank_kind, rank_level,
                required_score, aura_effect, source)
            VALUES (@revision, 'guard', 1, 0, 0, 'guard');
            """,
            """
            INSERT INTO equipment_rank_content_definitions (
                revision, rank_kind, rank_level,
                required_score, aura_effect, source)
            VALUES (@revision, 'guard', 2, 1, 1, 'guard');
            """);
        await AssertPolicyCountGuardAsync(
            dataSource,
            "holy-suit effects",
            """
            INSERT INTO holy_suit_effect_content_definitions (
                revision, effect_key, stat_type,
                unlock_points, effect_value, source)
            VALUES (@revision, 'guard-a', 1, 0, 0, 'guard');
            """,
            """
            INSERT INTO holy_suit_effect_content_definitions (
                revision, effect_key, stat_type,
                unlock_points, effect_value, source)
            VALUES (@revision, 'guard-b', 1, 0, 0, 'guard');
            """);
    }

    private static async Task AssertPolicyCountGuardAsync(
        NpgsqlDataSource dataSource,
        string family,
        string firstInsert,
        string overflowInsert)
    {
        var revision = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(
                $"item-policy-guard:{family}:{Guid.NewGuid():N}")));
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var rolledBack = false;
        try
        {
            await using (var release = new NpgsqlCommand("""
                INSERT INTO item_template_content_revisions (
                    revision, entry_count, source, manifest_version,
                    attribute_count, equipment_rank_count,
                    holy_suit_effect_count)
                VALUES (@revision, 1, 'policy-count-guard', 2, 1, 1, 1);
                """, connection, transaction))
            {
                release.Parameters.AddWithValue("revision", revision);
                await release.ExecuteNonQueryAsync();
            }

            await ExecutePolicyInsertAsync(
                connection,
                transaction,
                firstInsert,
                revision);
            try
            {
                await ExecutePolicyInsertAsync(
                    connection,
                    transaction,
                    overflowInsert,
                    revision);
            }
            catch (PostgresException)
            {
                await transaction.RollbackAsync();
                rolledBack = true;
                return;
            }

            throw new InvalidOperationException(
                $"Item {family} accepted more than its declared count.");
        }
        finally
        {
            if (!rolledBack)
            {
                await transaction.RollbackAsync();
            }
        }
    }

    private static async Task ExecutePolicyInsertAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        string revision)
    {
        await using var command = new NpgsqlCommand(
            sql,
            connection,
            transaction);
        command.Parameters.AddWithValue("revision", revision);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task AssertHeaderSealMutationGuardsAsync(
        NpgsqlDataSource dataSource)
    {
        await AssertHeaderSealMutationRejectedAsync(
            dataSource,
            "declared count",
            manifestVersion: 2,
            attributeCount: 1,
            equipmentRankCount: 1,
            holySuitEffectCount: 1,
            """
            UPDATE item_template_content_revisions
            SET sealed_at = now(), attribute_count = 2
            WHERE revision = @revision;
            """);
        await AssertHeaderSealMutationRejectedAsync(
            dataSource,
            "manifest version",
            manifestVersion: 1,
            attributeCount: 0,
            equipmentRankCount: 0,
            holySuitEffectCount: 0,
            """
            UPDATE item_template_content_revisions
            SET sealed_at = now(),
                manifest_version = 2,
                attribute_count = 1,
                equipment_rank_count = 1,
                holy_suit_effect_count = 1
            WHERE revision = @revision;
            """);
        await AssertHeaderSealMutationRejectedAsync(
            dataSource,
            "material recipe count",
            manifestVersion: 4,
            attributeCount: 1,
            equipmentRankCount: 1,
            holySuitEffectCount: 1,
            """
            UPDATE item_template_content_revisions
            SET sealed_at = now(), material_recipe_count = 2
            WHERE revision = @revision;
            """,
            materialPolicyCount: 2,
            materialRecipeCount: 1);
    }

    private static async Task AssertHeaderSealMutationRejectedAsync(
        NpgsqlDataSource dataSource,
        string mutation,
        short manifestVersion,
        int attributeCount,
        int equipmentRankCount,
        int holySuitEffectCount,
        string mutationSql,
        int materialPolicyCount = 0,
        int materialRecipeCount = 0)
    {
        var revision = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(
                $"item-header-seal:{mutation}:{Guid.NewGuid():N}")));
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var rolledBack = false;
        try
        {
            await using (var release = new NpgsqlCommand("""
                INSERT INTO item_template_content_revisions (
                    revision, entry_count, source, manifest_version,
                    attribute_count, equipment_rank_count,
                    holy_suit_effect_count, material_policy_count,
                    material_recipe_count)
                VALUES (
                    @revision, 1, 'header-seal-guard', @manifestVersion,
                    @attributeCount, @equipmentRankCount,
                    @holySuitEffectCount, @materialPolicyCount,
                    @materialRecipeCount);
                """, connection, transaction))
            {
                release.Parameters.AddWithValue("revision", revision);
                release.Parameters.AddWithValue(
                    "manifestVersion",
                    manifestVersion);
                release.Parameters.AddWithValue(
                    "attributeCount",
                    attributeCount);
                release.Parameters.AddWithValue(
                    "equipmentRankCount",
                    equipmentRankCount);
                release.Parameters.AddWithValue(
                    "holySuitEffectCount",
                    holySuitEffectCount);
                release.Parameters.AddWithValue(
                    "materialPolicyCount",
                    materialPolicyCount);
                release.Parameters.AddWithValue(
                    "materialRecipeCount",
                    materialRecipeCount);
                await release.ExecuteNonQueryAsync();
            }

            try
            {
                await using var command = new NpgsqlCommand(
                    mutationSql,
                    connection,
                    transaction);
                command.Parameters.AddWithValue("revision", revision);
                await command.ExecuteNonQueryAsync();
            }
            catch (PostgresException)
            {
                await transaction.RollbackAsync();
                rolledBack = true;
                return;
            }

            throw new InvalidOperationException(
                $"Item header accepted a seal-time {mutation} mutation.");
        }
        finally
        {
            if (!rolledBack)
            {
                await transaction.RollbackAsync();
            }
        }
    }
}
