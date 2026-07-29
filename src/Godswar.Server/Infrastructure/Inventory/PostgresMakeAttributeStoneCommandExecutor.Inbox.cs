using System.Globalization;
using System.Text;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Inventory;
using Godswar.Server.State;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.Infrastructure.Inventory;

internal sealed partial class PostgresMakeAttributeStoneCommandExecutor
{
    private async Task<LockedCharacter?> LockCharacterAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CommandSubject subject,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            SELECT fighter_job_lv, inventory_revision
            FROM public.character_base
            WHERE account_id = @accountId
              AND id = @characterId
            FOR UPDATE;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("accountId", subject.AccountId);
        command.Parameters.AddWithValue(
            "characterId",
            subject.CharacterId);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken) ||
            reader.GetInt64(1) < 0)
        {
            return null;
        }

        return new LockedCharacter(
            reader.GetInt32(0),
            reader.GetInt64(1));
    }

    private async Task<StoredInbox?> ReadInboxAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string principalKey,
        string aggregateKey,
        byte[] operationId,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            SELECT
                id,
                request_hash,
                result_contract_version,
                result_code,
                result_payload::text,
                result_hash,
                audit_id
            FROM public.command_inbox
            WHERE principal_type = @principalType
              AND principal_key = @principalKey
              AND aggregate_type = @aggregateType
              AND aggregate_key = @aggregateKey
              AND command_family = @commandFamily
              AND operation_id = @operationId;
            """,
            connection,
            transaction);
        AddIdentityParameters(
            command,
            principalKey,
            aggregateKey,
            operationId);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new StoredInbox(
            reader.GetInt64(0),
            reader.GetFieldValue<byte[]>(1),
            reader.GetInt16(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetFieldValue<byte[]>(5),
            reader.GetInt64(6));
    }

    private async Task<MakeAttributeStoneExecutionReceipt>
        PersistTerminalResultAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            CommandEnvelope<GearMentorMakeAttributeStoneCommand>
                envelope,
            long inventoryRevision,
            MakeAttributeStoneResultStatus status,
            string principalKey,
            string aggregateKey,
            byte[] operationId,
            byte[] requestHash,
            CancellationToken cancellationToken)
    {
        var expected = CompactItemEntry.Parse(
            envelope.Command.ExpectedCompactItemState);
        ResolveReceiptItems(
            status,
            expected,
            out var sourceItemId,
            out var outputItemId,
            out var isBound);
        var auditId = await InsertAuditAsync(
            connection,
            transaction,
            envelope,
            status,
            principalKey,
            aggregateKey,
            operationId,
            requestHash,
            cancellationToken);
        var receipt = new MakeAttributeStoneExecutionReceipt(
            envelope.Subject.CharacterId,
            status,
            MakeAttributeStoneNativeResults.GetResultSubId(status),
            envelope.Command.SelectedKitBagSlot,
            sourceItemId,
            outputItemId,
            isBound,
            inventoryRevision,
            auditId.ToString(CultureInfo.InvariantCulture),
            outboxEventId: null);
        var payload = MakeAttributeStonePersistenceCodec.Encode(receipt);
        await ReachAsync(
            PostgresMakeAttributeStoneCommandStage.AuditInserted,
            cancellationToken);
        await InsertInboxAsync(
            connection,
            transaction,
            envelope,
            status,
            principalKey,
            aggregateKey,
            operationId,
            requestHash,
            MakeAttributeStonePersistenceCodec.Hash(payload),
            auditId,
            payload,
            cancellationToken);
        await ReachAsync(
            PostgresMakeAttributeStoneCommandStage.InboxInserted,
            cancellationToken);
        return receipt;
    }

    private async Task<long> InsertAuditAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CommandEnvelope<GearMentorMakeAttributeStoneCommand> envelope,
        MakeAttributeStoneResultStatus status,
        string principalKey,
        string aggregateKey,
        byte[] operationId,
        byte[] requestHash,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            INSERT INTO public.command_audit (
                principal_type,
                principal_key,
                aggregate_type,
                aggregate_key,
                command_family,
                operation_id,
                request_hash,
                outcome_code,
                detail_payload,
                retention_policy
            )
            VALUES (
                @principalType,
                @principalKey,
                @aggregateType,
                @aggregateKey,
                @commandFamily,
                @operationId,
                @requestHash,
                @outcomeCode,
                jsonb_build_object(
                    'npcId', @npcId,
                    'selectedKitBagSlot', @selectedKitBagSlot,
                    'status', @status),
                @retentionPolicy
            )
            RETURNING id;
            """,
            connection,
            transaction);
        AddIdentityParameters(
            command,
            principalKey,
            aggregateKey,
            operationId);
        command.Parameters.Add(
            "requestHash",
            NpgsqlDbType.Bytea).Value = requestHash;
        command.Parameters.AddWithValue(
            "outcomeCode",
            AuditOutcomeCode(status));
        command.Parameters.AddWithValue(
            "npcId",
            envelope.Command.NpcId);
        command.Parameters.AddWithValue(
            "selectedKitBagSlot",
            envelope.Command.SelectedKitBagSlot);
        command.Parameters.AddWithValue(
            "status",
            status.ToString());
        command.Parameters.AddWithValue(
            "retentionPolicy",
            MakeAttributeStonePersistenceCodec.RetentionPolicy);
        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        return scalar is long auditId && auditId > 0
            ? auditId
            : throw new InvalidDataException(
                "The Make Attribute Stone audit insert returned no " +
                "identity.");
    }

    private async Task<long> InsertInboxAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CommandEnvelope<GearMentorMakeAttributeStoneCommand> envelope,
        MakeAttributeStoneResultStatus status,
        string principalKey,
        string aggregateKey,
        byte[] operationId,
        byte[] requestHash,
        byte[] resultHash,
        long auditId,
        byte[] payload,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            INSERT INTO public.command_inbox (
                principal_type,
                principal_key,
                aggregate_type,
                aggregate_key,
                command_family,
                operation_id,
                request_hash,
                result_contract_version,
                result_code,
                result_payload,
                result_hash,
                audit_id,
                retention_policy
            )
            VALUES (
                @principalType,
                @principalKey,
                @aggregateType,
                @aggregateKey,
                @commandFamily,
                @operationId,
                @requestHash,
                @resultContractVersion,
                @resultCode,
                @resultPayload,
                @resultHash,
                @auditId,
                @retentionPolicy
            )
            RETURNING id;
            """,
            connection,
            transaction);
        AddIdentityParameters(
            command,
            principalKey,
            aggregateKey,
            operationId);
        command.Parameters.Add(
            "requestHash",
            NpgsqlDbType.Bytea).Value = requestHash;
        command.Parameters.AddWithValue(
            "resultContractVersion",
            MakeAttributeStonePersistenceCodec.ContractVersion);
        command.Parameters.AddWithValue(
            "resultCode",
            MakeAttributeStonePersistenceCodec.ResultCode(status));
        command.Parameters.Add(
            "resultPayload",
            NpgsqlDbType.Jsonb).Value =
            Encoding.UTF8.GetString(payload);
        command.Parameters.Add(
            "resultHash",
            NpgsqlDbType.Bytea).Value = resultHash;
        command.Parameters.AddWithValue("auditId", auditId);
        command.Parameters.AddWithValue(
            "retentionPolicy",
            MakeAttributeStonePersistenceCodec.RetentionPolicy);
        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        return scalar is long inboxId && inboxId > 0
            ? inboxId
            : throw new InvalidDataException(
                "The Make Attribute Stone inbox insert returned no " +
                "identity.");
    }

    private async Task RecordDuplicateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long inboxId,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            UPDATE public.command_inbox
            SET duplicate_count =
                    LEAST(duplicate_count + 1, 1000000),
                last_duplicate_at = now()
            WHERE id = @inboxId;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("inboxId", inboxId);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidDataException(
                "The Make Attribute Stone duplicate update was not " +
                "exact.");
        }
    }

    private async Task RecordRequestConflictAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long inboxId,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            UPDATE public.command_inbox
            SET request_conflict_count =
                    LEAST(request_conflict_count + 1, 1000000),
                last_request_conflict_at = now()
            WHERE id = @inboxId;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("inboxId", inboxId);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidDataException(
                "The Make Attribute Stone conflict update was not " +
                "exact.");
        }
    }

    private static MakeAttributeStoneExecutionReceipt
        ValidateStoredResult(StoredInbox stored)
    {
        if (stored.ResultContractVersion !=
            MakeAttributeStonePersistenceCodec.ContractVersion)
        {
            throw new InvalidDataException(
                "The stored Make Attribute Stone contract is " +
                "unsupported.");
        }

        return MakeAttributeStonePersistenceCodec.DecodeAndVerify(
            stored.ResultPayload,
            stored.ResultHash,
            stored.ResultCode,
            stored.AuditId);
    }

    private static void ResolveReceiptItems(
        MakeAttributeStoneResultStatus status,
        CompactItemEntry expected,
        out uint sourceItemId,
        out uint outputItemId,
        out bool? isBound)
    {
        if (status is MakeAttributeStoneResultStatus.StaleSelection or
            MakeAttributeStoneResultStatus.InvalidKitBagSlot)
        {
            sourceItemId = 0;
            outputItemId = 0;
            isBound = null;
            return;
        }

        sourceItemId = expected.Id;
        isBound = expected.Bound != 0;
        outputItemId =
            GearMentorMaterialCatalog.TryGetDust(
                expected.Id,
                out var dust)
                ? dust.AttributeStoneItemId
                : 0;
    }

    private static string AuditOutcomeCode(
        MakeAttributeStoneResultStatus status) =>
        status switch
        {
            MakeAttributeStoneResultStatus.Succeeded => "committed",
            MakeAttributeStoneResultStatus.InvalidDust =>
                "invalid_dust",
            MakeAttributeStoneResultStatus.InsufficientDust =>
                "insufficient_dust",
            MakeAttributeStoneResultStatus.InsufficientCapacity =>
                "insufficient_capacity",
            MakeAttributeStoneResultStatus.StaleSelection =>
                "stale_selection",
            MakeAttributeStoneResultStatus.InvalidKitBagSlot =>
                "invalid_slot",
            _ => throw new ArgumentOutOfRangeException(nameof(status))
        };

    private static void AddIdentityParameters(
        NpgsqlCommand command,
        string principalKey,
        string aggregateKey,
        byte[] operationId)
    {
        command.Parameters.AddWithValue(
            "principalType",
            MakeAttributeStonePersistenceCodec.PrincipalType);
        command.Parameters.AddWithValue("principalKey", principalKey);
        command.Parameters.AddWithValue(
            "aggregateType",
            MakeAttributeStonePersistenceCodec.AggregateType);
        command.Parameters.AddWithValue("aggregateKey", aggregateKey);
        command.Parameters.AddWithValue(
            "commandFamily",
            MakeAttributeStonePersistenceCodec.CommandFamily);
        command.Parameters.Add(
            "operationId",
            NpgsqlDbType.Bytea).Value = operationId;
    }
}
