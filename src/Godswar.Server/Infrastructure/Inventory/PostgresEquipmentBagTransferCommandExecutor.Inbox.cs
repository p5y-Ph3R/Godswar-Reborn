using System.Globalization;
using System.Text;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Inventory;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.Infrastructure.Inventory;

internal sealed partial class
    PostgresEquipmentBagTransferCommandExecutor
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
            TransferCommandContext context,
            string principalKey,
            string aggregateKey,
            byte[] operationId,
            byte[] requestHash,
            EquipmentBagTransferResultStatus status,
            string authoritativeEquipmentState,
            string authoritativeKitBagState,
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
            authoritativeEquipmentState,
            authoritativeKitBagState,
            cancellationToken);
        await ReachAsync(
            PostgresEquipmentBagTransferCommandStage.AuditInserted,
            0,
            cancellationToken);
        var receipt = new EquipmentBagTransferExecutionReceipt(
            context.Subject.CharacterId,
            context.Command.EquipmentSlot,
            context.Command.KitBagSlot,
            status,
            context.Command.ExpectedEquipmentCompactItemState,
            context.Command.ExpectedKitBagCompactItemState,
            authoritativeEquipmentState,
            authoritativeKitBagState,
            inventoryRevision,
            auditId.ToString(CultureInfo.InvariantCulture),
            outboxEventId);
        var payload =
            EquipmentBagTransferPersistenceCodec.Encode(receipt);
        var resultHash =
            EquipmentBagTransferPersistenceCodec.Hash(payload);
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
            PostgresEquipmentBagTransferCommandStage.InboxInserted,
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
        TransferCommandContext context,
        string principalKey,
        string aggregateKey,
        byte[] operationId,
        byte[] requestHash,
        EquipmentBagTransferResultStatus status,
        string authoritativeEquipmentState,
        string authoritativeKitBagState,
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
                    'equipmentSlot', @equipmentSlot,
                    'kitBagSlot', @kitBagSlot,
                    'status', @status,
                    'expectedEquipmentCompactItemState',
                        @expectedEquipmentState,
                    'expectedKitBagCompactItemState',
                        @expectedKitBagState,
                    'authoritativeEquipmentCompactItemState',
                        @authoritativeEquipmentState,
                    'authoritativeKitBagCompactItemState',
                        @authoritativeKitBagState),
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
            EquipmentBagTransferPersistenceCodec.ResultCode(status));
        command.Parameters.AddWithValue(
            "equipmentSlot",
            context.Command.EquipmentSlot);
        command.Parameters.AddWithValue(
            "kitBagSlot",
            context.Command.KitBagSlot);
        command.Parameters.AddWithValue("status", (short)status);
        command.Parameters.AddWithValue(
            "expectedEquipmentState",
            context.Command.ExpectedEquipmentCompactItemState);
        command.Parameters.AddWithValue(
            "expectedKitBagState",
            context.Command.ExpectedKitBagCompactItemState);
        command.Parameters.AddWithValue(
            "authoritativeEquipmentState",
            authoritativeEquipmentState);
        command.Parameters.AddWithValue(
            "authoritativeKitBagState",
            authoritativeKitBagState);
        command.Parameters.AddWithValue(
            "retentionPolicy",
            EquipmentBagTransferPersistenceCodec.RetentionPolicy);
        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        return scalar is long auditId && auditId > 0
            ? auditId
            : throw new InvalidDataException(
                "The equipment transfer audit returned no identity.");
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
        EquipmentBagTransferResultStatus status,
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
            EquipmentBagTransferPersistenceCodec.ContractVersion);
        command.Parameters.AddWithValue(
            "resultCode",
            EquipmentBagTransferPersistenceCodec.ResultCode(status));
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
            EquipmentBagTransferPersistenceCodec.RetentionPolicy);
        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        return scalar is long inboxId && inboxId > 0
            ? inboxId
            : throw new InvalidDataException(
                "The equipment transfer inbox returned no identity.");
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
                "The equipment transfer duplicate update was not " +
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
                "The equipment transfer conflict update was not " +
                "exact.");
        }
    }

    private static EquipmentBagTransferExecutionReceipt
        ValidateStoredResult(
            StoredInbox stored,
            CommandSubject subject)
    {
        if (stored.ResultContractVersion !=
            EquipmentBagTransferPersistenceCodec.ContractVersion)
        {
            throw new InvalidDataException(
                "The stored equipment transfer contract is " +
                "unsupported.");
        }

        var receipt =
            EquipmentBagTransferPersistenceCodec.DecodeAndVerify(
                stored.ResultPayload,
                stored.ResultHash);
        if (receipt.CharacterId != subject.CharacterId ||
            !string.Equals(
                receipt.AuditReference,
                stored.AuditId.ToString(CultureInfo.InvariantCulture),
                StringComparison.Ordinal) ||
            !string.Equals(
                stored.ResultCode,
                EquipmentBagTransferPersistenceCodec.ResultCode(
                    receipt.Status),
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The stored equipment transfer identity is invalid.");
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
            EquipmentBagTransferPersistenceCodec.PrincipalType);
        command.Parameters.AddWithValue("principalKey", principalKey);
        command.Parameters.AddWithValue(
            "aggregateType",
            EquipmentBagTransferPersistenceCodec.AggregateType);
        command.Parameters.AddWithValue("aggregateKey", aggregateKey);
        command.Parameters.AddWithValue(
            "commandFamily",
            EquipmentBagTransferPersistenceCodec.CommandFamilyCode);
        command.Parameters.Add(
            "operationId",
            NpgsqlDbType.Bytea).Value = operationId;
    }
}
