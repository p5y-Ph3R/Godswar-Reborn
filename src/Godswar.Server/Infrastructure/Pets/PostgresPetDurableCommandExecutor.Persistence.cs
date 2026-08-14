using System.Globalization;
using System.Text;
using System.Text.Json;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Pets;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.Infrastructure.Pets;

internal sealed partial class PostgresPetDurableCommandExecutor
{
    private async Task<LockedCharacter?> LockCharacterAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CommandSubject subject,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            SELECT
                profession,
                fighter_job_lv,
                inventory_revision,
                pet_shed_capacity,
                pet_shed_revision
            FROM public.character_base
            WHERE id = @characterId
              AND account_id = @accountId
              AND lifecycle_state = 'active'
            FOR UPDATE;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue(
            "characterId",
            subject.CharacterId);
        command.Parameters.AddWithValue("accountId", subject.AccountId);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new LockedCharacter(
                reader.GetInt16(0),
                reader.GetInt32(1),
                reader.GetInt64(2),
                reader.GetInt16(3),
                reader.GetInt64(4))
            : null;
    }

    private async Task<StoredInbox?> ReadInboxAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CommandSubject subject,
        CommandFamily family,
        byte[] operationId,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            SELECT
                id, request_hash, result_contract_version,
                result_code, result_payload::text, result_hash,
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
            subject,
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

    private async Task<PetDurableExecutionResult>
        PersistTransitionAsync<T>(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            CommandEnvelope<T> envelope,
            PetTransition transition,
            byte[] operationId,
            byte[] requestHash,
            CancellationToken cancellationToken)
    {
        var aggregateRevision = transition.Succeeded
            ? await AdvanceStreamAsync(
                connection,
                transaction,
                envelope.Subject.CharacterId,
                cancellationToken)
            : await ReadStreamVersionAsync(
                connection,
                transaction,
                envelope.Subject.CharacterId,
                cancellationToken);
        Guid? eventId =
            transition.Succeeded ? Guid.NewGuid() : null;
        var auditId = await InsertAuditAsync(
            connection,
            transaction,
            envelope,
            transition,
            operationId,
            requestHash,
            cancellationToken);
        var receipt = new PetDurableReceipt(
            envelope.Family,
            transition.Status,
            envelope.Subject.AccountId,
            envelope.Subject.CharacterId,
            transition.KitBagSlot,
            transition.EquipmentSlot,
            transition.PetId,
            transition.PetLevel,
            transition.PetExperience,
            transition.PetRevision,
            transition.IsCarried,
            transition.IsSummoned,
            transition.PresenceOperation,
            aggregateRevision,
            auditId.ToString(CultureInfo.InvariantCulture),
            eventId,
            transition.DeputyPetId,
            transition.PetMergeDelta,
            transition.GrowthPreview,
            transition.BasicSavvyPreview,
            transition.HatchRank,
            transition.AppearanceChange,
            transition.SoulContract,
            transition.PetManagerUtility,
            transition.RebirthGrowth,
            transition.SkillLearn);
        receipt.Validate();
        var payload = PetDurablePersistenceCodec.Encode(receipt);
        var resultHash = PetDurablePersistenceCodec.Hash(payload);
        var inboxId = await InsertInboxAsync(
            connection,
            transaction,
            envelope,
            operationId,
            requestHash,
            auditId,
            payload,
            resultHash,
            cancellationToken);
        if (transition.InventoryMutations is { Count: > 0 }
            inventoryMutations)
        {
            await PersistInventoryMutationsAsync(
                connection,
                transaction,
                envelope.Subject,
                inboxId,
                inventoryMutations,
                cancellationToken);
        }
        if (eventId is { } durableEventId)
        {
            await EnsureOutboxPositionAsync(
                connection,
                transaction,
                envelope.Subject.CharacterId,
                aggregateRevision - 1,
                cancellationToken);
            await InsertOutboxAsync(
                connection,
                transaction,
                envelope,
                inboxId,
                durableEventId,
                aggregateRevision,
                payload,
                cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return transition.Succeeded
            ? PetDurableExecutionResult.Committed(receipt)
            : PetDurableExecutionResult.Rejected(receipt);
    }

    private async Task<long> InsertAuditAsync<T>(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CommandEnvelope<T> envelope,
        PetTransition transition,
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
                @outcomeCode, @detailPayload, @retentionPolicy
            )
            RETURNING id;
            """,
            connection,
            transaction);
        AddIdentityParameters(
            command,
            envelope.Subject,
            envelope.Family,
            operationId);
        command.Parameters.Add(
            "requestHash",
            NpgsqlDbType.Bytea).Value = requestHash;
        command.Parameters.AddWithValue(
            "outcomeCode",
            transition.Succeeded ? "committed" : "rejected");
        command.Parameters.Add(
            "detailPayload",
            NpgsqlDbType.Jsonb).Value =
            EncodeAuditDetail(transition);
        command.Parameters.AddWithValue(
            "retentionPolicy",
            PetDurablePersistenceCodec.RetentionPolicy);
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken),
            CultureInfo.InvariantCulture);
    }

    private static string EncodeAuditDetail(PetTransition transition) =>
        transition.SkillLearn is { } learned
            ? JsonSerializer.Serialize(new
            {
                status = (byte)transition.Status,
                pet_skill_learn = learned
            })
            : transition.PetManagerUtility is { } utility
            ? JsonSerializer.Serialize(new
            {
                status = (byte)transition.Status,
                pet_manager_utility = utility
            })
            : transition.SoulContract is { } soulContract
            ? JsonSerializer.Serialize(new
            {
                status = (byte)transition.Status,
                soul_contract = new
                {
                    pet_id = soulContract.PetId,
                    previous_stage = soulContract.PreviousStage,
                    new_stage = soulContract.NewStage,
                    material_item_id = soulContract.MaterialTemplateId,
                    material_quantity = soulContract.MaterialQuantity,
                    basic_savvy_increase_hundredths =
                        soulContract.BasicSavvyIncreaseHundredths
                }
            })
            : transition.AppearanceChange is { } appearance
            ? JsonSerializer.Serialize(new
            {
                status = (byte)transition.Status,
                appearance_change = new
                {
                    old_species_id = appearance.OldSpeciesId,
                    old_species_name = appearance.OldSpeciesName,
                    new_species_id = appearance.NewSpeciesId,
                    new_species_name = appearance.NewSpeciesName,
                    magic_jade_item_id = appearance.MagicJadeItemId,
                    magic_jade_display_name =
                        appearance.MagicJadeDisplayName,
                    magic_jade_item_instance_id =
                        appearance.MagicJadeItemInstanceId,
                    kit_bag_slot = appearance.KitBagSlot,
                    pet_content_revision = appearance.PetContentRevision,
                    item_content_revision = appearance.ItemContentRevision
                }
            })
            : transition.HatchRank is { } hatchRank
            ? JsonSerializer.Serialize(new
            {
                status = (byte)transition.Status,
                hatch_rank = new
                {
                    rank = hatchRank.Rank,
                    outcome_order = hatchRank.OutcomeOrder,
                    roll = hatchRank.Roll,
                    content_revision = hatchRank.ContentRevision
                }
            })
            : $$"""{"status":{{(byte)transition.Status}}}""";

    private async Task<long> InsertInboxAsync<T>(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CommandEnvelope<T> envelope,
        byte[] operationId,
        byte[] requestHash,
        long auditId,
        byte[] payload,
        byte[] resultHash,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            INSERT INTO public.command_inbox (
                principal_type, principal_key,
                aggregate_type, aggregate_key,
                command_family, operation_id, request_hash,
                result_contract_version, result_code,
                result_payload, result_hash, audit_id,
                retention_policy
            )
            VALUES (
                @principalType, @principalKey,
                @aggregateType, @aggregateKey,
                @commandFamily, @operationId, @requestHash,
                @contractVersion, @resultCode,
                @resultPayload, @resultHash, @auditId,
                @retentionPolicy
            )
            RETURNING id;
            """,
            connection,
            transaction);
        AddIdentityParameters(
            command,
            envelope.Subject,
            envelope.Family,
            operationId);
        command.Parameters.Add(
            "requestHash",
            NpgsqlDbType.Bytea).Value = requestHash;
        command.Parameters.AddWithValue(
            "contractVersion",
            PetDurablePersistenceCodec.ContractVersionFor(
                envelope.Family));
        command.Parameters.AddWithValue(
            "resultCode",
            "pet_result");
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
            PetDurablePersistenceCodec.RetentionPolicy);
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken),
            CultureInfo.InvariantCulture);
    }

    private async Task InsertOutboxAsync<T>(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CommandEnvelope<T> envelope,
        long inboxId,
        Guid eventId,
        long aggregateRevision,
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
                @aggregateType, @aggregateKey, @aggregateVersion,
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
            PetDurablePersistenceCodec.ConsumerKey);
        command.Parameters.AddWithValue(
            "aggregateType",
            PetDurablePersistenceCodec.AggregateType);
        command.Parameters.AddWithValue(
            "aggregateKey",
            PetDurablePersistenceCodec.AggregateKey(
                envelope.Subject.CharacterId));
        command.Parameters.AddWithValue(
            "aggregateVersion",
            aggregateRevision);
        command.Parameters.AddWithValue(
            "eventType",
            PetDurablePersistenceCodec.EventType(envelope.Family));
        command.Parameters.AddWithValue(
            "contractVersion",
            PetDurablePersistenceCodec.ContractVersionFor(
                envelope.Family));
        command.Parameters.AddWithValue(
            "orderingPolicy",
            PetDurablePersistenceCodec.OrderingPolicy);
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
                "The pet outbox insert was not exact.");
        }
    }
}
