using System.Diagnostics;
using System.Security.Cryptography;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Pets;
using Godswar.Server.Infrastructure.Inventory;
using Godswar.Server.Infrastructure.Messaging;
using Npgsql;

namespace Godswar.Server.Infrastructure.Pets;

internal sealed partial class PostgresPetDurableCommandExecutor :
    IPetDurableCommandExecutor
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly int _commandTimeoutSeconds;
    private readonly short _maximumOutboxAttempts;

    public PostgresPetDurableCommandExecutor(
        NpgsqlDataSource dataSource,
        PostgresOutboxDispatcherOptions options)
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
    }

    public Task<PetDurableExecutionResult> ExecuteAsync(
        CommandEnvelope<BagItemActivationCommand> envelope,
        CancellationToken cancellationToken = default) =>
        ExecuteCoreAsync(
            envelope,
            BagItemActivationCommandEnvelope.Validate(envelope),
            ExecuteBagItemActivationAsync,
            cancellationToken);

    public Task<PetDurableExecutionResult> ExecuteAsync(
        CommandEnvelope<PetLevelUpgradeCommand> envelope,
        CancellationToken cancellationToken = default) =>
        ExecuteCoreAsync(
            envelope,
            PetLevelUpgradeCommandEnvelope.Validate(envelope),
            ExecutePetLevelUpgradeAsync,
            cancellationToken);

    public Task<PetDurableExecutionResult> ExecuteAsync(
        CommandEnvelope<PetPresenceTransitionCommand> envelope,
        CancellationToken cancellationToken = default) =>
        ExecuteCoreAsync(
            envelope,
            PetPresenceTransitionCommandEnvelope.Validate(envelope),
            ExecutePetPresenceAsync,
            cancellationToken);

    private async Task<PetDurableExecutionResult> ExecuteCoreAsync<T>(
        CommandEnvelope<T> envelope,
        CommandEnvelopeValidation validation,
        Func<
            NpgsqlConnection,
            NpgsqlTransaction,
            CommandEnvelope<T>,
            LockedCharacter,
            CancellationToken,
            Task<PetTransition>> transition,
        CancellationToken cancellationToken)
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
                return PetDurableExecutionResult.NonDurable(
                    PetDurableExecutionDisposition
                        .RequestHashConflict);
            }
            if (validation != CommandEnvelopeValidation.Valid)
            {
                outcome = "invalid_intent";
                return PetDurableExecutionResult.NonDurable(
                    PetDurableExecutionDisposition.InvalidIntent);
            }

            var result = await ExecuteTransactionAsync(
                envelope,
                transition,
                cancellationToken);
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
                PetDurablePersistenceCodec.FamilyCode(
                    envelope.Family),
                outcome,
                Stopwatch.GetElapsedTime(started));
        }
    }

    private async Task<PetDurableExecutionResult>
        ExecuteTransactionAsync<T>(
            CommandEnvelope<T> envelope,
            Func<
                NpgsqlConnection,
                NpgsqlTransaction,
                CommandEnvelope<T>,
                LockedCharacter,
                CancellationToken,
                Task<PetTransition>> transition,
            CancellationToken cancellationToken)
    {
        var operationId = DecodeDigest(envelope.OperationId);
        var requestHash = DecodeDigest(envelope.RequestHash);
        await using var connection =
            await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction =
            await connection.BeginTransactionAsync(cancellationToken);
        var character = await LockCharacterAsync(
            connection,
            transaction,
            envelope.Subject,
            cancellationToken);
        if (character is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return PetDurableExecutionResult.NonDurable(
                PetDurableExecutionDisposition.CharacterNotFound);
        }

        var stored = await ReadInboxAsync(
            connection,
            transaction,
            envelope.Subject,
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

        if (envelope.Family == CommandFamily.BagItemActivation &&
            !await PostgresCharacterEconomyBaseline.EnsureAsync(
                connection,
                transaction,
                envelope.Subject.AccountId,
                envelope.Subject.CharacterId,
                _commandTimeoutSeconds,
                cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            return PetDurableExecutionResult.NonDurable(
                PetDurableExecutionDisposition.CharacterNotFound);
        }

        var mutation = await transition(
            connection,
            transaction,
            envelope,
            character.Value,
            cancellationToken);
        return await PersistTransitionAsync(
            connection,
            transaction,
            envelope,
            mutation,
            operationId,
            requestHash,
            cancellationToken);
    }

    private async Task<PetDurableExecutionResult> ReplayAsync<T>(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CommandEnvelope<T> envelope,
        StoredInbox stored,
        byte[] requestHash,
        CancellationToken cancellationToken)
    {
        if (!CryptographicOperations.FixedTimeEquals(
                stored.RequestHash,
                requestHash))
        {
            await RecordConflictAsync(
                connection,
                transaction,
                stored.InboxId,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return PetDurableExecutionResult.NonDurable(
                PetDurableExecutionDisposition.RequestHashConflict);
        }

        var receipt = ValidateStoredReceipt(stored, envelope);
        await RecordDuplicateAsync(
            connection,
            transaction,
            stored.InboxId,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return PetDurableExecutionResult.Duplicate(receipt);
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
        var bytes = Convert.FromHexString(digest);
        return bytes.Length == CommandEnvelopeContract.DigestBytes
            ? bytes
            : throw new InvalidDataException(
                "The pet command digest has an invalid size.");
    }

    private readonly record struct LockedCharacter(
        int Profession,
        int Level,
        long InventoryRevision);

    private sealed record StoredInbox(
        long InboxId,
        byte[] RequestHash,
        short ContractVersion,
        string ResultCode,
        string ResultPayload,
        byte[] ResultHash,
        long AuditId);

    private sealed record PetTransition(
        PetDurableReceiptStatus Status,
        int KitBagSlot = -1,
        int EquipmentSlot = -1,
        long PetId = 0,
        short PetLevel = 0,
        long PetExperience = 0,
        long PetRevision = 0,
        bool IsCarried = false,
        bool IsSummoned = false,
        byte PresenceOperation = 0,
        IReadOnlyList<InventoryMutation>? InventoryMutations = null)
    {
        public bool Succeeded =>
            Status is PetDurableReceiptStatus.EggHatched or
                PetDurableReceiptStatus.EquipmentEquipped or
                PetDurableReceiptStatus.PetLevelUpgraded or
                PetDurableReceiptStatus.PresenceChanged;
    }

    private sealed record InventoryMutation(
        long ItemInstanceId,
        string MutationKind,
        string BeforeState,
        string? AfterState,
        string ReasonCode,
        long InventoryRevision);
}
