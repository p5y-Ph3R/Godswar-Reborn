using System.Globalization;
using System.Text;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Inventory;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.Infrastructure.Inventory;

internal sealed partial class
    PostgresGearMentorMaterialConversionCommandExecutor
{
    private async Task<LockedClassSuitCharacter?>
        LockClassSuitCharacterAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            CommandSubject subject,
            CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            SELECT profession, fighter_job_lv, inventory_revision
            FROM public.character_base
            WHERE account_id = @accountId
              AND id = @characterId
            FOR UPDATE;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("accountId", subject.AccountId);
        command.Parameters.AddWithValue("characterId", subject.CharacterId);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken) ||
            reader.GetInt64(2) < 0 ||
            reader.GetInt16(0) is < 0 or > 3)
        {
            return null;
        }

        return new LockedClassSuitCharacter(
            checked((byte)reader.GetInt16(0)),
            reader.GetInt32(1),
            reader.GetInt64(2));
    }

    private async Task<ClassSuitStoredInbox?> ReadClassSuitInboxAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CommandFamily family,
        string principalKey,
        string aggregateKey,
        byte[] operationId,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            SELECT
                id, request_hash, result_contract_version,
                result_code, result_payload::text, result_hash, audit_id
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
        AddClassSuitIdentityParameters(
            command,
            family,
            principalKey,
            aggregateKey,
            operationId);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }
        if (reader.GetInt16(2) != ClassSuitPersistenceCodec.ContractVersion)
        {
            throw new InvalidDataException(
                "The stored Class Suit result contract is unsupported.");
        }

        return new ClassSuitStoredInbox(
            reader.GetInt64(0),
            reader.GetFieldValue<byte[]>(1),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetFieldValue<byte[]>(5),
            reader.GetInt64(6));
    }

    private async Task<ClassSuitExecutionReceipt>
        PersistClassSuitTerminalAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            ClassSuitCommandContext context,
            long inventoryRevision,
            ClassSuitCommandResultStatus status,
            string principalKey,
            string aggregateKey,
            byte[] operationId,
            byte[] requestHash,
            CancellationToken cancellationToken)
    {
        var auditId = await InsertClassSuitAuditAsync(
            connection,
            transaction,
            context,
            status,
            principalKey,
            aggregateKey,
            operationId,
            requestHash,
            cancellationToken);
        var receipt = new ClassSuitExecutionReceipt(
            context.Family,
            context.Subject.CharacterId,
            context.Command.Operation,
            context.Command.NpcId,
            context.Command.DialogIndex,
            context.ReplayIntent,
            status,
            ClassSuitNativeResults.Resolve(context.Command.Operation, status),
            [],
            inventoryRevision,
            auditId.ToString(CultureInfo.InvariantCulture),
            null);
        var payload = ClassSuitPersistenceCodec.Encode(receipt);
        await InsertClassSuitInboxAsync(
            connection,
            transaction,
            context.Family,
            status,
            principalKey,
            aggregateKey,
            operationId,
            requestHash,
            auditId,
            payload,
            cancellationToken);
        return receipt;
    }

    private async Task<long> InsertClassSuitAuditAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ClassSuitCommandContext context,
        ClassSuitCommandResultStatus status,
        string principalKey,
        string aggregateKey,
        byte[] operationId,
        byte[] requestHash,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            INSERT INTO public.command_audit (
                principal_type, principal_key,
                aggregate_type, aggregate_key,
                command_family, operation_id, request_hash,
                outcome_code, detail_payload, retention_policy
            )
            VALUES (
                @principalType, @principalKey,
                @aggregateType, @aggregateKey,
                @commandFamily, @operationId, @requestHash,
                @outcomeCode,
                jsonb_build_object(
                    'operation', @operation,
                    'npcId', @npcId,
                    'gearSlot', @gearSlot,
                    'primaryMaterialSlot', @primaryMaterialSlot,
                    'secondaryMaterialSlot', @secondaryMaterialSlot,
                    'status', @status),
                @retentionPolicy
            )
            RETURNING id;
            """,
            connection,
            transaction);
        AddClassSuitIdentityParameters(
            command,
            context.Family,
            principalKey,
            aggregateKey,
            operationId);
        command.Parameters.Add(
            "requestHash",
            NpgsqlDbType.Bytea).Value = requestHash;
        command.Parameters.AddWithValue(
            "outcomeCode",
            status == ClassSuitCommandResultStatus.Succeeded
                ? "committed"
                : status.ToString().ToLowerInvariant());
        command.Parameters.AddWithValue(
            "operation",
            context.Command.Operation.ToString());
        command.Parameters.AddWithValue("npcId", context.Command.NpcId);
        command.Parameters.AddWithValue(
            "gearSlot",
            context.Command.Gear.KitBagSlot);
        command.Parameters.AddWithValue(
            "primaryMaterialSlot",
            context.Command.PrimaryMaterial?.KitBagSlot ?? -1);
        command.Parameters.AddWithValue(
            "secondaryMaterialSlot",
            context.Command.SecondaryMaterial?.KitBagSlot ?? -1);
        command.Parameters.AddWithValue("status", status.ToString());
        command.Parameters.AddWithValue(
            "retentionPolicy",
            ClassSuitPersistenceCodec.RetentionPolicy);
        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        return scalar is long value && value > 0
            ? value
            : throw new InvalidDataException(
                "The Class Suit audit insert returned no identity.");
    }

    private async Task<long> InsertClassSuitInboxAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CommandFamily family,
        ClassSuitCommandResultStatus status,
        string principalKey,
        string aggregateKey,
        byte[] operationId,
        byte[] requestHash,
        long auditId,
        byte[] payload,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            INSERT INTO public.command_inbox (
                principal_type, principal_key,
                aggregate_type, aggregate_key,
                command_family, operation_id, request_hash,
                result_contract_version, result_code,
                result_payload, result_hash, audit_id, retention_policy
            )
            VALUES (
                @principalType, @principalKey,
                @aggregateType, @aggregateKey,
                @commandFamily, @operationId, @requestHash,
                @contractVersion, @resultCode,
                @resultPayload, @resultHash, @auditId, @retentionPolicy
            )
            RETURNING id;
            """,
            connection,
            transaction);
        AddClassSuitIdentityParameters(
            command,
            family,
            principalKey,
            aggregateKey,
            operationId);
        command.Parameters.Add(
            "requestHash",
            NpgsqlDbType.Bytea).Value = requestHash;
        command.Parameters.AddWithValue(
            "contractVersion",
            ClassSuitPersistenceCodec.ContractVersion);
        command.Parameters.AddWithValue(
            "resultCode",
            ClassSuitPersistenceCodec.ResultCode(status));
        command.Parameters.Add(
            "resultPayload",
            NpgsqlDbType.Jsonb).Value = Encoding.UTF8.GetString(payload);
        command.Parameters.Add(
            "resultHash",
            NpgsqlDbType.Bytea).Value =
            ClassSuitPersistenceCodec.Hash(payload);
        command.Parameters.AddWithValue("auditId", auditId);
        command.Parameters.AddWithValue(
            "retentionPolicy",
            ClassSuitPersistenceCodec.RetentionPolicy);
        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        return scalar is long value && value > 0
            ? value
            : throw new InvalidDataException(
                "The Class Suit inbox insert returned no identity.");
    }

    private static void AddClassSuitIdentityParameters(
        NpgsqlCommand command,
        CommandFamily family,
        string principalKey,
        string aggregateKey,
        byte[] operationId)
    {
        command.Parameters.AddWithValue(
            "principalType",
            ClassSuitPersistenceCodec.PrincipalType);
        command.Parameters.AddWithValue("principalKey", principalKey);
        command.Parameters.AddWithValue(
            "aggregateType",
            ClassSuitPersistenceCodec.AggregateType);
        command.Parameters.AddWithValue("aggregateKey", aggregateKey);
        command.Parameters.AddWithValue(
            "commandFamily",
            ClassSuitPersistenceCodec.FamilyCode(family));
        command.Parameters.Add(
            "operationId",
            NpgsqlDbType.Bytea).Value = operationId;
    }
}
