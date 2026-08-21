using Godswar.Server.Infrastructure.Database;
using Godswar.Server.Infrastructure.Pets;
using Godswar.Server.Application.Pets;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresPetOwnerMergeContentPublicationIntegrationChecks
{
    public const string CheckName =
        "PostgreSQL immutable pet owner-Merge publication";

    private const string ConnectionStringVariable =
        "GODSWAR_TEST_POSTGRES_CONNECTION_STRING";

    public static async Task RunAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable(
            ConnectionStringVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Console.WriteLine(
                $"SKIP {CheckName} ({ConnectionStringVariable} is not set)");
            return;
        }

        await PostgresSchemaStartup.InitializeAsync(connectionString);
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var first = await PostgresPetOwnerMergeContentBaselinePublisher
            .EnsurePublishedAsync(dataSource);
        var loaded = await PostgresPetOwnerMergeContentReader.LoadAsync(
            dataSource);
        Check.True(
            first.Revision == loaded.Revision.Sha256 &&
            first.EntryCount == 116 &&
            loaded.EffectBases.Count == 16 &&
            loaded.Bands.Count == 5 &&
            loaded.Rates.Count == 95 &&
            loaded.Revision.Source == PetOwnerMergeContentBaseline.Source &&
            loaded.Rates.Where(static value =>
                    value.SourceSavvy == PetOwnerMergeSavvyStat.Agility &&
                    value.Effect == PetOwnerMergeEffectCode.DamageRebound)
                .All(static value => value.RatePerSavvy == 0m),
            "owner-Merge bootstrap pins one complete SHA-256 publication");
        var publishedRows = await ReadScalarAsync<long>(
            dataSource,
            """
            SELECT count(*)
            FROM published_pet_owner_merge_balance
            WHERE revision = @revision
              AND btrim(source_savvy) <> ''
              AND btrim(effect_key) <> '';
            """,
            ("revision", loaded.Revision.Sha256));
        Check.Equal(
            95L,
            publishedRows,
            "web administration view exposes every named active rate");

        var repeated = await PostgresPetOwnerMergeContentBaselinePublisher
            .EnsurePublishedAsync(dataSource);
        Check.True(
            !repeated.Created &&
            repeated.Revision == loaded.Revision.Sha256,
            "owner-Merge publication is idempotent and preserves the official pointer");

        await AssertReviewedPredecessorsPromoteToV3Async(
            dataSource,
            loaded);
        await AssertUnknownPublicationIsPreservedAsync(dataSource, loaded);
        await AssertSealedContentRejectsMutationAsync(
            dataSource,
            loaded.Revision.Sha256);
        await AssertIncompletePublicationRejectedAsync(dataSource);
        await AssertProjectionRevisionForeignKeyAsync(
            dataSource,
            loaded.Revision.Sha256);
    }

    private static async Task AssertSealedContentRejectsMutationAsync(
        NpgsqlDataSource dataSource,
        string revision)
    {
        await AssertRejectedAsync(
            dataSource,
            """
            UPDATE pet_owner_merge_effect_bases
            SET base_value = base_value + 1
            WHERE revision = @revision
              AND effect_code = 0;
            """,
            ("revision", revision),
            "sealed owner-Merge bases reject mutation");
        await AssertRejectedAsync(
            dataSource,
            """
            DELETE FROM pet_owner_merge_content_publication
            WHERE family = 'pet-owner-merge';
            """,
            ("revision", revision),
            "official owner-Merge publication rejects deletion");
    }

    private static async Task AssertIncompletePublicationRejectedAsync(
        NpgsqlDataSource dataSource)
    {
        var revision = Convert.ToHexString(
            System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        try
        {
            await using (var header = new NpgsqlCommand(
                """
                INSERT INTO pet_owner_merge_content_revisions (
                    revision, policy_version, effect_base_count,
                    band_count, rate_count, source
                ) VALUES (
                    @revision, 'incomplete-test-v1', 16, 5, 95,
                    'integration-test'
                );
                """,
                connection,
                transaction))
            {
                header.Parameters.AddWithValue("revision", revision);
                await header.ExecuteNonQueryAsync();
            }

            await using var publish = new NpgsqlCommand(
                """
                UPDATE pet_owner_merge_content_publication
                SET revision = @revision,
                    published_at = now()
                WHERE family = 'pet-owner-merge';
                """,
                connection,
                transaction);
            publish.Parameters.AddWithValue("revision", revision);
            await publish.ExecuteNonQueryAsync();
        }
        catch (PostgresException exception) when (
            exception.MessageText.Contains(
                "is incomplete",
                StringComparison.Ordinal))
        {
            await transaction.RollbackAsync();
            return;
        }

        await transaction.RollbackAsync();
        throw new InvalidOperationException(
            "An incomplete owner-Merge revision became official.");
    }

    private static async Task AssertProjectionRevisionForeignKeyAsync(
        NpgsqlDataSource dataSource,
        string revision)
    {
        var persisted = await ReadScalarAsync<string>(
            dataSource,
            """
            SELECT revision
            FROM pet_owner_merge_content_revisions
            WHERE revision = @revision;
            """,
            ("revision", revision));
        Check.Equal(
            revision,
            persisted,
            "derived owner-Merge bonuses can reference the official balance revision");

        await AssertRejectedAsync(
            dataSource,
            """
            UPDATE character_pet_character_bonuses
            SET balance_revision = repeat('A', 64)
            WHERE pet_id = (
                SELECT pet_id
                FROM character_pet_character_bonuses
                LIMIT 1
            );
            """,
            ("revision", revision),
            "derived owner-Merge bonuses reject unknown balance revisions",
            allowNoRows: true);
    }

    private static async Task AssertRejectedAsync(
        NpgsqlDataSource dataSource,
        string sql,
        (string Name, object Value) parameter,
        string assertion,
        bool allowNoRows = false)
    {
        try
        {
            await using var command = dataSource.CreateCommand(sql);
            command.Parameters.AddWithValue(
                parameter.Name,
                parameter.Value);
            var affected = await command.ExecuteNonQueryAsync();
            if (allowNoRows && affected == 0)
            {
                return;
            }
        }
        catch (PostgresException)
        {
            return;
        }

        throw new InvalidOperationException(assertion + " was accepted.");
    }

    private static async Task<T> ReadScalarAsync<T>(
        NpgsqlDataSource dataSource,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var command = dataSource.CreateCommand(sql);
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(
                parameter.Name,
                parameter.Value);
        }
        return (T)(await command.ExecuteScalarAsync()
            ?? throw new InvalidOperationException(
                "Owner-Merge publication query returned no value."));
    }
}
