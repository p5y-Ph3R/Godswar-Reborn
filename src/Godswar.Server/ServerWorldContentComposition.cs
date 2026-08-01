using Godswar.Server.Application.World;
using Godswar.Server.Infrastructure.WorldContent;

namespace Godswar.Server;

internal static class ServerWorldContentComposition
{
    public static async ValueTask<IWorldContentReader?> TryLoadAsync(
        ServerOptions options)
    {
        try
        {
            return await PostgresWorldContentBootstrapper.LoadAsync(
                options.Storage.PostgresConnectionString);
        }
        catch (WorldContentUnavailableException error)
        {
            Console.Error.WriteLine(
                "[world-content] startup rejected " +
                $"family={error.Family} reason={error.Reason}");
            Environment.ExitCode = 3;
            return null;
        }
    }
}
