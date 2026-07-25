using Godswar.Server.Security.Authentication;

namespace Godswar.Server.State;

internal static class AccountCredentialPersistence
{
    public static string RequireUsername(string username)
    {
        ArgumentNullException.ThrowIfNull(username);
        if (username.Length is < 1 or >
                AuthenticationOptions.MaximumUsernameBytes ||
            username.Any(static character =>
                character is < '!' or > '~'))
        {
            throw new ArgumentException(
                $"Account username must contain 1..{AuthenticationOptions.MaximumUsernameBytes} printable ASCII bytes.",
                nameof(username));
        }

        return username;
    }

    public static string RequireVersionedVerifier(
        string versionedVerifier)
    {
        ArgumentNullException.ThrowIfNull(versionedVerifier);
        if (!PasswordVerifierRecord.TryParse(
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

    public static GameAccount WithoutCredential(GameAccount account)
    {
        ArgumentNullException.ThrowIfNull(account);
        return new GameAccount
        {
            Id = account.Id,
            Username = account.Username,
            Password = string.Empty,
            VipTier = account.VipTier,
            VipExpiresAt = account.VipExpiresAt,
            CreatedUtc = account.CreatedUtc
        };
    }
}
