using System.Globalization;
using System.Text;
using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Commands;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.Infrastructure.Characters;

internal sealed partial class PostgresCharacterLifecycleCommandExecutor
{
    private async Task<CharacterLifecycleExecutionResult>
        PersistTransitionAsync<T>(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            CommandEnvelope<T> envelope,
            LifecycleTransition transition,
            byte[] operationId,
            byte[] requestHash,
            CancellationToken cancellationToken)
    {
        var eventId = transition.Succeeded
            ? Guid.NewGuid()
            : (Guid?)null;
        var auditId = await InsertAuditAsync(
            connection,
            transaction,
            envelope,
            transition,
            operationId,
            requestHash,
            cancellationToken);
        var receipt = new CharacterLifecycleReceipt(
            envelope.Family,
            transition.Status,
            envelope.Subject.AccountId,
            CharacterLifecycleCommandContract.SingleCharacterSlot,
            transition.CharacterId,
            transition.LifecycleVersion,
            transition.CharacterName,
            transition.RestoreUntil,
            transition.PurgeAfter,
            auditId.ToString(CultureInfo.InvariantCulture),
            eventId);
        var payload =
            CharacterLifecyclePersistenceCodec.Encode(receipt);
        var inboxId = await InsertInboxAsync(
            connection,
            transaction,
            envelope.Subject.AccountId,
            envelope.Family,
            operationId,
            requestHash,
            receipt,
            payload,
            auditId,
            cancellationToken);
        if (transition.Succeeded)
        {
            await InsertOutboxAsync(
                connection,
                transaction,
                envelope.Subject.AccountId,
                envelope.Family,
                inboxId,
                transition.LifecycleVersion,
                eventId!.Value,
                payload,
                cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return transition.Succeeded
            ? CharacterLifecycleExecutionResult.Committed(receipt)
            : CharacterLifecycleExecutionResult
                .TerminalRejected(receipt);
    }

    private async Task<long> InsertAuditAsync<T>(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CommandEnvelope<T> envelope,
        LifecycleTransition transition,
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
                    'status', @status,
                    'characterSlot', @characterSlot,
                    'characterId', @characterId,
                    'lifecycleVersion', @lifecycleVersion,
                    'characterName', @characterName,
                    'restoreUntil', @restoreUntil,
                    'purgeAfter', @purgeAfter
                ),
                @retentionPolicy
            )
            RETURNING id;
            """,
            connection,
            transaction);
        AddIdentityParameters(
            command,
            envelope.Subject.AccountId,
            envelope.Family,
            operationId);
        command.Parameters.Add(
            "requestHash",
            NpgsqlDbType.Bytea).Value = requestHash;
        command.Parameters.AddWithValue(
            "outcomeCode",
            transition.Succeeded
                ? CharacterLifecyclePersistenceCodec.CommittedResultCode
                : CharacterLifecyclePersistenceCodec
                    .TerminalRejectedResultCode);
        command.Parameters.AddWithValue(
            "status",
            checked((short)transition.Status));
        command.Parameters.AddWithValue(
            "characterSlot",
            CharacterLifecycleCommandContract.SingleCharacterSlot);
        command.Parameters.AddWithValue(
            "characterId",
            transition.CharacterId);
        command.Parameters.AddWithValue(
            "lifecycleVersion",
            transition.LifecycleVersion);
        command.Parameters.AddWithValue(
            "characterName",
            transition.CharacterName);
        AddOptionalTimestamp(
            command,
            "restoreUntil",
            transition.RestoreUntil);
        AddOptionalTimestamp(
            command,
            "purgeAfter",
            transition.PurgeAfter);
        command.Parameters.AddWithValue(
            "retentionPolicy",
            CharacterLifecyclePersistenceCodec.RetentionPolicy);
        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        return scalar is long auditId && auditId > 0
            ? auditId
            : throw new InvalidDataException(
                "The character lifecycle audit returned no identity.");
    }

    private async Task<long> InsertInboxAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int accountId,
        CommandFamily family,
        byte[] operationId,
        byte[] requestHash,
        CharacterLifecycleReceipt receipt,
        byte[] payload,
        long auditId,
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
                @contractVersion,
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
            accountId,
            family,
            operationId);
        command.Parameters.Add(
            "requestHash",
            NpgsqlDbType.Bytea).Value = requestHash;
        command.Parameters.AddWithValue(
            "contractVersion",
            CharacterLifecyclePersistenceCodec.ContractVersion);
        command.Parameters.AddWithValue(
            "resultCode",
            CharacterLifecyclePersistenceCodec.ResultCode(receipt));
        command.Parameters.Add(
            "resultPayload",
            NpgsqlDbType.Jsonb).Value =
            Encoding.UTF8.GetString(payload);
        command.Parameters.Add(
            "resultHash",
            NpgsqlDbType.Bytea).Value =
            CharacterLifecyclePersistenceCodec.Hash(payload);
        command.Parameters.AddWithValue("auditId", auditId);
        command.Parameters.AddWithValue(
            "retentionPolicy",
            CharacterLifecyclePersistenceCodec.RetentionPolicy);
        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        return scalar is long inboxId && inboxId > 0
            ? inboxId
            : throw new InvalidDataException(
                "The character lifecycle inbox returned no identity.");
    }

    private async Task InsertOutboxAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int accountId,
        CommandFamily family,
        long inboxId,
        long aggregateVersion,
        Guid eventId,
        byte[] payload,
        CancellationToken cancellationToken)
    {
        await EnsureInitialOutboxPositionAsync(
            connection,
            transaction,
            accountId,
            aggregateVersion,
            cancellationToken);
        await using var command = CreateCommand(
            """
            INSERT INTO public.outbox_events (
                event_id,
                command_inbox_id,
                consumer_key,
                aggregate_type,
                aggregate_key,
                aggregate_version,
                event_type,
                contract_version,
                ordering_policy,
                payload,
                max_attempts
            )
            VALUES (
                @eventId,
                @inboxId,
                @consumerKey,
                @aggregateType,
                @aggregateKey,
                @aggregateVersion,
                @eventType,
                @contractVersion,
                @orderingPolicy,
                @payload,
                @maxAttempts
            );
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("eventId", eventId);
        command.Parameters.AddWithValue("inboxId", inboxId);
        command.Parameters.AddWithValue(
            "consumerKey",
            CharacterLifecyclePersistenceCodec.ConsumerKey);
        command.Parameters.AddWithValue(
            "aggregateType",
            CharacterLifecyclePersistenceCodec.AggregateType);
        command.Parameters.AddWithValue(
            "aggregateKey",
            CharacterLifecyclePersistenceCodec.AggregateKey(
                accountId,
                CharacterLifecycleCommandContract.SingleCharacterSlot));
        command.Parameters.AddWithValue(
            "aggregateVersion",
            aggregateVersion);
        command.Parameters.AddWithValue(
            "eventType",
            CharacterLifecyclePersistenceCodec.EventType(family));
        command.Parameters.AddWithValue(
            "contractVersion",
            CharacterLifecyclePersistenceCodec.ContractVersion);
        command.Parameters.AddWithValue(
            "orderingPolicy",
            CharacterLifecyclePersistenceCodec.OrderingPolicy);
        command.Parameters.Add(
            "payload",
            NpgsqlDbType.Jsonb).Value =
            Encoding.UTF8.GetString(payload);
        command.Parameters.AddWithValue(
            "maxAttempts",
            _maximumOutboxAttempts);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidDataException(
                "The character lifecycle outbox insert was not exact.");
        }
    }

    private async Task EnsureInitialOutboxPositionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int accountId,
        long aggregateVersion,
        CancellationToken cancellationToken)
    {
        if (aggregateVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(aggregateVersion));
        }

        await using var command = CreateCommand(
            """
            INSERT INTO public.outbox_consumer_positions (
                consumer_key,
                aggregate_type,
                aggregate_key,
                ordering_policy,
                current_version
            )
            SELECT
                @consumerKey,
                @aggregateType,
                @aggregateKey,
                @orderingPolicy,
                @currentVersion
            WHERE NOT EXISTS (
                SELECT 1
                FROM public.outbox_consumer_positions existing
                WHERE existing.consumer_key = @consumerKey
                  AND existing.aggregate_type = @aggregateType
                  AND existing.aggregate_key = @aggregateKey
            )
            ON CONFLICT (
                consumer_key,
                aggregate_type,
                aggregate_key
            ) DO NOTHING;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue(
            "consumerKey",
            CharacterLifecyclePersistenceCodec.ConsumerKey);
        command.Parameters.AddWithValue(
            "aggregateType",
            CharacterLifecyclePersistenceCodec.AggregateType);
        command.Parameters.AddWithValue(
            "aggregateKey",
            CharacterLifecyclePersistenceCodec.AggregateKey(
                accountId,
                CharacterLifecycleCommandContract.SingleCharacterSlot));
        command.Parameters.AddWithValue(
            "orderingPolicy",
            CharacterLifecyclePersistenceCodec.OrderingPolicy);
        command.Parameters.AddWithValue(
            "currentVersion",
            aggregateVersion - 1);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddOptionalTimestamp(
        NpgsqlCommand command,
        string name,
        DateTimeOffset? value)
    {
        command.Parameters.Add(
            name,
            NpgsqlDbType.TimestampTz).Value =
            value?.UtcDateTime ?? (object)DBNull.Value;
    }
}
