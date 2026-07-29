using System.Globalization;
using System.Text;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Inventory;
using Godswar.Server.State;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.Infrastructure.Inventory;

internal sealed partial class PostgresGearMentorDecomposeCommandExecutor
{
    private async Task<LockedCharacter?> LockCharacterAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CommandSubject subject,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            SELECT fighter_job_lv, inventory_revision
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
        if (!await reader.ReadAsync(cancellationToken) ||
            reader.GetInt64(1) < 0)
        {
            return null;
        }

        return new LockedCharacter(
            reader.GetInt32(0),
            reader.GetInt64(1));
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

    private async Task<GearMentorDecomposeGearExecutionReceipt>
        PersistTerminalResultAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            DecomposeCommandContext context,
            long inventoryRevision,
            GearMentorDecomposeGearResultStatus status,
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
        var receipt =
            new GearMentorDecomposeGearExecutionReceipt(
                context.Subject.CharacterId,
                status,
                GearMentorDecomposeGearNativeResults.GetResultSubId(
                    status),
                CreateReceiptSelections(context),
                dustOutcomes: [],
                inventoryRevision,
                auditId.ToString(CultureInfo.InvariantCulture),
                outboxEventId: null);
        var payload =
            GearMentorDecomposePersistenceCodec.Encode(receipt);
        await ReachAsync(
            PostgresGearMentorDecomposeCommandStage.AuditInserted,
            cancellationToken);
        await InsertInboxAsync(
            connection,
            transaction,
            status,
            principalKey,
            aggregateKey,
            operationId,
            requestHash,
            GearMentorDecomposePersistenceCodec.Hash(payload),
            auditId,
            payload,
            cancellationToken);
        await ReachAsync(
            PostgresGearMentorDecomposeCommandStage.InboxInserted,
            cancellationToken);
        return receipt;
    }

    private async Task<long> InsertAuditAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DecomposeCommandContext context,
        GearMentorDecomposeGearResultStatus status,
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
                    'npcId', @npcId,
                    'selectedKitBagSlots',
                        to_jsonb(@selectedKitBagSlots::integer[]),
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
        command.Parameters.AddWithValue("npcId", context.NpcId);
        command.Parameters.Add(
            "selectedKitBagSlots",
            NpgsqlDbType.Array | NpgsqlDbType.Integer).Value =
            context.Selections
                .Select(static selection =>
                    selection.SelectedKitBagSlot)
                .ToArray();
        command.Parameters.AddWithValue("status", status.ToString());
        command.Parameters.AddWithValue(
            "retentionPolicy",
            GearMentorDecomposePersistenceCodec.RetentionPolicy);
        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        return scalar is long auditId && auditId > 0
            ? auditId
            : throw new InvalidDataException(
                "The Decompose audit insert returned no identity.");
    }

    private async Task<long> InsertInboxAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        GearMentorDecomposeGearResultStatus status,
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
            GearMentorDecomposePersistenceCodec.ContractVersion);
        command.Parameters.AddWithValue(
            "resultCode",
            GearMentorDecomposePersistenceCodec.ResultCode(status));
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
            GearMentorDecomposePersistenceCodec.RetentionPolicy);
        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        return scalar is long inboxId && inboxId > 0
            ? inboxId
            : throw new InvalidDataException(
                "The Decompose inbox insert returned no identity.");
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
                "The Decompose duplicate update was not exact.");
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
                "The Decompose conflict update was not exact.");
        }
    }

    private static GearMentorDecomposeGearExecutionReceipt
        ValidateStoredResult(StoredInbox stored)
    {
        if (stored.ResultContractVersion !=
            GearMentorDecomposePersistenceCodec.ContractVersion)
        {
            throw new InvalidDataException(
                "The stored Decompose contract is unsupported.");
        }

        return GearMentorDecomposePersistenceCodec.DecodeAndVerify(
            stored.ResultPayload,
            stored.ResultHash,
            stored.ResultCode,
            stored.AuditId);
    }

    private static IReadOnlyList<GearMentorDecomposeReceiptSelection>
        CreateReceiptSelections(DecomposeCommandContext context) =>
        context.Selections
            .Select(static selection =>
                new GearMentorDecomposeReceiptSelection(
                    selection.SelectedKitBagSlot,
                    CompactItemEntry.Parse(
                        selection.ExpectedCompactItemState).Id))
            .ToArray();

    private static string AuditOutcomeCode(
        GearMentorDecomposeGearResultStatus status) =>
        status switch
        {
            GearMentorDecomposeGearResultStatus.Succeeded =>
                "committed",
            GearMentorDecomposeGearResultStatus.SelectionMissing =>
                "selection_missing",
            GearMentorDecomposeGearResultStatus.PlayerLevelTooLow =>
                "player_level_too_low",
            GearMentorDecomposeGearResultStatus.InvalidEquipment =>
                "invalid_equipment",
            GearMentorDecomposeGearResultStatus.EquipmentLevelTooLow =>
                "equipment_level_too_low",
            GearMentorDecomposeGearResultStatus
                .InsufficientEquipmentQuality =>
                "insufficient_equipment_quality",
            GearMentorDecomposeGearResultStatus.ClassSuit =>
                "class_suit",
            GearMentorDecomposeGearResultStatus.InsufficientCapacity =>
                "insufficient_capacity",
            GearMentorDecomposeGearResultStatus.StaleSelection =>
                "stale_selection",
            GearMentorDecomposeGearResultStatus.InvalidSelection =>
                "invalid_selection",
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
            GearMentorDecomposePersistenceCodec.PrincipalType);
        command.Parameters.AddWithValue("principalKey", principalKey);
        command.Parameters.AddWithValue(
            "aggregateType",
            GearMentorDecomposePersistenceCodec.AggregateType);
        command.Parameters.AddWithValue("aggregateKey", aggregateKey);
        command.Parameters.AddWithValue(
            "commandFamily",
            GearMentorDecomposePersistenceCodec.CommandFamilyCode);
        command.Parameters.Add(
            "operationId",
            NpgsqlDbType.Bytea).Value = operationId;
    }
}
