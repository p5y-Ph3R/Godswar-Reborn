using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Warehouse;
using Godswar.Server.Infrastructure.Characters;
using Godswar.Server.Infrastructure.Inventory;
using Godswar.Server.Infrastructure.Messaging;
using Npgsql;

namespace Godswar.Server.Infrastructure.Warehouse;

internal sealed partial class PostgresWarehouseExpansionCommandExecutor :
    IWarehouseExpansionCommandExecutor
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly PostgresPlayerOwnershipGuard _ownershipGuard;
    private readonly WarehouseExpansionPolicySnapshot _policy;
    private readonly int _commandTimeoutSeconds;
    private readonly short _maximumOutboxAttempts;
    private readonly IPostgresWarehouseCommandProbe? _probe;

    public PostgresWarehouseExpansionCommandExecutor(
        NpgsqlDataSource dataSource,
        PostgresOutboxDispatcherOptions options,
        WarehouseExpansionPolicySnapshot policy,
        IPostgresWarehouseCommandProbe? probe = null)
    {
        _dataSource = dataSource ??
            throw new ArgumentNullException(nameof(dataSource));
        _ownershipGuard = new PostgresPlayerOwnershipGuard(_dataSource);
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _policy.Validate();
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _commandTimeoutSeconds = Math.Max(
            1,
            (int)Math.Ceiling(options.CommandTimeout.TotalSeconds));
        _maximumOutboxAttempts = checked((short)options.MaximumDeliveryAttempts);
        _probe = probe;
    }

    public async Task<WarehouseExpansionExecutionResult> ExecuteAsync(
        CommandEnvelope<WarehouseExpansionCommand> envelope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var started = Stopwatch.GetTimestamp();
        var outcome = "provider_unavailable";
        try
        {
            var validation = WarehouseExpansionCommandEnvelope.Validate(envelope);
            if (validation == CommandEnvelopeValidation.RequestHashConflict)
            {
                outcome = "request_hash_conflict";
                return WarehouseExpansionExecutionResult.Terminal(
                    WarehouseExpansionExecutionDisposition.RequestHashConflict);
            }
            if (validation != CommandEnvelopeValidation.Valid ||
                envelope.Command.PolicyRevision != _policy.Revision ||
                !string.Equals(
                    envelope.Command.PolicySha256,
                    _policy.Sha256,
                    StringComparison.Ordinal))
            {
                outcome = "invalid_intent";
                return WarehouseExpansionExecutionResult.Terminal(
                    WarehouseExpansionExecutionDisposition.InvalidIntent);
            }

            var result = await ExecuteTransactionAsync(
                envelope,
                cancellationToken);
            if (result.Receipt is not null)
            {
                (await _ownershipGuard.ValidateCurrentAsync(
                    envelope.Subject,
                    envelope.Ownership,
                    cancellationToken)).RequireCurrent();
            }
            outcome = result.Disposition.ToString().ToLowerInvariant();
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
                WarehouseExpansionPersistenceCodec.CommandFamilyCode,
                outcome,
                Stopwatch.GetElapsedTime(started));
        }
    }

    public async Task<WarehouseExpansionExecutionResult> TryReplayAsync(
        CommandSubject subject,
        PlayerOwnershipFence ownership,
        WarehouseExpansionReplayIntent intent,
        WarehouseOperationIdentity identity,
        CancellationToken cancellationToken = default)
    {
        if (subject.AccountId <= 0 || subject.CharacterId <= 0 ||
            intent.RealmId <= 0 ||
            intent.ActionSubId != WarehouseExpansionCommandEnvelope.ActionSubId ||
            (!identity.IsSecureClient && !identity.IsRawLocalServer))
        {
            return WarehouseExpansionExecutionResult.Terminal(
                WarehouseExpansionExecutionDisposition.InvalidIntent);
        }
        var operationId = DecodeDigest(
            CommandEnvelopeContract.DeriveOperationId(
                CommandFamily.WarehouseExpansion,
                subject,
                WarehouseCommandIdentityRules.CreateScope(identity)));
        var principalKey = subject.AccountId.ToString(
            CultureInfo.InvariantCulture);
        var aggregateKey =
            WarehouseExpansionPersistenceCodec.WarehouseAggregateKey(
                subject.CharacterId);
        await using var connection =
            await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction =
            await connection.BeginTransactionAsync(cancellationToken);
        var ownershipResult = await _ownershipGuard.LockCurrentAsync(
            connection,
            transaction,
            subject,
            ownership,
            cancellationToken);
        if (ownershipResult.Status ==
            PlayerOwnershipValidationStatus.CharacterNotFound)
        {
            await transaction.RollbackAsync(cancellationToken);
            return WarehouseExpansionExecutionResult.Terminal(
                WarehouseExpansionExecutionDisposition.PreconditionFailed);
        }
        ownershipResult.RequireCurrent();
        if (await LockCharacterAsync(
                connection,
                transaction,
                subject,
                intent.RealmId,
                cancellationToken) is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return WarehouseExpansionExecutionResult.Terminal(
                WarehouseExpansionExecutionDisposition.PreconditionFailed);
        }
        var stored = await ReadInboxAsync(
            connection,
            transaction,
            principalKey,
            aggregateKey,
            operationId,
            cancellationToken);
        if (stored is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return WarehouseExpansionExecutionResult.Terminal(
                WarehouseExpansionExecutionDisposition.ReplayNotFound);
        }
        var receipt = ValidateStored(stored, subject);
        if (receipt.RealmId != intent.RealmId ||
            receipt.ActionSubId != intent.ActionSubId)
        {
            await UpdateInboxEvidenceAsync(
                connection,
                transaction,
                stored.InboxId,
                duplicate: false,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return WarehouseExpansionExecutionResult.Terminal(
                WarehouseExpansionExecutionDisposition.RequestHashConflict);
        }
        await UpdateInboxEvidenceAsync(
            connection,
            transaction,
            stored.InboxId,
            duplicate: true,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        (await _ownershipGuard.ValidateCurrentAsync(
            subject,
            ownership,
            cancellationToken)).RequireCurrent();
        return WarehouseExpansionExecutionResult.Terminal(
            WarehouseExpansionExecutionDisposition.Duplicate,
            receipt);
    }

    private async Task<WarehouseExpansionExecutionResult>
        ExecuteTransactionAsync(
            CommandEnvelope<WarehouseExpansionCommand> envelope,
            CancellationToken cancellationToken)
    {
        var operationId = DecodeDigest(envelope.OperationId);
        var requestHash = DecodeDigest(envelope.RequestHash);
        var principalKey = envelope.Subject.AccountId.ToString(
            CultureInfo.InvariantCulture);
        var aggregateKey =
            WarehouseExpansionPersistenceCodec.WarehouseAggregateKey(
                envelope.Subject.CharacterId);
        await using var connection =
            await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction =
            await connection.BeginTransactionAsync(cancellationToken);
        var ownership = await _ownershipGuard.LockCurrentAsync(
            connection,
            transaction,
            envelope.Subject,
            envelope.Ownership,
            cancellationToken);
        if (ownership.Status ==
            PlayerOwnershipValidationStatus.CharacterNotFound)
        {
            await transaction.RollbackAsync(cancellationToken);
            return WarehouseExpansionExecutionResult.Terminal(
                WarehouseExpansionExecutionDisposition.PreconditionFailed);
        }
        ownership.RequireCurrent();
        var character = await LockCharacterAsync(
            connection,
            transaction,
            envelope.Subject,
            envelope.Command.RealmId,
            cancellationToken);
        if (character is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return WarehouseExpansionExecutionResult.Terminal(
                WarehouseExpansionExecutionDisposition.PreconditionFailed);
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
            var receipt = ValidateStored(existing, envelope.Subject);
            if (!CryptographicOperations.FixedTimeEquals(
                    existing.RequestHash,
                    requestHash) ||
                receipt.RealmId != envelope.Command.RealmId ||
                receipt.ActionSubId != envelope.Command.ActionSubId)
            {
                await UpdateInboxEvidenceAsync(
                    connection,
                    transaction,
                    existing.InboxId,
                    duplicate: false,
                    cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return WarehouseExpansionExecutionResult.Terminal(
                    WarehouseExpansionExecutionDisposition.RequestHashConflict);
            }
            await UpdateInboxEvidenceAsync(
                connection,
                transaction,
                existing.InboxId,
                duplicate: true,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return WarehouseExpansionExecutionResult.Terminal(
                WarehouseExpansionExecutionDisposition.Duplicate,
                receipt);
        }
        if (!await PostgresCharacterEconomyBaseline.EnsureAsync(
                connection,
                transaction,
                envelope.Subject.AccountId,
                envelope.Subject.CharacterId,
                _commandTimeoutSeconds,
                cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            return WarehouseExpansionExecutionResult.Terminal(
                WarehouseExpansionExecutionDisposition.PreconditionFailed);
        }

        var plan = await BuildPlanAsync(
            connection,
            transaction,
            envelope.Command,
            envelope.Subject.CharacterId,
            character,
            cancellationToken);
        var inventoryEventId = plan.Succeeded ? Guid.NewGuid() : (Guid?)null;
        var capacityEventId = plan.Succeeded ? Guid.NewGuid() : (Guid?)null;
        var evidence = await PersistEvidenceAsync(
            connection,
            transaction,
            envelope,
            principalKey,
            aggregateKey,
            operationId,
            requestHash,
            character,
            plan,
            inventoryEventId,
            cancellationToken);
        if (!plan.Succeeded)
        {
            await transaction.CommitAsync(cancellationToken);
            return WarehouseExpansionExecutionResult.Terminal(
                WarehouseExpansionExecutionDisposition.TerminalRejected,
                evidence.Receipt);
        }

        await ApplyKeyMutationsAsync(
            connection,
            transaction,
            envelope.Subject.CharacterId,
            plan,
            cancellationToken);
        await AdvanceRevisionsAsync(
            connection,
            transaction,
            envelope.Subject,
            character,
            plan,
            cancellationToken);
        await InsertExpansionLedgersAsync(
            connection,
            transaction,
            envelope.Subject,
            evidence.InboxId,
            plan.NextInventoryRevision,
            plan,
            cancellationToken);
        await InsertExpansionOutboxesAsync(
            connection,
            transaction,
            evidence.InboxId,
            envelope.Subject.CharacterId,
            plan,
            inventoryEventId!.Value,
            capacityEventId!.Value,
            evidence,
            cancellationToken);
        await InsertSettlementAsync(
            connection,
            transaction,
            envelope,
            evidence,
            plan,
            inventoryEventId.Value,
            capacityEventId.Value,
            cancellationToken);
        if (_probe is not null)
        {
            await _probe.ReachedAsync(
                PostgresWarehouseCommandStage.ExpansionBeforeCommit,
                cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return WarehouseExpansionExecutionResult.Terminal(
            WarehouseExpansionExecutionDisposition.Committed,
            evidence.Receipt);
    }

    private NpgsqlCommand CreateCommand(
        string sql,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction) =>
        new(sql, connection, transaction)
        {
            CommandTimeout = _commandTimeoutSeconds
        };

    private static byte[] DecodeDigest(string digest)
    {
        var value = Convert.FromHexString(digest);
        return value.Length == CommandEnvelopeContract.DigestBytes
            ? value
            : throw new InvalidDataException("Warehouse digest size is invalid.");
    }

    private sealed record LockedCharacter(
        int Capacity,
        long WarehouseRevision,
        long InventoryRevision);

    private sealed record LockedKeyItem(
        long ItemInstanceId,
        short Slot,
        short BeforeStack,
        string BeforeState,
        int AfterStack);

    private sealed record ExpansionPlan(
        WarehouseExpansionResultStatus Status,
        int PreviousCapacity,
        int CurrentCapacity,
        int KeyItemId,
        int RequiredKeys,
        int ConsumedKeys,
        long NextWarehouseRevision,
        long NextInventoryRevision,
        IReadOnlyList<LockedKeyItem> KeyItems,
        IReadOnlyList<WarehouseItemMutation> Mutations)
    {
        public bool Succeeded => Status == WarehouseExpansionResultStatus.Expanded;
    }

    private sealed record StoredInbox(
        long InboxId,
        byte[] RequestHash,
        short ContractVersion,
        string ResultCode,
        string ResultPayload,
        byte[] ResultHash,
        long AuditId);

    private sealed record PersistedEvidence(
        long InboxId,
        long AuditId,
        byte[] Payload,
        WarehouseExpansionExecutionReceipt Receipt);
}
