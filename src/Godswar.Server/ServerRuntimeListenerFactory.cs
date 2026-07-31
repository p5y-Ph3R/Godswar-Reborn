using Godswar.Server.Networking;

namespace Godswar.Server;

internal static class ServerRuntimeListenerFactory
{
    public static (
        TcpEndpointServer? Login,
        TcpEndpointServer? Game) CreateRawPair(
        ServerListenerProfile? profile,
        ServerOptions options,
        IConnectionAdmission admission,
        Func<ClientSession, IClientHandler> loginHandlerFactory,
        Func<ClientSession, IClientHandler> gameHandlerFactory) =>
        profile?.Transport != ServerListenerTransport.RawTcp
            ? (null, null)
            : (
                CreateRawEndpoint(
                    NetworkEndpointRole.Login,
                    profile,
                    options,
                    admission,
                    loginHandlerFactory),
                CreateRawEndpoint(
                    NetworkEndpointRole.Game,
                    profile,
                    options,
                    admission,
                    gameHandlerFactory));

    private static TcpEndpointServer CreateRawEndpoint(
        NetworkEndpointRole role,
        ServerListenerProfile profile,
        ServerOptions options,
        IConnectionAdmission admission,
        Func<ClientSession, IClientHandler> handlerFactory) =>
        new(
            role,
            role == NetworkEndpointRole.Login
                ? profile.Login.Host
                : profile.Game.Host,
            role == NetworkEndpointRole.Login
                ? profile.Login.Port
                : profile.Game.Port,
            options.Network,
            admission,
            handlerFactory);
}
