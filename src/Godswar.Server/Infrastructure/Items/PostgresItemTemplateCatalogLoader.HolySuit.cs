using Godswar.Server.Application.Items;
using Npgsql;

namespace Godswar.Server.Infrastructure.Items;

internal static partial class PostgresItemTemplateCatalogLoader
{
    private sealed record LoadedHolySuitPolicies(
        IReadOnlyList<HolySuitTierDefinition> Tiers,
        IReadOnlyList<HolySuitUpgradeDefinition> Upgrades,
        IReadOnlyList<HolySuitConsumableDefinition> Consumables,
        HolySuitOperationPolicy OperationPolicy);

    private static async Task<LoadedHolySuitPolicies>
        ReadHolySuitPoliciesAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            string revision,
            CancellationToken cancellationToken)
    {
        var tiers = new List<HolySuitTierDefinition>();
        await using (var command = new NpgsqlCommand("""
            SELECT suit_type, display_name, max_level, ware_item_id, source
            FROM holy_suit_tier_content_definitions
            WHERE revision = @revision
            ORDER BY suit_type;
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("revision", revision);
            await using var reader =
                await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                tiers.Add(new HolySuitTierDefinition(
                    reader.GetInt16(0),
                    reader.GetString(1),
                    reader.GetInt16(2),
                    reader.IsDBNull(3)
                        ? null
                        : checked((uint)reader.GetInt32(3)),
                    reader.GetString(4)));
            }
        }

        var upgrades = new List<HolySuitUpgradeDefinition>();
        await using (var command = new NpgsqlCommand("""
            SELECT current_suit_type, current_level,
                   target_suit_type, target_level,
                   required_item_experience, ware_item_id,
                   ware_quantity, required_prisms, source
            FROM holy_suit_upgrade_content_definitions
            WHERE revision = @revision
            ORDER BY current_suit_type, current_level;
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("revision", revision);
            await using var reader =
                await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                upgrades.Add(new HolySuitUpgradeDefinition(
                    reader.GetInt16(0),
                    reader.GetInt16(1),
                    reader.GetInt16(2),
                    reader.GetInt16(3),
                    reader.GetInt64(4),
                    checked((uint)reader.GetInt32(5)),
                    reader.GetInt16(6),
                    reader.GetInt32(7),
                    reader.GetString(8)));
            }
        }

        var consumables = new List<HolySuitConsumableDefinition>();
        await using (var command = new NpgsqlCommand("""
            SELECT item_id, role, suit_type, experience_capacity,
                   stack_cap, granted_bound, source
            FROM holy_suit_consumable_content_definitions
            WHERE revision = @revision
            ORDER BY item_id;
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("revision", revision);
            await using var reader =
                await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                consumables.Add(new HolySuitConsumableDefinition(
                    checked((uint)reader.GetInt32(0)),
                    ParseHolySuitConsumableRole(reader.GetString(1)),
                    reader.IsDBNull(2) ? null : reader.GetInt16(2),
                    reader.GetInt64(3),
                    reader.GetInt16(4),
                    reader.GetInt16(5),
                    reader.GetString(6)));
            }
        }

        HolySuitOperationPolicy policy;
        await using (var command = new NpgsqlCommand("""
            SELECT minimum_player_level, minimum_gear_level,
                   daily_experience_per_player_level,
                   daily_experience_per_player,
                   per_operation_experience_maximum,
                   gear_experience_capacity, experience_prism_cost,
                   realm_day_time_zone, daily_quota_bypass_entitlement,
                   source
            FROM holy_suit_operation_policy_content_definitions
            WHERE revision = @revision AND policy_key = 'alpha';
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("revision", revision);
            await using var reader =
                await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidDataException(
                    $"Holy Suit policy is missing from revision {revision}.");
            }
            policy = new HolySuitOperationPolicy(
                reader.GetInt16(0),
                reader.GetInt16(1),
                reader.GetInt64(2),
                reader.IsDBNull(3) ? null : reader.GetInt64(3),
                reader.GetInt64(4),
                reader.GetInt64(5),
                reader.GetInt64(6),
                reader.GetString(7),
                reader.GetString(8),
                reader.GetString(9));
            if (await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidDataException(
                    $"Holy Suit revision {revision} has multiple policies.");
            }
        }

        return new LoadedHolySuitPolicies(
            tiers, upgrades, consumables, policy);
    }

    private static HolySuitConsumableRole ParseHolySuitConsumableRole(
        string value) => value switch
        {
            "ware" => HolySuitConsumableRole.Ware,
            "holy_box" => HolySuitConsumableRole.HolyBox,
            "experience_prism" => HolySuitConsumableRole.ExperiencePrism,
            _ => throw new InvalidDataException(
                $"Unknown Holy Suit consumable role '{value}'.")
        };
}
