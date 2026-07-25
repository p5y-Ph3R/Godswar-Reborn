using Godswar.Server.State;

namespace Godswar.Server.Security.Authentication;

internal enum AccountAuthenticationStatus
{
    Accepted,
    Rejected,
    PasswordResetRequired,
    InvalidInput,
    Busy,
    TimedOut
}

internal sealed record AccountAuthenticationResult(
    AccountAuthenticationStatus Status,
    GameAccount? Account = null,
    bool CredentialMigrated = false,
    bool AccountCreated = false)
{
    public bool IsAccepted =>
        Status == AccountAuthenticationStatus.Accepted &&
        Account is not null;
}
