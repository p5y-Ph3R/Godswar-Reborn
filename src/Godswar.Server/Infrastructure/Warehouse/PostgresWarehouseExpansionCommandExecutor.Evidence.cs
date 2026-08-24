using System.Globalization;
using System.Text;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Warehouse;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.Infrastructure.Warehouse;

internal sealed partial class PostgresWarehouseExpansionCommandExecutor
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
        CommandEnvelope<WarehouseExpansionCommand> envelope,
        string principalKey,
        string aggregateKey,
        byte[] operationId,
        byte[] requestHash,
        LockedCharacter character,
        ExpansionPlan plan,
        Guid? inventoryEventId,
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
            character,
            plan,
            cancellationToken);
        var receipt = new WarehouseExpansionExecutionReceipt(
            envelope.Subject.CharacterId,
            envelope.Command.RealmId,
            envelope.Command.ActionSubId,
            plan.Status,
            plan.PreviousCapacity,
            plan.CurrentCapacity,
            plan.KeyItemId,
            plan.RequiredKeys,
            plan.ConsumedKeys,
            _policy.Revision,
            _policy.Sha256,
            plan.NextWarehouseRevision,
            plan.NextInventoryRevision,
            plan.Mutations,
            auditId.ToString(CultureInfo.InvariantCulture),
            inventoryEventId);
        var payload = WarehouseExpansionPersistenceCodec.Encode(receipt);
        var hash = WarehouseExpansionPersistenceCodec.Hash(payload);
        var inboxId = await InsertInboxAsync(
            connection,
            transaction,
            principalKey,
            aggregateKey,
            operationId,
            requestHash,
            hash,
            auditId,
            plan.Status,
            payload,
            cancellationToken);
        return new PersistedEvidence(inboxId, auditId, payload, receipt);
    }

    private async Task<long> InsertAuditAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CommandEnvelope<WarehouseExpansionCommand> envelope,
        string principalKey,
        string aggregateKey,
        byte[] operationId,
        byte[] requestHash,
        LockedCharacter character,
        ExpansionPlan plan,
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
                    'npcId', @npcId,
                    'dialogIndex', @dialogIndex,
                    'actionSubId', @actionSubId,
                    'status', @status,
                    'expectedCapacity', @expectedCapacity,
                    'authoritativeCapacity', @authoritativeCapacity,
                    'targetCapacity', @targetCapacity,
                    'keyItemId', @keyItemId,
                    'requiredKeys', @requiredKeys,
                    'consumedKeys', @consumedKeys,
                    'policyRevision', @policyRevision,
                    'policySha256', @policySha256,
                    'warehouseRevision', @warehouseRevision,
                    'inventoryRevision', @inventoryRevision),
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
            WarehouseExpansionPersistenceCodec.ResultCode(plan.Status));
        command.Parameters.AddWithValue("realmId", envelope.Command.RealmId);
        command.Parameters.AddWithValue("npcId", envelope.Command.NpcId);
        command.Parameters.AddWithValue(
            "dialogIndex",
            envelope.Command.DialogIndex);
        command.Parameters.AddWithValue(
            "actionSubId",
            envelope.Command.ActionSubId);
        command.Parameters.AddWithValue("status", (short)plan.Status);
        command.Parameters.AddWithValue(
            "expectedCapacity",
            envelope.Command.ExpectedCapacity);
        command.Parameters.AddWithValue(
            "authoritativeCapacity",
            character.Capacity);
        command.Parameters.AddWithValue(
            "targetCapacity",
            envelope.Command.TargetCapacity);
        command.Parameters.AddWithValue("keyItemId", plan.KeyItemId);
        command.Parameters.AddWithValue("requiredKeys", plan.RequiredKeys);
        command.Parameters.AddWithValue("consumedKeys", plan.ConsumedKeys);
        command.Parameters.AddWithValue("policyRevision", _policy.Revision);
        command.Parameters.AddWithValue("policySha256", _policy.Sha256);
        command.Parameters.AddWithValue(
            "warehouseRevision",
            character.WarehouseRevision);
        command.Parameters.AddWithValue(
            "inventoryRevision",
            character.InventoryRevision);
        command.Parameters.AddWithValue(
            "retentionPolicy",
            WarehouseExpansionPersistenceCodec.RetentionPolicy);
        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        return scalar is long auditId && auditId > 0
            ? auditId
            : throw new InvalidDataException(
                "Warehouse expansion audit returned no identity.");
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
        WarehouseExpansionResultStatus status,
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
            WarehouseExpansionPersistenceCodec.ContractVersion);
        command.Parameters.AddWithValue(
            "resultCode",
            WarehouseExpansionPersistenceCodec.ResultCode(status));
        command.Parameters.Add("resultPayload", NpgsqlDbType.Jsonb).Value =
            Encoding.UTF8.GetString(payload);
        command.Parameters.Add("resultHash", NpgsqlDbType.Bytea).Value =
            resultHash;
        command.Parameters.AddWithValue("auditId", auditId);
        command.Parameters.AddWithValue(
            "retentionPolicy",
            WarehouseExpansionPersistenceCodec.RetentionPolicy);
        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        return scalar is long inboxId && inboxId > 0
            ? inboxId
            : throw new InvalidDataException(
                "Warehouse expansion inbox returned no identity.");
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
            "Warehouse expansion replay evidence was not exact.",
            cancellationToken);
    }

    private static WarehouseExpansionExecutionReceipt ValidateStored(
        StoredInbox stored,
        CommandSubject subject)
    {
        if (stored.ContractVersion !=
            WarehouseExpansionPersistenceCodec.ContractVersion)
        {
            throw new InvalidDataException(
                "Stored warehouse expansion contract is unsupported.");
        }
        var receipt = WarehouseExpansionPersistenceCodec.DecodeAndVerify(
            stored.ResultPayload,
            stored.ResultHash);
        if (receipt.CharacterId != subject.CharacterId ||
            !string.Equals(
                receipt.AuditReference,
                stored.AuditId.ToString(CultureInfo.InvariantCulture),
                StringComparison.Ordinal) ||
            !string.Equals(
                stored.ResultCode,
                WarehouseExpansionPersistenceCodec.ResultCode(receipt.Status),
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Stored warehouse expansion identity is invalid.");
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
            WarehouseExpansionPersistenceCodec.PrincipalType);
        command.Parameters.AddWithValue("principalKey", principalKey);
        command.Parameters.AddWithValue(
            "aggregateType",
            WarehouseExpansionPersistenceCodec.WarehouseAggregateType);
        command.Parameters.AddWithValue("aggregateKey", aggregateKey);
        command.Parameters.AddWithValue(
            "commandFamily",
            WarehouseExpansionPersistenceCodec.CommandFamilyCode);
        command.Parameters.Add("operationId", NpgsqlDbType.Bytea).Value =
            operationId;
    }
}
