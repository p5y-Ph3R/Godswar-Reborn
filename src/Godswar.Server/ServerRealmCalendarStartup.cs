using Godswar.Server.Application.Realms;
using Godswar.Server.Infrastructure.Realms;

namespace Godswar.Server;

internal static class ServerRealmCalendarStartup
{
    public static async Task<(
        RealmCalendarCatalog Catalog,
        RealmCalendar Selected)> LoadForProcessAsync(
        ServerOptions options,
        CancellationToken cancellationToken = default)
    {
        var catalog = await LoadAsync(options, cancellationToken);
        return (
            catalog,
            catalog.Require(options.Game.WorldInstances.ProcessRealmId));
    }

    public static async Task<RealmCalendarCatalog> LoadAsync(
        ServerOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var catalog = await PostgresRealmCalendarCatalogReader.LoadAsync(
            options.Storage.PostgresConnectionString,
            cancellationToken);
        _ = catalog.Require(options.Game.WorldInstances.ProcessRealmId);
        return catalog;
    }
}
