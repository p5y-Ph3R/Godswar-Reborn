using System.Globalization;
using System.Text;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Inventory;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.Infrastructure.Inventory;

internal sealed partial class PostgresGearEnhancementCommandExecutor
{
    private async Task<LockedCharacter?> LockCharacterAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CommandSubject subject,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            SELECT inventory_revision
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
        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        return scalar is long revision && revision >= 0
            ? new LockedCharacter(revision)
            : null;
    }

    private async Task<StoredInbox?> ReadInboxAsync(
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

        return new StoredInbox(
            reader.GetInt64(0),
            reader.GetFieldValue<byte[]>(1),
            reader.GetInt16(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetFieldValue<byte[]>(5),
            reader.GetInt64(6));
    }

    private async Task<GearEnhancementExecutionReceipt>
        PersistTerminalResultAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            GearEnhancementCommandContext context,
            long inventoryRevision,
            GearEnhancementCommandResultStatus status,
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
            principalKey,
            aggregateKey,
            operationId,
            requestHash,
            cancellationToken);
        var receipt = new GearEnhancementExecutionReceipt(
            context.Subject.CharacterId,
            context.Command.Operation,
            context.Command.NpcId,
            context.Command.DialogIndex,
            status,
            GearEnhancementNativeResults.GetResultSubId(
                context.Command.Operation,
                status),
            mutations: [],
            inventoryRevision,
            auditId.ToString(CultureInfo.InvariantCulture),
            outboxEventId: null);
        var payload = GearEnhancementPersistenceCodec.Encode(receipt);
        await ReachAsync(
            PostgresGearEnhancementCommandStage.AuditInserted,
            cancellationToken);
        await InsertInboxAsync(
            connection,
            transaction,
            context.Family,
            status,
            principalKey,
            aggregateKey,
            operationId,
            requestHash,
            GearEnhancementPersistenceCodec.Hash(payload),
            auditId,
            payload,
            cancellationToken);
        await ReachAsync(
            PostgresGearEnhancementCommandStage.InboxInserted,
            cancellationToken);
        return receipt;
    }

    private async Task<long> InsertAuditAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        GearEnhancementCommandContext context,
        GearEnhancementCommandResultStatus status,
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
                    'gearSlot', @gearSlot,
                    'catalystSlot', @catalystSlot,
                    'attributeStoneSlot', @attributeStoneSlot,
                    'status', @status),
                @retentionPolicy
            )
            RETURNING id;
            """,
            connection,
            transaction);
        AddIdentityParameters(
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
            AuditOutcomeCode(status));
        command.Parameters.AddWithValue(
            "operation",
            context.Command.Operation.ToString());
        command.Parameters.AddWithValue("npcId", context.Command.NpcId);
        command.Parameters.AddWithValue(
            "dialogIndex",
            context.Command.DialogIndex);
        command.Parameters.AddWithValue(
            "gearSlot",
            context.Command.Gear.KitBagSlot);
        command.Parameters.AddWithValue(
            "catalystSlot",
            context.Command.Catalyst.KitBagSlot);
        command.Parameters.AddWithValue(
            "attributeStoneSlot",
            context.Command.AttributeStone.KitBagSlot);
        command.Parameters.AddWithValue("status", status.ToString());
        command.Parameters.AddWithValue(
            "retentionPolicy",
            GearEnhancementPersistenceCodec.RetentionPolicy);
        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        return scalar is long auditId && auditId > 0
            ? auditId
            : throw new InvalidDataException(
                "The Gear Enhancement audit insert returned no identity.");
    }

    private async Task<long> InsertInboxAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CommandFamily family,
        GearEnhancementCommandResultStatus status,
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
            family,
            principalKey,
            aggregateKey,
            operationId);
        command.Parameters.Add(
            "requestHash",
            NpgsqlDbType.Bytea).Value = requestHash;
        command.Parameters.AddWithValue(
            "resultContractVersion",
            GearEnhancementPersistenceCodec.ContractVersion);
        command.Parameters.AddWithValue(
            "resultCode",
            GearEnhancementPersistenceCodec.ResultCode(status));
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
            GearEnhancementPersistenceCodec.RetentionPolicy);
        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        return scalar is long inboxId && inboxId > 0
            ? inboxId
            : throw new InvalidDataException(
                "The Gear Enhancement inbox insert returned no identity.");
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
                "The Gear Enhancement duplicate update was not exact.");
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
                "The Gear Enhancement conflict update was not exact.");
        }
    }

    private static GearEnhancementExecutionReceipt
        ValidateStoredResult(
            StoredInbox stored,
            CommandFamily family)
    {
        if (stored.ResultContractVersion !=
            GearEnhancementPersistenceCodec.ContractVersion)
        {
            throw new InvalidDataException(
                "The stored Gear Enhancement contract is unsupported.");
        }

        return GearEnhancementPersistenceCodec.DecodeAndVerify(
            stored.ResultPayload,
            stored.ResultHash,
            stored.ResultCode,
            stored.AuditId,
            family);
    }

    private static string AuditOutcomeCode(
        GearEnhancementCommandResultStatus status) =>
        status switch
        {
            GearEnhancementCommandResultStatus.Succeeded =>
                "committed",
            GearEnhancementCommandResultStatus.SelectionMissing =>
                "selection_missing",
            GearEnhancementCommandResultStatus.InvalidSelection =>
                "invalid_selection",
            GearEnhancementCommandResultStatus.StaleSelection =>
                "stale_selection",
            GearEnhancementCommandResultStatus.InvalidEquipment =>
                "invalid_equipment",
            GearEnhancementCommandResultStatus.UnsupportedEquipment =>
                "unsupported_equipment",
            GearEnhancementCommandResultStatus.InvalidAttributeState =>
                "invalid_attribute_state",
            GearEnhancementCommandResultStatus.InvalidAttributeStone =>
                "invalid_attribute_stone",
            GearEnhancementCommandResultStatus.InvalidCatalyst =>
                "invalid_catalyst",
            GearEnhancementCommandResultStatus.InsufficientMaterial =>
                "insufficient_material",
            GearEnhancementCommandResultStatus.AttributeNotAllowed =>
                "attribute_not_allowed",
            GearEnhancementCommandResultStatus.AttributeAlreadyPresent =>
                "attribute_already_present",
            GearEnhancementCommandResultStatus.AttributeSlotsFull =>
                "attribute_slots_full",
            GearEnhancementCommandResultStatus.AttributeMissing =>
                "attribute_missing",
            GearEnhancementCommandResultStatus.AttributeAmbiguous =>
                "attribute_ambiguous",
            GearEnhancementCommandResultStatus.AttributeNotEnhanceable =>
                "attribute_not_enhanceable",
            GearEnhancementCommandResultStatus.AttributeLevelMismatch =>
                "attribute_level_mismatch",
            GearEnhancementCommandResultStatus.QuartzLevelMismatch =>
                "quartz_level_mismatch",
            GearEnhancementCommandResultStatus.AttributeMaximumLevel =>
                "attribute_maximum_level",
            _ => throw new ArgumentOutOfRangeException(nameof(status))
        };

    private static void AddIdentityParameters(
        NpgsqlCommand command,
        CommandFamily family,
        string principalKey,
        string aggregateKey,
        byte[] operationId)
    {
        command.Parameters.AddWithValue(
            "principalType",
            GearEnhancementPersistenceCodec.PrincipalType);
        command.Parameters.AddWithValue("principalKey", principalKey);
        command.Parameters.AddWithValue(
            "aggregateType",
            GearEnhancementPersistenceCodec.AggregateType);
        command.Parameters.AddWithValue("aggregateKey", aggregateKey);
        command.Parameters.AddWithValue(
            "commandFamily",
            GearEnhancementPersistenceCodec.CommandFamilyCode(family));
        command.Parameters.Add(
            "operationId",
            NpgsqlDbType.Bytea).Value = operationId;
    }
}
