using System.Data;
using Godswar.Server.Application.Items;
using Godswar.Server.Application.Pets;
using Npgsql;

namespace Godswar.Server.Infrastructure.Pets;

internal static partial class PostgresPetContentReader
{
    public static async Task<PinnedPetContentCatalog> LoadAsync(
        NpgsqlDataSource dataSource,
        IItemTemplateCatalog itemCatalog,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(itemCatalog);
        await using var connection =
            await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction =
            await connection.BeginTransactionAsync(
                IsolationLevel.RepeatableRead,
                cancellationToken);
        await using (var readOnly = new NpgsqlCommand(
                         "SET TRANSACTION READ ONLY;",
                         connection,
                         transaction))
        {
            await readOnly.ExecuteNonQueryAsync(cancellationToken);
        }

        var manifest = await ReadPublishedManifestAsync(
            connection,
            transaction,
            cancellationToken) ?? throw new InvalidOperationException(
                "The official pet-content publication is missing.");
        var catalog = await ReadRevisionAsync(
            connection,
            transaction,
            manifest,
            cancellationToken);
        catalog.ValidateItemReferences(itemCatalog);
        await transaction.CommitAsync(cancellationToken);
        return catalog;
    }

    internal static async Task<PetContentManifest?>
        ReadPublishedManifestAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT revision.revision,
                   revision.species_count,
                   revision.aptitude_count,
                   revision.native_profile_count,
                   revision.experience_step_count,
                   revision.rebirth_step_count,
                   revision.merge_savvy_step_count,
                   revision.merge_savvy_lookup_count,
                   revision.hatch_rank_step_count,
                   revision.merge_rank_lookup_count,
                   revision.merge_rank_species_factor_count,
                   revision.merge_rank_spirit_step_count,
                   revision.source,
                   revision.sealed_at IS NOT NULL
            FROM pet_content_publication publication
            JOIN pet_content_revisions revision
              ON revision.revision = publication.revision
            WHERE publication.family = 'pets';
            """,
            connection,
            transaction);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var manifest = new PetContentManifest(
            reader.GetString(0),
            reader.GetInt32(1),
            reader.GetInt32(2),
            reader.GetInt32(3),
            reader.GetInt32(4),
            reader.GetInt32(5),
            reader.GetInt32(6),
            reader.GetInt32(7),
            reader.GetInt32(8),
            reader.GetInt32(9),
            reader.GetInt32(10),
            reader.GetInt32(11),
            reader.GetString(12),
            reader.GetBoolean(13));
        if (await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException(
                "The pet-content publication is ambiguous.");
        }

        ValidateManifest(manifest);
        return manifest;
    }

    internal static async Task<PetContentManifest?>
        ReadRevisionManifestAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            string revision,
            CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT revision, species_count, aptitude_count,
                   native_profile_count, experience_step_count,
                   rebirth_step_count, merge_savvy_step_count,
                   merge_savvy_lookup_count,
                   hatch_rank_step_count, merge_rank_lookup_count,
                   merge_rank_species_factor_count,
                   merge_rank_spirit_step_count,
                   source, sealed_at IS NOT NULL
            FROM pet_content_revisions
            WHERE revision = @revision;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("revision", revision);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new PetContentManifest(
            reader.GetString(0),
            reader.GetInt32(1),
            reader.GetInt32(2),
            reader.GetInt32(3),
            reader.GetInt32(4),
            reader.GetInt32(5),
            reader.GetInt32(6),
            reader.GetInt32(7),
            reader.GetInt32(8),
            reader.GetInt32(9),
            reader.GetInt32(10),
            reader.GetInt32(11),
            reader.GetString(12),
            reader.GetBoolean(13));
    }

    internal static async Task<PinnedPetContentCatalog> ReadRevisionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PetContentManifest manifest,
        CancellationToken cancellationToken,
        bool requireSealed = true)
    {
        ValidateManifest(manifest, requireSealed);
        var settings = await ReadSettingsAsync(
            connection,
            transaction,
            manifest.Revision,
            cancellationToken);
        var species = await ReadSpeciesAsync(
            connection,
            transaction,
            manifest.Revision,
            cancellationToken);
        var aptitudes = await ReadAptitudesAsync(
            connection,
            transaction,
            manifest.Revision,
            cancellationToken);
        var profiles = await ReadProfilesAsync(
            connection,
            transaction,
            manifest.Revision,
            cancellationToken);
        var experience = await ReadExperienceAsync(
            connection,
            transaction,
            manifest.Revision,
            cancellationToken);
        var rebirth = await ReadRebirthAsync(
            connection,
            transaction,
            manifest.Revision,
            cancellationToken);
        var mergeSavvySteps = await ReadMergeSavvyStepsAsync(
            connection,
            transaction,
            manifest.Revision,
            cancellationToken);
        var mergeSavvyLookup = await ReadMergeSavvyLookupAsync(
            connection,
            transaction,
            manifest.Revision,
            cancellationToken);
        var hatchRankSteps = await ReadHatchRankStepsAsync(
            connection,
            transaction,
            manifest.Revision,
            cancellationToken);
        var mergeRankLookup = await ReadMergeRankLookupAsync(
            connection,
            transaction,
            manifest.Revision,
            cancellationToken);
        var mergeRankSpeciesFactors =
            await ReadMergeRankSpeciesFactorsAsync(
                connection,
                transaction,
                manifest.Revision,
                cancellationToken);
        var mergeRankSpiritSteps = await ReadMergeRankSpiritStepsAsync(
            connection,
            transaction,
            manifest.Revision,
            cancellationToken);

        if (species.Count != manifest.SpeciesCount ||
            aptitudes.Count != manifest.AptitudeCount ||
            profiles.Count != manifest.NativeProfileCount ||
            experience.Count != manifest.ExperienceStepCount ||
            rebirth.Count != manifest.RebirthStepCount ||
            mergeSavvySteps.Count != manifest.MergeSavvyStepCount ||
            mergeSavvyLookup.Count != manifest.MergeSavvyLookupCount ||
            hatchRankSteps.Count != manifest.HatchRankStepCount ||
            mergeRankLookup.Count != manifest.MergeRankLookupCount ||
            mergeRankSpeciesFactors.Count !=
                manifest.MergeRankSpeciesFactorCount ||
            mergeRankSpiritSteps.Count != manifest.MergeRankSpiritStepCount)
        {
            throw new InvalidOperationException(
                $"Pet-content revision {manifest.Revision} does not match its declared counts.");
        }

        return PinnedPetContentCatalog.Create(
            manifest.Source,
            settings,
            species,
            aptitudes,
            profiles,
            experience,
            rebirth,
            mergeSavvySteps,
            mergeSavvyLookup,
            hatchRankSteps,
            mergeRankLookup,
            mergeRankSpeciesFactors,
            mergeRankSpiritSteps,
            manifest.Revision);
    }

    private static void ValidateManifest(
        PetContentManifest manifest,
        bool requireSealed = true)
    {
        if (requireSealed && !manifest.Sealed ||
            manifest.Revision.Length != 64 ||
            !manifest.Revision.All(static value =>
                value is >= '0' and <= '9' or >= 'A' and <= 'F') ||
            manifest.SpeciesCount is < 1 or > 1024 ||
            manifest.AptitudeCount is < 1 or > 255 ||
            manifest.NativeProfileCount is < 1 or > 100000 ||
            manifest.ExperienceStepCount is < 1 or > 254 ||
            manifest.RebirthStepCount is < 1 or > 1000 ||
            manifest.MergeSavvyStepCount is < 0 or > 1536 ||
            manifest.MergeSavvyLookupCount is < 0 or > 1024 ||
            manifest.HatchRankStepCount is < 0 or > 1024 ||
            manifest.MergeRankLookupCount is < 0 or > 1024 ||
            manifest.MergeRankSpeciesFactorCount is < 0 or > 1024 ||
            manifest.MergeRankSpiritStepCount is < 0 or > 100 ||
            string.IsNullOrWhiteSpace(manifest.Source) ||
            manifest.Source.Length > 96)
        {
            throw new InvalidOperationException(
                "The published pet-content manifest is malformed or unsealed.");
        }
    }
}

internal sealed record PetContentManifest(
    string Revision,
    int SpeciesCount,
    int AptitudeCount,
    int NativeProfileCount,
    int ExperienceStepCount,
    int RebirthStepCount,
    int MergeSavvyStepCount,
    int MergeSavvyLookupCount,
    int HatchRankStepCount,
    int MergeRankLookupCount,
    int MergeRankSpeciesFactorCount,
    int MergeRankSpiritStepCount,
    string Source,
    bool Sealed)
{
    public int EntryCount => checked(
        SpeciesCount + AptitudeCount + NativeProfileCount +
        ExperienceStepCount + RebirthStepCount + MergeSavvyStepCount +
        MergeSavvyLookupCount + HatchRankStepCount + MergeRankLookupCount +
        MergeRankSpeciesFactorCount + MergeRankSpiritStepCount + 1);
}
