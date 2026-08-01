using System.Security.Cryptography;
using Godswar.Server.Application.Accounts;
using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Commands;
using Godswar.Server.Infrastructure.Accounts;
using Godswar.Server.Infrastructure.Characters;
using Godswar.Server.Infrastructure.Messaging;
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

    private readonly NpgsqlDataSource _dataSource;
    private readonly PostgresAccountStore _accounts;
    private readonly ICharacterLifecycleCommandExecutor _characters;
    private readonly byte[] _password;
    private AccountIdentity? _account;

    private TransientAccountFixture(
        NpgsqlDataSource dataSource,
        PostgresAccountStore accounts,
        ICharacterLifecycleCommandExecutor characters,
        string loginName,
        string username,
        byte[] password)
    {
        _dataSource = dataSource;
        _accounts = accounts;
        _characters = characters;
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
        var dataSource = NpgsqlDataSource.Create(connectionString);
        var accounts = new PostgresAccountStore(dataSource);
        var characters = new PostgresCharacterLifecycleCommandExecutor(
            dataSource,
            new PostgresOutboxDispatcherOptions());
        var password = CreateAsciiSecret();
        TransientAccountFixture? fixture = null;
        try
        {
            for (var attempt = 0; attempt < 4; attempt++)
            {
                var loginName = $"smoke_{Convert.ToHexString(
                    RandomNumberGenerator.GetBytes(10))}".ToLowerInvariant();
                var username = PacketText.DecodeLoginName(loginName);
                if (await accounts.FindAccountByUsernameAsync(
                        username,
                        cancellationToken) is not null)
                {
                    continue;
                }

                fixture = new TransientAccountFixture(
                    dataSource,
                    accounts,
                    characters,
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
                await dataSource.DisposeAsync();
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
                _accounts,
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

        var characterName = $"Probe{Convert.ToHexString(
            RandomNumberGenerator.GetBytes(6))}";
        var create = CharacterCreateCommandEnvelope.Create(
            _account.Id,
            new CommandConnectionCorrelation(
                Guid.NewGuid(),
                CommandTransportKind.SecureTlsLegacy),
            DateTimeOffset.UtcNow,
            new CharacterCreateCommand(
                Guid.NewGuid(),
                CharacterLifecycleCommandContract.SingleCharacterSlot,
                characterName,
                Gender: 1,
                Camp: GameDefaults.SpartaCamp,
                Profession: 1,
                ZodiacType: 1,
                Hair: 0,
                Face: 0,
                Faith: 1));
        var result = await _characters.ExecuteAsync(
            create,
            cancellationToken);
        if (!result.IsSuccess ||
            result.Receipt?.Status !=
                CharacterLifecycleReceiptStatus.Created)
        {
            throw new InvalidOperationException(
                "Could not create the durable transient character.");
        }
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
            await _dataSource.DisposeAsync();
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
        await using var command = _dataSource.CreateCommand("""
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
        for (var attempt = 0; attempt < 40; attempt++)
        {
            await using var command = _dataSource.CreateCommand("""
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
