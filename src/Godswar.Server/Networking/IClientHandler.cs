namespace Godswar.Server.Networking;

internal interface IClientHandler
{
    Task RunAsync(CancellationToken cancellationToken);
}
