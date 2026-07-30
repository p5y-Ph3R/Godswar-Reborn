using Godswar.Server.Application.Characters;
using Npgsql;

namespace Godswar.Server.Infrastructure.Characters;

/// <summary>
/// PostgreSQL authority for the narrow, coalescible character checkpoint
/// facets. Valuable character state remains owned by its operation-specific
/// transaction.
/// </summary>
internal sealed partial class PostgresCharacterCheckpointStore :
    ICharacterCheckpointStore,
    IAsyncDisposable
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly bool _ownsDataSource;

    public PostgresCharacterCheckpointStore(string connectionString)
        : this(
            CreateDataSource(connectionString),
            ownsDataSource: true)
    {
    }

    internal PostgresCharacterCheckpointStore(
        NpgsqlDataSource dataSource)
        : this(dataSource, ownsDataSource: false)
    {
    }

    private PostgresCharacterCheckpointStore(
        NpgsqlDataSource dataSource,
        bool ownsDataSource)
    {
        _dataSource = dataSource ??
            throw new ArgumentNullException(nameof(dataSource));
        _ownsDataSource = ownsDataSource;
    }

    public ValueTask DisposeAsync() =>
        _ownsDataSource
            ? _dataSource.DisposeAsync()
            : ValueTask.CompletedTask;

    private static NpgsqlDataSource CreateDataSource(
        string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        return NpgsqlDataSource.Create(connectionString);
    }

    private static void ValidateIdentity(
        int accountId,
        int characterId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(accountId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(characterId);
    }

    private static void RequireExactlyOneRow(
        int affectedRows,
        string operation)
    {
        if (affectedRows != 1)
        {
            throw new InvalidOperationException(
                $"PostgreSQL checkpoint {operation} expected exactly " +
                $"one affected row; observed {affectedRows}.");
        }
    }
}
