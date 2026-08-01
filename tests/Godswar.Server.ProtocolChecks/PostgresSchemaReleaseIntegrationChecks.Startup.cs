using Godswar.Server.Infrastructure.Database;
using Godswar.Server.Infrastructure.Items;
using Godswar.Server.Infrastructure.WorldContent;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresSchemaReleaseIntegrationChecks
{
    private static async Task InitializeReleaseAsync(
        string connectionString)
    {
        await PostgresSchemaStartup.InitializeAsync(connectionString);
        await PostgresRelationalContentBaselineBootstrapper.EnsureAsync(
            connectionString);
        _ = await PostgresItemTemplateContentBootstrapper.LoadAsync(
            connectionString);
        _ = await PostgresWorldContentBootstrapper.LoadAsync(
            connectionString);
    }
}
