using System.Globalization;
using System.Text;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Inventory;
using Godswar.Server.State;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.Infrastructure.Inventory;

internal sealed partial class PostgresEquipmentForgeCommandExecutor
{
    private async Task<LockedCharacter?> LockCharacterAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CommandSubject subject,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            SELECT "Money", wallet_revision, inventory_revision
            FROM public.character_base
            WHERE account_id = @accountId
              AND id = @characterId
            FOR UPDATE;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("accountId", subject.AccountId);
        command.Parameters.AddWithValue(
            "characterId",
            subject.CharacterId);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var character = new LockedCharacter(
            reader.GetInt32(0),
            reader.GetInt64(1),
            reader.GetInt64(2));
        if (character.Silver < 0 ||
            character.WalletRevision < 0 ||
            character.InventoryRevision < 0)
        {
            throw new InvalidDataException(
                "The locked character economy state is invalid.");
        }

        return character;
    }

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

    private async Task<EquipmentForgeExecutionReceipt>
        PersistTerminalResultAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            EquipmentForgeCommandContext context,
            LockedCharacter character,
            EquipmentForgeCommandResultStatus status,
            string principalKey,
            string aggregateKey,
            byte[] operationId,
            byte[] requestHash,
            CancellationToken cancellationToken)
    {
        var auditId = await InsertAuditAsync(
            connection,
            transaction,
            context,
            status,
            plan: null,
            roll: -1,
            principalKey,
            aggregateKey,
            operationId,
            requestHash,
            cancellationToken);
        var receipt = new EquipmentForgeExecutionReceipt(
            context.Subject.CharacterId,
            status,
            materialType: 0,
            roll: -1,
            successProbability: 0,
            silverSpent: 0,
            equipmentBeforeCompactItemState: string.Empty,
            equipmentAfterCompactItemState: string.Empty,
            materials: [],
            character.WalletRevision,
            character.InventoryRevision,
            auditId.ToString(CultureInfo.InvariantCulture),
            outboxEventId: null);
        var payload = EquipmentForgePersistenceCodec.Encode(receipt);
        await ReachAsync(
            PostgresEquipmentForgeCommandStage.AuditInserted,
            ordinal: -1,
            cancellationToken);
        await InsertInboxAsync(
            connection,
            transaction,
            status,
            principalKey,
            aggregateKey,
            operationId,
            requestHash,
            EquipmentForgePersistenceCodec.Hash(payload),
            auditId,
            payload,
            cancellationToken);
        await ReachAsync(
            PostgresEquipmentForgeCommandStage.InboxInserted,
            ordinal: -1,
            cancellationToken);
        return receipt;
    }

    private async Task<long> InsertAuditAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        EquipmentForgeCommandContext context,
        EquipmentForgeCommandResultStatus status,
        ForgePersistencePlan? plan,
        int roll,
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
                    'equipmentSlot', @equipmentSlot,
                    'primaryMaterialSlot', @primaryMaterialSlot,
                    'oddsMaterialCount', @oddsMaterialCount,
                    'roll', @roll,
                    'probability', @probability,
                    'materialType', @materialType,
                    'silverSpent', @silverSpent,
                    'status', @status),
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
            AuditOutcomeCode(status));
        command.Parameters.AddWithValue(
            "equipmentSlot",
            context.Command.Equipment.KitBagSlot);
        command.Parameters.AddWithValue(
            "primaryMaterialSlot",
            context.Command.PrimaryMaterial.KitBagSlot);
        command.Parameters.AddWithValue(
            "oddsMaterialCount",
            context.Command.OddsMaterials.Length);
        command.Parameters.Add(
            "roll",
            NpgsqlDbType.Integer).Value =
            roll >= 0 ? roll : DBNull.Value;
        command.Parameters.Add(
            "probability",
            NpgsqlDbType.Integer).Value =
            plan is null
                ? DBNull.Value
                : plan.Calculation.SuccessProbability;
        command.Parameters.Add(
            "materialType",
            NpgsqlDbType.Integer).Value =
            plan is null
                ? DBNull.Value
                : (int)plan.Calculation.Operation;
        command.Parameters.AddWithValue(
            "silverSpent",
            plan is null
                ? 0
                : plan.Calculation.SilverCost);
        command.Parameters.AddWithValue("status", status.ToString());
        command.Parameters.AddWithValue(
            "retentionPolicy",
            EquipmentForgePersistenceCodec.RetentionPolicy);
        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        return scalar is long auditId && auditId > 0
            ? auditId
            : throw new InvalidDataException(
                "The equipment-forge audit insert returned no identity.");
    }

    private async Task<long> InsertInboxAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        EquipmentForgeCommandResultStatus status,
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
            EquipmentForgePersistenceCodec.ContractVersion);
        command.Parameters.AddWithValue(
            "resultCode",
            EquipmentForgePersistenceCodec.ResultCode(status));
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
            EquipmentForgePersistenceCodec.RetentionPolicy);
        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        return scalar is long inboxId && inboxId > 0
            ? inboxId
            : throw new InvalidDataException(
                "The equipment-forge inbox insert returned no identity.");
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
                "The equipment-forge duplicate update was not exact.");
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
                "The equipment-forge conflict update was not exact.");
        }
    }

    private static EquipmentForgeExecutionReceipt ValidateStoredResult(
        StoredInbox stored)
    {
        if (stored.ResultContractVersion !=
            EquipmentForgePersistenceCodec.ContractVersion)
        {
            throw new InvalidDataException(
                "The stored equipment-forge contract is unsupported.");
        }

        return EquipmentForgePersistenceCodec.DecodeAndVerify(
            stored.ResultPayload,
            stored.ResultHash,
            stored.ResultCode,
            stored.AuditId);
    }

    private static string AuditOutcomeCode(
        EquipmentForgeCommandResultStatus status) =>
        status switch
        {
            EquipmentForgeCommandResultStatus.Succeeded =>
                "committed_success",
            EquipmentForgeCommandResultStatus.FailedRoll =>
                "committed_failed_roll",
            EquipmentForgeCommandResultStatus.InvalidSelection =>
                "invalid_selection",
            EquipmentForgeCommandResultStatus.StaleSelection =>
                "stale_selection",
            EquipmentForgeCommandResultStatus.InvalidForge =>
                "invalid_forge",
            EquipmentForgeCommandResultStatus.InsufficientMaterials =>
                "insufficient_materials",
            EquipmentForgeCommandResultStatus.InsufficientSilver =>
                "insufficient_silver",
            _ => throw new ArgumentOutOfRangeException(nameof(status))
        };

    private static void AddIdentityParameters(
        NpgsqlCommand command,
        string principalKey,
        string aggregateKey,
        byte[] operationId)
    {
        command.Parameters.AddWithValue(
            "principalType",
            EquipmentForgePersistenceCodec.PrincipalType);
        command.Parameters.AddWithValue("principalKey", principalKey);
        command.Parameters.AddWithValue(
            "aggregateType",
            EquipmentForgePersistenceCodec.AggregateType);
        command.Parameters.AddWithValue("aggregateKey", aggregateKey);
        command.Parameters.AddWithValue(
            "commandFamily",
            EquipmentForgePersistenceCodec.CommandFamilyCode);
        command.Parameters.Add(
            "operationId",
            NpgsqlDbType.Bytea).Value = operationId;
    }
}
