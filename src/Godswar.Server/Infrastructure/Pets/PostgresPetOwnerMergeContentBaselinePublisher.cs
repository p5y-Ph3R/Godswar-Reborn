using System.Data;
using System.Text.Json;
using Godswar.Server.Application.Pets;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.Infrastructure.Pets;

internal static class PostgresPetOwnerMergeContentBaselinePublisher
{
    private const long PublicationLockId = 0x5045544D45524745;
    private const int MaximumPayloadBytes = 1024 * 1024;

    public static async Task<PetOwnerMergeContentPublicationResult>
        EnsurePublishedAsync(
            NpgsqlDataSource dataSource,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        await using var connection =
            await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction =
            await connection.BeginTransactionAsync(
                IsolationLevel.Serializable,
                cancellationToken);
        await AcquireLockAsync(connection, transaction, cancellationToken);

        var baseline = PetOwnerMergeContentBaseline.Create();
        var published = await PostgresPetOwnerMergeContentReader
            .ReadPublishedManifestAsync(
                connection,
                transaction,
                cancellationToken);
        if (published is not null)
        {
            _ = await PostgresPetOwnerMergeContentReader.ReadRevisionAsync(
                connection,
                transaction,
                published,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new PetOwnerMergeContentPublicationResult(
                published.Revision,
                published.EntryCount,
                Created: false);
        }

        var existing = await PostgresPetOwnerMergeContentReader
            .ReadRevisionManifestAsync(
                connection,
                transaction,
                baseline.Revision.Sha256,
                cancellationToken);
        if (existing is not null)
        {
            throw new InvalidDataException(
                "An unpublished pet owner-Merge baseline revision already exists.");
        }

        await InsertRevisionAsync(
            connection,
            transaction,
            baseline,
            cancellationToken);
        await InsertEffectBasesAsync(
            connection,
            transaction,
            baseline,
            cancellationToken);
        await InsertBandsAsync(
            connection,
            transaction,
            baseline,
            cancellationToken);
        await InsertRatesAsync(
            connection,
            transaction,
            baseline,
            cancellationToken);
        await PublishAsync(
            connection,
            transaction,
            baseline.Revision.Sha256,
            cancellationToken);

        published = await PostgresPetOwnerMergeContentReader
            .ReadPublishedManifestAsync(
                connection,
                transaction,
                cancellationToken) ?? throw new InvalidOperationException(
                    "The pet owner-Merge publication pointer was not created.");
        _ = await PostgresPetOwnerMergeContentReader.ReadRevisionAsync(
            connection,
            transaction,
            published,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new PetOwnerMergeContentPublicationResult(
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
        PinnedPetOwnerMergeContentCatalog baseline,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO pet_owner_merge_content_revisions (
                revision, policy_version, effect_base_count,
                band_count, rate_count, source
            )
            VALUES (
                @revision, @policyVersion, @effectBaseCount,
                @bandCount, @rateCount, @source
            );
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue(
            "revision",
            baseline.Revision.Sha256);
        command.Parameters.AddWithValue(
            "policyVersion",
            baseline.Revision.PolicyVersion);
        command.Parameters.AddWithValue(
            "effectBaseCount",
            checked((short)baseline.Revision.EffectBaseCount));
        command.Parameters.AddWithValue(
            "bandCount",
            checked((short)baseline.Revision.BandCount));
        command.Parameters.AddWithValue(
            "rateCount",
            checked((short)baseline.Revision.RateCount));
        command.Parameters.AddWithValue("source", baseline.Revision.Source);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidDataException(
                "The pet owner-Merge revision was not inserted exactly.");
        }
    }

    private static Task InsertEffectBasesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PinnedPetOwnerMergeContentCatalog baseline,
        CancellationToken cancellationToken) =>
        InsertJsonRowsAsync(
            """
            INSERT INTO pet_owner_merge_effect_bases (
                revision, effect_code, base_value
            )
            SELECT @revision,
                   (value->>'EffectCode')::smallint,
                   (value->>'BaseValue')::numeric
            FROM jsonb_array_elements(@payload) value;
            """,
            baseline.Revision.Sha256,
            baseline.EffectBases.Select(static value => new
            {
                EffectCode = (short)value.Effect,
                value.BaseValue
            }),
            baseline.Revision.EffectBaseCount,
            connection,
            transaction,
            cancellationToken);

    private static Task InsertBandsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PinnedPetOwnerMergeContentCatalog baseline,
        CancellationToken cancellationToken) =>
        InsertJsonRowsAsync(
            """
            INSERT INTO pet_owner_merge_savvy_bands (
                revision, band_index, minimum_savvy, maximum_savvy
            )
            SELECT @revision,
                   (value->>'BandIndex')::smallint,
                   (value->>'MinimumSavvy')::numeric,
                   CASE
                       WHEN value->'MaximumSavvy' = 'null'::jsonb THEN NULL
                       ELSE (value->>'MaximumSavvy')::numeric
                   END
            FROM jsonb_array_elements(@payload) value;
            """,
            baseline.Revision.Sha256,
            baseline.Bands,
            baseline.Revision.BandCount,
            connection,
            transaction,
            cancellationToken);

