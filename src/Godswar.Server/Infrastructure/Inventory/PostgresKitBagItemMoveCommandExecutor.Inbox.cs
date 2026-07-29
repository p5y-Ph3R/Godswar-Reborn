using System.Globalization;
using System.Text;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Inventory;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.Infrastructure.Inventory;

internal sealed partial class PostgresKitBagItemMoveCommandExecutor
{
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
        return await reader.ReadAsync(cancellationToken)
            ? new StoredInbox(
                reader.GetInt64(0),
                reader.GetFieldValue<byte[]>(1),
                reader.GetInt16(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetFieldValue<byte[]>(5),
                reader.GetInt64(6))
            : null;
    }

    private async Task<PersistedResultEvidence>
        PersistResultEvidenceAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            KitBagItemMoveCommandContext context,
            string principalKey,
            string aggregateKey,
            byte[] operationId,
            byte[] requestHash,
            KitBagItemMoveResultStatus status,
            string authoritativeSourceState,
            string authoritativeDestinationState,
            long inventoryRevision,
            Guid? outboxEventId,
            CancellationToken cancellationToken)
    {
        var auditId = await InsertAuditAsync(
            connection,
            transaction,
            context,
            principalKey,
            aggregateKey,
            operationId,
            requestHash,
            status,
            authoritativeSourceState,
            authoritativeDestinationState,
            cancellationToken);
        await ReachAsync(
            PostgresKitBagItemMoveCommandStage.AuditInserted,
            0,
            cancellationToken);
        var receipt = new KitBagItemMoveExecutionReceipt(
            context.Subject.CharacterId,
            context.Command.SourceKitBagSlot,
            context.Command.DestinationKitBagSlot,
            status,
            context.Command.ExpectedSourceCompactItemState,
            context.Command.ExpectedDestinationCompactItemState,
            authoritativeSourceState,
            authoritativeDestinationState,
            inventoryRevision,
            auditId.ToString(CultureInfo.InvariantCulture),
            outboxEventId);
        var payload =
            KitBagItemMovePersistenceCodec.Encode(receipt);
        var resultHash =
            KitBagItemMovePersistenceCodec.Hash(payload);
        var inboxId = await InsertInboxAsync(
            connection,
            transaction,
            principalKey,
            aggregateKey,
            operationId,
            requestHash,
            resultHash,
            auditId,
            status,
            payload,
            cancellationToken);
        await ReachAsync(
            PostgresKitBagItemMoveCommandStage.InboxInserted,
            0,
            cancellationToken);
        return new PersistedResultEvidence(
            inboxId,
            payload,
            receipt);
    }

    private async Task<long> InsertAuditAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        KitBagItemMoveCommandContext context,
        string principalKey,
        string aggregateKey,
        byte[] operationId,
        byte[] requestHash,
        KitBagItemMoveResultStatus status,
        string authoritativeSourceState,
        string authoritativeDestinationState,
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
                    'sourceKitBagSlot', @sourceSlot,
                    'destinationKitBagSlot', @destinationSlot,
                    'status', @status,
                    'expectedSourceCompactItemState',
                        @expectedSourceState,
                    'expectedDestinationCompactItemState',
                        @expectedDestinationState,
                    'authoritativeSourceCompactItemState',
                        @authoritativeSourceState,
                    'authoritativeDestinationCompactItemState',
                        @authoritativeDestinationState),
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
            KitBagItemMovePersistenceCodec.ResultCode(status));
        command.Parameters.AddWithValue(
            "sourceSlot",
            context.Command.SourceKitBagSlot);
        command.Parameters.AddWithValue(
            "destinationSlot",
            context.Command.DestinationKitBagSlot);
        command.Parameters.AddWithValue("status", (short)status);
        command.Parameters.AddWithValue(
            "expectedSourceState",
            context.Command.ExpectedSourceCompactItemState);
        command.Parameters.AddWithValue(
            "expectedDestinationState",
            context.Command.ExpectedDestinationCompactItemState);
        command.Parameters.AddWithValue(
            "authoritativeSourceState",
            authoritativeSourceState);
        command.Parameters.AddWithValue(
            "authoritativeDestinationState",
            authoritativeDestinationState);
        command.Parameters.AddWithValue(
            "retentionPolicy",
            KitBagItemMovePersistenceCodec.RetentionPolicy);
        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        return scalar is long auditId && auditId > 0
            ? auditId
            : throw new InvalidDataException(
                "The kit-bag move audit returned no identity.");
    }

    private async Task<long> InsertInboxAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string principalKey,
        string aggregateKey,
        byte[] operationId,
        byte[] requestHash,
        byte[] resultHash,
        long auditId,
        KitBagItemMoveResultStatus status,
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
            KitBagItemMovePersistenceCodec.ContractVersion);
        command.Parameters.AddWithValue(
            "resultCode",
            KitBagItemMovePersistenceCodec.ResultCode(status));
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
            KitBagItemMovePersistenceCodec.RetentionPolicy);
        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        return scalar is long inboxId && inboxId > 0
            ? inboxId
            : throw new InvalidDataException(
                "The kit-bag move inbox returned no identity.");
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
                "The kit-bag move duplicate update was not exact.");
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
                "The kit-bag move conflict update was not exact.");
        }
    }

    private static KitBagItemMoveExecutionReceipt ValidateStoredResult(
        StoredInbox stored,
        CommandSubject subject)
    {
        if (stored.ResultContractVersion !=
            KitBagItemMovePersistenceCodec.ContractVersion)
        {
            throw new InvalidDataException(
                "The stored kit-bag move contract is unsupported.");
        }

        var receipt =
            KitBagItemMovePersistenceCodec.DecodeAndVerify(
                stored.ResultPayload,
                stored.ResultHash);
        if (receipt.CharacterId != subject.CharacterId ||
            !string.Equals(
                receipt.AuditReference,
                stored.AuditId.ToString(CultureInfo.InvariantCulture),
                StringComparison.Ordinal) ||
            !string.Equals(
                stored.ResultCode,
                KitBagItemMovePersistenceCodec.ResultCode(
                    receipt.Status),
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The stored kit-bag move identity is invalid.");
        }
        return receipt;
    }

    private static void AddIdentityParameters(
        NpgsqlCommand command,
        string principalKey,
        string aggregateKey,
        byte[] operationId)
    {
        command.Parameters.AddWithValue(
            "principalType",
            KitBagItemMovePersistenceCodec.PrincipalType);
        command.Parameters.AddWithValue("principalKey", principalKey);
        command.Parameters.AddWithValue(
            "aggregateType",
            KitBagItemMovePersistenceCodec.AggregateType);
        command.Parameters.AddWithValue("aggregateKey", aggregateKey);
        command.Parameters.AddWithValue(
            "commandFamily",
            KitBagItemMovePersistenceCodec.CommandFamilyCode);
        command.Parameters.Add(
            "operationId",
            NpgsqlDbType.Bytea).Value = operationId;
    }
}
