using System.Globalization;
using System.Text;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Warehouse;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.Infrastructure.Warehouse;

internal sealed partial class PostgresWarehouseTransferCommandExecutor
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
            SELECT id, request_hash, result_contract_version, result_code,
                   result_payload::text, result_hash, audit_id
            FROM public.command_inbox
            WHERE principal_type = @principalType
              AND principal_key = @principalKey
              AND aggregate_type = @aggregateType
              AND aggregate_key = @aggregateKey
              AND command_family = @commandFamily
              AND operation_id = @operationId
            FOR UPDATE;
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

    private async Task<PersistedEvidence> PersistEvidenceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CommandEnvelope<WarehouseTransferCommand> envelope,
        string principalKey,
        string aggregateKey,
        byte[] operationId,
        byte[] requestHash,
        TransferPlan plan,
        LockedCharacter character,
        long inventoryRevision,
        Guid? outboxEventId,
        CancellationToken cancellationToken)
    {
        var auditId = await InsertAuditAsync(
            connection,
            transaction,
            envelope,
            principalKey,
            aggregateKey,
            operationId,
            requestHash,
            plan,
            character,
            cancellationToken);
        var receipt = new WarehouseTransferExecutionReceipt(
            envelope.Subject.CharacterId,
            envelope.Command.Operation,
            envelope.Command.WarehouseSlot,
            envelope.Command.KitBagSlot,
            envelope.Command.DestinationWarehouseSlot,
            plan.ActualWarehouseSlot,
            plan.ActualKitBagSlot,
            plan.Status,
            plan.MovedQuantity,
            character.Capacity,
            character.WarehouseRevision,
            inventoryRevision,
            plan.Mutations,
            auditId.ToString(CultureInfo.InvariantCulture),
            outboxEventId);
        var payload = WarehouseTransferPersistenceCodec.Encode(receipt);
        var resultHash = WarehouseTransferPersistenceCodec.Hash(payload);
        var inboxId = await InsertInboxAsync(
            connection,
            transaction,
            principalKey,
            aggregateKey,
            operationId,
            requestHash,
            resultHash,
            auditId,
            plan.Status,
            payload,
            cancellationToken);
        return new PersistedEvidence(inboxId, payload, receipt);
    }

    private async Task<long> InsertAuditAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CommandEnvelope<WarehouseTransferCommand> envelope,
        string principalKey,
        string aggregateKey,
        byte[] operationId,
        byte[] requestHash,
        TransferPlan plan,
        LockedCharacter character,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            INSERT INTO public.command_audit (
                principal_type, principal_key, aggregate_type,
                aggregate_key, command_family, operation_id, request_hash,
                outcome_code, detail_payload, retention_policy)
            VALUES (
                @principalType, @principalKey, @aggregateType,
                @aggregateKey, @commandFamily, @operationId, @requestHash,
                @outcomeCode,
                jsonb_build_object(
                    'realmId', @realmId,
                    'operation', @operation,
                    'warehouseSlot', @warehouseSlot,
                    'kitBagSlot', @kitBagSlot,
                    'destinationWarehouseSlot', @destinationWarehouseSlot,
                    'actualWarehouseSlot', @actualWarehouseSlot,
                    'actualKitBagSlot', @actualKitBagSlot,
                    'status', @status,
                    'capacity', @capacity,
                    'warehouseRevision', @warehouseRevision,
                    'inventoryRevision', @inventoryRevision,
                    'sourceState', @sourceState,
                    'destinationState', @destinationState),
                @retentionPolicy)
            RETURNING id;
            """,
            connection,
            transaction);
        AddIdentityParameters(
            command,
            principalKey,
            aggregateKey,
            operationId);
        command.Parameters.Add("requestHash", NpgsqlDbType.Bytea).Value =
            requestHash;
        command.Parameters.AddWithValue(
            "outcomeCode",
            WarehouseTransferPersistenceCodec.ResultCode(plan.Status));
        command.Parameters.AddWithValue("realmId", envelope.Command.RealmId);
        command.Parameters.AddWithValue(
            "operation",
            (short)envelope.Command.Operation);
        command.Parameters.AddWithValue(
            "warehouseSlot",
            envelope.Command.WarehouseSlot);
        command.Parameters.AddWithValue("kitBagSlot", envelope.Command.KitBagSlot);
        command.Parameters.AddWithValue(
            "destinationWarehouseSlot",
            envelope.Command.DestinationWarehouseSlot);
        command.Parameters.AddWithValue(
            "actualWarehouseSlot",
            plan.ActualWarehouseSlot);
        command.Parameters.AddWithValue(
            "actualKitBagSlot",
            plan.ActualKitBagSlot);
        command.Parameters.AddWithValue("status", (short)plan.Status);
        command.Parameters.AddWithValue("capacity", character.Capacity);
        command.Parameters.AddWithValue(
            "warehouseRevision",
            character.WarehouseRevision);
        command.Parameters.AddWithValue(
            "inventoryRevision",
            character.InventoryRevision);
        command.Parameters.AddWithValue(
            "sourceState",
            plan.Source?.Item.ToCompactString() ?? "[]");
        command.Parameters.AddWithValue(
            "destinationState",
            plan.Destination?.Item.ToCompactString() ?? "[]");
        command.Parameters.AddWithValue(
            "retentionPolicy",
            WarehouseTransferPersistenceCodec.RetentionPolicy);
        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        return scalar is long auditId && auditId > 0
            ? auditId
            : throw new InvalidDataException(
                "The warehouse transfer audit returned no identity.");
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
        WarehouseTransferResultStatus status,
        byte[] payload,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            INSERT INTO public.command_inbox (
                principal_type, principal_key, aggregate_type,
                aggregate_key, command_family, operation_id, request_hash,
                result_contract_version, result_code, result_payload,
                result_hash, audit_id, retention_policy)
            VALUES (
                @principalType, @principalKey, @aggregateType,
                @aggregateKey, @commandFamily, @operationId, @requestHash,
                @contractVersion, @resultCode, @resultPayload,
                @resultHash, @auditId, @retentionPolicy)
            RETURNING id;
            """,
            connection,
            transaction);
        AddIdentityParameters(
            command,
            principalKey,
            aggregateKey,
            operationId);
        command.Parameters.Add("requestHash", NpgsqlDbType.Bytea).Value =
            requestHash;
        command.Parameters.AddWithValue(
            "contractVersion",
            WarehouseTransferPersistenceCodec.ContractVersion);
        command.Parameters.AddWithValue(
            "resultCode",
            WarehouseTransferPersistenceCodec.ResultCode(status));
        command.Parameters.Add("resultPayload", NpgsqlDbType.Jsonb).Value =
            Encoding.UTF8.GetString(payload);
        command.Parameters.Add("resultHash", NpgsqlDbType.Bytea).Value =
            resultHash;
        command.Parameters.AddWithValue("auditId", auditId);
        command.Parameters.AddWithValue(
            "retentionPolicy",
            WarehouseTransferPersistenceCodec.RetentionPolicy);
        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        return scalar is long inboxId && inboxId > 0
            ? inboxId
            : throw new InvalidDataException(
                "The warehouse transfer inbox returned no identity.");
    }

    private async Task InsertTransferOutboxAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long inboxId,
        string aggregateKey,
        long revision,
        Guid eventId,
        byte[] payload,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            INSERT INTO public.outbox_events (
                event_id, command_inbox_id, consumer_key, aggregate_type,
                aggregate_key, aggregate_version, event_type,
                contract_version, ordering_policy, payload, max_attempts)
            VALUES (
                @eventId, @inboxId, @consumerKey, @aggregateType,
                @aggregateKey, @aggregateVersion, @eventType,
                @contractVersion, @orderingPolicy, @payload, @maxAttempts);
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("eventId", eventId);
        command.Parameters.AddWithValue("inboxId", inboxId);
        command.Parameters.AddWithValue(
            "consumerKey",
            WarehouseTransferPersistenceCodec.ConsumerKey);
        command.Parameters.AddWithValue(
            "aggregateType",
            WarehouseTransferPersistenceCodec.AggregateType);
        command.Parameters.AddWithValue("aggregateKey", aggregateKey);
        command.Parameters.AddWithValue("aggregateVersion", revision);
        command.Parameters.AddWithValue(
            "eventType",
            WarehouseTransferPersistenceCodec.EventType);
        command.Parameters.AddWithValue(
            "contractVersion",
            WarehouseTransferPersistenceCodec.ContractVersion);
        command.Parameters.AddWithValue(
            "orderingPolicy",
            WarehouseTransferPersistenceCodec.OrderingPolicy);
        command.Parameters.Add("payload", NpgsqlDbType.Jsonb).Value =
            Encoding.UTF8.GetString(payload);
        command.Parameters.AddWithValue("maxAttempts", _maximumOutboxAttempts);
        await RequireOneAsync(
            command,
            "The warehouse transfer outbox append was not exact.",
            cancellationToken);
    }

    private async Task UpdateInboxEvidenceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long inboxId,
        bool duplicate,
        CancellationToken cancellationToken)
    {
        var sql = duplicate
            ? """
              UPDATE public.command_inbox
              SET duplicate_count = LEAST(duplicate_count + 1, 1000000),
                  last_duplicate_at = now()
              WHERE id = @inboxId;
              """
            : """
              UPDATE public.command_inbox
              SET request_conflict_count =
                      LEAST(request_conflict_count + 1, 1000000),
                  last_request_conflict_at = now()
              WHERE id = @inboxId;
              """;
        await using var command = CreateCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("inboxId", inboxId);
        await RequireOneAsync(
            command,
            "Warehouse replay evidence was not exact.",
            cancellationToken);
    }

    private static WarehouseTransferExecutionReceipt ValidateStored(
        StoredInbox stored,
        CommandSubject subject)
    {
        if (stored.ContractVersion !=
            WarehouseTransferPersistenceCodec.ContractVersion)
        {
            throw new InvalidDataException(
                "The stored warehouse transfer contract is unsupported.");
        }
        var receipt = WarehouseTransferPersistenceCodec.DecodeAndVerify(
            stored.ResultPayload,
            stored.ResultHash);
        if (receipt.CharacterId != subject.CharacterId ||
            !string.Equals(
                receipt.AuditReference,
                stored.AuditId.ToString(CultureInfo.InvariantCulture),
                StringComparison.Ordinal) ||
            !string.Equals(
                stored.ResultCode,
                WarehouseTransferPersistenceCodec.ResultCode(receipt.Status),
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The stored warehouse transfer identity is invalid.");
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
            WarehouseTransferPersistenceCodec.PrincipalType);
        command.Parameters.AddWithValue("principalKey", principalKey);
        command.Parameters.AddWithValue(
            "aggregateType",
            WarehouseTransferPersistenceCodec.AggregateType);
        command.Parameters.AddWithValue("aggregateKey", aggregateKey);
        command.Parameters.AddWithValue(
            "commandFamily",
            WarehouseTransferPersistenceCodec.CommandFamilyCode);
        command.Parameters.Add("operationId", NpgsqlDbType.Bytea).Value =
            operationId;
    }
}
