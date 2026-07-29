using Godswar.Server.Networking;

namespace Godswar.Server;

internal enum ServerRuntimeProfileKind
{
    LocalDevelopment = 1,
    Production = 2
}

internal enum GameStorageProviderKind
{
    Json = 1,
    Postgres = 2
}

internal enum ServerStartupRejectionReason
{
    OptionsFileMissing = 1,
    RuntimeProfileMissing = 2,
    RuntimeProfileUnknown = 3,
    StorageProviderMissing = 4,
    StorageProviderUnknown = 5,
    JsonStorageForbidden = 6,
    PostgresConnectionMissing = 7,
    RawTransportForbidden = 8
}

internal sealed class ServerStartupConfigurationException :
    Exception
{
    public ServerStartupConfigurationException(
        ServerStartupRejectionReason reason,
        string message)
        : base(message)
    {
        Reason = reason;
    }

    public ServerStartupRejectionReason Reason { get; }
}

internal sealed record ValidatedServerRuntimeProfile(
    ServerRuntimeProfileKind RuntimeProfile,
    GameStorageProviderKind StorageProvider,
    ServerListenerTransport Transport)
{
    public bool AllowsLegacyAuthentication =>
        RuntimeProfile == ServerRuntimeProfileKind.LocalDevelopment &&
        Transport == ServerListenerTransport.RawTcp;
}

internal sealed class LegacyAuthenticationAccess
{
    private LegacyAuthenticationAccess()
    {
    }

    public static LegacyAuthenticationAccess? Create(
        ValidatedServerRuntimeProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return profile.AllowsLegacyAuthentication
            ? new LegacyAuthenticationAccess()
            : null;
    }
}

internal static class ServerRuntimeProfilePolicy
{
    public static ValidatedServerRuntimeProfile Validate(
        ServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var runtimeProfile = ParseRuntimeProfile(options.RuntimeProfile);
        var storage = options.Storage;
        var storageProvider = ParseStorageProvider(storage?.Provider);
        if (storageProvider == GameStorageProviderKind.Postgres &&
            string.IsNullOrWhiteSpace(
                storage?.PostgresConnectionString))
        {
            throw Reject(
                ServerStartupRejectionReason.PostgresConnectionMissing,
                "PostgreSQL storage requires an explicit connection string.");
        }

        if (storageProvider == GameStorageProviderKind.Json &&
            runtimeProfile !=
                ServerRuntimeProfileKind.LocalDevelopment)
        {
            throw Reject(
                ServerStartupRejectionReason.JsonStorageForbidden,
                "JSON storage is restricted to LocalDevelopment.");
        }

        var secure = options.Secure;
        var transport = secure?.Enabled == true
            ? ServerListenerTransport.SecureTls
            : ServerListenerTransport.RawTcp;
        if (transport == ServerListenerTransport.RawTcp &&
            runtimeProfile !=
                ServerRuntimeProfileKind.LocalDevelopment)
        {
            throw Reject(
                ServerStartupRejectionReason.RawTransportForbidden,
                "Raw TCP is restricted to LocalDevelopment.");
        }

        return new ValidatedServerRuntimeProfile(
            runtimeProfile,
            storageProvider,
            transport);
    }

    public static string RejectionCode(
        ServerStartupRejectionReason reason) =>
        reason switch
        {
            ServerStartupRejectionReason.OptionsFileMissing =>
                "options_file_missing",
            ServerStartupRejectionReason.RuntimeProfileMissing =>
                "runtime_profile_missing",
            ServerStartupRejectionReason.RuntimeProfileUnknown =>
                "runtime_profile_unknown",
            ServerStartupRejectionReason.StorageProviderMissing =>
                "storage_provider_missing",
            ServerStartupRejectionReason.StorageProviderUnknown =>
                "storage_provider_unknown",
            ServerStartupRejectionReason.JsonStorageForbidden =>
                "json_storage_forbidden",
            ServerStartupRejectionReason.PostgresConnectionMissing =>
                "postgres_connection_missing",
            ServerStartupRejectionReason.RawTransportForbidden =>
                "raw_transport_forbidden",
            _ => "invalid_configuration"
        };

    private static ServerRuntimeProfileKind ParseRuntimeProfile(
        string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw Reject(
                ServerStartupRejectionReason.RuntimeProfileMissing,
                "A runtime profile is required.");
        }

        if (value.Trim().Equals(
                nameof(ServerRuntimeProfileKind.LocalDevelopment),
                StringComparison.OrdinalIgnoreCase))
        {
            return ServerRuntimeProfileKind.LocalDevelopment;
        }

        if (value.Trim().Equals(
                nameof(ServerRuntimeProfileKind.Production),
                StringComparison.OrdinalIgnoreCase))
        {
            return ServerRuntimeProfileKind.Production;
        }

        throw Reject(
            ServerStartupRejectionReason.RuntimeProfileUnknown,
            "The runtime profile is not recognized.");
    }

    private static GameStorageProviderKind ParseStorageProvider(
        string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw Reject(
                ServerStartupRejectionReason.StorageProviderMissing,
                "A storage provider is required.");
        }

        if (value.Trim().Equals(
                nameof(GameStorageProviderKind.Json),
                StringComparison.OrdinalIgnoreCase))
        {
            return GameStorageProviderKind.Json;
        }

        if (value.Trim().Equals(
                nameof(GameStorageProviderKind.Postgres),
                StringComparison.OrdinalIgnoreCase))
        {
            return GameStorageProviderKind.Postgres;
        }

        throw Reject(
            ServerStartupRejectionReason.StorageProviderUnknown,
            "The storage provider is not recognized.");
    }

    private static ServerStartupConfigurationException Reject(
        ServerStartupRejectionReason reason,
        string message) =>
        new(reason, message);
}
