using Godswar.Server.Application.Accounts;
using Godswar.Server.Security.Authentication;

namespace Godswar.Server.State;

internal static class AccountCredentialPersistence
{
    public static string RequireUsername(string username) =>
        AccountUsername.Require(username);

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
