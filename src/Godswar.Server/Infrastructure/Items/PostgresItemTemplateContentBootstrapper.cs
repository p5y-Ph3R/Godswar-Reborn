using Godswar.Server.Application.Items;
using Npgsql;

namespace Godswar.Server.Infrastructure.Items;

internal static class PostgresItemTemplateContentBootstrapper
{
    public static async Task<PinnedItemTemplateCatalog> LoadAsync(
        string connectionString,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        await using var dataSource =
            NpgsqlDataSource.Create(connectionString);
        return await LoadAsync(dataSource, cancellationToken);
    }

    public static async Task<PinnedItemTemplateCatalog> LoadAsync(
        NpgsqlDataSource dataSource,
        CancellationToken cancellationToken = default)
    {
        var publication =
            await PostgresItemTemplateBaselinePublisher
                .EnsurePublishedAsync(dataSource, cancellationToken);
        Console.WriteLine(
            publication.Created
                ? "[item-content] published reviewed database baseline " +
                  $"revision={publication.Revision} " +
                  $"entries={publication.EntryCount}"
                : "[item-content] using official database publication " +
                  $"revision={publication.Revision} " +
                  $"entries={publication.EntryCount}");
        return await PostgresItemTemplateCatalogLoader.LoadAsync(
            dataSource,
            cancellationToken);
    }
}
