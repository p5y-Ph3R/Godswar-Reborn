using Godswar.Server.Application.Pets;
using Godswar.Server.Infrastructure.Pets;

namespace Godswar.Server;

internal static class ServerPetOwnerMergeContentComposition
{
    public static async Task<PinnedPetOwnerMergeContentCatalog> LoadAsync(
        ServerOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var content =
            await PostgresPetOwnerMergeContentBootstrapper.LoadAsync(
            options.Storage.PostgresConnectionString,
            cancellationToken);
        var reconciled =
            await PostgresPetOwnerMergeBonusReconciler.ReconcileAsync(
                options.Storage.PostgresConnectionString,
                content,
                cancellationToken);
        if (reconciled > 0)
        {
            Console.WriteLine(
                "[pet-owner-merge-content] rebuilt " +
                $"active_pet_bonuses={reconciled} " +
                $"revision={content.Revision.Sha256}");
        }
        return content;
    }
}
