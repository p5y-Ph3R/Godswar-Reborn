using Godswar.Server.Application.Pets;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.Infrastructure.Pets;

internal static partial class PostgresPetContentBaselinePublisher
{
    private static async Task InsertSettingsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PinnedPetContentCatalog baseline,
        CancellationToken cancellationToken)
    {
        var value = baseline.Settings;
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO pet_content_settings (
                revision, minimum_level, maximum_level,
                maximum_owned_pet_count, maximum_skill_count,
                maximum_rank,
                minimum_merge_level, minimum_owner_merge_amity,
                maximum_spirit_items, maximum_rebirth_count,
                required_rebirth_spirit_count, egg_hatch_runtime_skill_id,
                merge_spirit_item_id, restricted_merge_spirit_item_id,
                rebirth_spirit_item_id, restricted_rebirth_spirit_item_id,
                growth_policy_version, initial_savvy_policy_version,
                added_savvy_policy_version, added_savvy_weights)
            VALUES (
                @revision, @minimumLevel, @maximumLevel,
                @maximumOwned, @maximumSkills, @maximumRank,
                @minimumMerge,
                @minimumAmity, @maximumSpirits, @maximumRebirths,
                @requiredRebirthSpirits, @eggSkill,
                @mergeSpirit, @restrictedMergeSpirit,
                @rebirthSpirit, @restrictedRebirthSpirit,
                @growthVersion, @initialVersion, @addedVersion, @weights)
            ON CONFLICT (revision) DO NOTHING;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("revision", baseline.Revision.Sha256);
        command.Parameters.AddWithValue("minimumLevel", value.MinimumLevel);
        command.Parameters.AddWithValue("maximumLevel", value.MaximumLevel);
        command.Parameters.AddWithValue(
            "maximumOwned",
            value.MaximumOwnedPetCount);
        command.Parameters.AddWithValue(
            "maximumSkills",
            value.MaximumSkillCount);
        command.Parameters.AddWithValue("maximumRank", value.MaximumRank);
        command.Parameters.AddWithValue(
            "minimumMerge",
            value.MinimumMergeLevel);
        command.Parameters.AddWithValue(
            "minimumAmity",
            value.MinimumOwnerMergeAmity);
        command.Parameters.AddWithValue(
            "maximumSpirits",
            value.MaximumSpiritItems);
        command.Parameters.AddWithValue(
            "maximumRebirths",
            value.MaximumRebirthCount);
        command.Parameters.AddWithValue(
            "requiredRebirthSpirits",
            value.RequiredRebirthSpiritCount);
        command.Parameters.AddWithValue("eggSkill", value.EggHatchRuntimeSkillId);
        command.Parameters.AddWithValue("mergeSpirit", checked((int)value.MergeSpiritItemId));
        command.Parameters.AddWithValue("restrictedMergeSpirit", checked((int)value.RestrictedMergeSpiritItemId));
        command.Parameters.AddWithValue("rebirthSpirit", checked((int)value.RebirthSpiritItemId));
        command.Parameters.AddWithValue("restrictedRebirthSpirit", checked((int)value.RestrictedRebirthSpiritItemId));
        command.Parameters.AddWithValue("growthVersion", value.GrowthPolicyVersion);
        command.Parameters.AddWithValue("initialVersion", value.InitialSavvyPolicyVersion);
        command.Parameters.AddWithValue("addedVersion", value.AddedSavvyPolicyVersion);
        command.Parameters.AddWithValue(
            "weights",
            NpgsqlDbType.Array | NpgsqlDbType.Smallint,
            value.AddedSavvyWeights.ToArray());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
