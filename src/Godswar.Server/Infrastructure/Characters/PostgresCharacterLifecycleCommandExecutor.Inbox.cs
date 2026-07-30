using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Commands;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.Infrastructure.Characters;

internal sealed partial class PostgresCharacterLifecycleCommandExecutor
{
    private async Task<StoredInbox?> ReadInboxAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int accountId,
        CommandFamily family,
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
              AND operation_id = @operationId
            FOR UPDATE;
            """,
            connection,
            transaction);
        AddIdentityParameters(
            command,
            accountId,
            family,
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

    private async Task RecordDuplicateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long inboxId,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            UPDATE public.command_inbox
            SET duplicate_count = LEAST(duplicate_count + 1, 1000000),
                last_duplicate_at = transaction_timestamp()
            WHERE id = @inboxId;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("inboxId", inboxId);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidDataException(
                "The lifecycle duplicate update was not exact.");
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
                last_request_conflict_at = transaction_timestamp()
            WHERE id = @inboxId;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("inboxId", inboxId);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidDataException(
                "The lifecycle conflict update was not exact.");
        }
    }

    private static CharacterLifecycleReceipt ValidateStoredReceipt(
        StoredInbox stored,
        CommandFamily family,
        int accountId)
    {
        if (stored.ResultContractVersion !=
            CharacterLifecyclePersistenceCodec.ContractVersion)
        {
            throw new InvalidDataException(
                "The lifecycle result contract is unsupported.");
        }

        return CharacterLifecyclePersistenceCodec.DecodeAndVerify(
            stored.ResultPayload,
            stored.ResultHash,
            stored.ResultCode,
            stored.AuditId,
            family,
            accountId,
            CharacterLifecycleCommandContract.SingleCharacterSlot);
    }

    private static void AddIdentityParameters(
        NpgsqlCommand command,
        int accountId,
        CommandFamily family,
        byte[] operationId)
    {
        command.Parameters.AddWithValue(
            "principalType",
            CharacterLifecyclePersistenceCodec.PrincipalType);
        command.Parameters.AddWithValue(
            "principalKey",
            accountId.ToString(
                System.Globalization.CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue(
            "aggregateType",
            CharacterLifecyclePersistenceCodec.AggregateType);
        command.Parameters.AddWithValue(
            "aggregateKey",
            CharacterLifecyclePersistenceCodec.AggregateKey(
                accountId,
                CharacterLifecycleCommandContract.SingleCharacterSlot));
        command.Parameters.AddWithValue(
            "commandFamily",
            CharacterLifecyclePersistenceCodec.FamilyCode(family));
        command.Parameters.Add(
            "operationId",
            NpgsqlDbType.Bytea).Value = operationId;
    }
}
