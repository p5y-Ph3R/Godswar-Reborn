using Godswar.Server.Application.World;

namespace Godswar.Server.Infrastructure.WorldContent;

internal static class PostgresWorldContentBootstrapper
{
    public static async Task<IWorldContentReader> LoadAsync(
        string connectionString,
        CancellationToken cancellationToken = default)
    {
        var publication =
            await PostgresNpcContentBaselinePublisher.EnsurePublishedAsync(
                connectionString,
                cancellationToken);
        Console.WriteLine(
            publication.Created
                ? "[npc-content] published reviewed database baseline " +
                  $"revision={publication.Revision} " +
                  $"entries={publication.EntryCount}"
                : "[npc-content] using official database publication " +
                  $"revision={publication.Revision} " +
                  $"entries={publication.EntryCount}");
        return await PostgresWorldContentReaderLoader.LoadAsync(
            connectionString,
            cancellationToken);
    }
}
