using System.Text.Json;
using Godswar.Server.Application.Pets;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.Infrastructure.Pets;

internal static partial class PostgresPetContentBaselinePublisher
{
    private const int MaximumBaselinePayloadBytes = 4 * 1024 * 1024;

    private static async Task InsertDefinitionsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PinnedPetContentCatalog baseline,
        CancellationToken cancellationToken)
    {
        await InsertJsonRowsAsync(
            """
            INSERT INTO pet_content_species_definitions (
                revision, species_id, display_name, food_kind,
                starter_skill_id, starter_skill_name, lifetime_values,
                egg_item_id, egg_declared_species_id, magic_jade_item_id)
            SELECT @revision,
                   (content->>'SpeciesId')::smallint,
                   content->>'DisplayName',
                   (content->>'FoodKind')::smallint,
                   (content->>'StarterSkillId')::integer,
                   content->>'StarterSkillName',
                   ARRAY(
                       SELECT value::integer
                       FROM jsonb_array_elements_text(
                           content->'LifetimeValues') value),
                   (content->>'EggItemId')::integer,
                   (content->>'EggDeclaredSpeciesId')::smallint,
                   (content->>'MagicJadeItemId')::integer
            FROM jsonb_array_elements(@payload) content
            ON CONFLICT (revision, species_id) DO NOTHING;
            """,
            baseline.Revision.Sha256,
            baseline.Species,
            connection,
            transaction,
            cancellationToken);

        await InsertJsonRowsAsync(
            """
            INSERT INTO pet_content_aptitude_definitions (
                revision, aptitude, name_key, display_name,
                is_server_extension, minimum_total_growth,
                maximum_total_growth, maximum_growth_stat_deviation,
                minimum_initial_savvy, maximum_initial_savvy,
                maximum_initial_savvy_stat_deviation,
                minimum_added_savvy, maximum_added_savvy,
                innate_talent_mask)
            SELECT @revision,
                   (content->>'Aptitude')::smallint,
                   content->>'NameKey',
                   content->>'DisplayName',
                   (content->>'IsServerExtension')::boolean,
                   (content->>'MinimumTotalGrowth')::numeric,
                   (content->>'MaximumTotalGrowth')::numeric,
                   (content->>'MaximumGrowthStatDeviation')::numeric,
                   (content->>'MinimumInitialSavvy')::integer,
                   (content->>'MaximumInitialSavvy')::integer,
                   (content->>'MaximumInitialSavvyStatDeviation')::numeric,
                   (content->>'MinimumAddedSavvy')::integer,
                   (content->>'MaximumAddedSavvy')::integer,
                   (content->>'InnateTalentMask')::smallint
            FROM jsonb_array_elements(@payload) content
            ON CONFLICT (revision, aptitude) DO NOTHING;
            """,
            baseline.Revision.Sha256,
            baseline.Aptitudes,
            connection,
            transaction,
            cancellationToken);

        await InsertNativeProfilesAsync(
            connection,
            transaction,
            baseline,
            cancellationToken);
        await InsertStepsAsync(
            connection,
            transaction,
            baseline,
            cancellationToken);
        await InsertHatchRankStepsAsync(
            connection,
            transaction,
            baseline,
            cancellationToken);
        await InsertMergeRankContentAsync(
            connection,
            transaction,
            baseline,
            cancellationToken);
    }

    private static async Task InsertJsonRowsAsync<T>(
        string sql,
        string revision,
        IReadOnlyList<T> values,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(values);
        if (payload.Length == 0 ||
            System.Text.Encoding.UTF8.GetByteCount(payload) >
                MaximumBaselinePayloadBytes)
        {
            throw new InvalidOperationException(
                "The reviewed pet-content baseline payload is empty or oversized.");
        }

        await using var command = new NpgsqlCommand(
            sql,
            connection,
            transaction);
        command.Parameters.AddWithValue("revision", revision);
        command.Parameters.AddWithValue(
            "payload",
            NpgsqlDbType.Jsonb,
            payload);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
