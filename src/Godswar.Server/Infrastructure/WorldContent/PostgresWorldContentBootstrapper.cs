using Godswar.Server.Application.World;
using Godswar.Server.Infrastructure.Database;

namespace Godswar.Server.Infrastructure.WorldContent;

internal static class PostgresWorldContentBootstrapper
{
    public static async Task<IWorldContentReader> LoadAsync(
        string connectionString,
        CancellationToken cancellationToken = default)
    {
        await PostgresRelationalContentBaselineBootstrapper.EnsureAsync(
            connectionString,
            cancellationToken);
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
        var dialoguePublication =
            await PostgresNpcDialogueBaselinePublisher.EnsurePublishedAsync(
                connectionString,
                cancellationToken);
        Console.WriteLine(
            dialoguePublication.Created
                ? "[npc-dialogue] published reviewed database baseline " +
                  $"revision={dialoguePublication.Revision} " +
                  $"texts={dialoguePublication.TextCount} " +
                  $"routes={dialoguePublication.RouteCount}"
                : "[npc-dialogue] using official database publication " +
                  $"revision={dialoguePublication.Revision} " +
                  $"texts={dialoguePublication.TextCount} " +
                  $"routes={dialoguePublication.RouteCount}");
        var monsterPublication =
            await PostgresMonsterContentBaselinePublisher
                .EnsurePublishedAsync(
                    connectionString,
                    cancellationToken);
        Console.WriteLine(
            monsterPublication.Created
                ? "[monster-content] published reviewed database baseline " +
                  $"revision={monsterPublication.Revision} " +
                  $"entries={monsterPublication.EntryCount}"
                : "[monster-content] using official database publication " +
                  $"revision={monsterPublication.Revision} " +
                  $"entries={monsterPublication.EntryCount}");
        var enterBootstrapPublication =
            await PostgresEnterBootstrapBaselinePublisher
                .EnsurePublishedAsync(
                    connectionString,
                    cancellationToken);
        Console.WriteLine(
            enterBootstrapPublication.Created
                ? "[enter-bootstrap] published explicit safe baseline " +
                  $"revision={enterBootstrapPublication.Revision} " +
                  $"packets={enterBootstrapPublication.PacketCount}"
                : "[enter-bootstrap] using official database publication " +
                  $"revision={enterBootstrapPublication.Revision} " +
                  $"packets={enterBootstrapPublication.PacketCount}");
        var gameplayPublication =
            await PostgresGameplayContentPublisher.EnsurePublishedAsync(
                connectionString,
                cancellationToken);
        Console.WriteLine(
            gameplayPublication.Created
                ? "[gameplay-content] promoted reviewed database content " +
                  $"revision={gameplayPublication.Revision} " +
                  $"entries={gameplayPublication.EntryCount}"
                : "[gameplay-content] using official database publication " +
                  $"revision={gameplayPublication.Revision} " +
                  $"entries={gameplayPublication.EntryCount}");
        return await PostgresWorldContentReaderLoader.LoadAsync(
            connectionString,
            cancellationToken);
    }
}
