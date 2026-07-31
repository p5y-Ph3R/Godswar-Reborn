using System.Data;
using Godswar.Server.Application.Messaging;
using Godswar.Server.Application.Reconciliation;
using Godswar.Server.Infrastructure.Messaging;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.Infrastructure.Reconciliation;

internal sealed class PostgresReconciliationReader :
    IReconciliationReader
{
    private readonly NpgsqlDataSource _dataSource;

    private readonly string[] _consumerKeys;
    private readonly string[] _orderingPolicies;

    public PostgresReconciliationReader(
        NpgsqlDataSource dataSource,
        IEnumerable<IOutboxEventConsumer>? consumers = null)
    {
        _dataSource = dataSource ??
            throw new ArgumentNullException(nameof(dataSource));
        var registrations = (consumers ??
                PostgresOutboxConsumerCatalog.Create())
            .Select(consumer => new
            {
                Key = OutboxConsumerContract.RequireKey(
                    consumer.ConsumerKey),
                Policy = consumer.OrderingPolicy switch
                {
                    OutboxOrderingPolicy.StrictSequence =>
                        "strict",
                    OutboxOrderingPolicy.VersionedState =>
                        "latest_wins",
                    _ => throw new ArgumentOutOfRangeException(
                        nameof(consumers))
                }
            })
            .OrderBy(item => item.Key, StringComparer.Ordinal)
            .ToArray();
        if (registrations.Length == 0 ||
            registrations
                .Select(item => item.Key)
                .Distinct(StringComparer.Ordinal)
                .Count() != registrations.Length)
        {
            throw new InvalidOperationException(
                "Reconciliation requires unique outbox consumers.");
        }

        _consumerKeys = registrations
            .Select(item => item.Key)
            .ToArray();
        _orderingPolicies = registrations
            .Select(item => item.Policy)
            .ToArray();
    }

    public async Task<IReconciliationSnapshot> OpenSnapshotAsync(
        TimeSpan commandTimeout,
        CancellationToken cancellationToken)
    {
        if (commandTimeout < TimeSpan.FromMilliseconds(100) ||
            commandTimeout > TimeSpan.FromMinutes(1))
        {
            throw new ArgumentOutOfRangeException(
                nameof(commandTimeout));
        }

        var connection =
            await _dataSource.OpenConnectionAsync(cancellationToken);
        NpgsqlTransaction? transaction = null;
        try
        {
            transaction = await connection.BeginTransactionAsync(
                IsolationLevel.RepeatableRead,
                cancellationToken);
            var timeoutMilliseconds =
                checked((int)commandTimeout.TotalMilliseconds);
            await using var command = new NpgsqlCommand(
                """
                SET TRANSACTION READ ONLY;
                SELECT
                    set_config(
                        'statement_timeout',
                        @statement_timeout,
                        true),
                    set_config(
                        'lock_timeout',
                        @lock_timeout,
                        true),
                    set_config(
                        'idle_in_transaction_session_timeout',
                        @idle_timeout,
                        true);
                """,
                connection,
                transaction)
            {
                CommandTimeout = Seconds(commandTimeout)
            };
            command.Parameters.AddWithValue(
                "statement_timeout",
                $"{timeoutMilliseconds}ms");
            command.Parameters.AddWithValue(
                "lock_timeout",
                $"{Math.Min(timeoutMilliseconds, 1_000)}ms");
            command.Parameters.AddWithValue(
                "idle_timeout",
                $"{Math.Max(timeoutMilliseconds, 1_000)}ms");
            await command.ExecuteNonQueryAsync(cancellationToken);
            return new PostgresReconciliationSnapshot(
                connection,
                transaction,
                Seconds(commandTimeout),
                PostgresSchemaMigrationCatalog.All,
                _consumerKeys,
                _orderingPolicies);
        }
        catch
        {
            if (transaction is not null)
            {
                await transaction.DisposeAsync();
            }

            await connection.DisposeAsync();
            throw;
        }
    }

    private static int Seconds(TimeSpan timeout) =>
        Math.Max(1, checked((int)Math.Ceiling(timeout.TotalSeconds)));
}

internal sealed partial class PostgresReconciliationSnapshot :
    IReconciliationSnapshot
{
    private readonly NpgsqlConnection _connection;
    private readonly NpgsqlTransaction _transaction;
    private readonly int _commandTimeoutSeconds;
    private readonly IReadOnlyList<PostgresSchemaMigration>
        _expectedMigrations;
    private readonly string[] _consumerKeys;
    private readonly string[] _orderingPolicies;
    private bool _disposed;

    public PostgresReconciliationSnapshot(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int commandTimeoutSeconds,
        IReadOnlyList<PostgresSchemaMigration> expectedMigrations,
        string[] consumerKeys,
        string[] orderingPolicies)
    {
        _connection = connection;
        _transaction = transaction;
        _commandTimeoutSeconds = commandTimeoutSeconds;
        _expectedMigrations = expectedMigrations;
        _consumerKeys = consumerKeys;
        _orderingPolicies = orderingPolicies;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _transaction.DisposeAsync();
        await _connection.DisposeAsync();
    }

    private NpgsqlCommand CreateCommand(string sql) =>
        new(sql, _connection, _transaction)
        {
            CommandTimeout = _commandTimeoutSeconds
        };

    private static void Add(
        IDictionary<ReconciliationCategory, long> counts,
        ReconciliationCategory category,
        bool found)
    {
        if (!found)
        {
            return;
        }

        counts.TryGetValue(category, out var current);
        counts[category] = checked(current + 1);
    }

    private static IReadOnlyList<ReconciliationCategoryCount> ToCounts(
        IDictionary<ReconciliationCategory, long> counts) =>
        counts
            .OrderBy(pair => pair.Key)
            .Select(pair => new ReconciliationCategoryCount(
                pair.Key,
                pair.Value))
            .ToArray();
}