    private static Task InsertRatesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PinnedPetOwnerMergeContentCatalog baseline,
        CancellationToken cancellationToken) =>
        InsertJsonRowsAsync(
            """
            INSERT INTO pet_owner_merge_rates (
                revision, source_savvy, effect_code,
                band_index, rate_per_savvy
            )
            SELECT @revision,
                   value->>'SourceSavvy',
                   (value->>'EffectCode')::smallint,
                   (value->>'BandIndex')::smallint,
                   (value->>'RatePerSavvy')::numeric
            FROM jsonb_array_elements(@payload) value;
            """,
            baseline.Revision.Sha256,
            baseline.Rates.Select(static value => new
            {
                SourceSavvy = ToDatabaseSavvy(value.SourceSavvy),
                EffectCode = (short)value.Effect,
                value.BandIndex,
                value.RatePerSavvy
            }),
            baseline.Revision.RateCount,
            connection,
            transaction,
            cancellationToken);

    private static async Task InsertJsonRowsAsync<T>(
        string sql,
        string revision,
        IEnumerable<T> values,
        int expectedCount,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        var snapshot = values.ToArray();
        var payload = JsonSerializer.Serialize(snapshot);
        if (snapshot.Length != expectedCount ||
            System.Text.Encoding.UTF8.GetByteCount(payload) is 0 or >
                MaximumPayloadBytes)
        {
            throw new InvalidOperationException(
                "The pet owner-Merge baseline payload is incomplete or oversized.");
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
        if (await command.ExecuteNonQueryAsync(cancellationToken) !=
            expectedCount)
        {
            throw new InvalidDataException(
                "The pet owner-Merge definitions were not inserted exactly.");
        }
    }

    private static async Task PublishAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string revision,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO pet_owner_merge_content_publication (
                family, revision
            )
            VALUES ('pet-owner-merge', @revision);
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("revision", revision);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidDataException(
                "The pet owner-Merge publication was not inserted exactly.");
        }
    }

    private static string ToDatabaseSavvy(
        PetOwnerMergeSavvyStat value) =>
        value switch
        {
            PetOwnerMergeSavvyStat.Agility => "agility",
            PetOwnerMergeSavvyStat.Strength => "strength",
            PetOwnerMergeSavvyStat.Accuracy => "accuracy",
            PetOwnerMergeSavvyStat.Technique => "technique",
            PetOwnerMergeSavvyStat.Wisdom => "wisdom",
            PetOwnerMergeSavvyStat.Luck => "luck",
            _ => throw new ArgumentOutOfRangeException(nameof(value))
        };
}

internal sealed record PetOwnerMergeContentPublicationResult(
    string Revision,
    int EntryCount,
    bool Created);
