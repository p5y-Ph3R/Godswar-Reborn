using Godswar.Server.Application.Accounts;
using Godswar.Server.Security.Authentication;
using Npgsql;

namespace Godswar.Server.Infrastructure.Accounts;

/// <summary>
/// Focused PostgreSQL account adapter. The supplied data source owns pooling
/// and lifetime; this adapter does not dispose it.
/// </summary>
internal sealed class PostgresAccountStore(
    NpgsqlDataSource dataSource) :
    IAccountCredentialStore,
    IAccountDirectory,
    IAccountPresenceWriter,
    ILegacyAccountLoginStore
{
    private readonly NpgsqlDataSource _dataSource =
        dataSource ?? throw new ArgumentNullException(nameof(dataSource));

    public async Task<AccountIdentity>
        LoginOrCreateLegacyAccountAsync(
            string username,
            string password,
            CancellationToken cancellationToken = default)
    {
        username = AccountUsername.NormalizeLegacy(username);
        password ??= string.Empty;

        await using var command = _dataSource.CreateCommand("""
            INSERT INTO public.accounts (
                uuid, email, username, password, login_status,
                last_login_time, last_logout_time, last_login_ip,
                last_login_mac, total_online_time, status
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
            RETURNING id, username;
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
                "Legacy account upsert did not return a row.");
        }

        return ReadIdentity(reader);
    }

    public async Task<StoredAccountCredential?>
        FindAccountCredentialAsync(
            string username,
            CancellationToken cancellationToken = default)
    {
        username = AccountUsername.Require(username);
        await using var command = _dataSource.CreateCommand("""
            SELECT id, username, password
            FROM public.accounts
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
            ReadIdentity(reader),
            reader.GetString(2));
    }

    public async Task<AccountIdentity?> FindAccountByIdAsync(
        int accountId,
        CancellationToken cancellationToken = default)
    {
        if (accountId <= 0)
        {
            return null;
        }

        await using var command = _dataSource.CreateCommand("""
            SELECT id, username
            FROM public.accounts
            WHERE id = @accountId;
            """);
        command.Parameters.AddWithValue("accountId", accountId);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? ReadIdentity(reader)
            : null;
    }

    public async Task<AccountIdentity?> FindAccountByUsernameAsync(
        string username,
        CancellationToken cancellationToken = default)
    {
        username = AccountUsername.Require(username);
        await using var command = _dataSource.CreateCommand("""
            SELECT id, username
            FROM public.accounts
            WHERE username = @username;
            """);
        command.Parameters.AddWithValue("username", username);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? ReadIdentity(reader)
            : null;
    }

    public async Task<AccountIdentity?>
        TryCreateAccountWithCredentialAsync(
            string username,
            string versionedVerifier,
            CancellationToken cancellationToken = default)
    {
        username = AccountUsername.Require(username);
        versionedVerifier = RequireVersionedVerifier(
            versionedVerifier);
        await using var command = _dataSource.CreateCommand("""
            INSERT INTO public.accounts (
                uuid, email, username, password, login_status,
                last_login_time, last_logout_time, last_login_ip,
                last_login_mac, total_online_time, status
            )
            VALUES (
                '', '', @username, @verifier, 0, now(), now(),
                '', '', 0, 0
            )
            ON CONFLICT (username) DO NOTHING
            RETURNING id, username;
            """);
        command.Parameters.AddWithValue("username", username);
        command.Parameters.AddWithValue("verifier", versionedVerifier);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? ReadIdentity(reader)
            : null;
    }

    public async Task<bool> TryReplaceAccountCredentialAsync(
        int accountId,
        string expectedVerifier,
        string versionedVerifier,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expectedVerifier);
        versionedVerifier = RequireVersionedVerifier(
            versionedVerifier);
        if (accountId <= 0)
        {
            return false;
        }

        await using var command = _dataSource.CreateCommand("""
            UPDATE public.accounts
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
            UPDATE public.accounts
            SET login_status = 1,
                last_login_time = now()
            WHERE id = @accountId;
            """);
        command.Parameters.AddWithValue("accountId", accountId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task MarkAccountOfflineAsync(
        int accountId,
        CancellationToken cancellationToken = default)
    {
        if (accountId <= 0)
        {
            return;
        }

        await using var command = _dataSource.CreateCommand("""
            UPDATE public.accounts
            SET login_status = 0,
                last_logout_time = now(),
                total_online_time = total_online_time + GREATEST(
                    0,
                    EXTRACT(EPOCH FROM (now() - last_login_time))::bigint)
            WHERE id = @accountId;
            """);
        command.Parameters.AddWithValue("accountId", accountId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static AccountIdentity ReadIdentity(
        NpgsqlDataReader reader) =>
        new(reader.GetInt32(0), reader.GetString(1));

    private static string RequireVersionedVerifier(
        string versionedVerifier)
    {
        ArgumentNullException.ThrowIfNull(versionedVerifier);
        if (versionedVerifier.Length >
                StoredAccountCredential.MaximumVerifierLength ||
            !PasswordVerifierRecord.TryParse(
                versionedVerifier,
                out var parsed))
        {
            throw new ArgumentException(
                "Credential must be a structurally valid versioned password verifier.",
                nameof(versionedVerifier));
        }

        parsed!.Dispose();
        return versionedVerifier;
    }

}
