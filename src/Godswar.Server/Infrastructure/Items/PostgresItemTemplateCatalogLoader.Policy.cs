using Godswar.Server.Application.Items;
using Npgsql;

namespace Godswar.Server.Infrastructure.Items;

internal static partial class PostgresItemTemplateCatalogLoader
{
    private sealed record LoadedItemPolicies(
        IReadOnlyList<ItemAttributeDefinition> Attributes,
        IReadOnlyList<EquipmentRankDefinition> EquipmentRanks,
        IReadOnlyList<HolySuitEffectDefinition> HolySuitEffects);

    private static async Task<LoadedItemPolicies> ReadPoliciesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string revision,
        CancellationToken cancellationToken)
    {
        var attributes = new List<ItemAttributeDefinition>();
        await using (var command = new NpgsqlCommand("""
            SELECT id, name_key, stat_type, distribution, percent,
                   max_level, level_values::text, stats::text
            FROM item_attribute_content_definitions
            WHERE revision = @revision
            ORDER BY id;
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("revision", revision);
            await using var reader =
                await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                attributes.Add(new ItemAttributeDefinition(
                    reader.GetInt32(0),
                    reader.GetString(1),
                    reader.GetInt16(2),
                    reader.GetFieldValue<short[]>(3),
                    reader.GetBoolean(4),
                    reader.GetInt16(5),
                    reader.GetString(6),
                    reader.GetString(7)));
            }
        }

        var ranks = new List<EquipmentRankDefinition>();
        await using (var command = new NpgsqlCommand("""
            SELECT rank_kind, rank_level, required_score,
                   aura_effect, source
            FROM equipment_rank_content_definitions
            WHERE revision = @revision
            ORDER BY rank_kind, rank_level;
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("revision", revision);
            await using var reader =
                await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                ranks.Add(new EquipmentRankDefinition(
                    reader.GetString(0),
                    reader.GetInt16(1),
                    reader.GetInt32(2),
                    reader.GetInt32(3),
                    reader.GetString(4)));
            }
        }

        var holySuitEffects = new List<HolySuitEffectDefinition>();
        await using (var command = new NpgsqlCommand("""
            SELECT effect_key, stat_type, unlock_points,
                   effect_value::text, source
            FROM holy_suit_effect_content_definitions
            WHERE revision = @revision
            ORDER BY effect_key;
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("revision", revision);
            await using var reader =
                await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                holySuitEffects.Add(new HolySuitEffectDefinition(
                    reader.GetString(0),
                    reader.GetInt16(1),
                    reader.GetInt16(2),
                    reader.GetString(3),
                    reader.GetString(4)));
            }
        }

        return new LoadedItemPolicies(
            attributes,
            ranks,
            holySuitEffects);
    }
}
