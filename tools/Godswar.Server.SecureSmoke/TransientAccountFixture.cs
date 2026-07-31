using System.Security.Cryptography;
using Godswar.Server.Application.Accounts;
using Godswar.Server.Packets;
using Godswar.Server.Security.Authentication;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.SecureSmoke;

internal sealed class TransientAccountFixture : IAsyncDisposable
{
    private static readonly TimeSpan OfflineWaitTimeout =
        TimeSpan.FromSeconds(2);
    private static readonly TimeSpan CleanupTimeout =
        TimeSpan.FromSeconds(5);

    private readonly string _connectionString;
    private readonly PostgresGameStore _store;
    private readonly byte[] _password;
    private AccountIdentity? _account;
    private GameCharacter? _character;

    private TransientAccountFixture(
        string connectionString,
        PostgresGameStore store,
        string loginName,
        string username,
        byte[] password)
    {
        _connectionString = connectionString;
        _store = store;
        LoginName = loginName;
        Username = username;
        _password = password;
    }

    public string Username { get; }

    public string LoginName { get; }

    public ReadOnlyMemory<byte> Password => _password;

    public static async Task<TransientAccountFixture> CreateAsync(
        string connectionString,
        CancellationToken cancellationToken)
    {
        var store = new PostgresGameStore(connectionString);
        var password = CreateAsciiSecret();
        TransientAccountFixture? fixture = null;
        try
        {
            for (var attempt = 0; attempt < 4; attempt++)
            {
                var loginName = $"smoke_{Convert.ToHexString(
                    RandomNumberGenerator.GetBytes(10))}".ToLowerInvariant();
                var username = PacketText.DecodeLoginName(loginName);
                if (await store.FindAccountByUsernameAsync(
                        username,
                        cancellationToken) is not null)
                {
                    continue;
                }

                fixture = new TransientAccountFixture(
                    connectionString,
                    store,
                    loginName,
                    username,
                    password);
                await fixture.InitializeAsync(cancellationToken);
                return fixture;
            }

            throw new InvalidOperationException(
                "Could not reserve a unique transient smoke account.");
        }
        catch
        {
            if (fixture is not null)
            {
                await fixture.DisposeAsync();
            }
            else
            {
                CryptographicOperations.ZeroMemory(password);
                await store.DisposeAsync();
            }
            throw;
        }
    }

    private async Task InitializeAsync(
        CancellationToken cancellationToken)
    {
        var authenticationOptions = new AuthenticationOptions
        {
            Iterations =
                AuthenticationOptions.HardMinimumStoredIterations,
            MinimumStoredIterations =
                AuthenticationOptions.HardMinimumStoredIterations,
            MaximumStoredIterations =
                AuthenticationOptions.HardMinimumStoredIterations,
            MaximumConcurrentKdfs = 1,
            QueueCapacity = 1,
            QueueCredentialBytes =
                AuthenticationOptions.MaximumPasswordBytes,
            AllowRegistration = true,
            AllowPlaintextMigration = false
        };
        await using (var authentication =
            new AccountAuthenticationService(
                _store,
                authenticationOptions))
        {
            var created = await authentication.AuthenticateAsync(
                Username,
                _password,
                cancellationToken);
            if (!created.IsAccepted ||
                !created.AccountCreated ||
                created.Account is null)
            {
                throw new InvalidOperationException(
                    "Could not create the versioned transient account.");
            }
            _account = created.Account;
        }

        _character = await _store.CreateCharacterAsync(
            _account.Id,
            new GameCharacter
            {
                Name = $"Probe{Convert.ToHexString(
                    RandomNumberGenerator.GetBytes(6))}",
                Camp = GameDefaults.SpartaCamp,
                Profession = 1,
                Level = 80,
                CurrentHp = 8_000,
                MaxHp = 8_000,
                CurrentMp = 4_000,
                MaxMp = 4_000
            },
            cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        CryptographicOperations.ZeroMemory(_password);
        Exception? cleanupError = null;
        try
        {
            if (_account is not null)
            {
                try
                {
                    using var offlineWait =
                        new CancellationTokenSource(
                            OfflineWaitTimeout);
                    await WaitForOfflineAsync(
                        _account.Id,
                        offlineWait.Token);
                }
                catch
                {
                    // Cleanup still owns only its random account. A failed or
                    // incomplete server session must not prevent removal.
                }
            }
            if (_account is not null && _character is not null)
            {
                try
                {
                    using var cleanup =
                        new CancellationTokenSource(CleanupTimeout);
                    await _store.DeleteCharacterAsync(
                        _account.Id,
                        _character.Name,
                        cleanup.Token);
                }
                catch (Exception error)
                {
                    cleanupError = error;
                }
            }
            if (_account is not null)
            {
                try
                {
                    using var cleanup =
                        new CancellationTokenSource(CleanupTimeout);
                    await DeleteAccountAsync(
                        _account.Id,
                        Username,
                        cleanup.Token);
                }
                catch (Exception error)
                {
                    cleanupError ??= error;
                }
            }
        }
        finally
        {
            await _store.DisposeAsync();
        }
        if (cleanupError is not null)
        {
            throw new InvalidOperationException(
                "Transient smoke fixture cleanup failed.",
                cleanupError);
        }
    }

    private async Task DeleteAccountAsync(
        int accountId,
        string username,
        CancellationToken cancellationToken)
    {
        await using var dataSource =
            NpgsqlDataSource.Create(_connectionString);
        await using var command = dataSource.CreateCommand("""
            DELETE FROM accounts
            WHERE id = @accountId
              AND username = @username;
            """);
        command.Parameters.AddWithValue("accountId", accountId);
        command.Parameters.AddWithValue("username", username);
        var deleted = await command.ExecuteNonQueryAsync(
            cancellationToken);
        if (deleted != 1)
        {
            throw new InvalidOperationException(
                "Transient smoke-account cleanup did not remove exactly one account.");
        }
    }

    private async Task WaitForOfflineAsync(
        int accountId,
        CancellationToken cancellationToken)
    {
        await using var dataSource =
            NpgsqlDataSource.Create(_connectionString);
        for (var attempt = 0; attempt < 40; attempt++)
        {
            await using var command = dataSource.CreateCommand("""
                SELECT login_status
                FROM accounts
                WHERE id = @accountId;
                """);
            command.Parameters.AddWithValue("accountId", accountId);
            var status = await command.ExecuteScalarAsync(
                cancellationToken);
            if (status is null ||
                status is DBNull ||
                Convert.ToInt32(status) == 0)
            {
                return;
            }
            await Task.Delay(
                TimeSpan.FromMilliseconds(50),
                cancellationToken);
        }
    }

    private static byte[] CreateAsciiSecret()
    {
        var bytes = RandomNumberGenerator.GetBytes(18);
        var encoded = Convert.ToHexString(bytes);
        CryptographicOperations.ZeroMemory(bytes);
        return System.Text.Encoding.ASCII.GetBytes(encoded[..32]);
    }
}
