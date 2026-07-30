using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class
    PostgresCharacterLifecycleMigrationIntegrationChecks
{
    private static async Task AssertLifecycleConstraintsAsync(
        NpgsqlDataSource dataSource,
        LifecycleFixture fixture)
    {
        await AssertCheckViolationAsync(
            dataSource,
            fixture.OtherAccountId,
            $"B11S{fixture.Token}",
            "character_slot = 1",
            "native client cannot create a second character slot");
        await AssertCheckViolationAsync(
            dataSource,
            fixture.OtherAccountId,
            $"B11V{fixture.Token}",
            "lifecycle_version = 0",
            "lifecycle versions must remain positive");
        await AssertCheckViolationAsync(
            dataSource,
            fixture.OtherAccountId,
            $"B11T{fixture.Token}",
            """
            lifecycle_state = 'deleted',
            deleted_at = now(),
            restore_until = now() - interval '1 day',
            purge_after = now() + interval '1 day'
            """,
            "restore deadline must follow deletion");
        await AssertCheckViolationAsync(
            dataSource,
            fixture.OtherAccountId,
            $"B11O{fixture.Token}",
            """
            lifecycle_state = 'deleted',
            deleted_at = now(),
            restore_until = now() + interval '1 day',
            purge_after = now() + interval '2 days',
            checkpoint_owner_id =
                '11111111-1111-1111-1111-111111111111'::uuid,
            checkpoint_owner_generation = 1
            """,
            "deleted character cannot retain checkpoint ownership");
    }

    private static async Task<LifecycleFixture> CreateFixtureAsync(
        NpgsqlDataSource dataSource)
    {
        var token = Guid.NewGuid().ToString("N")[..12];
        await using var command = dataSource.CreateCommand("""
            INSERT INTO public.accounts (username, password)
            VALUES
                (@firstUsername, ''),
                (@secondUsername, '')
            RETURNING id;
            """);
        command.Parameters.AddWithValue(
            "firstUsername",
            $"b11_lifecycle_a_{token}");
        command.Parameters.AddWithValue(
            "secondUsername",
            $"b11_lifecycle_b_{token}");
        var accountIds = new List<int>(2);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            accountIds.Add(reader.GetInt32(0));
        }

        Check.Equal(
            2,
            accountIds.Count,
            "lifecycle fixture creates two isolated accounts");
        return new LifecycleFixture(
            accountIds[0],
            accountIds[1],
            token);
    }

    private static async Task DeleteFixtureAsync(
        NpgsqlDataSource dataSource,
        LifecycleFixture fixture)
    {
        await using var command = dataSource.CreateCommand("""
            DELETE FROM public.accounts
            WHERE id = ANY(@accountIds);
            """);
        command.Parameters.AddWithValue(
            "accountIds",
            new[]
            {
                fixture.AccountId,
                fixture.OtherAccountId
            });
        _ = await command.ExecuteNonQueryAsync();
    }

    private static async Task<int> InsertActiveAsync(
        NpgsqlDataSource dataSource,
        int accountId,
        string name)
    {
        await using var command = dataSource.CreateCommand("""
            WITH next_version AS (
                UPDATE public.accounts
                SET character_lifecycle_version =
                        character_lifecycle_version + 1
                WHERE id = @accountId
                RETURNING character_lifecycle_version
            )
            INSERT INTO public.character_base (
                account_id,
                name,
                lifecycle_version
            )
            SELECT
                @accountId,
                @name,
                next_version.character_lifecycle_version
            FROM next_version
            RETURNING id;
            """);
        command.Parameters.AddWithValue("accountId", accountId);
        command.Parameters.AddWithValue("name", name);
        return Convert.ToInt32(
            await command.ExecuteScalarAsync());
    }

    private static async Task<int> InsertDeletedAsync(
        NpgsqlDataSource dataSource,
        int accountId,
        string name)
    {
        await using var command = dataSource.CreateCommand("""
            WITH next_version AS (
                UPDATE public.accounts
                SET character_lifecycle_version =
                        character_lifecycle_version + 1
                WHERE id = @accountId
                RETURNING character_lifecycle_version
            )
            INSERT INTO public.character_base (
                account_id,
                name,
                lifecycle_version,
                lifecycle_state,
                deleted_at,
                restore_until,
                purge_after
            )
            SELECT
                @accountId,
                @name,
                next_version.character_lifecycle_version,
                'deleted',
                now(),
                now() + interval '30 days',
                now() + interval '90 days'
            FROM next_version
            RETURNING id;
            """);
        command.Parameters.AddWithValue("accountId", accountId);
        command.Parameters.AddWithValue("name", name);
        return Convert.ToInt32(
            await command.ExecuteScalarAsync());
    }

    private static async Task MarkDeletedAsync(
        NpgsqlDataSource dataSource,
        int characterId)
    {
        await using var command = dataSource.CreateCommand("""
            WITH target AS (
                SELECT account_id
                FROM public.character_base
                WHERE id = @characterId
            ),
            next_version AS (
                UPDATE public.accounts account_row
                SET character_lifecycle_version =
                        account_row.character_lifecycle_version + 1
                FROM target
                WHERE account_row.id = target.account_id
                RETURNING account_row.character_lifecycle_version
            )
            UPDATE public.character_base
            SET lifecycle_state = 'deleted',
                lifecycle_version =
                    next_version.character_lifecycle_version,
                deleted_at = now(),
                restore_until = now() + interval '30 days',
                purge_after = now() + interval '90 days',
                checkpoint_owner_id = NULL
            FROM next_version
            WHERE id = @characterId;
            """);
        command.Parameters.AddWithValue(
            "characterId",
            characterId);
        Check.Equal(
            1,
            await command.ExecuteNonQueryAsync(),
            "delete transition updates one character");
    }

    private static async Task RestoreAsync(
        NpgsqlDataSource dataSource,
        int characterId)
    {
        await using var command = dataSource.CreateCommand("""
            WITH target AS (
                SELECT account_id
                FROM public.character_base
                WHERE id = @characterId
            ),
            next_version AS (
                UPDATE public.accounts account_row
                SET character_lifecycle_version =
                        account_row.character_lifecycle_version + 1
                FROM target
                WHERE account_row.id = target.account_id
                RETURNING account_row.character_lifecycle_version
            )
            UPDATE public.character_base
            SET lifecycle_state = 'active',
                lifecycle_version =
                    next_version.character_lifecycle_version,
                deleted_at = NULL,
                restore_until = NULL,
                purge_after = NULL
            FROM next_version
            WHERE id = @characterId;
            """);
        command.Parameters.AddWithValue(
            "characterId",
            characterId);
        Check.Equal(
            1,
            await command.ExecuteNonQueryAsync(),
            "restore transition updates one character");
    }

    private static async Task<LifecycleState> ReadLifecycleAsync(
        NpgsqlDataSource dataSource,
        int characterId)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT
                character_slot,
                lifecycle_state,
                lifecycle_version,
                deleted_at,
                restore_until,
                purge_after
            FROM public.character_base
            WHERE id = @characterId;
            """);
        command.Parameters.AddWithValue(
            "characterId",
            characterId);
        await using var reader = await command.ExecuteReaderAsync();
        Check.True(
            await reader.ReadAsync(),
            "lifecycle row exists");
        return new LifecycleState(
            reader.GetInt16(0),
            reader.GetString(1),
            reader.GetInt64(2),
            reader.IsDBNull(3) ? null : reader.GetDateTime(3),
            reader.IsDBNull(4) ? null : reader.GetDateTime(4),
            reader.IsDBNull(5) ? null : reader.GetDateTime(5));
    }

    private static async Task AssertCheckViolationAsync(
        NpgsqlDataSource dataSource,
        int accountId,
        string name,
        string assignments,
        string description)
    {
        var sql = $"""
            INSERT INTO public.character_base (
                account_id,
                name
            )
            VALUES (
                @accountId,
                @name
            );
            UPDATE public.character_base
            SET {assignments}
            WHERE account_id = @accountId
              AND name = @name;
            """;
        await using var connection =
            await dataSource.OpenConnectionAsync();
        await using var transaction =
            await connection.BeginTransactionAsync();
        await using var command =
            new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("accountId", accountId);
        command.Parameters.AddWithValue("name", name);
        try
        {
            _ = await command.ExecuteNonQueryAsync();
            throw new InvalidOperationException(
                $"Expected a check violation: {description}.");
        }
        catch (PostgresException exception)
            when (exception.SqlState ==
                  PostgresErrorCodes.CheckViolation)
        {
            await transaction.RollbackAsync();
        }
    }

    private static async Task AssertUniqueViolationAsync(
        Func<Task> action,
        string description)
    {
        try
        {
            await action();
            throw new InvalidOperationException(
                $"Expected a unique violation: {description}.");
        }
        catch (PostgresException exception)
            when (exception.SqlState ==
                  PostgresErrorCodes.UniqueViolation)
        {
        }
    }

    private static async Task<int> ReadInt32Async(
        NpgsqlDataSource dataSource,
        string sql,
        int accountId)
    {
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("accountId", accountId);
        return Convert.ToInt32(
            await command.ExecuteScalarAsync());
    }

    private static async Task<long> ReadInt64Async(
        NpgsqlDataSource dataSource,
        string sql,
        int accountId)
    {
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("accountId", accountId);
        return Convert.ToInt64(
            await command.ExecuteScalarAsync());
    }

    private sealed record LifecycleFixture(
        int AccountId,
        int OtherAccountId,
        string Token);

    private sealed record LifecycleState(
        short Slot,
        string State,
        long Version,
        DateTime? DeletedAt,
        DateTime? RestoreUntil,
        DateTime? PurgeAfter);
}
