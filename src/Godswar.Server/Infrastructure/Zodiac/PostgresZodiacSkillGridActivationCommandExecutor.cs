using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Zodiac;
using Godswar.Server.Infrastructure.Inventory;
using Godswar.Server.Infrastructure.Messaging;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.Infrastructure.Zodiac;

internal sealed partial class
    PostgresZodiacSkillGridActivationCommandExecutor :
    IZodiacSkillGridActivationCommandExecutor
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly int _commandTimeoutSeconds;
    private readonly short _maximumOutboxAttempts;
    private readonly IPostgresZodiacSkillGridActivationCommandProbe? _probe;

    public PostgresZodiacSkillGridActivationCommandExecutor(
        NpgsqlDataSource dataSource,
        PostgresOutboxDispatcherOptions options,
        IPostgresZodiacSkillGridActivationCommandProbe? probe = null)
    {
        _dataSource = dataSource ??
            throw new ArgumentNullException(nameof(dataSource));
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _commandTimeoutSeconds = Math.Max(
            1,
            (int)Math.Ceiling(options.CommandTimeout.TotalSeconds));
        _maximumOutboxAttempts =
            checked((short)options.MaximumDeliveryAttempts);
        _probe = probe;
    }

    public async Task<ZodiacSkillGridActivationExecutionResult>
        ExecuteAsync(
            CommandEnvelope<ZodiacSkillGridActivationCommand> envelope,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var started = Stopwatch.GetTimestamp();
        var outcome = "provider_unavailable";
        try
        {
            var validation =
                ZodiacSkillGridActivationCommandEnvelope.Validate(envelope);
            if (validation ==
                CommandEnvelopeValidation.RequestHashConflict)
            {
                outcome = "request_hash_conflict";
                return ZodiacSkillGridActivationExecutionResult
                    .RequestHashConflict();
            }

            if (validation != CommandEnvelopeValidation.Valid)
            {
                outcome = "invalid_intent";
                return ZodiacSkillGridActivationExecutionResult
                    .InvalidIntent();
            }

            var result = await ExecuteTransactionAsync(
                envelope,
                cancellationToken);
            outcome = result.Disposition switch
            {
                ZodiacSkillGridActivationExecutionDisposition.Committed =>
                    "committed",
                ZodiacSkillGridActivationExecutionDisposition.Duplicate =>
                    "duplicate",
                ZodiacSkillGridActivationExecutionDisposition
                    .RequestHashConflict => "request_hash_conflict",
                ZodiacSkillGridActivationExecutionDisposition
                    .InvalidIntent => "invalid_intent",
                ZodiacSkillGridActivationExecutionDisposition
                    .PreconditionFailed => "precondition_failed",
                _ => throw new InvalidOperationException(
                    "Unknown Zodiac activation outcome.")
            };
            return result;
        }
        catch (OperationCanceledException)
        {
            outcome = "cancelled";
            throw;
        }
        finally
        {
            PostgresCommandMetrics.RecordInbox(
                ZodiacSkillGridActivationPersistenceCodec.CommandFamily,
                outcome,
                Stopwatch.GetElapsedTime(started));
        }
    }

    private async Task<ZodiacSkillGridActivationExecutionResult>
        ExecuteTransactionAsync(
            CommandEnvelope<ZodiacSkillGridActivationCommand> envelope,
            CancellationToken cancellationToken)
    {
        var operationId = DecodeDigest(envelope.OperationId);
        var requestHash = DecodeDigest(envelope.RequestHash);
        var principalKey = envelope.Subject.AccountId.ToString(
            CultureInfo.InvariantCulture);
        var aggregateKey =
            ZodiacSkillGridActivationPersistenceCodec.AggregateKey(
                envelope.Subject.CharacterId,
                envelope.Command.GridIndex);

        await using var connection =
            await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction =
            await connection.BeginTransactionAsync(cancellationToken);

        var character = await LockCharacterAsync(
            connection,
            transaction,
            envelope,
            cancellationToken);
        if (character is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return ZodiacSkillGridActivationExecutionResult
                .PreconditionFailed();
        }

        var existing = await ReadInboxAsync(
            connection,
            transaction,
            principalKey,
            aggregateKey,
            operationId,
            cancellationToken);
        if (existing is not null)
        {
            return await ReplayAsync(
                connection,
                transaction,
                envelope,
                character.Value,
                existing,
                requestHash,
                cancellationToken);
        }

        if (!await PostgresCharacterEconomyBaseline.EnsureAsync(
                connection,
                transaction,
                envelope.Subject.AccountId,
                envelope.Subject.CharacterId,
                _commandTimeoutSeconds,
                cancellationToken))
        {
            throw new InvalidDataException(
                "The Zodiac activation economy baseline is missing.");
        }

        var grid = await ReadGridAsync(
            connection,
            transaction,
            envelope.Subject.CharacterId,
            envelope.Command.GridIndex,
            cancellationToken);
        var domainResult = DeriveActivation(
            character.Value,
            grid,
            envelope.Command.GridIndex);
        if (!domainResult.Committed)
        {
            await transaction.RollbackAsync(cancellationToken);
            return ZodiacSkillGridActivationExecutionResult
                .PreconditionFailed(
                    domainResult.CurrentGold,
                    domainResult.CurrentLevel,
                    domainResult.SelectedSkillId,
                    character.Value.WalletRevision);
        }

        return await CommitActivationAsync(
            connection,
            transaction,
            envelope,
            character.Value,
            domainResult,
            principalKey,
            aggregateKey,
            operationId,
            requestHash,
            cancellationToken);
    }

    private async Task<ZodiacSkillGridActivationExecutionResult>
        ReplayAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            CommandEnvelope<ZodiacSkillGridActivationCommand> envelope,
            LockedCharacter character,
            StoredInbox existing,
            byte[] requestHash,
            CancellationToken cancellationToken)
    {
        if (!CryptographicOperations.FixedTimeEquals(
                existing.RequestHash,
                requestHash))
        {
            await RecordRequestConflictAsync(
                connection,
                transaction,
                existing.InboxId,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return ZodiacSkillGridActivationExecutionResult
                .RequestHashConflict();
        }

        var receipt = ValidateStoredResult(
            existing,
            envelope.Subject.CharacterId,
            envelope.Command.GridIndex);
        var grid = await ReadGridAsync(
            connection,
            transaction,
            envelope.Subject.CharacterId,
            envelope.Command.GridIndex,
            cancellationToken);
        var result = ZodiacSkillGridActivationExecutionResult.Duplicate(
            receipt,
            character.Gold,
            grid.Level,
            grid.SelectedSkillId,
            character.WalletRevision);
        await RecordDuplicateAsync(
            connection,
            transaction,
            existing.InboxId,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    private async Task<ZodiacSkillGridActivationExecutionResult>
        CommitActivationAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            CommandEnvelope<ZodiacSkillGridActivationCommand> envelope,
            LockedCharacter character,
            ZodiacSkillGridActivationResult activation,
            string principalKey,
            string aggregateKey,
            byte[] operationId,
            byte[] requestHash,
            CancellationToken cancellationToken)
    {
        var paid = activation.GoldCost > 0;
        var walletRevision = paid
            ? checked(character.WalletRevision + 1)
            : character.WalletRevision;
        var eventId = Guid.NewGuid();
        var auditId = await InsertAuditAsync(
            connection,
            transaction,
            principalKey,
            aggregateKey,
            operationId,
            requestHash,
            cancellationToken);
        var receipt = new ZodiacSkillGridActivationExecutionReceipt(
            envelope.Subject.CharacterId,
            envelope.Command.GridIndex,
            activation.GoldCost,
            character.Gold,
            activation.CurrentGold,
            activation.CurrentLevel,
            activation.SelectedSkillId,
            walletRevision,
            auditId.ToString(CultureInfo.InvariantCulture),
            eventId);
        var payload =
            ZodiacSkillGridActivationPersistenceCodec.Encode(receipt);
        var resultHash =
            ZodiacSkillGridActivationPersistenceCodec.Hash(payload);

        await ReachAsync(
            PostgresZodiacSkillGridActivationCommandStage.AuditInserted,
            cancellationToken);
        var inboxId = await InsertInboxAsync(
            connection,
            transaction,
            principalKey,
            aggregateKey,
            operationId,
            requestHash,
            resultHash,
            auditId,
            payload,
            cancellationToken);
        await ReachAsync(
            PostgresZodiacSkillGridActivationCommandStage.InboxInserted,
            cancellationToken);

        await ApplyGridMutationAsync(
            connection,
            transaction,
            envelope.Subject.CharacterId,
            envelope.Command.GridIndex,
            activation.CurrentLevel,
            activation.SelectedSkillId,
            cancellationToken);
        await ReachAsync(
            PostgresZodiacSkillGridActivationCommandStage.GridMutated,
            cancellationToken);

        if (paid)
        {
            await UpdateGoldWalletAsync(
                connection,
                transaction,
                envelope,
                character,
                activation.CurrentGold,
                walletRevision,
                cancellationToken);
            await ReachAsync(
                PostgresZodiacSkillGridActivationCommandStage.WalletUpdated,
                cancellationToken);
            await InsertGoldLedgerAsync(
                connection,
                transaction,
                inboxId,
                envelope,
                character,
                activation.CurrentGold,
                walletRevision,
                cancellationToken);
            await ReachAsync(
                PostgresZodiacSkillGridActivationCommandStage
                    .CurrencyLedgerInserted,
                cancellationToken);
        }

        await InsertOutboxAsync(
            connection,
            transaction,
            inboxId,
            aggregateKey,
            eventId,
            payload,
            cancellationToken);
        await ReachAsync(
            PostgresZodiacSkillGridActivationCommandStage.OutboxInserted,
            cancellationToken);
        await ReachAsync(
            PostgresZodiacSkillGridActivationCommandStage.BeforeCommit,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await ReachAsync(
            PostgresZodiacSkillGridActivationCommandStage.AfterCommit,
            cancellationToken);
        return ZodiacSkillGridActivationExecutionResult.Committed(receipt);
    }

    private static ZodiacSkillGridActivationResult DeriveActivation(
        LockedCharacter locked,
        StoredGrid grid,
        int gridIndex)
    {
        var character = new GameCharacter
        {
            Gold = locked.Gold,
            ZodiacSkillGridLevels =
                ZodiacSkillGridCatalog.CreateEmptyLevels(),
            ZodiacSkillGridSkillIds =
                ZodiacSkillGridCatalog.CreateEmptySkillIds()
        };
        character.ZodiacSkillGridLevels[gridIndex] = grid.Level;
        character.ZodiacSkillGridSkillIds[gridIndex] =
            grid.SelectedSkillId;
        return ZodiacSkillGridActivation.Apply(character, gridIndex);
    }

    private async ValueTask ReachAsync(
        PostgresZodiacSkillGridActivationCommandStage stage,
        CancellationToken cancellationToken)
    {
        if (_probe is not null)
        {
            await _probe.ReachedAsync(stage, cancellationToken);
        }
    }

    private static byte[] DecodeDigest(string value)
    {
        var bytes = Convert.FromHexString(value);
        if (bytes.Length != CommandEnvelopeContract.DigestBytes)
        {
            throw new InvalidDataException(
                "The command digest has an invalid size.");
        }

        return bytes;
    }

    private NpgsqlCommand CreateCommand(
        string sql,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction) =>
        new(sql, connection, transaction)
        {
            CommandTimeout = _commandTimeoutSeconds
        };

    private readonly record struct LockedCharacter(
        int Gold,
        long WalletRevision);

    private readonly record struct StoredGrid(
        byte Level,
        int SelectedSkillId);

    private sealed record StoredInbox(
        long InboxId,
        byte[] RequestHash,
        short ResultContractVersion,
        string ResultCode,
        string ResultPayload,
        byte[] ResultHash,
        long AuditId);
}
