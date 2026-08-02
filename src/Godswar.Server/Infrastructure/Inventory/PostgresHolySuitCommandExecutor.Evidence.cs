using System.Globalization;
using System.Text;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Inventory;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.Infrastructure.Inventory;

internal sealed partial class PostgresHolySuitCommandExecutor
{
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

    private async Task<HolySuitExecutionReceipt>
        PersistTerminalResultAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        HolySuitCommandContext context,
        LockedCharacter character,
        DailyUsage daily,
        bool battlePass,
        HolySuitCommandResultStatus status,
        string principalKey,
        string aggregateKey,
        byte[] operationId,
        byte[] requestHash,
        CancellationToken cancellationToken)
    {
        var plan = new HolySuitPlan(
            status,
            [],
            character.Experience,
            daily.StoredExperience,
            0,
            0,
            0);
        var auditId = await InsertAuditAsync(
            connection,
            transaction,
            context,
            status,
            principalKey,
            aggregateKey,
            operationId,
            requestHash,
            character,
            plan,
            battlePass,
            cancellationToken);
        var receipt = new HolySuitExecutionReceipt(
            context.Subject.CharacterId,
            context.Command.Operation,
            context.Command.NpcId,
            context.Command.DialogIndex,
            status,
            HolySuitNativeResults.GetResultSubId(
                context.Command.Operation,
                status),
            context.Command.ExperienceToStore,
            context.Command.PrismsToCreate,
            character.Experience,
            character.Experience,
            daily.StoredExperience,
            daily.StoredExperience,
            battlePass,
            0,
            0,
            [],
            character.ProgressionRevision,
            character.InventoryRevision,
            auditId.ToString(CultureInfo.InvariantCulture),
            null);
        var payload = HolySuitPersistenceCodec.Encode(receipt);
        await InsertInboxAsync(
            connection,
            transaction,
            context.Family,
            status,
            principalKey,
            aggregateKey,
            operationId,
            requestHash,
            HolySuitPersistenceCodec.Hash(payload),
            auditId,
            payload,
            cancellationToken);
        return receipt;
    }

    private async Task<long> InsertAuditAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        HolySuitCommandContext context,
        HolySuitCommandResultStatus status,
        string principalKey,
        string aggregateKey,
        byte[] operationId,
        byte[] requestHash,
        LockedCharacter character,
        HolySuitPlan plan,
        bool battlePass,
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
                    'primarySlot', @primarySlot,
                    'secondarySlot', @secondarySlot,
                    'requestedExperience', @requestedExperience,
                    'automaticStoreMaximum', @automaticStoreMaximum,
                    'appliedExperience', @appliedExperience,
                    'requestedPrisms', @requestedPrisms,
                    'experienceBefore', @experienceBefore,
                    'experienceAfter', @experienceAfter,
                    'dailyStoredAfter', @dailyStoredAfter,
                    'battlePassExempt', @battlePassExempt,
                    'prismsCreated', @prismsCreated,
                    'prismsConsumed', @prismsConsumed,
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
        command.Parameters.Add("requestHash", NpgsqlDbType.Bytea).Value =
            requestHash;
        command.Parameters.AddWithValue(
            "outcomeCode",
            HolySuitPersistenceCodec.ResultCode(status));
        command.Parameters.AddWithValue(
            "operation",
            context.Command.Operation.ToString());
        command.Parameters.AddWithValue("npcId", context.Command.NpcId);
        command.Parameters.AddWithValue(
            "dialogIndex",
            context.Command.DialogIndex);
        command.Parameters.AddWithValue(
            "primarySlot",
            context.Command.PrimaryKitBagSlot);
        command.Parameters.AddWithValue(
            "secondarySlot",
            context.Command.SecondaryKitBagSlot);
        command.Parameters.AddWithValue(
            "requestedExperience",
            context.Command.ExperienceToStore);
        command.Parameters.AddWithValue(
            "automaticStoreMaximum",
            context.Command.Operation ==
                HolySuitCommandOperation.StoreExperience &&
            context.Command.ExperienceToStore == 0);
        command.Parameters.AddWithValue(
            "appliedExperience",
            plan.StoredExperience);
        command.Parameters.AddWithValue(
            "requestedPrisms",
            context.Command.PrismsToCreate);
        command.Parameters.AddWithValue(
            "experienceBefore",
            character.Experience);
        command.Parameters.AddWithValue(
            "experienceAfter",
            plan.CharacterExperienceAfter);
        command.Parameters.AddWithValue(
            "dailyStoredAfter",
            plan.DailyStoredExperienceAfter);
        command.Parameters.AddWithValue("battlePassExempt", battlePass);
        command.Parameters.AddWithValue(
            "prismsCreated",
            plan.PrismsCreated);
        command.Parameters.AddWithValue(
            "prismsConsumed",
            plan.PrismsConsumed);
        command.Parameters.AddWithValue("status", status.ToString());
        command.Parameters.AddWithValue(
            "retentionPolicy",
            HolySuitPersistenceCodec.RetentionPolicy);
        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        return scalar is long auditId && auditId > 0
            ? auditId
            : throw new InvalidDataException(
                "The Holy Suit audit returned no identity.");
    }

    private async Task<long> InsertInboxAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CommandFamily family,
        HolySuitCommandResultStatus status,
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
        command.Parameters.Add("requestHash", NpgsqlDbType.Bytea).Value =
            requestHash;
        command.Parameters.AddWithValue(
            "resultContractVersion",
            HolySuitPersistenceCodec.ContractVersion);
        command.Parameters.AddWithValue(
            "resultCode",
            HolySuitPersistenceCodec.ResultCode(status));
        command.Parameters.Add("resultPayload", NpgsqlDbType.Jsonb).Value =
            Encoding.UTF8.GetString(payload);
        command.Parameters.Add("resultHash", NpgsqlDbType.Bytea).Value =
            resultHash;
        command.Parameters.AddWithValue("auditId", auditId);
        command.Parameters.AddWithValue(
            "retentionPolicy",
            HolySuitPersistenceCodec.RetentionPolicy);
        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        return scalar is long inboxId && inboxId > 0
            ? inboxId
            : throw new InvalidDataException(
                "The Holy Suit inbox returned no identity.");
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
                last_duplicate_at = now()
            WHERE id = @inboxId;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("inboxId", inboxId);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidDataException(
                "The Holy Suit duplicate marker was not exact.");
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
                "The Holy Suit request-conflict marker was not exact.");
        }
    }

    private static HolySuitExecutionReceipt ValidateStoredResult(
        StoredInbox stored,
        CommandFamily family)
    {
        if (stored.ResultContractVersion !=
            HolySuitPersistenceCodec.ContractVersion)
        {
            throw new InvalidDataException(
                "The stored Holy Suit result contract is unsupported.");
        }
        return HolySuitPersistenceCodec.DecodeAndVerify(
            stored.ResultPayload,
            stored.ResultHash,
            stored.ResultCode,
            stored.AuditId,
            family);
    }

    private static void AddIdentityParameters(
        NpgsqlCommand command,
        CommandFamily family,
        string principalKey,
        string aggregateKey,
        byte[] operationId)
    {
        command.Parameters.AddWithValue(
            "principalType",
            HolySuitPersistenceCodec.PrincipalType);
        command.Parameters.AddWithValue("principalKey", principalKey);
        command.Parameters.AddWithValue(
            "aggregateType",
            HolySuitPersistenceCodec.AggregateType);
        command.Parameters.AddWithValue("aggregateKey", aggregateKey);
        command.Parameters.AddWithValue(
            "commandFamily",
            HolySuitPersistenceCodec.CommandFamilyCode(family));
        command.Parameters.Add("operationId", NpgsqlDbType.Bytea).Value =
            operationId;
    }
}
