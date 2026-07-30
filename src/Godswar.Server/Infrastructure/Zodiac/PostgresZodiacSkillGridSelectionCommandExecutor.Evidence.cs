using System.Text;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Zodiac;
using Godswar.Server.State;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.Infrastructure.Zodiac;

internal sealed partial class
    PostgresZodiacSkillGridSelectionCommandExecutor
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
            SELECT id, request_hash, result_contract_version,
                   result_code, result_payload::text, result_hash,
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
        AddIdentity(
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

    private async Task<long> InsertAuditAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CommandEnvelope<ZodiacSkillGridSelectionCommand> envelope,
        ZodiacSkillGridSelectionResult selection,
        string principalKey,
        string aggregateKey,
        byte[] operationId,
        byte[] requestHash,
        string resultCode,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            INSERT INTO public.command_audit (
                principal_type, principal_key, aggregate_type,
                aggregate_key, command_family, operation_id,
                request_hash, outcome_code, detail_payload,
                retention_policy
            )
            VALUES (
                @principalType, @principalKey, @aggregateType,
                @aggregateKey, @commandFamily, @operationId,
                @requestHash, @resultCode,
                jsonb_build_object(
                    'gridIndex', @gridIndex,
                    'status', @status,
                    'currentLevel', @currentLevel,
                    'previousSkillKind', @previousSkillKind,
                    'selectedSkillKind', @selectedSkillKind
                ),
                @retentionPolicy
            )
            RETURNING id;
            """,
            connection,
            transaction);
        AddIdentity(
            command,
            principalKey,
            aggregateKey,
            operationId);
        command.Parameters.Add(
            "requestHash",
            NpgsqlDbType.Bytea).Value = requestHash;
        command.Parameters.AddWithValue("resultCode", resultCode);
        command.Parameters.AddWithValue(
            "gridIndex",
            selection.GridIndex);
        command.Parameters.AddWithValue(
            "status",
            checked((short)selection.Status));
        command.Parameters.AddWithValue(
            "currentLevel",
            checked((short)selection.CurrentLevel));
        command.Parameters.AddWithValue(
            "previousSkillKind",
            selection.PreviousSkillKind);
        command.Parameters.AddWithValue(
            "selectedSkillKind",
            selection.SelectedSkillKind);
        command.Parameters.AddWithValue(
            "retentionPolicy",
            ZodiacSkillGridSelectionPersistenceCodec.RetentionPolicy);
        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        return scalar is long id && id > 0
            ? id
            : throw new InvalidDataException(
                "The Zodiac selection audit returned no identity.");
    }

    private async Task<long> InsertInboxAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string principalKey,
        string aggregateKey,
        byte[] operationId,
        byte[] requestHash,
        string resultCode,
        byte[] resultHash,
        long auditId,
        byte[] payload,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            INSERT INTO public.command_inbox (
                principal_type, principal_key, aggregate_type,
                aggregate_key, command_family, operation_id,
                request_hash, result_contract_version, result_code,
                result_payload, result_hash, audit_id,
                retention_policy
            )
            VALUES (
                @principalType, @principalKey, @aggregateType,
                @aggregateKey, @commandFamily, @operationId,
                @requestHash, @contractVersion, @resultCode,
                @resultPayload, @resultHash, @auditId,
                @retentionPolicy
            )
            RETURNING id;
            """,
            connection,
            transaction);
        AddIdentity(
            command,
            principalKey,
            aggregateKey,
            operationId);
        command.Parameters.Add(
            "requestHash",
            NpgsqlDbType.Bytea).Value = requestHash;
        command.Parameters.AddWithValue(
            "contractVersion",
            ZodiacSkillGridSelectionPersistenceCodec.ContractVersion);
        command.Parameters.AddWithValue("resultCode", resultCode);
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
            ZodiacSkillGridSelectionPersistenceCodec.RetentionPolicy);
        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        return scalar is long id && id > 0
            ? id
            : throw new InvalidDataException(
                "The Zodiac selection inbox returned no identity.");
    }

    private async Task InsertOutboxAsync(
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
                event_id, command_inbox_id, consumer_key,
                aggregate_type, aggregate_key, aggregate_version,
                event_type, contract_version, ordering_policy,
                payload, max_attempts
            )
            VALUES (
                @eventId, @inboxId, @consumerKey,
                @aggregateType, @aggregateKey, @revision,
                @eventType, @contractVersion, @orderingPolicy,
                @payload, @maxAttempts
            );
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("eventId", eventId);
        command.Parameters.AddWithValue("inboxId", inboxId);
        command.Parameters.AddWithValue(
            "consumerKey",
            ZodiacSkillGridSelectionPersistenceCodec.ConsumerKey);
        command.Parameters.AddWithValue(
            "aggregateType",
            ZodiacSkillGridSelectionPersistenceCodec.AggregateType);
        command.Parameters.AddWithValue("aggregateKey", aggregateKey);
        command.Parameters.AddWithValue("revision", revision);
        command.Parameters.AddWithValue(
            "eventType",
            ZodiacSkillGridSelectionPersistenceCodec.EventType);
        command.Parameters.AddWithValue(
            "contractVersion",
            ZodiacSkillGridSelectionPersistenceCodec.ContractVersion);
        command.Parameters.AddWithValue(
            "orderingPolicy",
            ZodiacSkillGridSelectionPersistenceCodec.OrderingPolicy);
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
                "The Zodiac selection outbox insert was not exact.");
        }
    }

    private async Task RecordDuplicateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long inboxId,
        CancellationToken cancellationToken) =>
        await UpdateInboxCounterAsync(
            connection,
            transaction,
            inboxId,
            conflict: false,
            cancellationToken);

    private async Task RecordRequestConflictAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long inboxId,
        CancellationToken cancellationToken) =>
        await UpdateInboxCounterAsync(
            connection,
            transaction,
            inboxId,
            conflict: true,
            cancellationToken);

    private async Task UpdateInboxCounterAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long inboxId,
        bool conflict,
        CancellationToken cancellationToken)
    {
        var sql = conflict
            ? """
              UPDATE public.command_inbox
              SET request_conflict_count =
                      LEAST(request_conflict_count + 1, 1000000),
                  last_request_conflict_at = now()
              WHERE id = @inboxId;
              """
            : """
              UPDATE public.command_inbox
              SET duplicate_count =
                      LEAST(duplicate_count + 1, 1000000),
                  last_duplicate_at = now()
              WHERE id = @inboxId;
              """;
        await using var command =
            CreateCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("inboxId", inboxId);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidDataException(
                "The Zodiac selection inbox update was not exact.");
        }
    }

    private static ZodiacSkillGridSelectionExecutionReceipt
        ValidateStoredResult(
            StoredInbox stored,
            int characterId,
            int gridIndex)
    {
        if (stored.ResultContractVersion !=
            ZodiacSkillGridSelectionPersistenceCodec.ContractVersion)
        {
            throw new InvalidDataException(
                "The stored Zodiac selection contract is unsupported.");
        }

        var receipt =
            ZodiacSkillGridSelectionPersistenceCodec.DecodeAndVerify(
                stored.ResultPayload,
                stored.ResultHash,
                stored.ResultCode,
                stored.AuditId);
        return receipt.CharacterId == characterId &&
            receipt.GridIndex == gridIndex
            ? receipt
            : throw new InvalidDataException(
                "The stored Zodiac selection scope is invalid.");
    }

    private static void AddIdentity(
        NpgsqlCommand command,
        string principalKey,
        string aggregateKey,
        byte[] operationId)
    {
        command.Parameters.AddWithValue(
            "principalType",
            ZodiacSkillGridSelectionPersistenceCodec.PrincipalType);
        command.Parameters.AddWithValue("principalKey", principalKey);
        command.Parameters.AddWithValue(
            "aggregateType",
            ZodiacSkillGridSelectionPersistenceCodec.AggregateType);
        command.Parameters.AddWithValue("aggregateKey", aggregateKey);
        command.Parameters.AddWithValue(
            "commandFamily",
            ZodiacSkillGridSelectionPersistenceCodec.CommandFamily);
        command.Parameters.Add(
            "operationId",
            NpgsqlDbType.Bytea).Value = operationId;
    }
}
