using Godswar.Server.Networking;

namespace Godswar.Server.ProtocolChecks;

internal static class ServerListenerProfileChecks
{
    public static Task RunAsync()
    {
        var rawOptions = new ServerOptions();
        rawOptions.RuntimeProfile = "LocalDevelopment";
        rawOptions.Storage.Provider = "Postgres";
        rawOptions.Storage.PostgresConnectionString =
            "Host=127.0.0.1;Database=listener-check";
        rawOptions.Authentication.AllowLegacyRawAuthentication =
            true;
        rawOptions.Login.Port = 5999;
        rawOptions.Game.Port = 7000;
        rawOptions.Secure.Enabled = false;
        var raw = ServerListenerProfile.Build(rawOptions);
        Check.True(
            raw.Transport == ServerListenerTransport.RawTcp,
            "raw profile transport");
        Check.Equal(5999, raw.Login.Port, "raw login port");
        Check.Equal(7000, raw.Game.Port, "raw game port");

        var secureOptions = new ServerOptions();
        secureOptions.RuntimeProfile = "Production";
        secureOptions.Storage.Provider = "Postgres";
        secureOptions.Storage.PostgresConnectionString =
            "Host=127.0.0.1;Database=listener-check";
        secureOptions.Authentication.AllowPlaintextMigration = false;
        secureOptions.Secure.Enabled = true;
        secureOptions.Secure.Login.Port = 6599;
        secureOptions.Secure.Game.Port = 7443;
        var secure = ServerListenerProfile.Build(secureOptions);
        Check.True(
            secure.Transport == ServerListenerTransport.SecureTls,
            "secure profile transport");
        Check.Equal(6599, secure.Login.Port, "secure login port");
        Check.Equal(7443, secure.Game.Port, "secure game port");

        secureOptions.Secure.Game.Port = 6599;
        Check.Throws<InvalidDataException>(
            () => ServerListenerProfile.Build(secureOptions),
            "mixed/colliding secure profile");

        var productionRawOptions = new ServerOptions
        {
            RuntimeProfile = "Production"
        };
        productionRawOptions.Storage.Provider = "Postgres";
        productionRawOptions.Storage.PostgresConnectionString =
            "Host=127.0.0.1;Database=listener-check";
        Check.Throws<ServerStartupConfigurationException>(
            () => ServerListenerProfile.Build(productionRawOptions),
            "production raw listener fails closed");
        return Task.CompletedTask;
    }
}
