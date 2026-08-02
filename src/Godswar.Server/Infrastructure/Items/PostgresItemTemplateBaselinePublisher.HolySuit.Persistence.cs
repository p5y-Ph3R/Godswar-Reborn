using Godswar.Server.Application.Items;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.Infrastructure.Items;

internal static partial class PostgresItemTemplateBaselinePublisher
{
    private static async Task<HolySuitPolicySnapshot>
        ReadPublishedHolySuitPoliciesAsync(
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
                    ParseHolySuitRole(reader.GetString(1)),
                    reader.IsDBNull(2) ? null : reader.GetInt16(2),
                    reader.GetInt64(3),
                    reader.GetInt16(4),
                    reader.GetInt16(5),
                    reader.GetString(6)));
            }
        }

        HolySuitOperationPolicy operationPolicy;
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
            operationPolicy = new HolySuitOperationPolicy(
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

        return new HolySuitPolicySnapshot(
            tiers, upgrades, consumables, operationPolicy);
    }

    private static async Task InsertHolySuitPoliciesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string revision,
        HolySuitPolicySnapshot holySuit,
        CancellationToken cancellationToken)
    {
        await InsertHolySuitTiersAsync(
            connection, transaction, revision, holySuit.Tiers,
            cancellationToken);
        await InsertHolySuitConsumablesAsync(
            connection, transaction, revision, holySuit.Consumables,
            cancellationToken);
        await InsertHolySuitUpgradesAsync(
            connection, transaction, revision, holySuit.Upgrades,
            cancellationToken);
        await InsertHolySuitOperationPolicyAsync(
            connection, transaction, revision, holySuit.OperationPolicy,
            cancellationToken);
    }

    private static async Task InsertHolySuitTiersAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string revision,
        IReadOnlyList<HolySuitTierDefinition> values,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO holy_suit_tier_content_definitions (
                revision, suit_type, display_name, max_level,
                ware_item_id, source)
            VALUES (
                @revision, @suitType, @displayName, @maxLevel,
                @wareItemId, @source);
            """, connection, transaction);
        foreach (var value in values)
        {
            command.Parameters.Clear();
            command.Parameters.AddWithValue("revision", revision);
            command.Parameters.AddWithValue("suitType", value.SuitType);
            command.Parameters.AddWithValue("displayName", value.Name);
            command.Parameters.AddWithValue("maxLevel", value.MaxLevel);
            command.Parameters.Add(new NpgsqlParameter(
                "wareItemId", NpgsqlDbType.Integer)
            {
                Value = value.WareItemId.HasValue
                    ? checked((int)value.WareItemId.Value)
                    : DBNull.Value
            });
            command.Parameters.AddWithValue("source", value.Source);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task InsertHolySuitConsumablesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string revision,
        IReadOnlyList<HolySuitConsumableDefinition> values,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO holy_suit_consumable_content_definitions (
                revision, item_id, role, suit_type, experience_capacity,
                stack_cap, granted_bound, source)
            VALUES (
                @revision, @itemId, @role, @suitType, @experienceCapacity,
                @stackCap, @grantedBound, @source);
            """, connection, transaction);
        foreach (var value in values)
        {
            command.Parameters.Clear();
            command.Parameters.AddWithValue("revision", revision);
            command.Parameters.AddWithValue("itemId", checked((int)value.ItemId));
            command.Parameters.AddWithValue("role", FormatHolySuitRole(value.Role));
            command.Parameters.Add(new NpgsqlParameter(
                "suitType", NpgsqlDbType.Smallint)
            {
                Value = value.SuitType.HasValue
                    ? value.SuitType.Value
                    : DBNull.Value
            });
            command.Parameters.AddWithValue(
                "experienceCapacity", value.ExperienceCapacity);
            command.Parameters.AddWithValue("stackCap", value.StackCap);
            command.Parameters.AddWithValue("grantedBound", value.GrantedBound);
            command.Parameters.AddWithValue("source", value.Source);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task InsertHolySuitUpgradesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string revision,
        IReadOnlyList<HolySuitUpgradeDefinition> values,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO holy_suit_upgrade_content_definitions (
                revision, current_suit_type, current_level,
                target_suit_type, target_level, required_item_experience,
                ware_item_id, ware_quantity, required_prisms, source)
            VALUES (
                @revision, @currentSuitType, @currentLevel,
                @targetSuitType, @targetLevel, @requiredItemExperience,
                @wareItemId, @wareQuantity, @requiredPrisms, @source);
            """, connection, transaction);
        foreach (var value in values)
        {
            command.Parameters.Clear();
            command.Parameters.AddWithValue("revision", revision);
            command.Parameters.AddWithValue(
                "currentSuitType", value.CurrentSuitType);
            command.Parameters.AddWithValue("currentLevel", value.CurrentLevel);
            command.Parameters.AddWithValue(
                "targetSuitType", value.TargetSuitType);
            command.Parameters.AddWithValue("targetLevel", value.TargetLevel);
            command.Parameters.AddWithValue(
                "requiredItemExperience", value.RequiredItemExperience);
            command.Parameters.AddWithValue(
                "wareItemId", checked((int)value.WareItemId));
            command.Parameters.AddWithValue("wareQuantity", value.WareQuantity);
            command.Parameters.AddWithValue(
                "requiredPrisms", value.RequiredPrisms);
            command.Parameters.AddWithValue("source", value.Source);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task InsertHolySuitOperationPolicyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string revision,
        HolySuitOperationPolicy value,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO holy_suit_operation_policy_content_definitions (
                revision, policy_key, minimum_player_level,
                minimum_gear_level, daily_experience_per_player_level,
                daily_experience_per_player,
                per_operation_experience_maximum, gear_experience_capacity,
                experience_prism_cost, realm_day_time_zone,
                daily_quota_bypass_entitlement, source)
            VALUES (
                @revision, 'alpha', @minimumPlayerLevel,
                @minimumGearLevel, @dailyExperiencePerPlayerLevel,
                @dailyExperiencePerPlayer,
                @perOperationExperienceMaximum, @gearExperienceCapacity,
                @experiencePrismCost, @realmDayTimeZone,
                @dailyQuotaBypassEntitlement, @source);
            """, connection, transaction);
        command.Parameters.AddWithValue("revision", revision);
        command.Parameters.AddWithValue(
            "minimumPlayerLevel", value.MinimumPlayerLevel);
        command.Parameters.AddWithValue(
            "minimumGearLevel", value.MinimumGearLevel);
        command.Parameters.AddWithValue(
            "dailyExperiencePerPlayerLevel",
            value.LegacyDailyExperiencePerPlayerLevel);
        command.Parameters.Add(new NpgsqlParameter(
            "dailyExperiencePerPlayer", NpgsqlDbType.Bigint)
        {
            Value = value.DailyExperiencePerPlayer.HasValue
                ? value.DailyExperiencePerPlayer.Value
                : DBNull.Value
        });
        command.Parameters.AddWithValue(
            "perOperationExperienceMaximum",
            value.PerOperationExperienceMaximum);
        command.Parameters.AddWithValue(
            "gearExperienceCapacity", value.GearExperienceCapacity);
        command.Parameters.AddWithValue(
            "experiencePrismCost", value.ExperiencePrismCost);
        command.Parameters.AddWithValue(
            "realmDayTimeZone", value.RealmDayTimeZone);
        command.Parameters.AddWithValue(
            "dailyQuotaBypassEntitlement",
            value.DailyQuotaBypassEntitlement);
        command.Parameters.AddWithValue("source", value.Source);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string FormatHolySuitRole(HolySuitConsumableRole role) =>
        role switch
        {
            HolySuitConsumableRole.Ware => "ware",
            HolySuitConsumableRole.HolyBox => "holy_box",
            HolySuitConsumableRole.ExperiencePrism => "experience_prism",
            _ => throw new ArgumentOutOfRangeException(nameof(role))
        };

    private static HolySuitConsumableRole ParseHolySuitRole(string role) =>
        role switch
        {
            "ware" => HolySuitConsumableRole.Ware,
            "holy_box" => HolySuitConsumableRole.HolyBox,
            "experience_prism" => HolySuitConsumableRole.ExperiencePrism,
            _ => throw new InvalidDataException(
                $"Unknown Holy Suit consumable role '{role}'.")
        };
}
