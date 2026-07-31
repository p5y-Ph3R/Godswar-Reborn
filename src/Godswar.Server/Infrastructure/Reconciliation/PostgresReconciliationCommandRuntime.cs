using Godswar.Server.Application.Reconciliation;
using Godswar.Server.Infrastructure.Messaging;
using Npgsql;

namespace Godswar.Server.Infrastructure.Reconciliation;

internal sealed record PostgresReconciliationRepairOptions(
    int BatchSize,
    int PollIntervalMilliseconds,
    int LeaseMilliseconds,
    int MaximumDeliveryAttempts,
    int BaseRetryDelayMilliseconds,
    int MaximumRetryDelayMilliseconds,
    int GapRetryDelayMilliseconds,
    int CommandTimeoutMilliseconds)
{
    public PostgresOutboxDispatcherOptions
        CreateDispatcherOptions()
    {
        var options = new PostgresOutboxDispatcherOptions
        {
            BatchSize = BatchSize,
            PollIntervalMilliseconds = PollIntervalMilliseconds,
            LeaseMilliseconds = LeaseMilliseconds,
            MaximumDeliveryAttempts = MaximumDeliveryAttempts,
            BaseRetryDelayMilliseconds =
                BaseRetryDelayMilliseconds,
            MaximumRetryDelayMilliseconds =
                MaximumRetryDelayMilliseconds,
            GapRetryDelayMilliseconds =
                GapRetryDelayMilliseconds,
            CommandTimeoutMilliseconds =
                CommandTimeoutMilliseconds
        };
        options.Validate();
        return options;
    }
}

internal sealed record PostgresReconciliationCommandExecutionOptions(
    string ConnectionString,
    ReconciliationOptions Reconciliation,
    PostgresReconciliationRepairOptions? Repair,
    int MaximumRepairs)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString))
        {
            throw new ArgumentException(
                "A PostgreSQL connection string is required.",
                nameof(ConnectionString));
        }

        ArgumentNullException.ThrowIfNull(Reconciliation);
        Reconciliation.Validate();
        if (Repair is null)
        {
            if (MaximumRepairs != 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(MaximumRepairs));
            }

            return;
        }

        if (MaximumRepairs is < 1 or > 500)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaximumRepairs));
        }
    }
}

internal sealed record PostgresReconciliationCommandExecutionResult(
    ReconciliationReport Report,
    ExpiredOutboxLeaseRepairResult? Repair);

internal static class PostgresReconciliationCommandRuntime
{
    public static async Task<
        PostgresReconciliationCommandExecutionResult> ExecuteAsync(
            PostgresReconciliationCommandExecutionOptions options,
            Action? repairAttemptStarting,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        var dispatcherOptions =
            options.Repair?.CreateDispatcherOptions();
        await using var dataSource =
            NpgsqlDataSource.Create(options.ConnectionString);
        var metrics = new ReconciliationMetrics();
        var consumers = PostgresOutboxConsumerCatalog.Create();
        ExpiredOutboxLeaseRepairResult? repair = null;
        if (dispatcherOptions is not null)
        {
            repairAttemptStarting?.Invoke();
            var dispatcher = new PostgresOutboxDispatcher(
                dataSource,
                consumers,
                dispatcherOptions);
            repair =
                await new PostgresExpiredOutboxLeaseRepairer(
                        dispatcher,
                        metrics)
                    .RecoverExpiredOutboxLeasesAsync(
                        options.MaximumRepairs,
                        cancellationToken);
        }

        var report = await new ReconciliationRunner(
                new PostgresReconciliationReader(
                    dataSource,
                    consumers),
                options.Reconciliation,
                metrics)
            .RunAsync(cancellationToken);
        return new PostgresReconciliationCommandExecutionResult(
            report,
            repair);
    }
}
