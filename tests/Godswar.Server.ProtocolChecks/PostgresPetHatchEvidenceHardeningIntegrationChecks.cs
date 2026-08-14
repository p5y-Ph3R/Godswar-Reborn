using System.Text.RegularExpressions;
using Godswar.Server.Application.Pets;
using Godswar.Server.Infrastructure.Database;
using Godswar.Server.Infrastructure.Items;
using Godswar.Server.Infrastructure.Pets;
using Godswar.Server.State;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.ProtocolChecks;

internal static class PostgresPetHatchEvidenceHardeningIntegrationChecks
{
    public const string CheckName =
        "PostgreSQL immutable pet hatch-rank evidence integration";
    private const string ConnectionStringVariable =
        "GODSWAR_TEST_POSTGRES_CONNECTION_STRING";
    private static readonly Regex DisposableDatabasePattern = new(
        @"^godswar_(?:b03_[a-f0-9]{10}_smoke_[0-9]{2}|b12_[a-z0-9_]{1,40})$",
        RegexOptions.CultureInvariant);

    public static async Task RunAsync()
    {
        var connectionString =
            Environment.GetEnvironmentVariable(ConnectionStringVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Console.WriteLine(
                $"SKIP {CheckName} ({ConnectionStringVariable} is not set)");
            return;
        }

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        if (!await IsDisposableDatabaseAsync(dataSource))
        {
            return;
        }

        var migrations = PostgresSchemaMigrationCatalog.All;
        var migrationIndex = migrations
            .Select((migration, index) => (migration, index))
            .Single(value => value.migration.Id ==
                "20260812_082_pet_hatch_evidence_hardening")
            .index;
        await new PostgresSchemaMigrationRunner(dataSource).InitializeAsync(
            LegacySchemaBootstrap.LoadAsync,
            migrations.Take(migrationIndex).ToArray());
        var legacyPetId = await InsertLegacyNullPetAsync(dataSource);

        await PostgresSchemaStartup.InitializeAsync(connectionString);
        var itemCatalog =
            await PostgresItemTemplateContentBootstrapper.LoadAsync(
                connectionString);
        var petCatalog = await PostgresPetContentBootstrapper.LoadAsync(
            connectionString,
            itemCatalog);
        var step = petCatalog.HatchRankSteps
            .Single(value =>
                value.Aptitude == 1 && value.OutcomeOrder == 0);

        await AssertValidEvidenceAcceptedAsync(
            dataSource,
            petCatalog,
            step);
        await AssertEvidenceMutationRejectedAsync(
            dataSource,
            petCatalog,
            step);
        await AssertEvidenceAptitudeMutationRejectedAsync(
            dataSource,
            petCatalog,
            step);
        await AssertWrongRollRejectedAsync(dataSource, petCatalog);
        await AssertDraftRevisionRejectedAsync(dataSource, petCatalog);
        await AssertFractionalEvidenceRejectedAsync(dataSource, petCatalog);
        await AssertNewNullEvidenceRejectedAsync(dataSource);
        await AssertLegacyNullEvidenceRemainsMutableAsync(
            dataSource,
            legacyPetId);
    }

    private static async Task<bool> IsDisposableDatabaseAsync(
        NpgsqlDataSource dataSource)
    {
        await using var command = dataSource.CreateCommand(
            "SELECT current_database();");
        var database = await command.ExecuteScalarAsync() as string ??
            string.Empty;
        if (DisposableDatabasePattern.IsMatch(database))
        {
            return true;
        }

        Console.WriteLine(
            $"SKIP {CheckName} requires a disposable B03/B12 database; " +
            $"received '{database}'");
        return false;
    }

