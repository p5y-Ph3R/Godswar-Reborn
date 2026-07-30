using System.Globalization;
using System.Text.RegularExpressions;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Progression;
using Godswar.Server.Infrastructure.Messaging;
using Godswar.Server.Infrastructure.Progression;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static class
    PostgresProgressionIntervalSettlementIntegrationChecks
{
    public const string CheckName =
        "PostgreSQL durable online progression intervals";

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
                $"SKIP {CheckName} " +
                $"({ConnectionStringVariable} is not set)");
            return;
        }

        await using var dataSource =
            NpgsqlDataSource.Create(connectionString);
        var databaseName = await ReadDatabaseNameAsync(dataSource);
        if (!DisposableDatabasePattern.IsMatch(databaseName))
        {
            Console.WriteLine(
                $"SKIP {CheckName} requires a disposable B03/B12 " +
                $"database; received '{databaseName}'");
            return;
        }

        await using (var store =
                     new PostgresGameStore(connectionString))
        {
            await store.EnsureSeedDataAsync();
        }

        var fixture = await CreateFixtureAsync(dataSource);
        var policy = new ZodiacEnergyPolicy(
            true,
            TickSeconds: 10,
            BoostedDailySeconds: 3_600,
            BoostedEnergyPerTickX100: 100,
            NormalEnergyPerTickX100: 100,
            CompensationOnlineThresholdSeconds: 0,
            CompensationSeconds: 0,
            ServerUtcOffsetMinutes: 0);
        var options = new PostgresOutboxDispatcherOptions();
        var executor =
            new PostgresProgressionIntervalSettlementCommandExecutor(
                dataSource,
                options,
                policy);
        var sessionId = Guid.NewGuid();
        var from = new DateTimeOffset(
            2026,
            7,
            31,
            4,
            0,
            0,
            TimeSpan.Zero);
        var first = CreateEnvelope(
            fixture,
            sessionId,
            1,
            from,
            from.AddSeconds(30));

        var concurrent = await Task.WhenAll(
            executor.ExecuteAsync(first),
            executor.ExecuteAsync(first));
        Check.Equal(
            1,
            concurrent.Count(result =>
                result.Disposition ==
                    ProgressionIntervalSettlementDisposition.Committed),
            "one concurrent interval commits");
        Check.Equal(
            1,
            concurrent.Count(result =>
                result.Disposition ==
                    ProgressionIntervalSettlementDisposition.Duplicate),
            "one concurrent interval replays");
        var afterFirst = await ReadStateAsync(dataSource, fixture);
        Check.True(
            afterFirst is
            {
                ZodiacEnergy: 3,
                RemainingBoostTicks: 900_000_000,
                LastSequence: 1,
                AggregateRevision: 1,
                AuditCount: 1,
                InboxCount: 1,
                OutboxCount: 1
            },
            "one transaction advances both clocks and one evidence set");

        var restartedExecutor =
            new PostgresProgressionIntervalSettlementCommandExecutor(
                dataSource,
                options,
                policy);
        var restartReplay =
            await restartedExecutor.ExecuteAsync(first);
        Check.Equal(
            (int)ProgressionIntervalSettlementDisposition.Duplicate,
            (int)restartReplay.Disposition,
            "restart replay returns durable evidence");
        Check.Equal(
            afterFirst,
            await ReadStateAsync(dataSource, fixture),
            "restart replay does not mutate progression again");

        var overlap = await executor.ExecuteAsync(
            CreateEnvelope(
                fixture,
                sessionId,
                2,
                from.AddSeconds(29),
                from.AddSeconds(60)));
        Check.True(
            overlap.Disposition ==
                ProgressionIntervalSettlementDisposition
                    .IntervalConflict &&
            overlap.Conflict == ProgressionIntervalConflict.Overlap,
            "overlap is rejected");
        var gap = await executor.ExecuteAsync(
            CreateEnvelope(
                fixture,
                sessionId,
                2,
                from.AddSeconds(31),
                from.AddSeconds(60)));
        Check.True(
            gap.Disposition ==
                ProgressionIntervalSettlementDisposition
                    .IntervalConflict &&
            gap.Conflict == ProgressionIntervalConflict.Gap,
            "same-session gap is rejected");
        var reordered = await executor.ExecuteAsync(
            CreateEnvelope(
                fixture,
                sessionId,
                3,
                from.AddSeconds(30),
                from.AddSeconds(60)));
        Check.True(
            reordered.Disposition ==
                ProgressionIntervalSettlementDisposition
                    .IntervalConflict &&
            reordered.Conflict ==
                ProgressionIntervalConflict.InvalidSequence,
            "reordered sequence is rejected");
        Check.Equal(
            afterFirst,
            await ReadStateAsync(dataSource, fixture),
            "rejected intervals leave all durable value unchanged");

        var second = await executor.ExecuteAsync(
            CreateEnvelope(
                fixture,
                sessionId,
                2,
                from.AddSeconds(30),
                from.AddSeconds(60)));
        Check.Equal(
            (int)ProgressionIntervalSettlementDisposition.Committed,
            (int)second.Disposition,
            "contiguous second interval commits");
        var afterSecond = await ReadStateAsync(dataSource, fixture);
        Check.True(
            afterSecond is
            {
                ZodiacEnergy: 6,
                RemainingBoostTicks: 600_000_000,
                LastSequence: 2,
                AggregateRevision: 2,
                AuditCount: 2,
                InboxCount: 2,
                OutboxCount: 2
            },
            "second interval advances the strict aggregate revision");

        var replacementId = Guid.NewGuid();
        var replacement = await executor.ExecuteAsync(
            CreateEnvelope(
                fixture,
                replacementId,
                1,
                from.AddMinutes(10),
                from.AddMinutes(10).AddSeconds(30)));
        Check.Equal(
            (int)ProgressionIntervalSettlementDisposition.Committed,
            (int)replacement.Disposition,
            "reconnect starts a new sequence after an offline gap");
        var final = await ReadStateAsync(dataSource, fixture);
        Check.True(
            final.ZodiacEnergy == 9 &&
            final.RemainingBoostTicks == 300_000_000 &&
            final.OnlineSessionId == replacementId &&
            final.LastSequence == 1 &&
            final.AggregateRevision == 3,
            "offline gap is skipped while the online replacement interval is charged");
    }

    private static CommandEnvelope<
        ProgressionIntervalSettlementCommand> CreateEnvelope(
            Fixture fixture,
            Guid sessionId,
            long sequence,
            DateTimeOffset from,
            DateTimeOffset until) =>
        PlayerOwnershipTestFences.Bind(
            ProgressionIntervalSettlementCommandEnvelope.Create(
            new CommandSubject(
                fixture.AccountId,
                fixture.CharacterId),
            sessionId,
            sequence,
            from,
            until,
            CommandTransportKind.LegacyTcp));

    private static async Task<Fixture> CreateFixtureAsync(
        NpgsqlDataSource dataSource)
    {
        var token = Guid.NewGuid().ToString("N")[..12];
        await using var connection =
            await dataSource.OpenConnectionAsync();
        await using var transaction =
            await connection.BeginTransactionAsync();
        int accountId;
        await using (var account = new NpgsqlCommand(
            """
            INSERT INTO public.accounts (username, password)
            VALUES (@username, '')
            RETURNING id;
            """,
            connection,
            transaction))
        {
            account.Parameters.AddWithValue(
                "username",
                $"b12_interval_{token}");
            accountId = Convert.ToInt32(
                await account.ExecuteScalarAsync());
        }

        int characterId;
        await using (var character = new NpgsqlCommand(
            """
            INSERT INTO public.character_base (
                account_id,
                name,
                zodiac_level
            )
            VALUES (@accountId, @name, 1)
            RETURNING id;
            """,
            connection,
            transaction))
        {
            character.Parameters.AddWithValue(
                "accountId",
                accountId);
            character.Parameters.AddWithValue(
                "name",
                $"B12Interval{token}");
            characterId = Convert.ToInt32(
                await character.ExecuteScalarAsync());
        }

        await using (var modifier = new NpgsqlCommand(
            """
            INSERT INTO public.character_experience_modifiers (
                character_id,
                status_id,
                kind,
                bonus_basis_points,
                activated_at,
                expires_at,
                remaining_online_ticks
            )
            VALUES (
                @characterId,
                99001,
                99001,
                1000,
                @activatedAt,
                NULL,
                1200000000
            );
            """,
            connection,
            transaction))
        {
            modifier.Parameters.AddWithValue(
                "characterId",
                characterId);
            modifier.Parameters.AddWithValue(
                "activatedAt",
                new DateTimeOffset(
                    2026,
                    7,
                    31,
                    4,
                    0,
                    0,
                    TimeSpan.Zero));
            await modifier.ExecuteNonQueryAsync();
        }

        await PlayerOwnershipTestFences.InstallAsync(
            connection,
            transaction,
            accountId,
            characterId);
        await transaction.CommitAsync();
        return new Fixture(accountId, characterId);
    }

    private static async Task<DurableState> ReadStateAsync(
        NpgsqlDataSource dataSource,
        Fixture fixture)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT
                character.zodiac_energy,
                modifier.remaining_online_ticks,
                authority.online_session_id,
                authority.last_interval_sequence,
                authority.aggregate_revision,
                (
                    SELECT count(*)::bigint
                    FROM public.command_audit audit
                    WHERE audit.principal_key = @principalKey
                      AND audit.aggregate_key = @aggregateKey
                      AND audit.command_family = @commandFamily
                ),
                (
                    SELECT count(*)::bigint
                    FROM public.command_inbox inbox
                    WHERE inbox.principal_key = @principalKey
                      AND inbox.aggregate_key = @aggregateKey
                      AND inbox.command_family = @commandFamily
                ),
                (
                    SELECT count(*)::bigint
                    FROM public.outbox_events outbox
                    WHERE outbox.aggregate_key = @aggregateKey
                      AND outbox.consumer_key = @consumerKey
                )
            FROM public.character_base character
            JOIN public.character_experience_modifiers modifier
              ON modifier.character_id = character.id
            JOIN public.character_progression_interval_authority authority
              ON authority.character_id = character.id
            WHERE character.account_id = @accountId
              AND character.id = @characterId;
            """);
        command.Parameters.AddWithValue(
            "accountId",
            fixture.AccountId);
        command.Parameters.AddWithValue(
            "characterId",
            fixture.CharacterId);
        command.Parameters.AddWithValue(
            "principalKey",
            fixture.AccountId.ToString(CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue(
            "aggregateKey",
            ProgressionIntervalSettlementPersistenceCodec.AggregateKey(
                fixture.CharacterId));
        command.Parameters.AddWithValue(
            "commandFamily",
            ProgressionIntervalSettlementPersistenceCodec.CommandFamily);
        command.Parameters.AddWithValue(
            "consumerKey",
            ProgressionIntervalSettlementPersistenceCodec.ConsumerKey);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new InvalidDataException(
                "The progression fixture state is missing.");
        }

        return new DurableState(
            reader.GetInt32(0),
            reader.GetInt64(1),
            reader.GetGuid(2),
            reader.GetInt64(3),
            reader.GetInt64(4),
            reader.GetInt64(5),
            reader.GetInt64(6),
            reader.GetInt64(7));
    }

    private static async Task<string> ReadDatabaseNameAsync(
        NpgsqlDataSource dataSource)
    {
        await using var command =
            dataSource.CreateCommand("SELECT current_database();");
        return await command.ExecuteScalarAsync() as string ??
            throw new InvalidDataException(
                "PostgreSQL returned no database name.");
    }

    private readonly record struct Fixture(
        int AccountId,
        int CharacterId);

    private readonly record struct DurableState(
        int ZodiacEnergy,
        long RemainingBoostTicks,
        Guid OnlineSessionId,
        long LastSequence,
        long AggregateRevision,
        long AuditCount,
        long InboxCount,
        long OutboxCount);
}
