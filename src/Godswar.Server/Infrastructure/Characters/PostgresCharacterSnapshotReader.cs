using System.Data;
using Godswar.Server.Application.Characters;
using Npgsql;

namespace Godswar.Server.Infrastructure.Characters;

internal enum PostgresCharacterSnapshotReadStage : byte
{
    CoreLoaded = 1
}

internal interface IPostgresCharacterSnapshotReadProbe
{
    ValueTask ReachedAsync(
        PostgresCharacterSnapshotReadStage stage,
        CancellationToken cancellationToken);
}

internal sealed partial class PostgresCharacterSnapshotReader :
    ICharacterSnapshotReader,
    IAsyncDisposable
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly IPostgresCharacterSnapshotReadProbe? _probe;
    private readonly bool _ownsDataSource;

    public PostgresCharacterSnapshotReader(string connectionString)
        : this(
            CreateDataSource(connectionString),
            probe: null,
            ownsDataSource: true)
    {
    }

    internal PostgresCharacterSnapshotReader(
        string connectionString,
        IPostgresCharacterSnapshotReadProbe? probe)
        : this(
            CreateDataSource(connectionString),
            probe,
            ownsDataSource: true)
    {
    }

    internal PostgresCharacterSnapshotReader(
        NpgsqlDataSource dataSource,
        IPostgresCharacterSnapshotReadProbe? probe = null)
        : this(dataSource, probe, ownsDataSource: false)
    {
    }

    private PostgresCharacterSnapshotReader(
        NpgsqlDataSource dataSource,
        IPostgresCharacterSnapshotReadProbe? probe,
        bool ownsDataSource)
    {
        _dataSource = dataSource ??
            throw new ArgumentNullException(nameof(dataSource));
        _probe = probe;
        _ownsDataSource = ownsDataSource;
    }

    public async Task<CharacterAccountSnapshot> ReadAsync(
        int accountId,
        CancellationToken cancellationToken = default)
    {
        if (accountId <= 0)
        {
            throw new CharacterSnapshotUnavailableException(
                CharacterSnapshotFailureReason.InvalidData,
                "A character snapshot requires a positive account ID.");
        }

        try
        {
            var read = await ReadTransactionAsync(accountId, cancellationToken);
            var snapshot = new CharacterAccountSnapshot(
                CharacterSnapshotContractVersions.Current,
                accountId,
                read.ProviderSnapshotToken,
                read.ReadAtUtc,
                CharacterSlotPolicy.SingleCharacterV1,
                read.Character);
            CharacterSnapshotContract.Validate(snapshot);
            return snapshot;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (CharacterSnapshotUnavailableException)
        {
            throw;
        }
        catch (Exception ex) when (
            ex is InvalidDataException or
                InvalidCastException or
                OverflowException or
                IndexOutOfRangeException)
        {
            throw new CharacterSnapshotUnavailableException(
                CharacterSnapshotFailureReason.InvalidData,
                "PostgreSQL returned an invalid character snapshot.",
                ex);
        }
        catch (Exception ex) when (
            ex is NpgsqlException or
                TimeoutException or
                IOException)
        {
            throw new CharacterSnapshotUnavailableException(
                CharacterSnapshotFailureReason.ProviderUnavailable,
                "PostgreSQL character snapshot loading is unavailable.",
                ex);
        }
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

    private async Task<TransactionReadResult> ReadTransactionAsync(
        int accountId,
        CancellationToken cancellationToken)
    {
        await using var connection =
            await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction =
            await connection.BeginTransactionAsync(
                IsolationLevel.RepeatableRead,
                cancellationToken);
        await SetReadOnlyAsync(connection, transaction, cancellationToken);

        var metadata = await ReadMetadataAsync(
            connection,
            transaction,
            accountId,
            cancellationToken);
        if (!metadata.AccountExists)
        {
            throw new CharacterSnapshotUnavailableException(
                CharacterSnapshotFailureReason.AccountNotFound,
                "The authenticated account no longer exists.");
        }

        var characters = await ReadCoreCharactersAsync(
            connection,
            transaction,
            accountId,
            cancellationToken);
        if (characters.Count > 1)
        {
            throw new CharacterSnapshotUnavailableException(
                CharacterSnapshotFailureReason.AmbiguousCharacterSlot,
                "SingleCharacterV1 found more than one character.");
        }

        CharacterLoadSnapshot? snapshot = null;
        if (characters.Count == 1)
        {
            if (_probe is not null)
            {
                await _probe.ReachedAsync(
                    PostgresCharacterSnapshotReadStage.CoreLoaded,
                    cancellationToken);
            }

            var core = characters[0];
            var related = await ReadRelatedAsync(
                connection,
                transaction,
                accountId,
                core.Identity.CharacterId,
                metadata.ReadAtUtc,
                cancellationToken);
            snapshot = core.ToSnapshot(related);
        }

        await transaction.CommitAsync(cancellationToken);
        return new TransactionReadResult(
            metadata.ProviderSnapshotToken,
            metadata.ReadAtUtc,
            snapshot);
    }

    private static async Task SetReadOnlyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SET TRANSACTION READ ONLY;",
            connection,
            transaction);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<SnapshotMetadata> ReadMetadataAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int accountId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT
                transaction_timestamp(),
                sha256(convert_to(
                    pg_current_snapshot()::text,
                    'UTF8')),
                EXISTS (
                    SELECT 1
                    FROM accounts
                    WHERE id = @accountId
                );
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("accountId", accountId);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidDataException(
                "Character snapshot metadata was not returned.");
        }

        var readAt = new DateTimeOffset(
            reader.GetDateTime(0).ToUniversalTime());
        var token = PostgresCharacterSnapshotToken.FromDigest(
            (byte[])reader[1]);
        if (token.Length == 0 ||
            token.Length >
            CharacterSnapshotLimits.ProviderSnapshotTokenLength)
        {
            throw new CharacterSnapshotUnavailableException(
                CharacterSnapshotFailureReason.BoundsExceeded,
                "PostgreSQL snapshot token is outside the contract bounds.");
        }

        return new SnapshotMetadata(
            readAt,
            token,
            reader.GetBoolean(2));
    }

    private sealed record SnapshotMetadata(
        DateTimeOffset ReadAtUtc,
        string ProviderSnapshotToken,
        bool AccountExists);

    private sealed record TransactionReadResult(
        string ProviderSnapshotToken,
        DateTimeOffset ReadAtUtc,
        CharacterLoadSnapshot? Character);
}