    private static async Task AssertValidEvidenceAcceptedAsync(
        NpgsqlDataSource dataSource,
        IPetContentCatalog catalog,
        PetHatchRankStepContentDefinition step)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var petId = await InsertPetAsync(
            connection,
            transaction,
            step.Rank,
            step.Rank,
            roll: 0,
            step.OutcomeOrder,
            catalog.Revision.Sha256);
        await using var command = new NpgsqlCommand(
            """
            SELECT birth_rank, hatch_rank_roll,
                   hatch_rank_outcome_order,
                   hatch_rank_content_revision
            FROM public.character_pets
            WHERE id = @petId;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("petId", petId);
        await using var reader = await command.ExecuteReaderAsync();
        Check.True(
            await reader.ReadAsync() &&
            reader.GetDecimal(0) == step.Rank &&
            reader.GetInt16(1) == 0 &&
            reader.GetInt16(2) == step.OutcomeOrder &&
            reader.GetString(3) == catalog.Revision.Sha256,
            "a hatch insert accepts evidence matching its pinned content row and roll interval");
        await reader.DisposeAsync();
        await transaction.RollbackAsync();
    }

    private static async Task AssertEvidenceMutationRejectedAsync(
        NpgsqlDataSource dataSource,
        IPetContentCatalog catalog,
        PetHatchRankStepContentDefinition step)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var petId = await InsertPetAsync(
            connection,
            transaction,
            step.Rank,
            step.Rank,
            roll: 0,
            step.OutcomeOrder,
            catalog.Revision.Sha256);
        await AssertRejectedAsync(
            new NpgsqlCommand(
                """
                UPDATE public.character_pets
                SET hatch_rank_roll = 1
                WHERE id = @petId;
                """,
                connection,
                transaction),
            ("petId", petId),
            "pet hatch-rank evidence is immutable");
        await transaction.RollbackAsync();
    }

    private static async Task AssertWrongRollRejectedAsync(
        NpgsqlDataSource dataSource,
        IPetContentCatalog catalog)
    {
        var middle = catalog.HatchRankSteps.Single(value =>
            value.Aptitude == 1 && value.OutcomeOrder == 1);
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await AssertInsertRejectedAsync(
            connection,
            transaction,
            middle.Rank,
            middle.Rank,
            roll: 0,
            middle.OutcomeOrder,
            catalog.Revision.Sha256,
            "does not match published content");
        await transaction.RollbackAsync();
    }

    private static async Task AssertEvidenceAptitudeMutationRejectedAsync(
        NpgsqlDataSource dataSource,
        IPetContentCatalog catalog,
        PetHatchRankStepContentDefinition step)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var petId = await InsertPetAsync(
            connection,
            transaction,
            step.Rank,
            step.Rank,
            roll: 0,
            step.OutcomeOrder,
            catalog.Revision.Sha256);
        await AssertRejectedAsync(
            new NpgsqlCommand(
                """
                UPDATE public.character_pets
                SET aptitude = 2
                WHERE id = @petId;
                """,
                connection,
                transaction),
            ("petId", petId),
            "pet hatch-rank evidence is immutable");
        await transaction.RollbackAsync();
    }

    private static async Task AssertFractionalEvidenceRejectedAsync(
        NpgsqlDataSource dataSource,
        IPetContentCatalog catalog)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await AssertInsertRejectedAsync(
            connection,
            transaction,
            rank: 0.01m,
            birthRank: 0.001m,
            roll: 0,
            outcome: 0,
            catalog.Revision.Sha256,
            "hatch-rank evidence");
        await transaction.RollbackAsync();
    }

    private static async Task AssertDraftRevisionRejectedAsync(
        NpgsqlDataSource dataSource,
        IPetContentCatalog catalog)
    {
        var step = catalog.HatchRankSteps.Single(value =>
            value.Aptitude == 1 && value.OutcomeOrder == 0);
        var draft = Convert.ToHexString(
            System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var clone = new NpgsqlCommand(
            """
            INSERT INTO public.pet_content_revisions
            SELECT (jsonb_populate_record(
                NULL::public.pet_content_revisions,
                to_jsonb(original) || jsonb_build_object(
                    'revision', @draft,
                    'sealed_at', NULL))).*
            FROM public.pet_content_revisions original
            WHERE original.revision = @published;

            INSERT INTO public.pet_content_aptitude_definitions
            SELECT (jsonb_populate_record(
                NULL::public.pet_content_aptitude_definitions,
                to_jsonb(original) || jsonb_build_object(
                    'revision', @draft))).*
            FROM public.pet_content_aptitude_definitions original
            WHERE original.revision = @published
              AND original.aptitude = 1;

            INSERT INTO public.pet_content_hatch_rank_steps
            SELECT (jsonb_populate_record(
                NULL::public.pet_content_hatch_rank_steps,
                to_jsonb(original) || jsonb_build_object(
                    'revision', @draft))).*
            FROM public.pet_content_hatch_rank_steps original
            WHERE original.revision = @published
              AND original.aptitude = 1;
            """,
            connection,
            transaction))
        {
            clone.Parameters.AddWithValue("draft", draft);
            clone.Parameters.AddWithValue(
                "published", catalog.Revision.Sha256);
            await clone.ExecuteNonQueryAsync();
        }

        await AssertInsertRejectedAsync(
            connection,
            transaction,
            step.Rank,
            step.Rank,
            roll: 0,
            step.OutcomeOrder,
            draft,
            "does not match published content");
        await transaction.RollbackAsync();
    }

    private static async Task AssertNewNullEvidenceRejectedAsync(
        NpgsqlDataSource dataSource)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        try
        {
            _ = await InsertPetAsync(
                connection,
                transaction,
                rank: 0m,
                birthRank: null,
                roll: null,
                outcome: null,
                contentRevision: null);
            throw new InvalidOperationException(
                "Database accepted a new pet without hatch-rank evidence.");
        }
        catch (PostgresException exception) when (
            exception.MessageText.Contains(
                "new pets require complete hatch-rank evidence",
                StringComparison.Ordinal))
        {
            // Expected fail-closed boundary.
        }
        await transaction.RollbackAsync();
    }

    private static async Task AssertLegacyNullEvidenceRemainsMutableAsync(
        NpgsqlDataSource dataSource,
        long petId)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var update = new NpgsqlCommand(
            """
            UPDATE public.character_pets
            SET rank = 0.30
            WHERE id = @petId;
            """,
            connection,
            transaction);
        update.Parameters.AddWithValue("petId", petId);
        Check.Equal(
            1,
            await update.ExecuteNonQueryAsync(),
            "legacy null evidence remains untouched while ordinary pet state can advance");
        await transaction.RollbackAsync();
    }

    private static async Task<long> InsertLegacyNullPetAsync(
        NpgsqlDataSource dataSource)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var petId = await InsertPetAsync(
            connection,
            transaction,
            rank: 0m,
            birthRank: null,
            roll: null,
            outcome: null,
            contentRevision: null);
        await transaction.CommitAsync();
        return petId;
    }

    private static async Task AssertInsertRejectedAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        decimal rank,
        decimal birthRank,
        short roll,
        short outcome,
        string contentRevision,
        string messageFragment)
    {
        try
        {
            _ = await InsertPetAsync(
                connection,
                transaction,
                rank,
                birthRank,
                roll,
                outcome,
                contentRevision);
            throw new InvalidOperationException(
                "Database accepted invalid hatch-rank evidence.");
        }
        catch (PostgresException exception)
            when (exception.MessageText.Contains(
                      messageFragment,
                      StringComparison.OrdinalIgnoreCase) ||
                  exception.ConstraintName ==
                      "ck_character_pets_birth_rank_hundredths")
        {
            // Expected fail-closed database boundary.
        }
    }

    private static async Task AssertRejectedAsync(
        NpgsqlCommand command,
        (string Name, object Value) parameter,
        string messageFragment)
    {
        await using (command)
        {
            command.Parameters.AddWithValue(parameter.Name, parameter.Value);
            try
            {
                await command.ExecuteNonQueryAsync();
                throw new InvalidOperationException(
                    "Database accepted mutable hatch-rank evidence.");
            }
            catch (PostgresException exception)
                when (exception.MessageText.Contains(
                    messageFragment,
                    StringComparison.OrdinalIgnoreCase))
            {
                // Expected fail-closed database boundary.
            }
        }
    }

    private static async Task<long> InsertPetAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        decimal rank,
        decimal? birthRank,
        short? roll,
        short? outcome,
        string? contentRevision)
    {
        var token = Guid.NewGuid().ToString("N")[..12];
        await using var command = new NpgsqlCommand(
            """
            WITH account AS (
                INSERT INTO public.accounts (username)
                VALUES (@username)
                RETURNING id
            ), character_row AS (
                INSERT INTO public.character_base (account_id, name)
                SELECT id, @characterName
                FROM account
                RETURNING id
            )
            INSERT INTO public.character_pets (
                user_id, species_id, name, sex, aptitude, rank,
                birth_rank, hatch_rank_roll,
                hatch_rank_outcome_order,
                hatch_rank_content_revision,
                initial_savvy_baseline_total,
                initial_savvy_policy_version,
                rarity_added_savvy_baseline_total,
                rarity_added_savvy_policy_version,
                initial_savvy_source_version
            )
            SELECT id, 1, @petName, 0, 1, @rank,
                   @birthRank, @roll, @outcome, @contentRevision,
                   60, @savvyPolicy, 60, @savvyPolicy, @savvySource
            FROM character_row
            RETURNING id;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("username", $"hatch_{token}");
        command.Parameters.AddWithValue("characterName", $"Hatch{token}");
        command.Parameters.AddWithValue("petName", $"Pet{token}");
        command.Parameters.AddWithValue("rank", rank);
        command.Parameters.Add("birthRank", NpgsqlDbType.Numeric).Value =
            (object?)birthRank ?? DBNull.Value;
        command.Parameters.Add("roll", NpgsqlDbType.Smallint).Value =
            (object?)roll ?? DBNull.Value;
        command.Parameters.Add("outcome", NpgsqlDbType.Smallint).Value =
            (object?)outcome ?? DBNull.Value;
        command.Parameters.Add("contentRevision", NpgsqlDbType.Varchar).Value =
            (object?)contentRevision ?? DBNull.Value;
        command.Parameters.AddWithValue(
            "savvyPolicy",
            PetInitialSavvyPolicy.Version);
        command.Parameters.AddWithValue(
            "savvySource",
            PetSavvyRuntimeSemantics.SourceVersion);
        var petId = Convert.ToInt64(await command.ExecuteScalarAsync());

        await using var stats = new NpgsqlCommand(
            """
            INSERT INTO public.character_pet_stat_values (
                pet_id, stat_code, initial_savvy, added_savvy,
                base_growth_rate, growth_acceleration,
                birth_initial_savvy, rarity_added_savvy
            )
            SELECT @petId, stat_code, 10, 0.01, 0.01, 0, 10, 10
            FROM generate_series(1, 6) stat(stat_code);
            """,
            connection,
            transaction);
        stats.Parameters.AddWithValue("petId", petId);
        Check.Equal(
            6,
            await stats.ExecuteNonQueryAsync(),
            "hatch evidence fixture receives six valid stat rows");
        return petId;
    }
}
