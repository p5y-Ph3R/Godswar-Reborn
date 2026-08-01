using Godswar.Server.Application.Accounts;
using Godswar.Server.Infrastructure;

namespace Godswar.Server;

internal sealed record ServerAccountPersistenceProviders(
    IAccountCredentialStore Credentials,
    IAccountDirectory Directory,
    IAccountPresenceWriter Presence,
    ILegacyAccountLoginStore LegacyLogin);

internal static class ServerAccountPersistenceComposition
{
    public static ServerAccountPersistenceProviders Create(
        PostgresApplicationDataRuntime postgresRuntime)
    {
        ArgumentNullException.ThrowIfNull(postgresRuntime);
        IAccountCredentialStore provider = postgresRuntime.Accounts;
        IAccountDirectory directory =
            provider as IAccountDirectory ??
            throw Missing("directory");
        IAccountPresenceWriter presence =
            provider as IAccountPresenceWriter ??
            throw Missing("presence writer");
        ILegacyAccountLoginStore legacyLogin =
            provider as ILegacyAccountLoginStore ??
            throw Missing("legacy login adapter");

        return new ServerAccountPersistenceProviders(
            provider,
            directory,
            presence,
            legacyLogin);
    }

    private static InvalidOperationException Missing(string provider) =>
        new($"The account {provider} was not composed.");
}
