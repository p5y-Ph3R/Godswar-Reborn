using Godswar.Server.Application.Pets;
using Npgsql;

namespace Godswar.Server.Infrastructure.Pets;

internal static partial class PostgresPetContentBaselinePublisher
{
    private static Task InsertNativeProfilesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PinnedPetContentCatalog baseline,
        CancellationToken cancellationToken) =>
        InsertJsonRowsAsync(
            """
            INSERT INTO pet_content_native_profiles (
                revision, species_id, aptitude,
                starting_agility, starting_strength, starting_accuracy,
                starting_technique, starting_wisdom, starting_luck,
                genius_agility, genius_strength, genius_accuracy,
                genius_technique, genius_wisdom, genius_luck,
                native_quality, native_samsara, native_genius,
                starter_skill_id, native_skill_count, native_procreate,
                lifetime)
            SELECT @revision,
                   (content->>'SpeciesId')::smallint,
                   (content->>'Aptitude')::smallint,
                   (content->'StartingTraits'->>'Agility')::numeric,
                   (content->'StartingTraits'->>'Strength')::numeric,
                   (content->'StartingTraits'->>'Accuracy')::numeric,
                   (content->'StartingTraits'->>'Technique')::numeric,
                   (content->'StartingTraits'->>'Wisdom')::numeric,
                   (content->'StartingTraits'->>'Luck')::numeric,
                   (content->'GeniusTraits'->>'Agility')::numeric,
                   (content->'GeniusTraits'->>'Strength')::numeric,
                   (content->'GeniusTraits'->>'Accuracy')::numeric,
                   (content->'GeniusTraits'->>'Technique')::numeric,
                   (content->'GeniusTraits'->>'Wisdom')::numeric,
                   (content->'GeniusTraits'->>'Luck')::numeric,
                   (content->>'NativeQuality')::integer,
                   (content->>'NativeSamsara')::integer,
                   (content->>'NativeGenius')::integer,
                   (content->>'StarterSkillId')::integer,
                   (content->>'NativeSkillCount')::integer,
                   (content->>'NativeProcreate')::integer,
                   (content->>'Lifetime')::integer
            FROM jsonb_array_elements(@payload) content
            ON CONFLICT (revision, species_id, aptitude) DO NOTHING;
            """,
            baseline.Revision.Sha256,
            baseline.NativeProfiles,
            connection,
            transaction,
            cancellationToken);

    private static async Task InsertStepsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PinnedPetContentCatalog baseline,
        CancellationToken cancellationToken)
    {
        await InsertJsonRowsAsync(
            """
            INSERT INTO pet_content_experience_steps (
                revision, current_level, required_experience)
            SELECT @revision,
                   (content->>'CurrentLevel')::smallint,
                   (content->>'RequiredExperience')::integer
            FROM jsonb_array_elements(@payload) content
            ON CONFLICT (revision, current_level) DO NOTHING;
            """,
            baseline.Revision.Sha256,
            baseline.ExperienceSteps,
            connection,
            transaction,
            cancellationToken);

        await InsertJsonRowsAsync(
            """
            INSERT INTO pet_content_rebirth_steps (
                revision, rebirth_number, required_pet_level,
                chance_item_id, chance_item_name,
                minimum_increase_per_stat, maximum_increase_per_stat)
            SELECT @revision,
                   (content->>'RebirthNumber')::smallint,
                   (content->>'RequiredPetLevel')::smallint,
                   (content->>'ChanceItemId')::integer,
                   content->>'ChanceItemName',
                   (content->>'MinimumIncreasePerStat')::numeric,
                   (content->>'MaximumIncreasePerStat')::numeric
            FROM jsonb_array_elements(@payload) content
            ON CONFLICT (revision, rebirth_number) DO NOTHING;
            """,
            baseline.Revision.Sha256,
            baseline.RebirthSteps,
            connection,
            transaction,
            cancellationToken);
    }
}
