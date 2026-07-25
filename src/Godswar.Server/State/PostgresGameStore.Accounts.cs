using Godswar.Server.Security.Authentication;

namespace Godswar.Server.State;

internal sealed partial class PostgresGameStore
{
    public async Task<GameAccount> LoginOrCreateAccountAsync(
        string username,
        string password,
        CancellationToken cancellationToken = default)
    {
        username = CleanUsername(username);
        password ??= string.Empty;

        await using var command = _dataSource.CreateCommand($"""
            INSERT INTO accounts (
                uuid, email, username, password, login_status, last_login_time,
                last_logout_time, last_login_ip, last_login_mac,
                total_online_time, status
            )
            VALUES (
                '', '', @username, @password, 1, now(), now(),
                '', '', 0, 0
            )
            ON CONFLICT (username) DO UPDATE
            SET password = CASE
                    WHEN accounts.password LIKE @versionedPrefixPattern
                        THEN accounts.password
                    ELSE EXCLUDED.password
                END,
                login_status = 1,
                last_login_time = now()
            RETURNING {AccountColumns};
            """);
        command.Parameters.AddWithValue("username", username);
        command.Parameters.AddWithValue("password", password);
        command.Parameters.AddWithValue(
            "versionedPrefixPattern",
            PasswordVerifierRecord.VersionedPrefix + "%");

        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException(
                "Account upsert did not return a row.");
        }

        return ReadAccount(reader);
    }

    public async Task<StoredAccountCredential?>
        FindAccountCredentialAsync(
            string username,
            CancellationToken cancellationToken = default)
    {
        username = AccountCredentialPersistence.RequireUsername(username);
        await using var command = _dataSource.CreateCommand($"""
            SELECT {AccountColumns}
            FROM accounts
            WHERE username = @username;
            """);
        command.Parameters.AddWithValue("username", username);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new StoredAccountCredential(
            ReadAccount(reader),
            reader.GetString(2));
    }

    public async Task<GameAccount?> FindAccountByIdAsync(
        int accountId,
        CancellationToken cancellationToken = default)
    {
        if (accountId <= 0)
        {
            return null;
        }

        await using var command = _dataSource.CreateCommand($"""
            SELECT {AccountColumns}
            FROM accounts
            WHERE id = @accountId;
            """);
        command.Parameters.AddWithValue("accountId", accountId);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? ReadAccount(reader)
            : null;
    }

    public async Task<GameAccount?> FindAccountByUsernameAsync(
        string username,
        CancellationToken cancellationToken = default)
    {
        username = AccountCredentialPersistence.RequireUsername(username);
        await using var command = _dataSource.CreateCommand($"""
            SELECT {AccountColumns}
            FROM accounts
            WHERE username = @username;
            """);
        command.Parameters.AddWithValue("username", username);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? ReadAccount(reader)
            : null;
    }

    public async Task<GameAccount?> TryCreateAccountWithCredentialAsync(
        string username,
        string versionedVerifier,
        CancellationToken cancellationToken = default)
    {
        username = AccountCredentialPersistence.RequireUsername(username);
        versionedVerifier =
            AccountCredentialPersistence.RequireVersionedVerifier(
                versionedVerifier);
        await using var command = _dataSource.CreateCommand($"""
            INSERT INTO accounts (
                uuid, email, username, password, login_status, last_login_time,
                last_logout_time, last_login_ip, last_login_mac,
                total_online_time, status
            )
            VALUES (
                '', '', @username, @verifier, 0, now(), now(),
                '', '', 0, 0
            )
            ON CONFLICT (username) DO NOTHING
            RETURNING {AccountColumns};
            """);
        command.Parameters.AddWithValue("username", username);
        command.Parameters.AddWithValue("verifier", versionedVerifier);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? ReadAccount(reader)
            : null;
    }

    public async Task<bool> TryReplaceAccountCredentialAsync(
        int accountId,
        string expectedVerifier,
        string versionedVerifier,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expectedVerifier);
        versionedVerifier =
            AccountCredentialPersistence.RequireVersionedVerifier(
                versionedVerifier);
        if (accountId <= 0)
        {
            return false;
        }

        await using var command = _dataSource.CreateCommand("""
            UPDATE accounts
            SET password = @versionedVerifier
            WHERE id = @accountId
              AND password = @expectedVerifier;
            """);
        command.Parameters.AddWithValue(
            "versionedVerifier",
            versionedVerifier);
        command.Parameters.AddWithValue(
            "expectedVerifier",
            expectedVerifier);
        command.Parameters.AddWithValue("accountId", accountId);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task MarkAccountOnlineAsync(
        int accountId,
        CancellationToken cancellationToken = default)
    {
        if (accountId <= 0)
        {
            return;
        }

        await using var command = _dataSource.CreateCommand("""
            UPDATE accounts
            SET login_status = 1,
                last_login_time = now()
            WHERE id = @accountId;
            """);
        command.Parameters.AddWithValue("accountId", accountId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
