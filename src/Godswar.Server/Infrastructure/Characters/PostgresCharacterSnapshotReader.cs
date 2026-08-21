using System.Data;
using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Inventory;
using Godswar.Server.Application.Items;
using Godswar.Server.Application.Pets;
using Godswar.Server.Infrastructure.Database;
using Godswar.Server.Domain.World.Instances;
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
    ICharacterRuntimeProjectionReader,
    IOwnedPetSnapshotReader,
    ISealedPetSnapshotReader,
    IAsyncDisposable
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly IPostgresCharacterSnapshotReadProbe? _probe;
    private readonly bool _ownsDataSource;
    private readonly string _itemContentRevision;
    private readonly string? _gameplayContentRevision;
    private readonly string? _petLearnedSkillRevision;
    private readonly HolySpiritBalanceSnapshot _holySpiritBalance;

    public PostgresCharacterSnapshotReader(
        string connectionString,
        IItemTemplateCatalog itemTemplates)
        : this(
            CreateDataSource(connectionString),
            itemTemplates,
            probe: null,
            gameplayContentRevision: null,
            petLearnedSkillRevision: null,
            holySpiritBalance: null,
            ownsDataSource: true)
    {
    }

    internal PostgresCharacterSnapshotReader(
        string connectionString,
        IItemTemplateCatalog itemTemplates,
        IPostgresCharacterSnapshotReadProbe? probe)
        : this(
            CreateDataSource(connectionString),
            itemTemplates,
            probe,
            gameplayContentRevision: null,
            petLearnedSkillRevision: null,
            holySpiritBalance: null,
            ownsDataSource: true)
    {
    }

    internal PostgresCharacterSnapshotReader(
        NpgsqlDataSource dataSource,
        IItemTemplateCatalog itemTemplates,
        IPostgresCharacterSnapshotReadProbe? probe = null)
        : this(
            dataSource,
            itemTemplates,
            probe,
            gameplayContentRevision: null,
            petLearnedSkillRevision: null,
            holySpiritBalance: null,
            ownsDataSource: false)
    {
    }

    internal PostgresCharacterSnapshotReader(
        NpgsqlDataSource dataSource,
        IItemTemplateCatalog itemTemplates,
        string gameplayContentRevision)
        : this(
            dataSource,
            itemTemplates,
            probe: null,
            gameplayContentRevision: gameplayContentRevision,
            petLearnedSkillRevision: null,
            holySpiritBalance: null,
            ownsDataSource: false)
    {
    }

    internal PostgresCharacterSnapshotReader(
        NpgsqlDataSource dataSource,
        IItemTemplateCatalog itemTemplates,
        string gameplayContentRevision,
        string petLearnedSkillRevision,
        HolySpiritBalanceSnapshot? holySpiritBalance = null)
        : this(
            dataSource,
            itemTemplates,
            probe: null,
            gameplayContentRevision,
            petLearnedSkillRevision,
            holySpiritBalance,
            ownsDataSource: false)
    {
    }

    private PostgresCharacterSnapshotReader(
        NpgsqlDataSource dataSource,
        IItemTemplateCatalog itemTemplates,
        IPostgresCharacterSnapshotReadProbe? probe,
        string? gameplayContentRevision,
        string? petLearnedSkillRevision,
        HolySpiritBalanceSnapshot? holySpiritBalance,
        bool ownsDataSource)
    {
        _dataSource = dataSource ??
            throw new ArgumentNullException(nameof(dataSource));
        ArgumentNullException.ThrowIfNull(itemTemplates);
        _itemContentRevision = itemTemplates.Revision.Sha256;
        _gameplayContentRevision =
            PostgresGameplayContentBinding.ValidateOptional(
                gameplayContentRevision);
        _petLearnedSkillRevision =
            PostgresPetLearnedSkillContentBinding.ValidateOptional(
                petLearnedSkillRevision);
        _holySpiritBalance = holySpiritBalance ??
            HolySpiritBalanceSnapshot.HistoricalAcceptanceEnvelope;
        _holySpiritBalance.Validate();
        _probe = probe;
        _ownsDataSource = ownsDataSource;
    }

    public async Task<CharacterAccountSnapshot> ReadAsync(
        int accountId,
        CancellationToken cancellationToken = default) =>
        await ReadAsync(
            accountId,
            RealmId.Tempest,
            cancellationToken);

    public async Task<CharacterAccountSnapshot> ReadAsync(
        int accountId,
        RealmId realmId,
        CancellationToken cancellationToken = default)
    {
        if (accountId <= 0 || !realmId.IsValid)
        {
            throw new CharacterSnapshotUnavailableException(
                CharacterSnapshotFailureReason.InvalidData,
                "A character snapshot requires positive account and realm IDs.");
        }

        try
        {
            var read = await ReadTransactionAsync(
                accountId,
                realmId,
                cancellationToken);
            var snapshot = new CharacterAccountSnapshot(
                CharacterSnapshotContractVersions.Current,
                accountId,
                read.ProviderSnapshotToken,
                read.ReadAtUtc,
                CharacterSlotPolicy.SingleCharacterV1,
                read.Character)
            {
                RealmId = realmId
            };
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
        RealmId realmId,
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
            realmId,
            cancellationToken);
        if (!metadata.AccountExists)
        {
            throw new CharacterSnapshotUnavailableException(
                CharacterSnapshotFailureReason.AccountNotFound,
                "The authenticated account no longer exists.");
        }
        if (!metadata.RealmExists)
        {
            throw new CharacterSnapshotUnavailableException(
                CharacterSnapshotFailureReason.InvalidData,
                "The requested character realm does not exist.");
        }

        var characters = await ReadCoreCharactersAsync(
            connection,
            transaction,
            accountId,
            realmId,
            _itemContentRevision,
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
                _itemContentRevision,
                _gameplayContentRevision,
                _petLearnedSkillRevision,
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
        RealmId realmId,
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
                ),
                EXISTS (
                    SELECT 1
                    FROM server
                    WHERE id = @realmId
                );
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("accountId", accountId);
        command.Parameters.AddWithValue("realmId", realmId.Value);
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
            reader.GetBoolean(2),
            reader.GetBoolean(3));
    }

    private sealed record SnapshotMetadata(
        DateTimeOffset ReadAtUtc,
        string ProviderSnapshotToken,
        bool AccountExists,
        bool RealmExists);

    private sealed record TransactionReadResult(
        string ProviderSnapshotToken,
        DateTimeOffset ReadAtUtc,
        CharacterLoadSnapshot? Character);
}
