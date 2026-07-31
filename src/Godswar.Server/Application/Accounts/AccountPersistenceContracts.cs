namespace Godswar.Server.Application.Accounts;

/// <summary>
/// Owns credential reads, conflict-safe registration, and password-verifier
/// compare-and-swap. Password hashing deliberately occurs outside this
/// persistence boundary.
/// </summary>
internal interface IAccountCredentialStore
{
    Task<StoredAccountCredential?> FindAccountCredentialAsync(
        string username,
        CancellationToken cancellationToken = default);

    Task<AccountIdentity?> TryCreateAccountWithCredentialAsync(
        string username,
        string versionedVerifier,
        CancellationToken cancellationToken = default);

    Task<bool> TryReplaceAccountCredentialAsync(
        int accountId,
        string expectedVerifier,
        string versionedVerifier,
        CancellationToken cancellationToken = default);
}

internal interface IAccountDirectory
{
    Task<AccountIdentity?> FindAccountByIdAsync(
        int accountId,
        CancellationToken cancellationToken = default);

    Task<AccountIdentity?> FindAccountByUsernameAsync(
        string username,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Maintains the legacy accounts.login_status/online-time projection. It is
/// not authentication, connection ownership, or distributed presence
/// authority.
/// </summary>
internal interface IAccountPresenceWriter
{
    Task MarkAccountOnlineAsync(
        int accountId,
        CancellationToken cancellationToken = default);

    Task MarkAccountOfflineAsync(
        int accountId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Explicit local-development compatibility boundary for the original raw
/// login protocol. Production authentication must use versioned verifiers
/// through <see cref="IAccountCredentialStore"/>.
/// </summary>
internal interface ILegacyAccountLoginStore
{
    Task<AccountIdentity> LoginOrCreateLegacyAccountAsync(
        string username,
        string password,
        CancellationToken cancellationToken = default);
}
