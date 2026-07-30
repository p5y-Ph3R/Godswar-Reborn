using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Zodiac;
using Godswar.Server.Infrastructure.Messaging;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.Infrastructure.Zodiac;

internal sealed partial class
    PostgresZodiacSkillGridUpgradeCommandExecutor :
    IZodiacSkillGridUpgradeCommandExecutor
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly int _commandTimeoutSeconds;
    private readonly short _maximumOutboxAttempts;
    private readonly IPostgresZodiacSkillGridUpgradeCommandProbe? _probe;

    public PostgresZodiacSkillGridUpgradeCommandExecutor(
        NpgsqlDataSource dataSource,
        PostgresOutboxDispatcherOptions options,
        IPostgresZodiacSkillGridUpgradeCommandProbe? probe = null)
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

    public async Task<ZodiacSkillGridUpgradeExecutionResult>
        ExecuteAsync(
            CommandEnvelope<ZodiacSkillGridUpgradeCommand> envelope,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var started = Stopwatch.GetTimestamp();
        var outcome = "provider_unavailable";
        try
        {
            var validation =
                ZodiacSkillGridUpgradeCommandEnvelope.Validate(envelope);
            if (validation ==
                CommandEnvelopeValidation.RequestHashConflict)
            {
                outcome = "request_hash_conflict";
                return ZodiacSkillGridUpgradeExecutionResult
                    .RequestHashConflict();
            }
            if (validation != CommandEnvelopeValidation.Valid)
            {
                outcome = "invalid_intent";
                return ZodiacSkillGridUpgradeExecutionResult
                    .InvalidIntent();
            }

            var result = await ExecuteTransactionAsync(
                envelope,
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
                ZodiacSkillGridUpgradePersistenceCodec.CommandFamily,
                outcome,
                Stopwatch.GetElapsedTime(started));
        }
    }

    private async Task<ZodiacSkillGridUpgradeExecutionResult>
        ExecuteTransactionAsync(
            CommandEnvelope<ZodiacSkillGridUpgradeCommand> envelope,
            CancellationToken cancellationToken)
    {
        var operationId = DecodeDigest(envelope.OperationId);
        var requestHash = DecodeDigest(envelope.RequestHash);
        var principalKey = envelope.Subject.AccountId.ToString(
            CultureInfo.InvariantCulture);
        var commandAggregateKey =
            ZodiacSkillGridUpgradePersistenceCodec.CommandAggregateKey(
                envelope.Subject.CharacterId);

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
            return ZodiacSkillGridUpgradeExecutionResult
                .PreconditionFailed();
        }

        // Command identity is checked before reading mutable grid state. This
        // preserves the original terminal result if a later retry would pass.
        var existing = await ReadInboxAsync(
            connection,
            transaction,
            principalKey,
            commandAggregateKey,
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

        var grid = await ReadGridAsync(
            connection,
            transaction,
            envelope.Subject.CharacterId,
            envelope.Command.GridIndex,
            cancellationToken);
        var domainResult = DeriveUpgrade(
            character.Value,
            grid,
            envelope.Command.GridIndex);
        return await PersistResultAsync(
            connection,
            transaction,
            envelope,
            character.Value,
            domainResult,
            principalKey,
            commandAggregateKey,
            operationId,
            requestHash,
            cancellationToken);
    }

    private async Task<ZodiacSkillGridUpgradeExecutionResult>
        ReplayAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            CommandEnvelope<ZodiacSkillGridUpgradeCommand> envelope,
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
            return ZodiacSkillGridUpgradeExecutionResult
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
        await RecordDuplicateAsync(
            connection,
            transaction,
            existing.InboxId,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ZodiacSkillGridUpgradeExecutionResult.Duplicate(
            receipt,
            character.Energy,
            character.EnergyRemainderX100,
            character.TalentPoints,
            grid.Level,
            grid.SelectedSkillId);
    }

    private async Task<ZodiacSkillGridUpgradeExecutionResult>
        PersistResultAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            CommandEnvelope<ZodiacSkillGridUpgradeCommand> envelope,
            LockedCharacter character,
            ZodiacSkillGridUpgradeResult upgrade,
            string principalKey,
            string commandAggregateKey,
            byte[] operationId,
            byte[] requestHash,
            CancellationToken cancellationToken)
    {
        var status = MapStatus(upgrade.Status);
        var eventId = upgrade.Committed ? Guid.NewGuid() : (Guid?)null;
        var resultCode =
            ZodiacSkillGridUpgradePersistenceCodec.ResultCode(status);
        var auditId = await InsertAuditAsync(
            connection,
            transaction,
            envelope,
            character,
            upgrade,
            status,
            principalKey,
            commandAggregateKey,
            operationId,
            requestHash,
            resultCode,
            cancellationToken);
        var receipt = CreateReceipt(
            envelope,
            character,
            upgrade,
            status,
            auditId,
            eventId);
        var payload =
            ZodiacSkillGridUpgradePersistenceCodec.Encode(receipt);
        await ReachAsync(
            PostgresZodiacSkillGridUpgradeCommandStage.AuditInserted,
            cancellationToken);
        var inboxId = await InsertInboxAsync(
            connection,
            transaction,
            principalKey,
            commandAggregateKey,
            operationId,
            requestHash,
            resultCode,
            ZodiacSkillGridUpgradePersistenceCodec.Hash(payload),
            auditId,
            payload,
            cancellationToken);
        await ReachAsync(
            PostgresZodiacSkillGridUpgradeCommandStage.InboxInserted,
            cancellationToken);

        if (!upgrade.Committed)
        {
            await ReachAsync(
                PostgresZodiacSkillGridUpgradeCommandStage.BeforeCommit,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            await ReachAsync(
                PostgresZodiacSkillGridUpgradeCommandStage.AfterCommit,
                cancellationToken);
            return ZodiacSkillGridUpgradeExecutionResult
                .TerminalRejected(receipt);
        }

        await UpdateResourcesAsync(
            connection,
            transaction,
            envelope,
            character,
            upgrade,
            cancellationToken);
        await ReachAsync(
            PostgresZodiacSkillGridUpgradeCommandStage.ResourcesUpdated,
            cancellationToken);
        await UpdateGridAsync(
            connection,
            transaction,
            envelope.Subject.CharacterId,
            envelope.Command.GridIndex,
            upgrade,
            cancellationToken);
        await ReachAsync(
            PostgresZodiacSkillGridUpgradeCommandStage.GridUpdated,
            cancellationToken);
        await InsertOutboxAsync(
            connection,
            transaction,
            inboxId,
            ZodiacSkillGridUpgradePersistenceCodec.EventAggregateKey(
                envelope.Subject.CharacterId,
                envelope.Command.GridIndex),
            receipt.AggregateRevision!.Value,
            eventId!.Value,
            payload,
            cancellationToken);
        await ReachAsync(
            PostgresZodiacSkillGridUpgradeCommandStage.OutboxInserted,
            cancellationToken);
        await ReachAsync(
            PostgresZodiacSkillGridUpgradeCommandStage.BeforeCommit,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await ReachAsync(
            PostgresZodiacSkillGridUpgradeCommandStage.AfterCommit,
            cancellationToken);
        return ZodiacSkillGridUpgradeExecutionResult.Committed(receipt);
    }

    private static ZodiacSkillGridUpgradeExecutionReceipt CreateReceipt(
        CommandEnvelope<ZodiacSkillGridUpgradeCommand> envelope,
        LockedCharacter character,
        ZodiacSkillGridUpgradeResult result,
        ZodiacSkillGridUpgradeReceiptStatus status,
        long auditId,
        Guid? eventId) =>
        new(
            envelope.Subject.CharacterId,
            status,
            envelope.Command.GridIndex,
            result.PreviousLevel,
            result.CurrentLevel,
            character.ZodiacLevel,
            result.RequiredZodiacLevel,
            result.EnergyCost,
            character.Energy,
            character.EnergyRemainderX100,
            result.CurrentEnergy,
            result.CurrentEnergyRemainderX100,
            result.TalentPointCost,
            character.TalentPoints,
            result.CurrentTalentPoints,
            result.SelectedSkillId,
            auditId.ToString(CultureInfo.InvariantCulture),
            eventId);

    private static ZodiacSkillGridUpgradeResult DeriveUpgrade(
        LockedCharacter locked,
        StoredGrid grid,
        int gridIndex)
    {
        var character = new GameCharacter
        {
            ZodiacLevel = locked.ZodiacLevel,
            ZodiacEnergy = locked.Energy,
            ZodiacEnergyRemainderX100 =
                locked.EnergyRemainderX100,
            TalentPoints = locked.TalentPoints,
            ZodiacSkillGridLevels =
                ZodiacSkillGridCatalog.CreateEmptyLevels(),
            ZodiacSkillGridSkillIds =
                ZodiacSkillGridCatalog.CreateEmptySkillIds()
        };
        character.ZodiacSkillGridLevels[gridIndex] = grid.Level;
        character.ZodiacSkillGridSkillIds[gridIndex] =
            grid.SelectedSkillId;
        return ZodiacSkillGridUpgrade.Apply(character, gridIndex);
    }

    private static ZodiacSkillGridUpgradeReceiptStatus MapStatus(
        ZodiacSkillGridUpgradeStatus status) =>
        status switch
        {
            ZodiacSkillGridUpgradeStatus.Succeeded =>
                ZodiacSkillGridUpgradeReceiptStatus.Succeeded,
            ZodiacSkillGridUpgradeStatus.InactiveGrid =>
                ZodiacSkillGridUpgradeReceiptStatus.InactiveGrid,
            ZodiacSkillGridUpgradeStatus.MaximumLevelReached =>
                ZodiacSkillGridUpgradeReceiptStatus.MaximumLevelReached,
            ZodiacSkillGridUpgradeStatus.ZodiacLevelTooLow =>
                ZodiacSkillGridUpgradeReceiptStatus.ZodiacLevelTooLow,
            ZodiacSkillGridUpgradeStatus.InsufficientEnergy =>
                ZodiacSkillGridUpgradeReceiptStatus.InsufficientEnergy,
            ZodiacSkillGridUpgradeStatus.InsufficientTalentPoints =>
                ZodiacSkillGridUpgradeReceiptStatus
                    .InsufficientTalentPoints,
            _ => throw new InvalidDataException(
                $"Unexpected Zodiac upgrade result {status}.")
        };

    private static string OutcomeCode(
        ZodiacSkillGridUpgradeExecutionDisposition disposition) =>
        disposition switch
        {
            ZodiacSkillGridUpgradeExecutionDisposition.Committed =>
                "committed",
            ZodiacSkillGridUpgradeExecutionDisposition.Duplicate =>
                "duplicate",
            ZodiacSkillGridUpgradeExecutionDisposition.TerminalRejected =>
                "terminal_rejected",
            ZodiacSkillGridUpgradeExecutionDisposition
                .RequestHashConflict => "request_hash_conflict",
            ZodiacSkillGridUpgradeExecutionDisposition.InvalidIntent =>
                "invalid_intent",
            ZodiacSkillGridUpgradeExecutionDisposition
                .PreconditionFailed => "precondition_failed",
            _ => throw new InvalidOperationException(
                "Unknown Zodiac upgrade command outcome.")
        };

    private async ValueTask ReachAsync(
        PostgresZodiacSkillGridUpgradeCommandStage stage,
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
        byte ZodiacLevel,
        int Energy,
        int EnergyRemainderX100,
        int TalentPoints);

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
