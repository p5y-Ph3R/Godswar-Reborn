using Godswar.Server.Application.Accounts;

namespace Godswar.Server.State;

internal sealed partial class PostgresGameStore
{
    public async Task<GameAccount> LoginOrCreateAccountAsync(
        string username,
        string password,
        CancellationToken cancellationToken = default) =>
        ToLegacyAccount(
            await _accountStore.LoginOrCreateLegacyAccountAsync(
                username,
                password,
                cancellationToken));

    public async Task<StoredAccountCredential?>
        FindAccountCredentialAsync(
            string username,
            CancellationToken cancellationToken = default)
    {
        var stored = await _accountStore.FindAccountCredentialAsync(
            username,
            cancellationToken);
        return stored is null
            ? null
            : new StoredAccountCredential(
                ToLegacyAccount(stored.Account),
                stored.Verifier);
    }

    public async Task<GameAccount?> FindAccountByIdAsync(
        int accountId,
        CancellationToken cancellationToken = default)
    {
        var account = await _accountStore.FindAccountByIdAsync(
            accountId,
            cancellationToken);
        return account is null ? null : ToLegacyAccount(account);
    }

    public async Task<GameAccount?> FindAccountByUsernameAsync(
        string username,
        CancellationToken cancellationToken = default)
    {
        var account = await _accountStore.FindAccountByUsernameAsync(
            username,
            cancellationToken);
        return account is null ? null : ToLegacyAccount(account);
    }

    public async Task<GameAccount?> TryCreateAccountWithCredentialAsync(
        string username,
        string versionedVerifier,
        CancellationToken cancellationToken = default)
    {
        var account =
            await _accountStore.TryCreateAccountWithCredentialAsync(
                username,
                versionedVerifier,
                cancellationToken);
        return account is null ? null : ToLegacyAccount(account);
    }

    public Task<bool> TryReplaceAccountCredentialAsync(
        int accountId,
        string expectedVerifier,
        string versionedVerifier,
        CancellationToken cancellationToken = default) =>
        _accountStore.TryReplaceAccountCredentialAsync(
            accountId,
            expectedVerifier,
            versionedVerifier,
            cancellationToken);

    public Task MarkAccountOnlineAsync(
        int accountId,
        CancellationToken cancellationToken = default) =>
        _accountStore.MarkAccountOnlineAsync(
            accountId,
            cancellationToken);

    Task<AccountIdentity>
        ILegacyAccountLoginStore.LoginOrCreateLegacyAccountAsync(
            string username,
            string password,
            CancellationToken cancellationToken) =>
        _accountStore.LoginOrCreateLegacyAccountAsync(
            username,
            password,
            cancellationToken);

    Task<Godswar.Server.Application.Accounts.StoredAccountCredential?>
        IAccountCredentialStore.FindAccountCredentialAsync(
            string username,
            CancellationToken cancellationToken) =>
        _accountStore.FindAccountCredentialAsync(
            username,
            cancellationToken);

    Task<AccountIdentity?>
        IAccountCredentialStore.TryCreateAccountWithCredentialAsync(
            string username,
            string versionedVerifier,
            CancellationToken cancellationToken) =>
        _accountStore.TryCreateAccountWithCredentialAsync(
            username,
            versionedVerifier,
            cancellationToken);

    Task<bool> IAccountCredentialStore.TryReplaceAccountCredentialAsync(
        int accountId,
        string expectedVerifier,
        string versionedVerifier,
        CancellationToken cancellationToken) =>
        _accountStore.TryReplaceAccountCredentialAsync(
            accountId,
            expectedVerifier,
            versionedVerifier,
            cancellationToken);

    Task<AccountIdentity?> IAccountDirectory.FindAccountByIdAsync(
        int accountId,
        CancellationToken cancellationToken) =>
        _accountStore.FindAccountByIdAsync(
            accountId,
            cancellationToken);

    Task<AccountIdentity?> IAccountDirectory.FindAccountByUsernameAsync(
        string username,
        CancellationToken cancellationToken) =>
        _accountStore.FindAccountByUsernameAsync(
            username,
            cancellationToken);

    Task IAccountPresenceWriter.MarkAccountOnlineAsync(
        int accountId,
        CancellationToken cancellationToken) =>
        _accountStore.MarkAccountOnlineAsync(
            accountId,
            cancellationToken);

    Task IAccountPresenceWriter.MarkAccountOfflineAsync(
        int accountId,
        CancellationToken cancellationToken) =>
        _accountStore.MarkAccountOfflineAsync(
            accountId,
            cancellationToken);

    Task IAccountPresenceWriter.MarkAccountPlayerOnlineAsync(
        int accountId,
        Guid presenceToken,
        CancellationToken cancellationToken) =>
        _accountStore.MarkAccountPlayerOnlineAsync(
            accountId,
            presenceToken,
            cancellationToken);

    Task<bool>
        IAccountPresenceWriter.TryMarkAccountPlayerOfflineAsync(
            int accountId,
            Guid presenceToken,
            CancellationToken cancellationToken) =>
        _accountStore.TryMarkAccountPlayerOfflineAsync(
            accountId,
            presenceToken,
            cancellationToken);

    private static GameAccount ToLegacyAccount(
        AccountIdentity account) =>
        new()
        {
            Id = account.Id,
            Username = account.Username,
            Password = string.Empty
        };
}
