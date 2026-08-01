using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.Infrastructure.Items;

internal static partial class PostgresItemTemplateBaselinePublisher
{
    private static async Task InsertCorePoliciesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string revision,
        ItemPolicySnapshot policies,
        CancellationToken cancellationToken)
    {
        await using (var command = new NpgsqlCommand("""
            INSERT INTO item_attribute_content_definitions (
                revision, id, name_key, stat_type, distribution,
                percent, max_level, level_values, stats)
            VALUES (
                @revision, @id, @nameKey, @statType, @distribution,
                @percent, @maxLevel, CAST(@levelValues AS numeric[]), @stats);
            """, connection, transaction))
        {
            foreach (var value in policies.Attributes)
            {
                command.Parameters.Clear();
                command.Parameters.AddWithValue("revision", revision);
                command.Parameters.AddWithValue("id", value.Id);
                command.Parameters.AddWithValue("nameKey", value.NameKey);
                command.Parameters.AddWithValue("statType", value.StatType);
                command.Parameters.Add(new NpgsqlParameter(
                    "distribution", NpgsqlDbType.Array | NpgsqlDbType.Smallint)
                {
                    Value = value.Distribution.ToArray()
                });
                command.Parameters.AddWithValue("percent", value.Percent);
                command.Parameters.AddWithValue("maxLevel", value.MaxLevel);
                command.Parameters.Add(new NpgsqlParameter(
                    "levelValues", NpgsqlDbType.Text) { Value = value.LevelValues });
                command.Parameters.Add(new NpgsqlParameter(
                    "stats", NpgsqlDbType.Jsonb) { Value = value.StatsJson });
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        await using (var command = new NpgsqlCommand("""
            INSERT INTO equipment_rank_content_definitions (
                revision, rank_kind, rank_level, required_score,
                aura_effect, source)
            VALUES (
                @revision, @rankKind, @rankLevel, @requiredScore,
                @auraEffect, @source);
            """, connection, transaction))
        {
            foreach (var value in policies.EquipmentRanks)
            {
                command.Parameters.Clear();
                command.Parameters.AddWithValue("revision", revision);
                command.Parameters.AddWithValue("rankKind", value.RankKind);
                command.Parameters.AddWithValue("rankLevel", value.RankLevel);
                command.Parameters.AddWithValue("requiredScore", value.RequiredScore);
                command.Parameters.AddWithValue("auraEffect", value.AuraEffect);
                command.Parameters.AddWithValue("source", value.Source);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        await using var holyCommand = new NpgsqlCommand("""
            INSERT INTO holy_suit_effect_content_definitions (
                revision, effect_key, stat_type, unlock_points,
                effect_value, source)
            VALUES (
                @revision, @effectKey, @statType, @unlockPoints,
                CAST(@effectValue AS numeric), @source);
            """, connection, transaction);
        foreach (var value in policies.HolySuitEffects)
        {
            holyCommand.Parameters.Clear();
            holyCommand.Parameters.AddWithValue("revision", revision);
            holyCommand.Parameters.AddWithValue("effectKey", value.EffectKey);
            holyCommand.Parameters.AddWithValue("statType", value.StatType);
            holyCommand.Parameters.AddWithValue("unlockPoints", value.UnlockPoints);
            holyCommand.Parameters.Add(new NpgsqlParameter(
                "effectValue", NpgsqlDbType.Text) { Value = value.EffectValue });
            holyCommand.Parameters.AddWithValue("source", value.Source);
            await holyCommand.ExecuteNonQueryAsync(cancellationToken);
        }
    }
}
