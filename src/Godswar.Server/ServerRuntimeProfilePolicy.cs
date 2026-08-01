using Godswar.Server.Networking;

namespace Godswar.Server;

internal enum ServerRuntimeProfileKind
{
    LocalDevelopment = 1,
    Production = 2
}

internal enum GameStorageProviderKind
{
    Postgres = 2
}

internal enum ServerStartupRejectionReason
{
    OptionsFileMissing = 1,
    RuntimeProfileMissing = 2,
    RuntimeProfileUnknown = 3,
    StorageProviderMissing = 4,
    StorageProviderUnknown = 5,
    PostgresConnectionMissing = 7,
    RawTransportForbidden = 8,
    LegacyRawAuthenticationDisabled = 9,
    LegacyRawAuthenticationScopeInvalid = 10,
    PlaintextMigrationForbidden = 11
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
    ServerListenerTransport Transport,
    bool AllowsLegacyAuthentication);

internal sealed class LegacyAuthenticationAccess
{
    private LegacyAuthenticationAccess()
    {
    }

    public static LegacyAuthenticationAccess? Create(
        ValidatedServerRuntimeProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return profile.AllowsLegacyAuthentication &&
            profile.RuntimeProfile ==
                ServerRuntimeProfileKind.LocalDevelopment &&
            profile.Transport == ServerListenerTransport.RawTcp
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

        var secure = options.Secure;
        var backhaulWorker = options.Backhaul?.Enabled == true;
        var transport = secure?.Enabled == true
            ? ServerListenerTransport.SecureTls
            : ServerListenerTransport.RawTcp;
        if (transport == ServerListenerTransport.RawTcp &&
            !backhaulWorker &&
            runtimeProfile !=
                ServerRuntimeProfileKind.LocalDevelopment)
        {
            throw Reject(
                ServerStartupRejectionReason.RawTransportForbidden,
                "Raw TCP is restricted to LocalDevelopment.");
        }

        var allowsLegacyAuthentication =
            options.Authentication?.
                AllowLegacyRawAuthentication == true;
        if (transport == ServerListenerTransport.RawTcp &&
            !backhaulWorker &&
            !allowsLegacyAuthentication)
        {
            throw Reject(
                ServerStartupRejectionReason.
                    LegacyRawAuthenticationDisabled,
                "Raw TCP requires the explicit local-development legacy authentication rollback capability.");
        }

        if (allowsLegacyAuthentication &&
            (backhaulWorker ||
             runtimeProfile !=
                ServerRuntimeProfileKind.LocalDevelopment ||
             transport != ServerListenerTransport.RawTcp))
        {
            throw Reject(
                ServerStartupRejectionReason.
                    LegacyRawAuthenticationScopeInvalid,
                "Legacy raw authentication is valid only for the LocalDevelopment raw TCP rollback profile.");
        }

        if (runtimeProfile == ServerRuntimeProfileKind.Production &&
            options.Authentication?.AllowPlaintextMigration == true)
        {
            throw Reject(
                ServerStartupRejectionReason.
                    PlaintextMigrationForbidden,
                "Plaintext credential migration is forbidden in Production.");
        }

        return new ValidatedServerRuntimeProfile(
            runtimeProfile,
            storageProvider,
            transport,
            allowsLegacyAuthentication);
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
            ServerStartupRejectionReason.PostgresConnectionMissing =>
                "postgres_connection_missing",
            ServerStartupRejectionReason.RawTransportForbidden =>
                "raw_transport_forbidden",
            ServerStartupRejectionReason.
                LegacyRawAuthenticationDisabled =>
                "legacy_raw_authentication_disabled",
            ServerStartupRejectionReason.
                LegacyRawAuthenticationScopeInvalid =>
                "legacy_raw_authentication_scope_invalid",
            ServerStartupRejectionReason.
                PlaintextMigrationForbidden =>
                "plaintext_migration_forbidden",
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
