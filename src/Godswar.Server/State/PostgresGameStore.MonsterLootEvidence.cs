using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.State;

internal sealed partial class PostgresGameStore
{
    private static async Task AdvanceLootInventoryRevisionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int accountId,
        int characterId,
        long before,
        long after,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            UPDATE public.character_base
            SET inventory_revision = @after
            WHERE account_id = @accountId AND id = @characterId
              AND inventory_revision = @before;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("after", after);
        command.Parameters.AddWithValue("accountId", accountId);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("before", before);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidDataException(
                "Loot pickup inventory revision was not advanced once.");
        }
    }

    private static async Task<long> InsertLootCommandEvidenceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int accountId,
        int characterId,
        Guid deathEventId,
        int lootIndex,
        uint itemId,
        int quantity,
        long inventoryRevision,
        CancellationToken cancellationToken)
    {
        var operationId = new byte[20];
        deathEventId.TryWriteBytes(operationId.AsSpan(0, 16));
        BinaryPrimitives.WriteInt32BigEndian(operationId.AsSpan(16), lootIndex);
        var payload = JsonSerializer.Serialize(new
        {
            deathEventId,
            lootIndex,
            itemId,
            quantity,
            inventoryRevision
        });
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        var principalKey = $"account:{accountId}";
        var aggregateKey = $"character:{characterId}";
        long auditId;
        await using (var command = new NpgsqlCommand(
            """
            INSERT INTO public.command_audit (
                principal_type, principal_key, aggregate_type,
                aggregate_key, command_family, operation_id,
                request_hash, outcome_code, detail_payload)
            VALUES ('account', @principalKey, 'character', @aggregateKey,
                    'monster_loot_pickup', @operationId, @hash,
                    'committed', @payload)
            RETURNING id;
            """,
            connection,
            transaction))
        {
            command.Parameters.AddWithValue("principalKey", principalKey);
            command.Parameters.AddWithValue("aggregateKey", aggregateKey);
            command.Parameters.Add("operationId", NpgsqlDbType.Bytea).Value =
                operationId;
            command.Parameters.Add("hash", NpgsqlDbType.Bytea).Value = hash;
            command.Parameters.Add("payload", NpgsqlDbType.Jsonb).Value =
                payload;
            auditId = await command.ExecuteScalarAsync(cancellationToken)
                is long value && value > 0
                ? value
                : throw new InvalidDataException(
                    "Loot pickup audit returned no identity.");
        }

        await using var inbox = new NpgsqlCommand(
            """
            INSERT INTO public.command_inbox (
                principal_type, principal_key, aggregate_type,
                aggregate_key, command_family, operation_id,
                request_hash, result_contract_version, result_code,
                result_payload, result_hash, audit_id)
            VALUES ('account', @principalKey, 'character', @aggregateKey,
                    'monster_loot_pickup', @operationId, @hash,
                    1, 'committed', @payload, @hash, @auditId)
            RETURNING id;
            """,
            connection,
            transaction);
        inbox.Parameters.AddWithValue("principalKey", principalKey);
        inbox.Parameters.AddWithValue("aggregateKey", aggregateKey);
        inbox.Parameters.Add("operationId", NpgsqlDbType.Bytea).Value =
            operationId;
        inbox.Parameters.Add("hash", NpgsqlDbType.Bytea).Value = hash;
        inbox.Parameters.Add("payload", NpgsqlDbType.Jsonb).Value = payload;
        inbox.Parameters.AddWithValue("auditId", auditId);
        return await inbox.ExecuteScalarAsync(cancellationToken)
            is long inboxId && inboxId > 0
            ? inboxId
            : throw new InvalidDataException(
                "Loot pickup inbox returned no identity.");
    }

    private static async Task InsertLootInventoryLedgerAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long inboxId,
        int accountId,
        int characterId,
        long inventoryRevision,
        IReadOnlyList<LootMutation> mutations,
        CancellationToken cancellationToken)
    {
        for (var index = 0; index < mutations.Count; index++)
        {
            var mutation = mutations[index];
            await using var command = new NpgsqlCommand(
                """
                INSERT INTO public.character_inventory_ledger (
                    command_inbox_id, account_id, character_id,
                    inventory_revision, entry_ordinal, item_instance_id,
                    mutation_kind, before_state, after_state, reason_code)
                VALUES (@inboxId, @accountId, @characterId,
                        @revision, @ordinal, @itemId, @kind,
                        @before, @after, 'monster_loot_pickup');
                """,
                connection,
                transaction);
            command.Parameters.AddWithValue("inboxId", inboxId);
            command.Parameters.AddWithValue("accountId", accountId);
            command.Parameters.AddWithValue("characterId", characterId);
            command.Parameters.AddWithValue("revision", inventoryRevision);
            command.Parameters.AddWithValue("ordinal", checked((short)index));
            command.Parameters.AddWithValue("itemId", mutation.InstanceId);
            command.Parameters.AddWithValue("kind", mutation.Kind);
            command.Parameters.Add("before", NpgsqlDbType.Jsonb).Value =
                mutation.BeforeState is null
                    ? DBNull.Value
                    : mutation.BeforeState;
            command.Parameters.Add("after", NpgsqlDbType.Jsonb).Value =
                mutation.AfterState is null
                    ? DBNull.Value
                    : mutation.AfterState;
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new InvalidDataException(
                    "Loot inventory ledger row was not inserted once.");
            }
        }
    }

    private static async Task InsertLootClaimAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid deathEventId,
        int lootIndex,
        int accountId,
        int characterId,
        uint itemId,
        int quantity,
        long inventoryRevision,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO public.monster_loot_pickup_claims (
                death_event_id, loot_index, account_id, character_id,
                item_id, quantity, inventory_revision)
            VALUES (@deathEventId, @lootIndex, @accountId, @characterId,
                    @itemId, @quantity, @inventoryRevision);
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("deathEventId", deathEventId);
        command.Parameters.AddWithValue("lootIndex", checked((short)lootIndex));
        command.Parameters.AddWithValue("accountId", accountId);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("itemId", checked((int)itemId));
        command.Parameters.AddWithValue("quantity", checked((short)quantity));
        command.Parameters.AddWithValue("inventoryRevision", inventoryRevision);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidDataException(
                "Monster loot claim was not inserted exactly once.");
        }
    }
}
