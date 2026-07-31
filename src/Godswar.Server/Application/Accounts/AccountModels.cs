namespace Godswar.Server.Application.Accounts;

internal sealed record AccountIdentity
{
    public const int MaximumUsernameLength = 32;

    public AccountIdentity(int id, string username)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(id);

        Id = id;
        Username = AccountUsername.Require(username);
    }

    public int Id { get; }

    public string Username { get; }
}

internal sealed record StoredAccountCredential
{
    public const int MaximumVerifierLength = 255;

    public StoredAccountCredential(
        AccountIdentity account,
        string verifier)
    {
        Account = account ?? throw new ArgumentNullException(
            nameof(account));
        ArgumentNullException.ThrowIfNull(verifier);
        if (verifier.Length > MaximumVerifierLength)
        {
            throw new ArgumentException(
                $"Stored account verifier exceeds {MaximumVerifierLength} characters.",
                nameof(verifier));
        }

        Verifier = verifier;
    }

    public AccountIdentity Account { get; }

    public string Verifier { get; }
}
