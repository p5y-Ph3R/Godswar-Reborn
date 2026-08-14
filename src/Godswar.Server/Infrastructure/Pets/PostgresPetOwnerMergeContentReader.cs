using System.Data;
using Godswar.Server.Application.Pets;
using Npgsql;

namespace Godswar.Server.Infrastructure.Pets;

internal static class PostgresPetOwnerMergeContentReader
{
    public static async Task<PinnedPetOwnerMergeContentCatalog> LoadAsync(
        NpgsqlDataSource dataSource,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
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
                "The official pet owner-Merge publication is missing.");
        var catalog = await ReadRevisionAsync(
            connection,
            transaction,
            manifest,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return catalog;
    }

    internal static async Task<PetOwnerMergeContentManifest?>
        ReadPublishedManifestAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT revision.revision, revision.policy_version,
                   revision.effect_base_count, revision.band_count,
                   revision.rate_count, revision.source,
                   revision.sealed_at IS NOT NULL
            FROM pet_owner_merge_content_publication publication
            JOIN pet_owner_merge_content_revisions revision
              ON revision.revision = publication.revision
            WHERE publication.family = 'pet-owner-merge';
            """,
            connection,
            transaction);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var manifest = ReadManifest(reader);
        if (await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException(
                "The pet owner-Merge publication is ambiguous.");
        }
        ValidateManifest(manifest);
        return manifest;
    }

    internal static async Task<PetOwnerMergeContentManifest?>
        ReadRevisionManifestAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            string revision,
            CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT revision, policy_version, effect_base_count, band_count,
                   rate_count, source, sealed_at IS NOT NULL
            FROM pet_owner_merge_content_revisions
            WHERE revision = @revision;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("revision", revision);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? ReadManifest(reader)
            : null;
    }

    internal static async Task<PinnedPetOwnerMergeContentCatalog>
        ReadRevisionAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            PetOwnerMergeContentManifest manifest,
            CancellationToken cancellationToken,
            bool requireSealed = true)
    {
        ValidateManifest(manifest, requireSealed);
        var effectBases = await ReadEffectBasesAsync(
            connection,
            transaction,
            manifest.Revision,
            cancellationToken);
        var bands = await ReadBandsAsync(
            connection,
            transaction,
            manifest.Revision,
            cancellationToken);
        var rates = await ReadRatesAsync(
            connection,
            transaction,
            manifest.Revision,
            cancellationToken);
        if (effectBases.Count != manifest.EffectBaseCount ||
            bands.Count != manifest.BandCount ||
            rates.Count != manifest.RateCount)
        {
            throw new InvalidOperationException(
                $"Pet owner-Merge revision {manifest.Revision} does not match its declared counts.");
        }

        return PinnedPetOwnerMergeContentCatalog.Create(
            manifest.Source,
            manifest.PolicyVersion,
            effectBases,
            bands,
            rates,
            manifest.Revision);
    }

    private static async Task<IReadOnlyList<
        PetOwnerMergeEffectBaseContentDefinition>> ReadEffectBasesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string revision,
        CancellationToken cancellationToken)
    {
        var values = new List<PetOwnerMergeEffectBaseContentDefinition>();
        await using var command = CreateRevisionCommand(
            """
            SELECT effect_code, base_value
            FROM pet_owner_merge_effect_bases
            WHERE revision = @revision
            ORDER BY effect_code;
            """,
            connection,
            transaction,
            revision);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var effect = (PetOwnerMergeEffectCode)reader.GetInt16(0);
            if (!Enum.IsDefined(effect))
            {
                throw new InvalidDataException(
                    "Pet owner-Merge content contains an unknown effect code.");
            }
            values.Add(new(effect, reader.GetDecimal(1)));
        }
        return values;
    }

    private static async Task<IReadOnlyList<
        PetOwnerMergeBandContentDefinition>> ReadBandsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string revision,
        CancellationToken cancellationToken)
    {
        var values = new List<PetOwnerMergeBandContentDefinition>();
        await using var command = CreateRevisionCommand(
            """
            SELECT band_index, minimum_savvy, maximum_savvy
            FROM pet_owner_merge_savvy_bands
            WHERE revision = @revision
            ORDER BY band_index;
            """,
            connection,
            transaction,
            revision);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            values.Add(new(
                reader.GetInt16(0),
                reader.GetDecimal(1),
                reader.IsDBNull(2) ? null : reader.GetDecimal(2)));
        }
        return values;
    }

    private static async Task<IReadOnlyList<
        PetOwnerMergeRateContentDefinition>> ReadRatesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string revision,
        CancellationToken cancellationToken)
    {
        var values = new List<PetOwnerMergeRateContentDefinition>();
        await using var command = CreateRevisionCommand(
            """
            SELECT source_savvy, effect_code, band_index, rate_per_savvy
            FROM pet_owner_merge_rates
            WHERE revision = @revision
            ORDER BY source_savvy, effect_code, band_index;
            """,
            connection,
            transaction,
            revision);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var effect = (PetOwnerMergeEffectCode)reader.GetInt16(1);
            if (!Enum.IsDefined(effect))
            {
                throw new InvalidDataException(
                    "Pet owner-Merge content contains an unknown effect code.");
            }
            values.Add(new(
                ParseSavvy(reader.GetString(0)),
                effect,
                reader.GetInt16(2),
                reader.GetDecimal(3)));
        }
        return values;
    }

    private static PetOwnerMergeSavvyStat ParseSavvy(string value) =>
        value switch
        {
            "agility" => PetOwnerMergeSavvyStat.Agility,
            "strength" => PetOwnerMergeSavvyStat.Strength,
            "accuracy" => PetOwnerMergeSavvyStat.Accuracy,
            "technique" => PetOwnerMergeSavvyStat.Technique,
            "wisdom" => PetOwnerMergeSavvyStat.Wisdom,
            "luck" => PetOwnerMergeSavvyStat.Luck,
            _ => throw new InvalidDataException(
                $"Pet owner-Merge content has unknown Savvy source '{value}'.")
        };

    private static PetOwnerMergeContentManifest ReadManifest(
        NpgsqlDataReader reader) =>
        new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetInt16(2),
            reader.GetInt16(3),
            reader.GetInt16(4),
            reader.GetString(5),
            reader.GetBoolean(6));

    private static void ValidateManifest(
        PetOwnerMergeContentManifest manifest,
        bool requireSealed = true)
    {
        if (requireSealed && !manifest.Sealed ||
            manifest.Revision.Length != 64 ||
            !manifest.Revision.All(static value =>
                value is >= '0' and <= '9' or >= 'A' and <= 'F') ||
            string.IsNullOrWhiteSpace(manifest.PolicyVersion) ||
            manifest.PolicyVersion.Length > 64 ||
            manifest.EffectBaseCount !=
                Enum.GetValues<PetOwnerMergeEffectCode>().Length ||
            manifest.BandCount != 5 ||
            manifest.RateCount != 95 ||
            string.IsNullOrWhiteSpace(manifest.Source) ||
            manifest.Source.Length > 96)
        {
            throw new InvalidOperationException(
                "The published pet owner-Merge manifest is malformed or unsealed.");
        }
    }

    private static NpgsqlCommand CreateRevisionCommand(
        string sql,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string revision)
    {
        var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("revision", revision);
        return command;
    }
}

internal sealed record PetOwnerMergeContentManifest(
    string Revision,
    string PolicyVersion,
    short EffectBaseCount,
    short BandCount,
    short RateCount,
    string Source,
    bool Sealed)
{
    public int EntryCount => checked(
        EffectBaseCount + BandCount + RateCount);
}
