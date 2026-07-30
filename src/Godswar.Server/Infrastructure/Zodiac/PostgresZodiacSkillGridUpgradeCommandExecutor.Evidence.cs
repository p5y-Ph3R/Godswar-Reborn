using System.Text;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Zodiac;
using Godswar.Server.State;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.Infrastructure.Zodiac;

internal sealed partial class
    PostgresZodiacSkillGridUpgradeCommandExecutor
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
        CommandEnvelope<ZodiacSkillGridUpgradeCommand> envelope,
        LockedCharacter character,
        ZodiacSkillGridUpgradeResult result,
        ZodiacSkillGridUpgradeReceiptStatus status,
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
                @resultCode,
                jsonb_build_object(
                    'gridIndex', @gridIndex,
                    'status', @status,
                    'previousLevel', @previousLevel,
                    'currentLevel', @currentLevel,
                    'zodiacLevel', @zodiacLevel,
                    'requiredZodiacLevel', @requiredZodiacLevel,
                    'energyCost', @energyCost,
                    'energyBefore', @energyBefore,
                    'energyRemainderBeforeX100', @remainderBefore,
                    'energyAfter', @energyAfter,
                    'energyRemainderAfterX100', @remainderAfter,
                    'talentPointCost', @talentPointCost,
                    'talentPointsBefore', @talentPointsBefore,
                    'talentPointsAfter', @talentPointsAfter,
                    'selectedSkillId', @selectedSkillId
                ),
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
        command.Parameters.AddWithValue("resultCode", resultCode);
        command.Parameters.AddWithValue(
            "gridIndex",
            envelope.Command.GridIndex);
        command.Parameters.AddWithValue(
            "status",
            checked((short)status));
        command.Parameters.AddWithValue(
            "previousLevel",
            checked((short)result.PreviousLevel));
        command.Parameters.AddWithValue(
            "currentLevel",
            checked((short)result.CurrentLevel));
        command.Parameters.AddWithValue(
            "zodiacLevel",
            checked((short)character.ZodiacLevel));
        command.Parameters.AddWithValue(
            "requiredZodiacLevel",
            checked((short)result.RequiredZodiacLevel));
        command.Parameters.AddWithValue("energyCost", result.EnergyCost);
        command.Parameters.AddWithValue(
            "energyBefore",
            character.Energy);
        command.Parameters.AddWithValue(
            "remainderBefore",
            character.EnergyRemainderX100);
        command.Parameters.AddWithValue(
            "energyAfter",
            result.CurrentEnergy);
        command.Parameters.AddWithValue(
            "remainderAfter",
            result.CurrentEnergyRemainderX100);
        command.Parameters.AddWithValue(
            "talentPointCost",
            result.TalentPointCost);
        command.Parameters.AddWithValue(
            "talentPointsBefore",
            character.TalentPoints);
        command.Parameters.AddWithValue(
            "talentPointsAfter",
            result.CurrentTalentPoints);
        command.Parameters.AddWithValue(
            "selectedSkillId",
            result.SelectedSkillId);
        command.Parameters.AddWithValue(
            "retentionPolicy",
            ZodiacSkillGridUpgradePersistenceCodec.RetentionPolicy);
        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        return scalar is long auditId && auditId > 0
            ? auditId
            : throw new InvalidDataException(
                "The Zodiac upgrade audit returned no identity.");
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
            ZodiacSkillGridUpgradePersistenceCodec.ContractVersion);
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
            ZodiacSkillGridUpgradePersistenceCodec.RetentionPolicy);
        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        return scalar is long inboxId && inboxId > 0
            ? inboxId
            : throw new InvalidDataException(
                "The Zodiac upgrade inbox returned no identity.");
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
                "The Zodiac upgrade duplicate update was not exact.");
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
                "The Zodiac upgrade conflict update was not exact.");
        }
    }

    private static ZodiacSkillGridUpgradeExecutionReceipt
        ValidateStoredResult(
            StoredInbox stored,
            int expectedCharacterId,
            int expectedGridIndex)
    {
        if (stored.ResultContractVersion !=
            ZodiacSkillGridUpgradePersistenceCodec.ContractVersion)
        {
            throw new InvalidDataException(
                "The stored Zodiac upgrade contract is unsupported.");
        }

        var receipt =
            ZodiacSkillGridUpgradePersistenceCodec.DecodeAndVerify(
                stored.ResultPayload,
                stored.ResultHash,
                stored.ResultCode,
                stored.AuditId);
        if (receipt.CharacterId != expectedCharacterId ||
            receipt.GridIndex != expectedGridIndex)
        {
            throw new InvalidDataException(
                "The stored Zodiac upgrade scope is invalid.");
        }

        return receipt;
    }

    private static void AddIdentityParameters(
        NpgsqlCommand command,
        string principalKey,
        string aggregateKey,
        byte[] operationId)
    {
        command.Parameters.AddWithValue(
            "principalType",
            ZodiacSkillGridUpgradePersistenceCodec.PrincipalType);
        command.Parameters.AddWithValue("principalKey", principalKey);
        command.Parameters.AddWithValue(
            "aggregateType",
            ZodiacSkillGridUpgradePersistenceCodec.AggregateType);
        command.Parameters.AddWithValue("aggregateKey", aggregateKey);
        command.Parameters.AddWithValue(
            "commandFamily",
            ZodiacSkillGridUpgradePersistenceCodec.CommandFamily);
        command.Parameters.Add(
            "operationId",
            NpgsqlDbType.Bytea).Value = operationId;
    }
}
