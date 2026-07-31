namespace Godswar.Server.Application.Accounts;

/// <summary>
/// Defines the one account-username rule used before persistence and when
/// constructing credential-free account identities.
/// </summary>
internal static class AccountUsername
{
    private const string LegacyFallback = "player";

    public static string Require(string username)
    {
        ArgumentNullException.ThrowIfNull(username);
        if (username.Length is < 1 or >
                AccountIdentity.MaximumUsernameLength ||
            username.Any(static character =>
                character is < '!' or > '~'))
        {
            throw new ArgumentException(
                $"Account username must contain 1..{AccountIdentity.MaximumUsernameLength} printable ASCII bytes.",
                nameof(username));
        }

        return username;
    }

    public static string NormalizeLegacy(string username)
    {
        ArgumentNullException.ThrowIfNull(username);
        var normalized = username.Trim('\0', ' ', '\t', '\r', '\n');
        return Require(
            string.IsNullOrWhiteSpace(normalized)
                ? LegacyFallback
                : normalized);
    }
}
