using System.Data;
using Godswar.Server.Application.Items;
using Godswar.Server.Application.Pets;
using Npgsql;

namespace Godswar.Server.Infrastructure.Pets;

internal static partial class PostgresPetContentBaselinePublisher
{
    private const long PublicationLockId = 0x504554434F4E544E;

    public static async Task<PetContentPublicationResult>
        EnsurePublishedAsync(
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
                IsolationLevel.Serializable,
                cancellationToken);
        await AcquireLockAsync(connection, transaction, cancellationToken);

        var existing = await PostgresPetContentReader
            .ReadPublishedManifestAsync(
                connection,
                transaction,
                cancellationToken);
        if (existing is not null)
        {
            var catalog = await PostgresPetContentReader.ReadRevisionAsync(
                connection,
                transaction,
                existing,
                cancellationToken);
            catalog.ValidateItemReferences(itemCatalog);
            await transaction.CommitAsync(cancellationToken);
            return new PetContentPublicationResult(
                existing.Revision,
                existing.EntryCount,
                Created: false);
        }

        var baseline = PetContentBaseline.Create();
        baseline.ValidateItemReferences(itemCatalog);
        await InsertRevisionAsync(
            connection,
            transaction,
            baseline,
            cancellationToken);
        await InsertSettingsAsync(
            connection,
            transaction,
            baseline,
            cancellationToken);
        await InsertDefinitionsAsync(
            connection,
            transaction,
            baseline,
            cancellationToken);

        var insertedManifest = await PostgresPetContentReader
            .ReadRevisionManifestAsync(
                connection,
                transaction,
                baseline.Revision.Sha256,
                cancellationToken) ?? throw new InvalidOperationException(
                    "The inserted pet-content revision disappeared.");
        if (insertedManifest.SpeciesCount != baseline.Revision.SpeciesCount ||
            insertedManifest.AptitudeCount != baseline.Revision.AptitudeCount ||
            insertedManifest.NativeProfileCount !=
                baseline.Revision.NativeProfileCount ||
            insertedManifest.ExperienceStepCount !=
                baseline.Revision.ExperienceStepCount ||
            insertedManifest.RebirthStepCount !=
                baseline.Revision.RebirthStepCount ||
            !insertedManifest.Source.Equals(
                baseline.Revision.Source,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "An existing pet-content revision conflicts with the reviewed baseline.");
        }

        var inserted = await PostgresPetContentReader.ReadRevisionAsync(
            connection,
            transaction,
            insertedManifest,
            cancellationToken,
            requireSealed: false);
        inserted.ValidateItemReferences(itemCatalog);
        await PublishAsync(
            connection,
            transaction,
            baseline.Revision.Sha256,
            cancellationToken);

        var published = await PostgresPetContentReader
            .ReadPublishedManifestAsync(
                connection,
                transaction,
                cancellationToken) ?? throw new InvalidOperationException(
                    "The pet-content publication pointer was not created.");
        _ = await PostgresPetContentReader.ReadRevisionAsync(
            connection,
            transaction,
            published,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new PetContentPublicationResult(
            published.Revision,
            published.EntryCount,
            Created: true);
    }

    private static async Task AcquireLockAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(@lockId);",
            connection,
            transaction);
        command.Parameters.AddWithValue("lockId", PublicationLockId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertRevisionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PinnedPetContentCatalog baseline,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO pet_content_revisions (
                revision, species_count, aptitude_count,
                native_profile_count, experience_step_count,
                rebirth_step_count, source)
            VALUES (
                @revision, @speciesCount, @aptitudeCount,
                @profileCount, @experienceCount, @rebirthCount, @source)
            ON CONFLICT (revision) DO NOTHING;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("revision", baseline.Revision.Sha256);
        command.Parameters.AddWithValue(
            "speciesCount",
            baseline.Revision.SpeciesCount);
        command.Parameters.AddWithValue(
            "aptitudeCount",
            baseline.Revision.AptitudeCount);
        command.Parameters.AddWithValue(
            "profileCount",
            baseline.Revision.NativeProfileCount);
        command.Parameters.AddWithValue(
            "experienceCount",
            baseline.Revision.ExperienceStepCount);
        command.Parameters.AddWithValue(
            "rebirthCount",
            baseline.Revision.RebirthStepCount);
        command.Parameters.AddWithValue("source", baseline.Revision.Source);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task PublishAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string revision,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO pet_content_publication (family, revision)
            VALUES ('pets', @revision);
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("revision", revision);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
