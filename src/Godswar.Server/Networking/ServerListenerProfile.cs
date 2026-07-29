using Godswar.Server.Networking.Secure;

namespace Godswar.Server.Networking;

internal enum ServerListenerTransport
{
    RawTcp = 0,
    SecureTls = 1
}

internal sealed record ServerListenerBinding(
    NetworkEndpointRole Role,
    ServerListenerTransport Transport,
    string Host,
    int Port);

internal sealed class ServerListenerProfile
{
    private ServerListenerProfile(
        ServerListenerBinding login,
        ServerListenerBinding game)
    {
        if (login.Role != NetworkEndpointRole.Login ||
            game.Role != NetworkEndpointRole.Game ||
            login.Transport != game.Transport ||
            string.IsNullOrWhiteSpace(login.Host) ||
            string.IsNullOrWhiteSpace(game.Host) ||
            login.Port is < 1 or > ushort.MaxValue ||
            game.Port is < 1 or > ushort.MaxValue ||
            login.Port == game.Port)
        {
            throw new InvalidDataException(
                "A listener profile must contain one coherent, distinct login/game pair.");
        }

        Login = login;
        Game = game;
    }

    public ServerListenerBinding Login { get; }

    public ServerListenerBinding Game { get; }

    public ServerListenerTransport Transport => Login.Transport;

    public static ServerListenerProfile Build(ServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var runtimeProfile =
            ServerRuntimeProfilePolicy.Validate(options);
        var secure = options.Secure
            ?? throw new InvalidDataException(
                "Secure network options are required.");
        if (runtimeProfile.Transport ==
            ServerListenerTransport.SecureTls)
        {
            return new ServerListenerProfile(
                Binding(
                    NetworkEndpointRole.Login,
                    ServerListenerTransport.SecureTls,
                    secure.Login),
                Binding(
                    NetworkEndpointRole.Game,
                    ServerListenerTransport.SecureTls,
                    secure.Game));
        }

        return new ServerListenerProfile(
            new ServerListenerBinding(
                NetworkEndpointRole.Login,
                ServerListenerTransport.RawTcp,
                options.Login.BindHost,
                options.Login.Port),
            new ServerListenerBinding(
                NetworkEndpointRole.Game,
                ServerListenerTransport.RawTcp,
                options.Game.BindHost,
                options.Game.Port));
    }

    private static ServerListenerBinding Binding(
        NetworkEndpointRole role,
        ServerListenerTransport transport,
        SecureEndpointOptions endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        return new ServerListenerBinding(
            role,
            transport,
            endpoint.BindHost,
            endpoint.Port);
    }
}
