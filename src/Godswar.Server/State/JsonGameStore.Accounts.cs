using Godswar.Server.Security.Authentication;

namespace Godswar.Server.State;

internal sealed partial class JsonGameStore
{
    public async Task<GameAccount> LoginOrCreateAccountAsync(
        string username,
        string password,
        CancellationToken cancellationToken = default)
    {
        username = CleanUsername(username);
        password ??= string.Empty;

        await _lock.WaitAsync(cancellationToken);
        try
        {
            var db = await LoadUnsafeAsync(cancellationToken);
            var account = FindByUsername(db, username);
            if (account is null)
            {
                account = new GameAccount
                {
                    Id = db.NextAccountId++,
                    Username = username,
                    Password = password,
                    CreatedUtc = DateTime.UtcNow
                };
                db.Accounts.Add(account);
            }
            else if (!PasswordVerifierRecord.IsVersionedCandidate(
                         account.Password))
            {
                // Raw emulator compatibility may still replace plaintext.
                // Once secure authentication writes a versioned verifier,
                // legacy game login must never erase it with an empty string.
                account.Password = password;
            }

            await SaveUnsafeAsync(db, cancellationToken);
            return AccountCredentialPersistence.WithoutCredential(account);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<StoredAccountCredential?>
        FindAccountCredentialAsync(
            string username,
            CancellationToken cancellationToken = default)
    {
        username = AccountCredentialPersistence.RequireUsername(username);
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var account = FindByUsername(
                await LoadUnsafeAsync(cancellationToken),
                username);
            return account is null
                ? null
                : new StoredAccountCredential(
                    AccountCredentialPersistence.WithoutCredential(account),
                    account.Password ?? string.Empty);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<GameAccount?> FindAccountByIdAsync(
        int accountId,
        CancellationToken cancellationToken = default)
    {
        if (accountId <= 0)
        {
            return null;
        }

        await _lock.WaitAsync(cancellationToken);
        try
        {
            var account = (await LoadUnsafeAsync(cancellationToken))
                .Accounts
                .FirstOrDefault(candidate => candidate.Id == accountId);
            return account is null
                ? null
                : AccountCredentialPersistence.WithoutCredential(account);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<GameAccount?> FindAccountByUsernameAsync(
        string username,
        CancellationToken cancellationToken = default)
    {
        username = AccountCredentialPersistence.RequireUsername(username);
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var account = FindByUsername(
                await LoadUnsafeAsync(cancellationToken),
                username);
            return account is null
                ? null
                : AccountCredentialPersistence.WithoutCredential(account);
        }
        finally
        {
            _lock.Release();
        }
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

        await _lock.WaitAsync(cancellationToken);
        try
        {
            var db = await LoadUnsafeAsync(cancellationToken);
            if (FindByUsername(db, username) is not null)
            {
                return null;
            }

            var account = new GameAccount
            {
                Id = db.NextAccountId++,
                Username = username,
                Password = versionedVerifier,
                CreatedUtc = DateTime.UtcNow
            };
            db.Accounts.Add(account);
            await SaveUnsafeAsync(db, cancellationToken);
            return AccountCredentialPersistence.WithoutCredential(account);
        }
        finally
        {
            _lock.Release();
        }
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

        await _lock.WaitAsync(cancellationToken);
        try
        {
            var db = await LoadUnsafeAsync(cancellationToken);
            var account = db.Accounts.FirstOrDefault(candidate =>
                candidate.Id == accountId);
            if (account is null ||
                !string.Equals(
                    account.Password,
                    expectedVerifier,
                    StringComparison.Ordinal))
            {
                return false;
            }

            account.Password = versionedVerifier;
            await SaveUnsafeAsync(db, cancellationToken);
            return true;
        }
        finally
        {
            _lock.Release();
        }
    }

    public Task MarkAccountOnlineAsync(
        int accountId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    private static GameAccount? FindByUsername(
        GameDatabase database,
        string username)
    {
        return database.Accounts.FirstOrDefault(account =>
            string.Equals(
                account.Username,
                username,
                StringComparison.OrdinalIgnoreCase));
    }
}
