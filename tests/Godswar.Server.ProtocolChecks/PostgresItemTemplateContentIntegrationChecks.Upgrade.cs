using System.Security.Cryptography;
using System.Text;
using Godswar.Server.Application.Items;
using Godswar.Server.Infrastructure.Items;
using Godswar.Server.State;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresItemTemplateContentIntegrationChecks
{
    private static async Task AssertV1UpgradeAsync(
        NpgsqlDataSource dataSource,
        string originalRevision)
    {
        var skillBookIds = SkillTalentSeeds.SkillBooks
            .Select(static value => value.ItemId)
            .Distinct()
            .Order()
            .ToArray();
        var legacyDefinition = await ReadLegacyFixtureDefinitionAsync(
            dataSource,
            originalRevision,
            skillBookIds);
        var displaySuffix = " v1 " + Guid.NewGuid().ToString("N");
        legacyDefinition = legacyDefinition with
        {
            DisplayName = legacyDefinition.DisplayName + displaySuffix
        };
        var v1Revision = ItemTemplateContentRevisionHasher
            .ComputeLegacyV1([legacyDefinition]);

        try
        {
            await using (var connection =
                         await dataSource.OpenConnectionAsync())
            await using (var transaction =
                         await connection.BeginTransactionAsync())
            {
                await using (var revision = new NpgsqlCommand("""
                    INSERT INTO item_template_content_revisions (
                        revision, entry_count, source)
                    VALUES (@revision, 1, 'historical-v1-upgrade-test');
                    """, connection, transaction))
                {
                    revision.Parameters.AddWithValue(
                        "revision",
                        v1Revision);
                    await revision.ExecuteNonQueryAsync();
                }

                await using (var definition = new NpgsqlCommand("""
                    INSERT INTO item_template_content_definitions (
                        revision, id, kind, name_key, display_name,
                        equipment_slot, class_ids, min_level, max_level,
                        hand, skill_flag, texture, icon, stats)
                    SELECT @v1Revision, definition.id, definition.kind,
                           definition.name_key,
                           definition.display_name || @displaySuffix,
                           definition.equipment_slot, definition.class_ids,
                           definition.min_level, definition.max_level,
                           definition.hand, definition.skill_flag,
                           definition.texture, definition.icon,
                           definition.stats
                    FROM item_template_content_definitions definition
                    WHERE definition.revision = @originalRevision
                      AND definition.id <> ALL(@skillBookIds)
                    ORDER BY definition.id
                    LIMIT 1;
                    """, connection, transaction))
                {
                    definition.Parameters.AddWithValue(
                        "v1Revision",
                        v1Revision);
                    definition.Parameters.AddWithValue(
                        "originalRevision",
                        originalRevision);
                    definition.Parameters.AddWithValue(
                        "displaySuffix",
                        displaySuffix);
                    definition.Parameters.Add(new NpgsqlParameter(
                        "skillBookIds",
                        NpgsqlDbType.Array | NpgsqlDbType.Integer)
                    {
                        Value = skillBookIds
                    });
                    Check.Equal(
                        1,
                        await definition.ExecuteNonQueryAsync(),
                        "historical v1 fixture copies one exact item row");
                }

                await using (var publish = new NpgsqlCommand("""
                    UPDATE item_template_content_publication
                    SET revision = @revision,
                        published_at = now()
                    WHERE family = 'items';
                    """, connection, transaction))
                {
                    publish.Parameters.AddWithValue(
                        "revision",
                        v1Revision);
                    Check.Equal(
                        1,
                        await publish.ExecuteNonQueryAsync(),
                        "historical v1 fixture becomes the official pointer");
                }

                await transaction.CommitAsync();
            }

            var v1Before = await ReadRevisionFingerprintAsync(
                dataSource,
                v1Revision);
            var upgraded = await PostgresItemTemplateBaselinePublisher
                .EnsurePublishedAsync(dataSource);
            Check.True(
                !upgraded.Revision.Equals(
                    v1Revision,
                    StringComparison.Ordinal),
                "startup advances a v1 item pointer to a new v4 release");
            Check.Equal(
                v1Before,
                await ReadRevisionFingerprintAsync(dataSource, v1Revision),
                "v1-to-v4 upgrade never mutates the sealed v1 row or definition");
            await AssertUpgradeShapeAsync(
                dataSource,
                v1Revision,
                upgraded.Revision,
                skillBookIds);

            var repeated = await PostgresItemTemplateBaselinePublisher
                .EnsurePublishedAsync(dataSource);
            Check.Equal(
                upgraded.Revision,
                repeated.Revision,
                "v1-to-v4 upgrade is idempotent after pointer advancement");
            Check.True(
                !repeated.Created,
                "idempotent v4 publication does not recreate content");
        }
        finally
        {
            await using var restore = dataSource.CreateCommand("""
                UPDATE item_template_content_publication
                SET revision = @revision,
                    published_at = now()
                WHERE family = 'items';
                """);
            restore.Parameters.AddWithValue("revision", originalRevision);
            await restore.ExecuteNonQueryAsync();
        }
    }

    private static async Task AssertCorruptV1RejectedAsync(
        NpgsqlDataSource dataSource,
        string originalRevision)
    {
        var corruptRevision = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(
                "corrupt-item-v1:" + Guid.NewGuid().ToString("N"))));
        try
        {
            await using var connection =
                await dataSource.OpenConnectionAsync();
            await using var transaction =
                await connection.BeginTransactionAsync();
            await using (var release = new NpgsqlCommand("""
                INSERT INTO item_template_content_revisions (
                    revision, entry_count, source)
                VALUES (@corruptRevision, 1, 'corrupt-v1-test');
                """, connection, transaction))
            {
                release.Parameters.AddWithValue(
                    "corruptRevision",
                    corruptRevision);
                await release.ExecuteNonQueryAsync();
            }

            await using (var command = new NpgsqlCommand("""
                INSERT INTO item_template_content_definitions (
                    revision, id, kind, name_key, display_name,
                    equipment_slot, class_ids, min_level, max_level,
                    hand, skill_flag, texture, icon, stats)
                SELECT @corruptRevision, id, kind, name_key, display_name,
                       equipment_slot, class_ids, min_level, max_level,
                       hand, skill_flag, texture, icon, stats
                FROM item_template_content_definitions
                WHERE revision = @originalRevision
                ORDER BY id
                LIMIT 1;

                UPDATE item_template_content_publication
                SET revision = @corruptRevision,
                    published_at = now()
                WHERE family = 'items';
                """, connection, transaction))
            {
                command.Parameters.AddWithValue(
                    "originalRevision",
                    originalRevision);
                command.Parameters.AddWithValue(
                    "corruptRevision",
                    corruptRevision);
                await command.ExecuteNonQueryAsync();
            }
            await transaction.CommitAsync();

            try
            {
                _ = await PostgresItemTemplateBaselinePublisher
                    .EnsurePublishedAsync(dataSource);
            }
            catch (InvalidOperationException exception) when (
                exception.Message.Contains(
                    "canonical count or hash",
                    StringComparison.Ordinal))
            {
                return;
            }

            throw new InvalidOperationException(
                "A corrupt manifest-v1 item publication was upgraded.");
        }
        finally
        {
            await using var restore = dataSource.CreateCommand("""
                UPDATE item_template_content_publication
                SET revision = @revision,
                    published_at = now()
                WHERE family = 'items';
                """);
            restore.Parameters.AddWithValue("revision", originalRevision);
            await restore.ExecuteNonQueryAsync();
        }
    }

    private static async Task<ItemTemplateDefinition>
        ReadLegacyFixtureDefinitionAsync(
            NpgsqlDataSource dataSource,
            string revision,
            int[] skillBookIds)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT id, kind, name_key, display_name, equipment_slot,
                   class_ids, min_level, max_level, hand, skill_flag,
                   texture, icon, stats::text
            FROM item_template_content_definitions
            WHERE revision = @revision
              AND id <> ALL(@skillBookIds)
            ORDER BY id
            LIMIT 1;
            """);
        command.Parameters.AddWithValue("revision", revision);
        command.Parameters.Add(new NpgsqlParameter(
            "skillBookIds",
            NpgsqlDbType.Array | NpgsqlDbType.Integer)
        {
            Value = skillBookIds
        });
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new InvalidOperationException(
                "No non-skill-book item exists for the v1 fixture.");
        }

        return new ItemTemplateDefinition(
            checked((uint)reader.GetInt32(0)),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetInt16(4),
            reader.GetFieldValue<short[]>(5),
            reader.IsDBNull(6) ? null : reader.GetInt32(6),
            reader.IsDBNull(7) ? null : reader.GetInt32(7),
            reader.IsDBNull(8) ? null : reader.GetInt16(8),
            reader.IsDBNull(9) ? null : reader.GetInt32(9),
            reader.GetString(10),
            reader.GetString(11),
            reader.GetString(12));
    }

    private static async Task AssertUpgradeShapeAsync(
        NpgsqlDataSource dataSource,
        string v1Revision,
        string v4Revision,
        int[] skillBookIds)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT
                release.manifest_version,
                release.attribute_count,
                release.equipment_rank_count,
                release.holy_suit_effect_count,
                release.material_policy_count,
                release.material_recipe_count,
                NOT EXISTS (
                    SELECT to_jsonb(old_definition) - 'revision'
                    FROM item_template_content_definitions old_definition
                    WHERE old_definition.revision = @v1Revision
                    EXCEPT
                    SELECT to_jsonb(new_definition) - 'revision'
                    FROM item_template_content_definitions new_definition
                    WHERE new_definition.revision = @v4Revision
                ),
                (
                    SELECT count(*)::integer
                    FROM item_template_content_definitions definition
                    WHERE definition.revision = @v4Revision
                      AND definition.id = ANY(@skillBookIds)
                ),
                (
                    SELECT count(*)::integer
                    FROM item_material_content_definitions definition
                    WHERE definition.revision = @v4Revision
                ),
                (
                    SELECT count(*)::integer
                    FROM item_material_content_definitions definition
                    WHERE definition.revision = @v4Revision
                      AND definition.recipe_kind IS NOT NULL
                )
            FROM item_template_content_revisions release
            WHERE release.revision = @v4Revision;
            """);
        command.Parameters.AddWithValue("v1Revision", v1Revision);
        command.Parameters.AddWithValue("v4Revision", v4Revision);
        command.Parameters.Add(new NpgsqlParameter(
            "skillBookIds",
            NpgsqlDbType.Array | NpgsqlDbType.Integer)
        {
            Value = skillBookIds
        });
        await using var reader = await command.ExecuteReaderAsync();
        Check.True(
            await reader.ReadAsync(),
            "upgraded item release exists");
        Check.Equal(4, reader.GetInt16(0), "upgraded item manifest version");
        Check.True(
            reader.GetInt32(1) > 0 &&
            reader.GetInt32(2) > 0 &&
            reader.GetInt32(3) > 0 &&
            reader.GetInt32(4) > 0 &&
            reader.GetInt32(5) > 0,
            "upgraded item release captures every policy family");
        Check.True(
            reader.GetBoolean(6),
            "upgraded v4 release is a monotonic superset of v1 definitions");
        Check.Equal(
            skillBookIds.Length,
            reader.GetInt32(7),
            "upgraded v4 release appends every reviewed skill-book item");
        Check.Equal(
            reader.GetInt32(4),
            reader.GetInt32(8),
            "upgraded v4 release publishes every declared material policy");
        Check.Equal(
            reader.GetInt32(5),
            reader.GetInt32(9),
            "upgraded v4 release publishes every declared material recipe");
    }

    private static async Task<string> ReadRevisionFingerprintAsync(
        NpgsqlDataSource dataSource,
        string revision)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT to_jsonb(release)::text || ':' ||
                   md5(string_agg(
                       (to_jsonb(definition) - 'revision')::text,
                       '|' ORDER BY definition.id))
            FROM item_template_content_revisions release
            JOIN item_template_content_definitions definition
              ON definition.revision = release.revision
            WHERE release.revision = @revision
            GROUP BY release.revision;
            """);
        command.Parameters.AddWithValue("revision", revision);
        return (string)(await command.ExecuteScalarAsync() ??
            throw new InvalidOperationException(
                $"Item revision {revision} is missing."));
    }
}
