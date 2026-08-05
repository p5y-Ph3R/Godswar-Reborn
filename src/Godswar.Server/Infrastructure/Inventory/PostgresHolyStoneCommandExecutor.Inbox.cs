using System.Globalization;
using System.Text;
using Godswar.Server.Application.Inventory;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.Infrastructure.Inventory;

internal sealed partial class PostgresHolyStoneCommandExecutor
{
    private async Task<StoredInbox?> ReadInboxAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        HolyStoneCommandOperation operation,
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
            operation,
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

    private async Task<long> InsertAuditAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        HolyStoneCommandContext context,
        LockedCharacter character,
        LockedCommandItems locked,
        HolyStonePlan plan,
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
                    'operation', @operation,
                    'npcId', @npcId,
                    'dialogIndex', @dialogIndex,
                    'targetLocation', @targetLocation,
                    'targetSlot', @targetSlot,
                    'targetItemInstanceId', @targetItemInstanceId,
                    'stoneSlot', @stoneSlot,
                    'stoneItemInstanceId', @stoneItemInstanceId,
                    'catalystSlot', @catalystSlot,
                    'catalystItemInstanceId', @catalystItemInstanceId,
                    'thirdMaterialSlot', @thirdMaterialSlot,
                    'thirdMaterialItemInstanceId',
                        @thirdMaterialItemInstanceId,
                    'socketIndex', @socketIndex,
                    'upgradeRoll', @upgradeRoll,
                    'upgradeSuccessRate', @upgradeSuccessRate,
                    'removedEffectId', @removedEffectId,
                    'removedLevel', @removedLevel,
                    'outputSlot', @outputSlot,
                    'goldSpent', @goldSpent,
                    'goldBefore', @goldBefore,
                    'goldAfter', @goldAfter,
                    'walletRevisionBefore', @walletRevisionBefore,
                    'walletRevisionAfter', @walletRevisionAfter,
                    'status', @status),
                @retentionPolicy
            )
            RETURNING id;
            """,
            connection,
            transaction);
        AddIdentityParameters(
            command,
            context.Command.Operation,
            principalKey,
            aggregateKey,
            operationId);
        command.Parameters.Add(
            "requestHash",
            NpgsqlDbType.Bytea).Value = requestHash;
        command.Parameters.AddWithValue(
            "outcomeCode",
            AuditOutcomeCode(plan.Status));
        command.Parameters.AddWithValue(
            "operation",
            context.Command.Operation.ToString());
        command.Parameters.AddWithValue("npcId", context.Command.NpcId);
        command.Parameters.AddWithValue(
            "dialogIndex",
            context.Command.DialogIndex);
        command.Parameters.AddWithValue(
            "targetLocation",
            context.Command.TargetLocation.ToString());
        command.Parameters.AddWithValue(
            "targetSlot",
            context.Command.TargetSlot);
        AddNullableBigint(
            command,
            "targetItemInstanceId",
            locked.Target?.ItemInstanceId);
        command.Parameters.AddWithValue(
            "stoneSlot",
            context.Command.StoneKitBagSlot);
        AddNullableBigint(
            command,
            "stoneItemInstanceId",
            locked.Stone?.ItemInstanceId);
        command.Parameters.AddWithValue(
            "catalystSlot",
            context.Command.CatalystKitBagSlot);
        AddNullableBigint(
            command,
            "catalystItemInstanceId",
            locked.Catalyst?.ItemInstanceId);
        command.Parameters.AddWithValue(
            "thirdMaterialSlot",
            context.Command.ThirdMaterialKitBagSlot);
        AddNullableBigint(
            command,
            "thirdMaterialItemInstanceId",
            locked.ThirdMaterial?.ItemInstanceId);
        command.Parameters.AddWithValue(
            "socketIndex",
            plan.SocketIndex);
        AddNullableInteger(command, "upgradeRoll", plan.UpgradeRoll);
        AddNullableInteger(
            command,
            "upgradeSuccessRate",
            plan.UpgradeSuccessRate);
        AddNullableSmallint(
            command,
            "removedEffectId",
            plan.RemovedEffectId);
        AddNullableSmallint(
            command,
            "removedLevel",
            plan.RemovedLevel);
        command.Parameters.AddWithValue(
            "outputSlot",
            plan.OutputKitBagSlot);
        command.Parameters.AddWithValue("goldSpent", plan.GoldSpent);
        command.Parameters.AddWithValue("goldBefore", character.Gold);
        command.Parameters.AddWithValue(
            "goldAfter",
            checked(character.Gold - plan.GoldSpent));
        command.Parameters.AddWithValue(
            "walletRevisionBefore",
            character.WalletRevision);
        command.Parameters.AddWithValue(
            "walletRevisionAfter",
            plan.GoldSpent > 0
                ? checked(character.WalletRevision + 1)
                : character.WalletRevision);
        command.Parameters.AddWithValue(
            "status",
            plan.Status.ToString());
        command.Parameters.AddWithValue(
            "retentionPolicy",
            HolyStonePersistenceCodec.RetentionPolicy);
        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        return scalar is long auditId && auditId > 0
            ? auditId
            : throw new InvalidDataException(
                "The Holy Stone audit insert returned no identity.");
    }

    private async Task<long> InsertInboxAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        HolyStoneCommandOperation operation,
        HolyStoneCommandResultStatus status,
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
            operation,
            principalKey,
            aggregateKey,
            operationId);
        command.Parameters.Add(
            "requestHash",
            NpgsqlDbType.Bytea).Value = requestHash;
        command.Parameters.AddWithValue(
            "resultContractVersion",
            HolyStonePersistenceCodec.ContractVersion);
        command.Parameters.AddWithValue(
            "resultCode",
            HolyStonePersistenceCodec.ResultCode(status));
        command.Parameters.Add(
            "resultPayload",
            NpgsqlDbType.Jsonb).Value =
            Encoding.UTF8.GetString(payload);
        command.Parameters.Add(
            "resultHash",
            NpgsqlDbType.Bytea).Value =
            HolyStonePersistenceCodec.Hash(payload);
        command.Parameters.AddWithValue("auditId", auditId);
        command.Parameters.AddWithValue(
            "retentionPolicy",
            HolyStonePersistenceCodec.RetentionPolicy);
        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        return scalar is long inboxId && inboxId > 0
            ? inboxId
            : throw new InvalidDataException(
                "The Holy Stone inbox insert returned no identity.");
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
                "The Holy Stone duplicate update was not exact.");
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
                "The Holy Stone conflict update was not exact.");
        }
    }

    private static HolyStoneExecutionReceipt ValidateStoredResult(
        StoredInbox stored,
        Godswar.Server.Application.Commands.CommandSubject subject,
        HolyStoneCommandOperation operation)
    {
        if (stored.ResultContractVersion !=
            HolyStonePersistenceCodec.ContractVersion)
        {
            throw new InvalidDataException(
                "The stored Holy Stone result contract is unsupported.");
        }
        var receipt = HolyStonePersistenceCodec.DecodeAndVerify(
            stored.ResultPayload,
            stored.ResultHash,
            stored.ResultCode,
            stored.AuditId,
            operation);
        if (receipt.CharacterId != subject.CharacterId)
        {
            throw new InvalidDataException(
                "The stored Holy Stone character identity is invalid.");
        }
        return receipt;
    }

    private static string AuditOutcomeCode(
        HolyStoneCommandResultStatus status) =>
        status switch
        {
            // Both failure results are committed mutations: materials are
            // consumed and the target is either downgraded or protected.
            // Keep their audit codes within command_audit.outcome_code's
            // established varchar(32) contract.
            HolyStoneCommandResultStatus.UpgradeFailedDowngraded =>
                "committed_upgrade_downgrade",
            HolyStoneCommandResultStatus.UpgradeFailedProtected =>
                "committed_upgrade_protected",
            _ when HolyStoneNativeResults.IsSuccess(status) =>
                "committed_" +
                    HolyStonePersistenceCodec.ResultCode(status),
            _ => HolyStonePersistenceCodec.ResultCode(status)
        };

    private static void AddIdentityParameters(
        NpgsqlCommand command,
        HolyStoneCommandOperation operation,
        string principalKey,
        string aggregateKey,
        byte[] operationId)
    {
        command.Parameters.AddWithValue(
            "principalType",
            HolyStonePersistenceCodec.PrincipalType);
        command.Parameters.AddWithValue("principalKey", principalKey);
        command.Parameters.AddWithValue(
            "aggregateType",
            HolyStonePersistenceCodec.AggregateType);
        command.Parameters.AddWithValue("aggregateKey", aggregateKey);
        command.Parameters.AddWithValue(
            "commandFamily",
            HolyStonePersistenceCodec.CommandFamilyCode(operation));
        command.Parameters.Add(
            "operationId",
            NpgsqlDbType.Bytea).Value = operationId;
    }

    private static void AddNullableBigint(
        NpgsqlCommand command,
        string name,
        long? value)
    {
        command.Parameters.Add(
            new NpgsqlParameter(name, NpgsqlDbType.Bigint)
            {
                Value = value.HasValue
                    ? value.Value
                    : DBNull.Value
            });
    }

    private static void AddNullableInteger(
        NpgsqlCommand command,
        string name,
        int? value)
    {
        command.Parameters.Add(
            new NpgsqlParameter(name, NpgsqlDbType.Integer)
            {
                Value = value.HasValue
                    ? value.Value
                    : DBNull.Value
            });
    }
}
