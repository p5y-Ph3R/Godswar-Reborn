using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresPetLevelMigrationIntegrationChecks
{
    private static async Task<MigrationFixture> InsertFixtureAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string token,
        string username)
    {
        var accountId = await ScalarAsync<int>(
            connection,
            transaction,
            """
            INSERT INTO public.accounts (username)
            VALUES (@username)
            RETURNING id;
            """,
            ("username", username));
        var characterId = await ScalarAsync<int>(
            connection,
            transaction,
            """
            INSERT INTO public.character_base (
                account_id,
                name
            )
            VALUES (@accountId, @name)
            RETURNING id;
            """,
            ("accountId", accountId),
            ("name", $"PetMig{token}"));
        var petId = await ScalarAsync<long>(
            connection,
            transaction,
            """
            INSERT INTO public.character_pets (
                user_id,
                species_id,
                name,
                sex,
                level,
                experience,
                aptitude,
                current_energy,
                maximum_energy,
                activity_state
            )
            VALUES (
                @characterId,
                1,
                @name,
                0,
                1,
                1500,
                1,
                100,
                100,
                'owned'
            )
            RETURNING id;
            """,
            ("characterId", characterId),
            ("name", $"PetMig{token}"));

        return new MigrationFixture(
            accountId,
            characterId,
            petId);
    }

    private static async Task<T> ScalarAsync<T>(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var command =
            new NpgsqlCommand(sql, connection, transaction);
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(
                parameter.Name,
                parameter.Value);
        }

        return (T)(await command.ExecuteScalarAsync()
                   ?? throw new InvalidOperationException(
                       "Pet-level migration fixture returned null."));
    }

    private sealed record MigrationFixture(
        int AccountId,
        int CharacterId,
        long PetId);
}
