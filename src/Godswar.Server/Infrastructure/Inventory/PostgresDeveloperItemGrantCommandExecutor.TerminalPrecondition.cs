using System.Text;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Inventory;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.Infrastructure.Inventory;

internal sealed partial class PostgresDeveloperItemGrantCommandExecutor
{
    private async Task InsertInsufficientCapacityResultAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CommandEnvelope<DeveloperItemGrantCommand> envelope,
        string principalKey,
        string aggregateKey,
        byte[] operationId,
        byte[] requestHash,
        CancellationToken cancellationToken)
    {
        var auditId = await InsertCapacityAuditAsync(
            connection,
            transaction,
            envelope,
            principalKey,
            aggregateKey,
            operationId,
            requestHash,
            cancellationToken);
        var payload =
            DeveloperItemGrantPersistenceCodec
                .EncodeInsufficientCapacity(
                    envelope.Subject.CharacterId,
                    envelope.Command.ItemId,
                    envelope.Command.Quantity,
                    auditId);
        var resultHash =
            DeveloperItemGrantPersistenceCodec.Hash(payload);
        await InsertCapacityInboxAsync(
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
    }

    private async Task<long> InsertCapacityAuditAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CommandEnvelope<DeveloperItemGrantCommand> envelope,
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
                    'itemId', @itemId,
                    'quantity', @quantity,
                    'reasonCode', @reasonCode),
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
            DeveloperItemGrantPersistenceCodec
                .PreconditionFailedResultCode);
        command.Parameters.AddWithValue(
            "itemId",
            checked((int)envelope.Command.ItemId));
        command.Parameters.AddWithValue(
            "quantity",
            envelope.Command.Quantity);
        command.Parameters.AddWithValue(
            "reasonCode",
            DeveloperItemGrantPersistenceCodec
                .InsufficientCapacityReasonCode);
        command.Parameters.AddWithValue(
            "retentionPolicy",
            DeveloperItemGrantPersistenceCodec.RetentionPolicy);
        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        return scalar is long auditId && auditId > 0
            ? auditId
            : throw new InvalidDataException(
                "The terminal grant audit returned no identity.");
    }

    private async Task InsertCapacityInboxAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
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
            );
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
            DeveloperItemGrantPersistenceCodec.ContractVersion);
        command.Parameters.AddWithValue(
            "resultCode",
            DeveloperItemGrantPersistenceCodec
                .PreconditionFailedResultCode);
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
            DeveloperItemGrantPersistenceCodec.RetentionPolicy);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidDataException(
                "The terminal grant inbox insert was not exact.");
        }
    }
}
