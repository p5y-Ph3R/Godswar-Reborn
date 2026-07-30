using Godswar.Server.Application.Pets;
using Npgsql;

namespace Godswar.Server.Infrastructure.Pets;

internal sealed partial class PostgresPetDurableCommandExecutor
{
    private async Task<long> AdvanceStreamAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            INSERT INTO public.pet_durable_stream_versions (
                character_id,
                current_version
            )
            VALUES (@characterId, 1)
            ON CONFLICT (character_id) DO UPDATE
            SET current_version =
                    public.pet_durable_stream_versions.current_version + 1,
                updated_at = transaction_timestamp()
            RETURNING current_version;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("characterId", characterId);
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken));
    }

    private async Task<long> ReadStreamVersionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            SELECT current_version
            FROM public.pet_durable_stream_versions
            WHERE character_id = @characterId
            FOR UPDATE;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("characterId", characterId);
        return await command.ExecuteScalarAsync(cancellationToken)
            is long version ? version : 0;
    }

    private async Task EnsureOutboxPositionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        long previousRevision,
        CancellationToken cancellationToken)
    {
        if (previousRevision < 0)
        {
            throw new InvalidDataException(
                "A pet stream cannot start below zero.");
        }

        // The dedicated stream always starts at one, so the generic guarded
        // outbox position is inserted at zero and advances contiguously.
        await using var command = CreateCommand(
            """
            INSERT INTO public.outbox_consumer_positions (
                consumer_key, aggregate_type, aggregate_key,
                ordering_policy, current_version
            )
            VALUES (
                @consumerKey, @aggregateType, @aggregateKey,
                @orderingPolicy, 0
            )
            ON CONFLICT (
                consumer_key, aggregate_type, aggregate_key
            ) DO NOTHING;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue(
            "consumerKey",
            PetDurablePersistenceCodec.ConsumerKey);
        command.Parameters.AddWithValue(
            "aggregateType",
            PetDurablePersistenceCodec.AggregateType);
        command.Parameters.AddWithValue(
            "aggregateKey",
            PetDurablePersistenceCodec.AggregateKey(characterId));
        command.Parameters.AddWithValue(
            "orderingPolicy",
            PetDurablePersistenceCodec.OrderingPolicy);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task RecordDuplicateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long inboxId,
        CancellationToken cancellationToken) =>
        await IncrementInboxCounterAsync(
            connection,
            transaction,
            inboxId,
            "duplicate_count",
            "last_duplicate_at",
            cancellationToken);

    private async Task RecordConflictAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long inboxId,
        CancellationToken cancellationToken) =>
        await IncrementInboxCounterAsync(
            connection,
            transaction,
            inboxId,
            "request_conflict_count",
            "last_request_conflict_at",
            cancellationToken);

    private async Task IncrementInboxCounterAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long inboxId,
        string counter,
        string timestamp,
        CancellationToken cancellationToken)
    {
        var allowed = counter switch
        {
            "duplicate_count" when
                timestamp == "last_duplicate_at" => true,
            "request_conflict_count" when
                timestamp == "last_request_conflict_at" => true,
            _ => false
        };
        if (!allowed)
        {
            throw new ArgumentException(
                "Unsupported inbox counter.");
        }

        await using var command = CreateCommand(
            $"""
            UPDATE public.command_inbox
            SET {counter} = LEAST({counter} + 1, 1000000),
                {timestamp} = transaction_timestamp()
            WHERE id = @inboxId;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("inboxId", inboxId);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidDataException(
                "The pet inbox counter update was not exact.");
        }
    }

    private static PetDurableReceipt ValidateStoredReceipt<T>(
        StoredInbox stored,
        Application.Commands.CommandEnvelope<T> envelope)
    {
        if (stored.ContractVersion !=
                PetDurablePersistenceCodec.ContractVersion ||
            !string.Equals(
                stored.ResultCode,
                "pet_result",
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The stored pet result contract is unsupported.");
        }

        var receipt = PetDurablePersistenceCodec.DecodeAndVerify(
            stored.ResultPayload,
            stored.ResultHash);
        if (receipt.Family != envelope.Family ||
            receipt.AccountId != envelope.Subject.AccountId ||
            receipt.CharacterId != envelope.Subject.CharacterId ||
            !string.Equals(
                receipt.AuditReference,
                stored.AuditId.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The stored pet result identity is inconsistent.");
        }

        return receipt;
    }

    private static void AddIdentityParameters(
        NpgsqlCommand command,
        Application.Commands.CommandSubject subject,
        Application.Commands.CommandFamily family,
        byte[] operationId)
    {
        command.Parameters.AddWithValue(
            "principalType",
            PetDurablePersistenceCodec.PrincipalType);
        command.Parameters.AddWithValue(
            "principalKey",
            subject.AccountId.ToString(
                System.Globalization.CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue(
            "aggregateType",
            PetDurablePersistenceCodec.AggregateType);
        command.Parameters.AddWithValue(
            "aggregateKey",
            PetDurablePersistenceCodec.AggregateKey(
                subject.CharacterId));
        command.Parameters.AddWithValue(
            "commandFamily",
            PetDurablePersistenceCodec.FamilyCode(family));
        command.Parameters.Add(
            "operationId",
            NpgsqlTypes.NpgsqlDbType.Bytea).Value = operationId;
    }
}
