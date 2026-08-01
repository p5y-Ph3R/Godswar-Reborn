using Godswar.Server.Application.Items;
using Godswar.Server.Application.Pets;
using Npgsql;

namespace Godswar.Server.Infrastructure.Pets;

internal static class PostgresPetContentBootstrapper
{
    public static async Task<PinnedPetContentCatalog> LoadAsync(
        string connectionString,
        IItemTemplateCatalog itemCatalog,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new ArgumentException(
                "A PostgreSQL connection string is required.",
                nameof(connectionString));
        }

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        return await LoadAsync(dataSource, itemCatalog, cancellationToken);
    }

    public static async Task<PinnedPetContentCatalog> LoadAsync(
        NpgsqlDataSource dataSource,
        IItemTemplateCatalog itemCatalog,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(itemCatalog);
        var publication =
            await PostgresPetContentBaselinePublisher.EnsurePublishedAsync(
                dataSource,
                itemCatalog,
                cancellationToken);
        Console.WriteLine(
            publication.Created
                ? "[pet-content] published reviewed database baseline " +
                  $"revision={publication.Revision} " +
                  $"entries={publication.EntryCount}"
                : "[pet-content] using official database publication " +
                  $"revision={publication.Revision} " +
                  $"entries={publication.EntryCount}");
        return await PostgresPetContentReader.LoadAsync(
            dataSource,
            itemCatalog,
            cancellationToken);
    }
}

internal sealed record PetContentPublicationResult(
    string Revision,
    int EntryCount,
    bool Created);
