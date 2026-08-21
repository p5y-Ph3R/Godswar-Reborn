using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Commands;
using Godswar.Server.Infrastructure.Messaging;
using Godswar.Server.Infrastructure.Database;
using Godswar.Server.Domain.World.Instances;
using Npgsql;

namespace Godswar.Server.Infrastructure.Characters;

internal sealed partial class PostgresCharacterLifecycleCommandExecutor :
    ICharacterLifecycleCommandExecutor
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly int _commandTimeoutSeconds;
    private readonly short _maximumOutboxAttempts;
    private readonly string? _gameplayContentRevision;

    public PostgresCharacterLifecycleCommandExecutor(
        NpgsqlDataSource dataSource,
        PostgresOutboxDispatcherOptions options,
        string? gameplayContentRevision = null)
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
        _gameplayContentRevision =
            PostgresGameplayContentBinding.ValidateOptional(
                gameplayContentRevision);
    }

    public Task<CharacterLifecycleExecutionResult> ExecuteAsync(
        CommandEnvelope<CharacterCreateCommand> envelope,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            envelope,
            CharacterCreateCommandEnvelope.Validate(envelope),
            ExecuteCreateTransitionAsync,
            cancellationToken);

    public Task<CharacterLifecycleExecutionResult> ExecuteAsync(
        CommandEnvelope<CharacterDeleteCommand> envelope,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            envelope,
            CharacterDeleteCommandEnvelope.Validate(envelope),
            ExecuteDeleteTransitionAsync,
            cancellationToken);

    public Task<CharacterLifecycleExecutionResult> ExecuteAsync(
        CommandEnvelope<CharacterRestoreCommand> envelope,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            envelope,
            CharacterRestoreCommandEnvelope.Validate(envelope),
            ExecuteRestoreTransitionAsync,
            cancellationToken);

    public Task<CharacterLifecycleExecutionResult> ExecuteAsync(
        CommandEnvelope<CharacterPurgeCommand> envelope,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            envelope,
            CharacterPurgeCommandEnvelope.Validate(envelope),
            ExecutePurgeTransitionAsync,
            cancellationToken);

    private async Task<CharacterLifecycleExecutionResult> ExecuteAsync<T>(
        CommandEnvelope<T> envelope,
        CommandEnvelopeValidation validation,
        Func<
            NpgsqlConnection,
            NpgsqlTransaction,
            CommandEnvelope<T>,
            LockedAccount,
            CancellationToken,
            Task<LifecycleTransition>> transition,
        CancellationToken cancellationToken)
        where T : IRealmScopedCharacterLifecycleCommand
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var started = Stopwatch.GetTimestamp();
        var outcome = "provider_unavailable";
        try
        {
            if (validation ==
                CommandEnvelopeValidation.RequestHashConflict)
            {
                outcome = "request_hash_conflict";
                return CharacterLifecycleExecutionResult
                    .RequestHashConflict();
            }
            if (validation != CommandEnvelopeValidation.Valid)
            {
                outcome = "invalid_intent";
                return CharacterLifecycleExecutionResult.InvalidIntent();
            }

            var operationId = DecodeDigest(envelope.OperationId);
            var requestHash = DecodeDigest(envelope.RequestHash);
            var result = await ExecuteTransactionAsync(
                envelope,
                operationId,
                requestHash,
                transition,
                cancellationToken);
            outcome = OutcomeCode(result.Disposition);
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
                CharacterLifecyclePersistenceCodec.FamilyCode(
                    envelope.Family),
                outcome,
                Stopwatch.GetElapsedTime(started));
        }
    }

    private async Task<CharacterLifecycleExecutionResult>
        ExecuteTransactionAsync<T>(
            CommandEnvelope<T> envelope,
            byte[] operationId,
            byte[] requestHash,
            Func<
                NpgsqlConnection,
                NpgsqlTransaction,
                CommandEnvelope<T>,
                LockedAccount,
                CancellationToken,
                Task<LifecycleTransition>> transition,
            CancellationToken cancellationToken)
        where T : IRealmScopedCharacterLifecycleCommand
    {
        await using var connection =
            await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction =
            await connection.BeginTransactionAsync(cancellationToken);
        var account = await LockAccountAsync(
            connection,
            transaction,
            envelope.Subject.AccountId,
            envelope.Command.RealmId,
            cancellationToken);
        if (account is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return CharacterLifecycleExecutionResult.AccountNotFound();
        }

        var stored = await ReadInboxAsync(
            connection,
            transaction,
            envelope.Subject.AccountId,
            envelope.Command.RealmId,
            envelope.Family,
            operationId,
            cancellationToken);
        if (stored is not null)
        {
            return await ReplayAsync(
                connection,
                transaction,
                envelope,
                stored,
                requestHash,
                cancellationToken);
        }

        var lifecycleTransition = await transition(
            connection,
            transaction,
            envelope,
            account.Value,
            cancellationToken);
        return await PersistTransitionAsync(
            connection,
            transaction,
            envelope,
            lifecycleTransition,
            operationId,
            requestHash,
            cancellationToken);
    }

    private async Task<CharacterLifecycleExecutionResult> ReplayAsync<T>(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CommandEnvelope<T> envelope,
        StoredInbox stored,
        byte[] requestHash,
        CancellationToken cancellationToken)
        where T : IRealmScopedCharacterLifecycleCommand
    {
        if (!CryptographicOperations.FixedTimeEquals(
                stored.RequestHash,
                requestHash))
        {
            await RecordRequestConflictAsync(
                connection,
                transaction,
                stored.InboxId,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return CharacterLifecycleExecutionResult
                .RequestHashConflict();
        }

        var receipt = ValidateStoredReceipt(
            stored,
            envelope.Family,
            envelope.Subject.AccountId,
            envelope.Command.RealmId);
        await RecordDuplicateAsync(
            connection,
            transaction,
            stored.InboxId,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return CharacterLifecycleExecutionResult.Duplicate(receipt);
    }

    private NpgsqlCommand CreateCommand(
        string sql,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction) =>
        new(sql, connection, transaction)
        {
            CommandTimeout = _commandTimeoutSeconds
        };

    private static byte[] DecodeDigest(string value)
    {
        var bytes = Convert.FromHexString(value);
        return bytes.Length == CommandEnvelopeContract.DigestBytes
            ? bytes
            : throw new InvalidDataException(
                "The lifecycle command digest has an invalid size.");
    }

    private static string OutcomeCode(
        CharacterLifecycleExecutionDisposition disposition) =>
        disposition switch
        {
            CharacterLifecycleExecutionDisposition.Committed =>
                "committed",
            CharacterLifecycleExecutionDisposition.Duplicate =>
                "duplicate",
            CharacterLifecycleExecutionDisposition.TerminalRejected =>
                "terminal_rejected",
            CharacterLifecycleExecutionDisposition.RequestHashConflict =>
                "request_hash_conflict",
            CharacterLifecycleExecutionDisposition.InvalidIntent =>
                "invalid_intent",
            CharacterLifecycleExecutionDisposition.AccountNotFound =>
                "account_not_found",
            _ => throw new ArgumentOutOfRangeException(
                nameof(disposition))
        };

    private readonly record struct LockedAccount(long LifecycleVersion);

    private sealed record StoredInbox(
        long InboxId,
        byte[] RequestHash,
        short ResultContractVersion,
        string ResultCode,
        string ResultPayload,
        byte[] ResultHash,
        long AuditId);

    private sealed record LifecycleTransition(
        CharacterLifecycleReceiptStatus Status,
        int CharacterId,
        long LifecycleVersion,
        string CharacterName,
        DateTimeOffset? RestoreUntil,
        DateTimeOffset? PurgeAfter)
    {
        public bool Succeeded => Status is
            CharacterLifecycleReceiptStatus.Created or
            CharacterLifecycleReceiptStatus.Deleted or
            CharacterLifecycleReceiptStatus.Restored or
            CharacterLifecycleReceiptStatus.Purged;
    }
}
