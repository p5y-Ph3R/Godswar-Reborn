using Godswar.Server.Infrastructure.Items;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresItemTemplateContentIntegrationChecks
{
    private static readonly int[] HolySuitCompatibilityItemIds =
    [
        9010, 9011, 9012, 9013, 9014, 9015, 9016,
        9020, 9021, 9022, 9023, 9024, 9025
    ];

    private static async Task
        AssertPublishedV6RepairsMutableHolySuitTemplatesAsync(
            NpgsqlDataSource dataSource,
            string publishedRevision)
    {
        var unrelatedBefore =
            await ReadMutableTemplateFingerprintAsync(dataSource, 1000);
        await DeleteMutableHolySuitTemplatesAsync(dataSource);
        Check.Equal(
            0,
            await CountMutableHolySuitTemplatesAsync(dataSource),
            "already-v6 fixture removes every mutable Holy Suit identity");

        var repaired = await PostgresItemTemplateBaselinePublisher
            .EnsurePublishedAsync(dataSource);
        Check.True(
            repaired.Revision == publishedRevision && !repaired.Created,
            "already-published v6 startup repairs without republishing");
        await AssertMutableHolySuitTemplatesMatchPublishedAsync(
            dataSource,
            publishedRevision,
            "already-published v6 startup repairs missing mutable identities");
        Check.Equal(
            unrelatedBefore,
            await ReadMutableTemplateFingerprintAsync(dataSource, 1000),
            "already-v6 compatibility repair leaves unrelated rows untouched");

        await using (var poison = dataSource.CreateCommand("""
            UPDATE item_templates
            SET display_name = 'Unsafe Holy Suit Conflict'
            WHERE id = 9010;
            """))
        {
            Check.Equal(
                1,
                await poison.ExecuteNonQueryAsync(),
                "conflict fixture changes one mutable Holy Suit identity");
        }
        await using (var removeForMixedFixture = dataSource.CreateCommand("""
            DELETE FROM item_templates WHERE id IN (9020, 9021);
            """))
        {
            Check.Equal(
                2,
                await removeForMixedFixture.ExecuteNonQueryAsync(),
                "mixed conflict fixture removes two other identities");
        }

        var rejected = false;
        try
        {
            _ = await PostgresItemTemplateBaselinePublisher
                .EnsurePublishedAsync(dataSource);
        }
        catch (InvalidOperationException error)
            when (error.Message.Contains(
                "no mutable row was overwritten",
                StringComparison.Ordinal))
        {
            rejected = true;
        }
        Check.True(
            rejected,
            "already-v6 startup fails closed on a conflicting mutable identity");
        Check.Equal(
            "Unsafe Holy Suit Conflict",
            await ReadMutableDisplayNameAsync(dataSource, 9010),
            "failed compatibility repair never overwrites the conflict");
        Check.Equal(
            HolySuitCompatibilityItemIds.Length - 2,
            await CountMutableHolySuitTemplatesAsync(dataSource),
            "failed mixed compatibility repair rolls back missing-row inserts");

        await using (var removeConflict = dataSource.CreateCommand(
                         "DELETE FROM item_templates WHERE id = 9010;"))
        {
            await removeConflict.ExecuteNonQueryAsync();
        }
        _ = await PostgresItemTemplateBaselinePublisher
            .EnsurePublishedAsync(dataSource);
        await AssertMutableHolySuitTemplatesMatchPublishedAsync(
            dataSource,
            publishedRevision,
            "removing a conflict permits a clean compatibility repair");
    }

    private static async Task DeleteMutableHolySuitTemplatesAsync(
        NpgsqlDataSource dataSource)
    {
        await using var command = dataSource.CreateCommand("""
            DELETE FROM item_templates WHERE id = ANY(@itemIds);
            """);
        command.Parameters.Add(new NpgsqlParameter(
            "itemIds", NpgsqlDbType.Array | NpgsqlDbType.Integer)
        {
            Value = HolySuitCompatibilityItemIds
        });
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<int> CountMutableHolySuitTemplatesAsync(
        NpgsqlDataSource dataSource)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT count(*)::integer
            FROM item_templates
            WHERE id = ANY(@itemIds);
            """);
        command.Parameters.Add(new NpgsqlParameter(
            "itemIds", NpgsqlDbType.Array | NpgsqlDbType.Integer)
        {
            Value = HolySuitCompatibilityItemIds
        });
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static async Task
        AssertMutableHolySuitTemplatesMatchPublishedAsync(
            NpgsqlDataSource dataSource,
            string publishedRevision,
            string message)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT count(*)::integer,
                   count(mutable.id)::integer,
                   count(*) FILTER (
                       WHERE mutable.id IS NOT NULL AND ROW(
                           mutable.kind, mutable.name_key,
                           mutable.display_name, mutable.equipment_slot,
                           mutable.class_ids, mutable.min_level,
                           mutable.max_level, mutable.hand,
                           mutable.skill_flag, mutable.texture,
                           mutable.icon, mutable.stats
                       ) IS NOT DISTINCT FROM ROW(
                           published.kind, published.name_key,
                           published.display_name,
                           published.equipment_slot,
                           published.class_ids, published.min_level,
                           published.max_level, published.hand,
                           published.skill_flag, published.texture,
                           published.icon, published.stats
                       ))::integer
            FROM item_template_content_definitions published
            LEFT JOIN item_templates mutable ON mutable.id = published.id
            WHERE published.revision = @revision
              AND published.id = ANY(@itemIds);
            """);
        command.Parameters.AddWithValue("revision", publishedRevision);
        command.Parameters.Add(new NpgsqlParameter(
            "itemIds", NpgsqlDbType.Array | NpgsqlDbType.Integer)
        {
            Value = HolySuitCompatibilityItemIds
        });
        await using var reader = await command.ExecuteReaderAsync();
        Check.True(await reader.ReadAsync(), message + " query returns a row");
        Check.True(
            reader.GetInt32(0) == HolySuitCompatibilityItemIds.Length &&
            reader.GetInt32(1) == HolySuitCompatibilityItemIds.Length &&
            reader.GetInt32(2) == HolySuitCompatibilityItemIds.Length,
            message);
    }

    private static async Task<string> ReadMutableTemplateFingerprintAsync(
        NpgsqlDataSource dataSource,
        int itemId)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT to_jsonb(template)::text
            FROM item_templates template
            WHERE id = @itemId;
            """);
        command.Parameters.AddWithValue("itemId", itemId);
        return (string)(await command.ExecuteScalarAsync() ??
            throw new InvalidOperationException(
                $"Mutable item template {itemId} is missing."));
    }

    private static async Task<string> ReadMutableDisplayNameAsync(
        NpgsqlDataSource dataSource,
        int itemId)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT display_name FROM item_templates WHERE id = @itemId;
            """);
        command.Parameters.AddWithValue("itemId", itemId);
        return (string)(await command.ExecuteScalarAsync() ??
            throw new InvalidOperationException(
                $"Mutable item template {itemId} is missing."));
    }
}
