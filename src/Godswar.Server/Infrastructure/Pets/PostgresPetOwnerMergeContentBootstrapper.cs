using Godswar.Server.Application.Pets;
using Npgsql;

namespace Godswar.Server.Infrastructure.Pets;

internal static class PostgresPetOwnerMergeContentBootstrapper
{
    public static async Task<PinnedPetOwnerMergeContentCatalog> LoadAsync(
        string connectionString,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new ArgumentException(
                "A PostgreSQL connection string is required.",
                nameof(connectionString));
        }

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        return await LoadAsync(dataSource, cancellationToken);
    }

    public static async Task<PinnedPetOwnerMergeContentCatalog> LoadAsync(
        NpgsqlDataSource dataSource,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        var publication =
            await PostgresPetOwnerMergeContentBaselinePublisher
                .EnsurePublishedAsync(dataSource, cancellationToken);
        Console.WriteLine(
            publication.Created
                ? "[pet-owner-merge-content] published reviewed database " +
                  $"baseline revision={publication.Revision} " +
                  $"entries={publication.EntryCount}"
                : "[pet-owner-merge-content] using official database " +
                  $"publication revision={publication.Revision} " +
                  $"entries={publication.EntryCount}");
        return await PostgresPetOwnerMergeContentReader.LoadAsync(
            dataSource,
            cancellationToken);
    }
}
