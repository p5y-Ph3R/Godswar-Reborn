using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class
    PostgresCharacterCheckpointIntegrationChecks
{
    private static async Task<CheckpointFixture> CreateFixtureAsync(
        NpgsqlDataSource dataSource)
    {
        var token = Guid.NewGuid().ToString("N")[..12];
        var username = $"b10_cp_{token}";
        var characterName = $"B10Cp{token}";

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
            account.Parameters.AddWithValue("username", username);
            accountId = Convert.ToInt32(
                await account.ExecuteScalarAsync());
        }

        int characterId;
        await using (var character = new NpgsqlCommand(
            """
            INSERT INTO public.character_base (
                account_id,
                server_id,
                name,
                gender,
                camp,
                profession,
                "curHP",
                "curMP",
                "Map",
                "Pos_X",
                "Pos_Z",
                "MaxHP",
                "MaxMP"
            )
            VALUES (
                @accountId,
                1,
                @characterName,
                'male',
                1,
                0,
                1500,
                177,
                1,
                165,
                -97,
                1500,
                177
            )
            RETURNING id;
            """,
            connection,
            transaction))
        {
            character.Parameters.AddWithValue(
                "accountId",
                accountId);
            character.Parameters.AddWithValue(
                "characterName",
                characterName);
            characterId = Convert.ToInt32(
                await character.ExecuteScalarAsync());
        }

        await transaction.CommitAsync();
        return new CheckpointFixture(
            accountId,
            characterId,
            username);
    }

    private static async Task AssertDefaultsAndConstraintsAsync(
        NpgsqlDataSource dataSource,
        CheckpointFixture fixture)
    {
        var opening = await ReadStateAsync(dataSource, fixture);
        Check.Equal(
            0L,
            opening.PositionRevision,
            "migration defaults position revision to zero");
        Check.Equal(
            0L,
            opening.VitalsRevision,
            "migration retains opening vitals revision");
        Check.True(
            opening.OwnerId is null,
            "migration leaves existing character unowned");
        Check.Equal(
            0L,
            opening.OwnerGeneration,
            "migration defaults owner generation to zero");

        await ExpectConstraintViolationAsync(
            dataSource,
            """
            UPDATE public.character_base
            SET position_revision = -1
            WHERE id = @characterId
              AND account_id = @accountId;
            """,
            fixture,
            "negative position revision is rejected");
        await ExpectConstraintViolationAsync(
            dataSource,
            """
            UPDATE public.character_base
            SET vitals_revision = -1
            WHERE id = @characterId
              AND account_id = @accountId;
            """,
            fixture,
            "negative vitals revision is rejected");
        await ExpectConstraintViolationAsync(
            dataSource,
            """
            UPDATE public.character_base
            SET checkpoint_owner_id =
                    '00000000-0000-0000-0000-000000000000'::uuid,
                checkpoint_owner_generation = 1
            WHERE id = @characterId
              AND account_id = @accountId;
            """,
            fixture,
            "empty active owner ID is rejected");
    }

    private static async Task ExpectConstraintViolationAsync(
        NpgsqlDataSource dataSource,
        string sql,
        CheckpointFixture fixture,
        string description)
    {
        try
        {
            await using var command = dataSource.CreateCommand(sql);
            command.Parameters.AddWithValue(
                "accountId",
                fixture.AccountId);
            command.Parameters.AddWithValue(
                "characterId",
                fixture.CharacterId);
            await command.ExecuteNonQueryAsync();
        }
        catch (PostgresException error)
            when (error.SqlState ==
                  PostgresErrorCodes.CheckViolation)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Assertion failed: {description}; expected PostgreSQL " +
            "check-constraint violation.");
    }

    private static async Task<CheckpointState> ReadStateAsync(
        NpgsqlDataSource dataSource,
        CheckpointFixture fixture)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT
                checkpoint_owner_id,
                checkpoint_owner_generation,
                position_revision,
                "Map",
                "Pos_X",
                "Pos_Z",
                vitals_revision,
                "curHP",
                "curMP"
            FROM public.character_base
            WHERE id = @characterId
              AND account_id = @accountId;
            """);
        command.Parameters.AddWithValue(
            "accountId",
            fixture.AccountId);
        command.Parameters.AddWithValue(
            "characterId",
            fixture.CharacterId);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new InvalidOperationException(
                "Checkpoint fixture disappeared.");
        }

        return new CheckpointState(
            reader.IsDBNull(0) ? null : reader.GetGuid(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            reader.GetInt16(3),
            reader.GetFloat(4),
            reader.GetFloat(5),
            reader.GetInt64(6),
            reader.GetInt32(7),
            reader.GetInt32(8));
    }

    private static async Task DeleteFixtureAsync(
        NpgsqlDataSource dataSource,
        CheckpointFixture fixture)
    {
        await using var command = dataSource.CreateCommand(
            """
            DELETE FROM public.accounts
            WHERE id = @accountId
              AND username = @username;
            """);
        command.Parameters.AddWithValue(
            "accountId",
            fixture.AccountId);
        command.Parameters.AddWithValue(
            "username",
            fixture.Username);
        var affected = await command.ExecuteNonQueryAsync();
        Check.Equal(
            1,
            affected,
            "checkpoint fixture cleanup is exact");
    }

    private readonly record struct CheckpointFixture(
        int AccountId,
        int CharacterId,
        string Username);

    private readonly record struct CheckpointState(
        Guid? OwnerId,
        long OwnerGeneration,
        long PositionRevision,
        short MapId,
        float PositionX,
        float PositionZ,
        long VitalsRevision,
        int CurrentHp,
        int CurrentMp);
}
