using Godswar.Server.Infrastructure.WorldInstances;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresMedusaTitleAwardStoreChecks
{
    public const string CheckName =
        "PostgreSQL Medusa title award settlement";

    private const string ConnectionStringVariable =
        "GODSWAR_TEST_POSTGRES_CONNECTION_STRING";

    public static async Task RunAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable(
            ConnectionStringVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Console.WriteLine(
                $"SKIP {CheckName} ({ConnectionStringVariable} is not set)");
            return;
        }

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var database = await ReadDatabaseAsync(dataSource);
        if (!PostgresMedusaAdmissionSchema.IsDisposableDatabaseName(database))
        {
            Console.WriteLine(
                $"SKIP {CheckName} (database '{database}' is not disposable)");
            return;
        }

        var wrongDatabase = database == "godswar_medusa_ffffffff"
            ? "godswar_medusa_eeeeeeee"
            : "godswar_medusa_ffffffff";
        await AssertThrowsAsync<InvalidOperationException>(
            () => PostgresMedusaTitleAwardSchema
                .CreateForDisposableDatabaseAsync(dataSource, wrongDatabase),
            "title schema rejects an inexact current database name");

        await PostgresMedusaAdmissionSchema.DropForDisposableDatabaseAsync(
            dataSource,
            database);
        await PostgresMedusaAdmissionSchema.CreateForDisposableDatabaseAsync(
            dataSource,
            database);
        try
        {
            await PostgresMedusaTitleAwardSchema
                .CreateForDisposableDatabaseAsync(dataSource, database);
            var admissionStore =
                new PostgresMedusaDurableAdmissionStore(dataSource);
            var titleStore = new PostgresMedusaTitleAwardStore(dataSource);
            await AssertAtomicRosterAwardAndReplayAsync(
                dataSource,
                admissionStore,
                titleStore);
            await AssertNoTitleSettlementAsync(admissionStore, titleStore);
            await AssertPreownedTitleStillCoversFrozenRosterAsync(
                admissionStore,
                titleStore);
            await AssertOwnershipIntegrityFailsClosedAsync(
                dataSource,
                admissionStore,
                titleStore);
            await AssertEvidenceAndTerminalConflictsAsync(
                admissionStore,
                titleStore);
            await AssertConcurrentSettlementAsync(
                admissionStore,
                titleStore);
            await AssertChangedConcurrentRequestConflictsAsync(
                admissionStore,
                titleStore);
        }
        finally
        {
            await PostgresMedusaTitleAwardSchema.DropForDisposableDatabaseAsync(
                dataSource,
                database);
            await PostgresMedusaAdmissionSchema.DropForDisposableDatabaseAsync(
                dataSource,
                database);
        }
    }
}
