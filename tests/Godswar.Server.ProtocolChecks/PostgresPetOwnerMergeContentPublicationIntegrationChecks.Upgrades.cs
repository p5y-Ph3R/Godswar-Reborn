using Godswar.Server.Application.Pets;
using Godswar.Server.Infrastructure.Pets;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class
    PostgresPetOwnerMergeContentPublicationIntegrationChecks
{
    private const string ReviewedV1Source =
        "reviewed-pet-owner-merge-v1";
    private const string ReviewedV1Policy =
        "project-pet-unite-piecewise-marginal-v2";
    private const string ReviewedV1Revision =
        "E6A6FA22C0D2AEE9D6B2E7C968D903E05E0576E6D40A179DC2A3715F434A4929";
    private const string ReviewedV2Source =
        "reviewed-pet-owner-merge-v2";
    private const string ReviewedV2Policy =
        "project-pet-unite-piecewise-marginal-v3";
    private const string ReviewedV2Revision =
        "EEA02574B39EDED6DBEFCACF80337AAE0166A44366115AB7E8360DD39B36C84D";
    private const long PublicationLockId = 5784121987279505221L;

    private static async Task AssertReviewedPredecessorsPromoteToV3Async(
        NpgsqlDataSource dataSource,
        PinnedPetOwnerMergeContentCatalog current)
    {
        try
        {
            foreach (var predecessor in new[]
                     {
                         CreateReviewedV1Catalog(current),
                         CreateReviewedV2Catalog(current)
                     })
            {
                await PublishFixtureAsync(dataSource, current, predecessor);
                var before = await PostgresPetOwnerMergeContentReader.LoadAsync(
                    dataSource);
                Check.Equal(
                    predecessor.Revision.Sha256,
                    before.Revision.Sha256,
                    "integration fixture publishes an exact reviewed predecessor");

                var result = await PostgresPetOwnerMergeContentBaselinePublisher
                    .EnsurePublishedAsync(dataSource);
                var after = await PostgresPetOwnerMergeContentReader.LoadAsync(
                    dataSource);
                Check.True(
                    result.Created &&
                    result.Revision == current.Revision.Sha256 &&
                    after.Revision.Sha256 == current.Revision.Sha256 &&
                    await IsSealedAsync(
                        dataSource,
                        predecessor.Revision.Sha256),
                    "reviewed V1/V2 publications promote to sealed V3 without rewriting history");
            }
        }
        finally
        {
            await SetPublishedRevisionAsync(
                dataSource,
                current.Revision.Sha256);
        }
    }

    private static async Task AssertUnknownPublicationIsPreservedAsync(
        NpgsqlDataSource dataSource,
        PinnedPetOwnerMergeContentCatalog current)
    {
        var custom = PinnedPetOwnerMergeContentCatalog.Create(
            "integration-test-owner-merge-custom-v1",
            "integration-test-owner-merge-policy-v1",
            current.EffectBases,
            current.Bands,
            current.Rates);
        try
        {
            await PublishFixtureAsync(dataSource, current, custom);
            var result = await PostgresPetOwnerMergeContentBaselinePublisher
                .EnsurePublishedAsync(dataSource);
            var after = await PostgresPetOwnerMergeContentReader.LoadAsync(
                dataSource);
            Check.True(
                !result.Created &&
                result.Revision == custom.Revision.Sha256 &&
                after.Revision.Sha256 == custom.Revision.Sha256 &&
                after.Revision.Source == custom.Revision.Source,
                "an unrelated complete official publication remains authoritative");
        }
        finally
        {
            await SetPublishedRevisionAsync(
                dataSource,
                current.Revision.Sha256);
        }
    }

    private static PinnedPetOwnerMergeContentCatalog CreateReviewedV1Catalog(
        PinnedPetOwnerMergeContentCatalog current)
    {
        var v2 = CreateReviewedV2Catalog(current);
        decimal[] reboundRates = [1.5m, 1.275m, 1.05m, 0.9m, 0.75m];
        var rates = v2.Rates.Select(value =>
            value.SourceSavvy == PetOwnerMergeSavvyStat.Agility &&
            value.Effect == PetOwnerMergeEffectCode.DamageRebound
                ? value with
                {
                    RatePerSavvy = reboundRates[value.BandIndex - 1]
                }
                : value).ToArray();
        return PinnedPetOwnerMergeContentCatalog.Create(
            ReviewedV1Source,
            ReviewedV1Policy,
            v2.EffectBases,
            v2.Bands,
            rates,
            ReviewedV1Revision);
    }

    private static PinnedPetOwnerMergeContentCatalog CreateReviewedV2Catalog(
        PinnedPetOwnerMergeContentCatalog current)
    {
        var bases = current.EffectBases.Select(static value =>
            value.Effect is
                PetOwnerMergeEffectCode.PhysicalDamageReduction or
                PetOwnerMergeEffectCode.MagicDamageReduction
                ? value with { BaseValue = value.BaseValue / 2m }
                : value).ToArray();
        var rates = current.Rates.Select(static value =>
            value.Effect is
                PetOwnerMergeEffectCode.PhysicalDamageReduction or
                PetOwnerMergeEffectCode.MagicDamageReduction
                ? value with { RatePerSavvy = value.RatePerSavvy / 2m }
                : value).ToArray();
        return PinnedPetOwnerMergeContentCatalog.Create(
            ReviewedV2Source,
            ReviewedV2Policy,
            bases,
            current.Bands,
            rates,
            ReviewedV2Revision);
    }

    private static async Task PublishFixtureAsync(
        NpgsqlDataSource dataSource,
        PinnedPetOwnerMergeContentCatalog template,
        PinnedPetOwnerMergeContentCatalog fixture)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await ExecuteAsync(
            connection,
            transaction,
            "SELECT pg_advisory_xact_lock(@lockId);",
            ("lockId", PublicationLockId));
        var exists = await ReadScalarAsync<bool>(
            connection,
            transaction,
            "SELECT EXISTS (SELECT 1 FROM pet_owner_merge_content_revisions WHERE revision = @revision);",
            ("revision", fixture.Revision.Sha256));
        if (!exists)
        {
            await InsertFixtureAsync(
                connection,
                transaction,
                template,
                fixture);
        }

        await SetPublishedRevisionAsync(
            connection,
            transaction,
            fixture.Revision.Sha256);
        await transaction.CommitAsync();

        var loaded = await PostgresPetOwnerMergeContentReader.LoadAsync(
            dataSource);
        Check.Equal(
            fixture.Revision.Sha256,
            loaded.Revision.Sha256,
            "integration fixture is complete, sealed, and hash-valid");
    }

    private static async Task InsertFixtureAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PinnedPetOwnerMergeContentCatalog template,
        PinnedPetOwnerMergeContentCatalog fixture)
    {
        await ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO pet_owner_merge_content_revisions (
                revision, policy_version, effect_base_count,
                band_count, rate_count, source
            ) VALUES (
                @revision, @policy, @baseCount,
                @bandCount, @rateCount, @source
            );
            """,
            ("revision", fixture.Revision.Sha256),
            ("policy", fixture.Revision.PolicyVersion),
            ("baseCount", checked((short)fixture.EffectBases.Count)),
            ("bandCount", checked((short)fixture.Bands.Count)),
            ("rateCount", checked((short)fixture.Rates.Count)),
            ("source", fixture.Revision.Source));
        await ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO pet_owner_merge_savvy_bands (
                revision, band_index, minimum_savvy, maximum_savvy
            ) SELECT @fixture, band_index, minimum_savvy, maximum_savvy
              FROM pet_owner_merge_savvy_bands
             WHERE revision = @template;
            """,
            ("fixture", fixture.Revision.Sha256),
            ("template", template.Revision.Sha256));

        foreach (var effectBase in fixture.EffectBases)
        {
            await ExecuteAsync(
                connection,
                transaction,
                """
                INSERT INTO pet_owner_merge_effect_bases (
                    revision, effect_code, base_value
                ) VALUES (@revision, @effect, @baseValue);
                """,
                ("revision", fixture.Revision.Sha256),
                ("effect", checked((short)effectBase.Effect)),
                ("baseValue", effectBase.BaseValue));
        }

        foreach (var rate in fixture.Rates)
        {
            await ExecuteAsync(
                connection,
                transaction,
                """
                INSERT INTO pet_owner_merge_rates (
                    revision, source_savvy, effect_code,
                    band_index, rate_per_savvy
                ) VALUES (
                    @revision, @source, @effect,
                    @band, @rate
                );
                """,
                ("revision", fixture.Revision.Sha256),
                ("source", ToDatabaseSavvy(rate.SourceSavvy)),
                ("effect", checked((short)rate.Effect)),
                ("band", rate.BandIndex),
                ("rate", rate.RatePerSavvy));
        }
    }

    private static async Task SetPublishedRevisionAsync(
        NpgsqlDataSource dataSource,
        string revision)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await ExecuteAsync(
            connection,
            transaction,
            "SELECT pg_advisory_xact_lock(@lockId);",
            ("lockId", PublicationLockId));
        await SetPublishedRevisionAsync(connection, transaction, revision);
        await transaction.CommitAsync();
    }

    private static Task SetPublishedRevisionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string revision) =>
        ExecuteAsync(
            connection,
            transaction,
            """
            UPDATE pet_owner_merge_content_publication
               SET revision = @revision,
                   published_at = now()
             WHERE family = 'pet-owner-merge';
            """,
            ("revision", revision));

    private static async Task<bool> IsSealedAsync(
        NpgsqlDataSource dataSource,
        string revision) =>
        await ReadScalarAsync<bool>(
            dataSource,
            """
            SELECT sealed_at IS NOT NULL
              FROM pet_owner_merge_content_revisions
             WHERE revision = @revision;
            """,
            ("revision", revision));

    private static async Task<T> ReadScalarAsync<T>(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(
            sql,
            connection,
            transaction);
        AddParameters(command, parameters);
        return (T)(await command.ExecuteScalarAsync()
            ?? throw new InvalidOperationException(
                "Owner-Merge fixture query returned no value."));
    }

    private static async Task ExecuteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(
            sql,
            connection,
            transaction);
        AddParameters(command, parameters);
        _ = await command.ExecuteNonQueryAsync();
    }

    private static void AddParameters(
        NpgsqlCommand command,
        IEnumerable<(string Name, object Value)> parameters)
    {
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(
                parameter.Name,
                parameter.Value);
        }
    }

    private static string ToDatabaseSavvy(PetOwnerMergeSavvyStat value) =>
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
