using System.Text.RegularExpressions;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Zodiac;
using Godswar.Server.Infrastructure.Database;
using Godswar.Server.Infrastructure.Messaging;
using Godswar.Server.Infrastructure.WorldContent;
using Godswar.Server.Infrastructure.Zodiac;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class
    PostgresZodiacSkillGridSelectionCommandIntegrationChecks
{
    public const string CheckName =
        "PostgreSQL durable Zodiac skill-grid selection";

    private const string ConnectionStringVariable =
        "GODSWAR_TEST_POSTGRES_CONNECTION_STRING";
    private static string? _gameplayContentRevision;
    private static readonly Regex DisposableDatabasePattern = new(
        @"^godswar_(?:b03_[a-f0-9]{10}_smoke_[0-9]{2}|b09_[a-z0-9_]{1,40})$",
        RegexOptions.CultureInvariant);

    public static async Task RunAsync()
    {
        var connectionString =
            Environment.GetEnvironmentVariable(ConnectionStringVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Console.WriteLine(
                "SKIP PostgreSQL Zodiac selection integration " +
                $"({ConnectionStringVariable} is not set)");
            return;
        }

        await using (var safety =
                     NpgsqlDataSource.Create(connectionString))
        {
            await using var command =
                safety.CreateCommand("SELECT current_database();");
            var databaseName =
                await command.ExecuteScalarAsync() as string ?? "";
            if (!DisposableDatabasePattern.IsMatch(databaseName))
            {
                Console.WriteLine(
                    "SKIP PostgreSQL Zodiac selection integration " +
                    "requires a disposable B03/B09 database; " +
                    $"received '{databaseName}'");
                return;
            }
        }

        await PostgresRelationalContentBaselineBootstrapper.EnsureAsync(
            connectionString);
        var gameplayPublication =
            await PostgresGameplayContentPublisher.EnsurePublishedAsync(
                connectionString);
        _gameplayContentRevision = gameplayPublication.Revision;

        await using (var store =
                     new Godswar.Server.State.PostgresGameStore(
                         connectionString))
        {
            await store.EnsureSeedDataAsync();
        }

        var fixture = await CreateFixtureAsync(connectionString);
        await using var dataSource =
            NpgsqlDataSource.Create(connectionString);
        var executor = CreateExecutor(dataSource);

        var operationId = Guid.NewGuid();
        var first = await executor.ExecuteAsync(
            Envelope(fixture, operationId, 0, 10_057));
        Check.True(
            first.Disposition ==
                ZodiacSkillGridSelectionExecutionDisposition.Committed &&
            first.Receipt is
            {
                Status:
                    ZodiacSkillGridSelectionReceiptStatus.Succeeded,
                PreviousSkillKind: -1,
                SelectedSkillKind: 10_057,
                AggregateRevision: 1
            } &&
            first.CurrentRevision == 1,
            "PostgreSQL commits one authoritative Zodiac selection");

        var replay = await executor.ExecuteAsync(
            Envelope(fixture, operationId, 0, 10_057));
        var conflict = await executor.ExecuteAsync(
            Envelope(fixture, operationId, 0, 10_050));
        Check.True(
            replay.Disposition ==
                ZodiacSkillGridSelectionExecutionDisposition.Duplicate &&
            replay.SelectedSkillKind == 10_057 &&
            conflict.Disposition ==
                ZodiacSkillGridSelectionExecutionDisposition
                    .RequestHashConflict,
            "PostgreSQL distinguishes exact replay from UUID conflict");

        var duplicateRow = await executor.ExecuteAsync(
            Envelope(fixture, Guid.NewGuid(), 1, 10_057));
        Check.True(
            duplicateRow.Disposition ==
                ZodiacSkillGridSelectionExecutionDisposition
                    .TerminalRejected &&
            duplicateRow.Receipt?.Status ==
                ZodiacSkillGridSelectionReceiptStatus.DuplicateSkillInRow,
            "PostgreSQL stores duplicate-row rejection without mutation");

        var concurrent = await Task.WhenAll(
            executor.ExecuteAsync(
                Envelope(fixture, Guid.NewGuid(), 1, 10_050)),
            executor.ExecuteAsync(
                Envelope(fixture, Guid.NewGuid(), 1, 10_050)));
        Check.Equal(
            1,
            concurrent.Count(static result =>
                result.Disposition ==
                ZodiacSkillGridSelectionExecutionDisposition.Committed),
            "concurrent Zodiac selection commits once");
        Check.Equal(
            1,
            concurrent.Count(static result =>
                result.Disposition ==
                    ZodiacSkillGridSelectionExecutionDisposition
                        .TerminalRejected &&
                result.Receipt?.Status ==
                    ZodiacSkillGridSelectionReceiptStatus.AlreadySelected),
            "losing concurrent Zodiac selection is durable rejection");

        var wrongOwner = await executor.ExecuteAsync(
            Envelope(
                fixture,
                Guid.NewGuid(),
                2,
                10_051,
                new CommandSubject(
                    fixture.AccountId + 1,
                    fixture.CharacterId)));
        Check.True(
            wrongOwner.Disposition ==
                ZodiacSkillGridSelectionExecutionDisposition
                    .PreconditionFailed &&
            wrongOwner.Receipt is null,
            "wrong owner creates no Zodiac selection evidence");

        var beforeFault =
            await ReadStateAsync(connectionString, fixture);
        var faulting = CreateExecutor(
            dataSource,
            new ThrowBeforeCommitProbe());
        var faultObserved = false;
        try
        {
            await faulting.ExecuteAsync(
                Envelope(
                    fixture,
                    Guid.NewGuid(),
                    2,
                    10_051));
        }
        catch (InjectedSelectionFault)
        {
            faultObserved = true;
        }

        Check.True(
            faultObserved,
            "fault before commit escapes to leave UUID retryable");
        var afterFault =
            await ReadStateAsync(connectionString, fixture);
        Check.Equal(
            beforeFault,
            afterFault,
            "fault before commit rolls back grid and all evidence");

        Check.True(
            afterFault.Grid0 == 10_057 &&
            afterFault.Grid1 == 10_050 &&
            afterFault.Grid2 == -1 &&
            afterFault.AuditCount == 4 &&
            afterFault.InboxCount == 4 &&
            afterFault.OutboxCount == 2 &&
            afterFault.DuplicateCount == 1 &&
            afterFault.ConflictCount == 1,
            "PostgreSQL selection state and evidence counts are exact");

        await CheckDefenseSelectionEligibilityAsync(
            connectionString,
            fixture,
            executor);
    }

    private static PostgresZodiacSkillGridSelectionCommandExecutor
        CreateExecutor(
            NpgsqlDataSource dataSource,
            IPostgresZodiacSkillGridSelectionCommandProbe? probe = null) =>
        new(
            dataSource,
            new PostgresOutboxDispatcherOptions(),
            GameplayContentRevision,
            probe);

    private static string GameplayContentRevision =>
        _gameplayContentRevision ?? throw new InvalidOperationException(
            "The Zodiac selection check has not pinned gameplay content.");

    private static CommandEnvelope<ZodiacSkillGridSelectionCommand>
        Envelope(
            Fixture fixture,
            Guid operationId,
            int gridIndex,
            int selectedKind,
            CommandSubject? subject = null)
    {
        if (!ZodiacSkillGridSelectionCommandEnvelope.TryCreateCommand(
                operationId,
                gridIndex,
                selectedKind,
                out var command))
        {
            throw new InvalidOperationException(
                "The fixture requested invalid selection intent.");
        }

        return PlayerOwnershipTestFences.Bind(
            ZodiacSkillGridSelectionCommandEnvelope.Create(
            subject ?? fixture.Subject,
            new CommandConnectionCorrelation(
                Guid.NewGuid(),
                CommandTransportKind.SecureTlsLegacy),
            DateTimeOffset.UtcNow,
            command));
    }

    private static async Task<Fixture> CreateFixtureAsync(
        string connectionString)
    {
        var token = Guid.NewGuid().ToString("N")[..12];
        await using var connection =
            new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction =
            await connection.BeginTransactionAsync();

        int accountId;
        await using (var command = new NpgsqlCommand(
            """
            INSERT INTO public.accounts (username, password)
            VALUES (@username, '')
            RETURNING id;
            """,
            connection,
            transaction))
        {
            command.Parameters.AddWithValue(
                "username",
                $"b09zsel_{token}");
            accountId = Convert.ToInt32(
                await command.ExecuteScalarAsync());
        }

        int characterId;
        await using (var command = new NpgsqlCommand(
            """
            INSERT INTO public.character_base (
                account_id, server_id, name, camp, profession, fighter_job_lv,
                zodiac_level, zodiac_energy,
                zodiac_energy_remainder_x100, "SkillPoint",
                "Money", "Stone", wallet_revision, inventory_revision
            )
            VALUES (
                @accountId, 1, @name, 1, 3, 80,
                30, 1000, 0, 1000, 1000, 1000, 0, 0
            )
            RETURNING id;
            """,
            connection,
            transaction))
        {
            command.Parameters.AddWithValue("accountId", accountId);
            command.Parameters.AddWithValue("name", $"ZS{token}");
            characterId = Convert.ToInt32(
                await command.ExecuteScalarAsync());
        }

        await using (var command = new NpgsqlCommand(
            """
            INSERT INTO public.character_zodiac_skill_grids (
                user_id, grid_index, level, selected_skill_id
            )
            SELECT @characterId, grid, 1, -1
            FROM unnest(ARRAY[0, 1, 2, 3, 8, 12]) AS grid;

            INSERT INTO public.character_skills (
                user_id, skill_id, skill_level, source
            )
            VALUES
                (@characterId, 570, 1, 'b09-test'),
                (@characterId, 500, 1, 'b09-test');
            """,
            connection,
            transaction))
        {
            command.Parameters.AddWithValue(
                "characterId",
                characterId);
            await command.ExecuteNonQueryAsync();
        }

        await PlayerOwnershipTestFences.InstallAsync(
            connection,
            transaction,
            accountId,
            characterId);
        await transaction.CommitAsync();
        return new Fixture(
            accountId,
            characterId,
            new CommandSubject(accountId, characterId));
    }

    private static async Task<State> ReadStateAsync(
        string connectionString,
        Fixture fixture)
    {
        await using var connection =
            new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT
                MAX(selected_skill_id)
                    FILTER (WHERE grid_index = 0),
                MAX(selected_skill_id)
                    FILTER (WHERE grid_index = 1),
                MAX(selected_skill_id)
                    FILTER (WHERE grid_index = 2),
                (
                    SELECT count(*)
                    FROM public.command_audit
                    WHERE command_family = @family
                      AND aggregate_key = @aggregateKey
                ),
                (
                    SELECT count(*)
                    FROM public.command_inbox
                    WHERE command_family = @family
                      AND aggregate_key = @aggregateKey
                ),
                (
                    SELECT count(*)
                    FROM public.outbox_events
                    WHERE consumer_key = @consumerKey
                      AND aggregate_type = @aggregateType
                      AND aggregate_key LIKE @eventPrefix
                ),
                (
                    SELECT COALESCE(sum(duplicate_count), 0)
                    FROM public.command_inbox
                    WHERE command_family = @family
                      AND aggregate_key = @aggregateKey
                ),
                (
                    SELECT COALESCE(sum(request_conflict_count), 0)
                    FROM public.command_inbox
                    WHERE command_family = @family
                      AND aggregate_key = @aggregateKey
                )
            FROM public.character_zodiac_skill_grids
            WHERE user_id = @characterId
              AND grid_index BETWEEN 0 AND 2;
            """,
            connection);
        command.Parameters.AddWithValue(
            "family",
            ZodiacSkillGridSelectionPersistenceCodec.CommandFamily);
        command.Parameters.AddWithValue(
            "aggregateKey",
            ZodiacSkillGridSelectionPersistenceCodec
                .CommandAggregateKey(fixture.CharacterId));
        command.Parameters.AddWithValue(
            "consumerKey",
            ZodiacSkillGridSelectionPersistenceCodec.ConsumerKey);
        command.Parameters.AddWithValue(
            "aggregateType",
            ZodiacSkillGridSelectionPersistenceCodec.AggregateType);
        command.Parameters.AddWithValue(
            "eventPrefix",
            $"character:{fixture.CharacterId}:zodiac-grid-selection:%");
        command.Parameters.AddWithValue(
            "characterId",
            fixture.CharacterId);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new InvalidDataException(
                "The Zodiac selection fixture disappeared.");
        }

        return new State(
            reader.GetInt32(0),
            reader.GetInt32(1),
            reader.GetInt32(2),
            reader.GetInt64(3),
            reader.GetInt64(4),
            reader.GetInt64(5),
            reader.GetInt64(6),
            reader.GetInt64(7));
    }

    private sealed class ThrowBeforeCommitProbe :
        IPostgresZodiacSkillGridSelectionCommandProbe
    {
        public ValueTask ReachedAsync(
            PostgresZodiacSkillGridSelectionCommandStage stage,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (stage ==
                PostgresZodiacSkillGridSelectionCommandStage.BeforeCommit)
            {
                throw new InjectedSelectionFault();
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class InjectedSelectionFault : Exception;

    private sealed record Fixture(
        int AccountId,
        int CharacterId,
        CommandSubject Subject);

    private sealed record State(
        int Grid0,
        int Grid1,
        int Grid2,
        long AuditCount,
        long InboxCount,
        long OutboxCount,
        long DuplicateCount,
        long ConflictCount);
}
