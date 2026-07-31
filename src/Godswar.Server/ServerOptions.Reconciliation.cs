using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Reconciliation;
using Godswar.Server.Infrastructure.Messaging;

namespace Godswar.Server;

internal sealed partial class ServerOptions
{
    private void ApplyReconciliationEnvironment()
    {
        var options = Storage.Reconciliation;
        options.Enabled = ReadBool(
            "GODSWAR_RECONCILIATION_ENABLED",
            options.Enabled);
        var mode = Environment.GetEnvironmentVariable(
            "GODSWAR_RECONCILIATION_MODE");
        if (!string.IsNullOrWhiteSpace(mode))
        {
            if (!Enum.TryParse<ReconciliationMode>(
                    mode,
                    ignoreCase: true,
                    out var parsed))
            {
                throw new InvalidDataException(
                    "GODSWAR_RECONCILIATION_MODE is invalid.");
            }

            options.Mode = parsed;
        }

        options.BatchSize = ReadInt(
            "GODSWAR_RECONCILIATION_BATCH_SIZE",
            options.BatchSize);
        options.MaximumCharactersPerRun = ReadInt(
            "GODSWAR_RECONCILIATION_MAXIMUM_CHARACTERS_PER_RUN",
            options.MaximumCharactersPerRun);
        options.MaximumOutboxEventsPerRun = ReadInt(
            "GODSWAR_RECONCILIATION_MAXIMUM_OUTBOX_EVENTS_PER_RUN",
            options.MaximumOutboxEventsPerRun);
        options.PollIntervalMilliseconds = ReadInt(
            "GODSWAR_RECONCILIATION_POLL_INTERVAL_MILLISECONDS",
            options.PollIntervalMilliseconds);
        options.CommandTimeoutMilliseconds = ReadInt(
            "GODSWAR_RECONCILIATION_COMMAND_TIMEOUT_MILLISECONDS",
            options.CommandTimeoutMilliseconds);
        options.RunTimeoutMilliseconds = ReadInt(
            "GODSWAR_RECONCILIATION_RUN_TIMEOUT_MILLISECONDS",
            options.RunTimeoutMilliseconds);
    }

    private void ValidateReconciliationStorage()
    {
        if (Storage.Reconciliation.Enabled &&
            !string.Equals(
                Storage.Provider,
                nameof(GameStorageProviderKind.Postgres),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "Enabled reconciliation requires PostgreSQL storage.");
        }
    }
}

internal sealed class StorageOptions
{
    public string Provider { get; set; } = string.Empty;

    public string PostgresConnectionString { get; set; } = string.Empty;

    public PostgresOutboxDispatcherOptions Outbox { get; set; } = new();

    public CharacterCheckpointWorkerOptions Checkpoints { get; set; } =
        new();

    public ReconciliationOptions Reconciliation { get; set; } = new();
}
